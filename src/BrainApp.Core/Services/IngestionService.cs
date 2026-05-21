using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Markdig;
using Microsoft.Extensions.Options;
using Serilog;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using BrainApp.Core.Config;
using BrainApp.Core.Models;

namespace BrainApp.Core.Services;

/// <summary>
/// Handles document ingestion: parsing, chunking, embedding, and duplicate detection.
/// Supports: PDF, DOCX, HTML, Markdown, TXT, and images (via Tesseract OCR).
/// </summary>
public class IngestionService
{
    private readonly LlamaService _llama;
    private readonly CacheService _cache;
    private readonly RetrievalService _retrieval;
    private readonly ProfileRepository _profileRepo;
    private readonly RetrievalSettings _retrievalSettings;
    private readonly StorageSettings _storageSettings;

    public IngestionService(
        LlamaService llama,
        CacheService cache,
        RetrievalService retrieval,
        ProfileRepository profileRepo,
        IOptions<RetrievalSettings> retrievalSettings,
        IOptions<StorageSettings> storageSettings)
    {
        _llama = llama;
        _cache = cache;
        _retrieval = retrieval;
        _profileRepo = profileRepo;
        _retrievalSettings = retrievalSettings.Value;
        _storageSettings = storageSettings.Value;
    }

    /// <summary>
    /// Ingest a file into a profile. Returns Document with chunks.
    /// Throws on parse error. Re-ingests previously failed/stuck docs in place; only `Ready` docs are treated as duplicates.
    /// On failure, the Document row is persisted with Status = Error so the user can retry without manual cleanup.
    /// </summary>
    public async Task<(Document document, List<DocumentChunk> chunks)> IngestFileAsync(
        string profileId,
        string filePath,
        IProgress<(int step, string message, int percent)>? progress = null,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        var extension = fileInfo.Extension.ToLowerInvariant();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Check file size limit
        var maxBytes = _storageSettings.MaxFileSizeMb * 1024 * 1024;
        if (fileInfo.Length > maxBytes)
            throw new InvalidOperationException($"File exceeds maximum size of {_storageSettings.MaxFileSizeMb} MB: {fileInfo.Name}");

        // Compute hash + look up existing doc
        var fileHash = await ComputeHashAsync(filePath, ct);
        var existingDoc = _profileRepo.GetDocumentByHash(profileId, fileHash);

        // Only treat truly Ready documents as duplicates. Error/Indexing rows are
        // recoverable: we reuse their Id and re-run the pipeline below.
        if (existingDoc != null && existingDoc.Status == DocumentStatus.Ready)
        {
            Log.Information("Duplicate file skipped: {FileName} (hash: {Hash})", fileInfo.Name, fileHash);
            throw new InvalidOperationException($"DUPLICATE: {fileInfo.Name} already indexed.");
        }

        var documentId = existingDoc?.Id ?? Guid.NewGuid().ToString("N")[..8];

        // Build document record (kept in scope so the catch block can persist error state)
        var document = new Document
        {
            Id = documentId,
            ProfileId = profileId,
            FileName = fileInfo.Name,
            FilePath = filePath,
            FileHash = fileHash,
            Type = GetDocumentType(extension),
            SizeBytes = fileInfo.Length,
            PageCount = 0,
            Status = DocumentStatus.Indexing,
            IndexedAt = DateTime.UtcNow
        };

        // Persist Indexing state immediately so a crash leaves a recoverable row,
        // not a phantom file that's silently been embedded but never recorded.
        _profileRepo.SaveDocument(document);

        // If we're reindexing an existing doc, drop its old chunks first.
        if (existingDoc != null)
        {
            await _retrieval.RemoveDocumentAsync(profileId, existingDoc.Id, ct);
        }

        try
        {
            progress?.Report((1, $"Parsing {fileInfo.Name}...", 10));

            // Parse content based on file type
            var (text, pageCount) = await ParseFileAsync(filePath, extension, ct);
            document.PageCount = pageCount;

            progress?.Report((2, "Chunking content...", 30));

            // Chunk the text. Only PDFs carry true page numbers (injected via "[Page N]" markers
            // during parsing); for every other format we fall back to a synthesized section index.
            var isPaginated = extension == ".pdf";
            var chunks = ChunkText(text, document.Id, profileId, fileInfo.Name, isPaginated);
            document.ChunkCount = chunks.Count;

            if (chunks.Count == 0)
                throw new InvalidOperationException($"No extractable text in {fileInfo.Name}.");

            progress?.Report((3, $"Embedding {chunks.Count} chunks...", 50));

            // Embed each chunk
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                chunk.Embedding = await _llama.EmbedAsync(chunk.Text, ct);
                progress?.Report((3, $"Embedding chunk {i + 1}/{chunks.Count}...", 50 + (int)(40.0 * i / chunks.Count)));
            }

            progress?.Report((4, "Saving to index...", 90));

            // Add chunks to retrieval index
            await _retrieval.AddChunksAsync(profileId, chunks, ct);

            // Finalize document state
            document.Status = DocumentStatus.Ready;
            document.IndexedAt = DateTime.UtcNow;
            document.ErrorMessage = null;
            _profileRepo.SaveDocument(document);

            // New content landed — invalidate cached answers so the next query reflects it.
            _cache.InvalidateProfile(profileId);

            progress?.Report((5, "Complete", 100));
            sw.Stop();
            Log.Information("Indexed {ChunkCount} chunks from {FileName} in {Elapsed}ms",
                chunks.Count, fileInfo.Name, sw.ElapsedMilliseconds);

            return (document, chunks);
        }
        catch (OperationCanceledException)
        {
            document.Status = DocumentStatus.Error;
            document.ErrorMessage = "Cancelled";
            _profileRepo.SaveDocument(document);
            throw;
        }
        catch (Exception ex)
        {
            document.Status = DocumentStatus.Error;
            document.ErrorMessage = ex.Message;
            _profileRepo.SaveDocument(document);
            Log.Error(ex, "Ingestion failed for {FileName} after {Elapsed}ms",
                fileInfo.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static DocumentType GetDocumentType(string extension) => extension switch
    {
        ".pdf" => DocumentType.Pdf,
        ".docx" => DocumentType.Docx,
        ".doc" => DocumentType.Doc,
        ".pptx" => DocumentType.Pptx,
        ".txt" => DocumentType.Txt,
        ".markdown" or ".md" => DocumentType.Markdown,
        ".html" or ".htm" => DocumentType.Html,
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => DocumentType.Image,
        _ => DocumentType.Unknown
    };

    private async Task<(string text, int pageCount)> ParseFileAsync(
        string filePath, string extension, CancellationToken ct)
    {
        return extension switch
        {
            ".pdf" => await ParsePdfAsync(filePath, ct),
            ".docx" => await ParseDocxAsync(filePath, ct),
            ".pptx" => await ParsePptxAsync(filePath, ct),
            ".txt" => await ParseTxtAsync(filePath, ct),
            ".html" or ".htm" => await ParseHtmlAsync(filePath, ct),
            ".markdown" or ".md" => await ParseMarkdownAsync(filePath, ct),
            ".png" or ".jpg" or ".jpeg" or ".webp" => await ParseImageAsync(filePath, ct),
            _ => (await File.ReadAllTextAsync(filePath, ct), 0)
        };
    }

    private static async Task<(string text, int pageCount)> ParsePdfAsync(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int pageCount = 0;

        await Task.Run(() =>
        {
            var options = new ParsingOptions { Password = "", UseLenientParsing = true };
            UglyToad.PdfPig.PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(path, options);
            }
            catch (PdfDocumentEncryptedException ex)
            {
                throw new InvalidOperationException(
                    $"PDF is encrypted or password-protected and cannot be indexed: {Path.GetFileName(path)}", ex);
            }

            using (doc)
            {
                pageCount = doc.NumberOfPages;
                foreach (var page in doc.GetPages())
                {
                    // Sort words by position (top→bottom, left→right) so the extracted text
                    // matches reading order. page.Text uses PDF stream order which is often
                    // scrambled in scanned/OCR'd PDFs, breaking both keyword and semantic search.
                    var words = page.GetWords()
                        .OrderByDescending(w => w.BoundingBox.Bottom)
                        .ThenBy(w => w.BoundingBox.Left)
                        .Select(w => w.Text);
                    var text = string.Join(" ", words);
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine($"[Page {page.Number}] {text}");
                }
            }
        }, ct);

        return (sb.ToString(), pageCount);
    }

    private static async Task<(string text, int pageCount)> ParsePptxAsync(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int slideCount = 0;

        await Task.Run(() =>
        {
            using var doc = PresentationDocument.Open(path, false);
            var slides = doc.PresentationPart?.SlideParts ?? Enumerable.Empty<SlidePart>();
            foreach (var slidePart in slides)
            {
                slideCount++;
                var text = slidePart.Slide?.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"[Slide {slideCount}] {text}");

                var notesPart = slidePart.NotesSlidePart;
                if (notesPart != null)
                {
                    var notesText = notesPart.NotesSlide.InnerText;
                    if (!string.IsNullOrWhiteSpace(notesText))
                        sb.AppendLine(notesText);
                }
            }
        }, ct);

        return (sb.ToString(), slideCount);
    }

    private static async Task<(string text, int pageCount)> ParseDocxAsync(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();

        await Task.Run(() =>
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body != null)
            {
                foreach (var para in body.Elements())
                {
                    var text = para.InnerText;
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text);
                }
            }
        }, ct);

        return (sb.ToString(), 1);
    }

    private static async Task<(string text, int pageCount)> ParseTxtAsync(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        return (text, 1);
    }

    private static async Task<(string text, int pageCount)> ParseHtmlAsync(string path, CancellationToken ct)
    {
        var html = await File.ReadAllTextAsync(path, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove script and style elements
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//head") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var text = doc.DocumentNode.InnerText;
        return (text, 1);
    }

    private static async Task<(string text, int pageCount)> ParseMarkdownAsync(string path, CancellationToken ct)
    {
        var md = await File.ReadAllTextAsync(path, ct);
        var html = Markdown.ToHtml(md);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var text = doc.DocumentNode.InnerText;
        return (text, 1);
    }

    private async Task<(string text, int pageCount)> ParseImageAsync(string path, CancellationToken ct)
    {
        // Tesseract OCR — graceful fallback if tessdata missing
        var tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessDataPath))
        {
            Log.Warning("tessdata folder not found at {Path}. OCR disabled. Install eng.traineddata for image indexing.", tessDataPath);
            return ($"[Image: {Path.GetFileName(path)} — install tessdata for OCR]", 1);
        }

        try
        {
            using var engine = new Tesseract.TesseractEngine(tessDataPath, _retrievalSettings.OcrLanguages, Tesseract.EngineMode.Default);
            using var img = Tesseract.Pix.LoadFromFile(path);
            using var page = engine.Process(img);
            var text = page.GetText();
            return (text, 1);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR failed for {Path}. Falling back to placeholder.", path);
            return ($"[Image: {Path.GetFileName(path)} — OCR failed: {ex.Message}]", 1);
        }
    }

    /// <summary>
    /// Split text into chunks of ChunkSize with ChunkOverlap, snapping to sentence boundaries.
    /// When <paramref name="isPaginated"/> is false, PageNumber is a 1-based section index
    /// (the chunk's own position) and IsPaginated is false so callers can render
    /// "[file, section N]" instead of "page N".
    /// </summary>
    private List<DocumentChunk> ChunkText(string text, string documentId, string profileId, string fileName, bool isPaginated)
    {
        var chunks = new List<DocumentChunk>();
        var sentences = SplitIntoSentences(text);
        var sb = new StringBuilder();
        int chunkIndex = 0;

        foreach (var sentence in sentences)
        {
            if (sb.Length + sentence.Length > _retrievalSettings.ChunkSize && sb.Length >= _retrievalSettings.MinChunkLength)
            {
                // Create chunk
                chunks.Add(new DocumentChunk
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    DocumentId = documentId,
                    ProfileId = profileId,
                    FileName = fileName,
                    Text = sb.ToString().Trim(),
                    ChunkIndex = chunkIndex,
                    PageNumber = isPaginated ? ExtractPageNumber(sb.ToString()) : chunkIndex + 1,
                    IsPaginated = isPaginated
                });
                chunkIndex++;

                // Keep overlap
                var overlapText = sb.ToString();
                sb.Clear();
                if (_retrievalSettings.ChunkOverlap > 0 && overlapText.Length > _retrievalSettings.ChunkOverlap)
                {
                    sb.Append(overlapText[^_retrievalSettings.ChunkOverlap..]);
                }
            }
            sb.Append(sentence);
        }

        // Final chunk
        if (sb.Length >= _retrievalSettings.MinChunkLength)
        {
            chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                DocumentId = documentId,
                ProfileId = profileId,
                FileName = fileName,
                Text = sb.ToString().Trim(),
                ChunkIndex = chunkIndex,
                PageNumber = isPaginated ? ExtractPageNumber(sb.ToString()) : chunkIndex + 1,
                IsPaginated = isPaginated
            });
        }

        return chunks;
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            sb.Append(c);
            if (c is '.' or '!' or '?' or '\n')
            {
                if (i + 1 < text.Length && text[i + 1] == ' ')
                {
                    sb.Append(' ');
                    i++;
                }
                if (sb.Length > 10) // Ignore very short fragments
                    sentences.Add(sb.ToString());
                sb.Clear();
            }
            i++;
        }
        if (sb.Length > 0)
            sentences.Add(sb.ToString());
        return sentences;
    }

    private static int ExtractPageNumber(string chunkText)
    {
        var match = System.Text.RegularExpressions.Regex.Match(chunkText, @"\[Page (\d+)\]");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>
    /// Compute SHA-256 hash of file (first 16 chars of hex).
    /// </summary>
    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Remove a document from the index.
    /// </summary>
    public async Task RemoveDocumentAsync(string profileId, string documentId)
    {
        await _retrieval.RemoveDocumentAsync(profileId, documentId);
    }
}