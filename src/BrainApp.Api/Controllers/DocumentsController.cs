using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// Document ingestion, listing, and management per profile.
/// </summary>
[ApiController]
[Route("profiles/{profileId}/[controller]")]
[Tags("Documents")]
public class DocumentsController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly IngestionService _ingestionService;
    private readonly RetrievalService _retrievalService;
    private readonly IndexingStatusService _indexingStatus;
    private readonly CacheService _cacheService;

    public DocumentsController(
        ProfileRepository profileRepo,
        IngestionService ingestionService,
        RetrievalService retrievalService,
        IndexingStatusService indexingStatus,
        CacheService cacheService)
    {
        _profileRepo = profileRepo;
        _ingestionService = ingestionService;
        _retrievalService = retrievalService;
        _indexingStatus = indexingStatus;
        _cacheService = cacheService;
    }

    /// <summary>
    /// Upload and ingest a document into a profile.
    /// Accepts multipart/form-data with a "file" field.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(52_428_800)] // 50 MB
    public async Task<IActionResult> Upload(string profileId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var file = Request.Form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded. Send multipart/form-data with a 'file' field." });

        var tempPath = Path.Combine(Path.GetTempPath(), $"brainapp_{Guid.NewGuid():N}_{file.FileName}");
        try
        {
            // Save uploaded file to temp path
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Ingest the file
            var progress = new Progress<(int step, string message, int percent)>(p =>
            {
                Log.Debug("Ingest progress: {Step} - {Message} ({Percent}%)", p.step, p.message, p.percent);
            });

            // IngestFileAsync now persists the document row and adds chunks to the
            // retrieval index internally, so we don't double-save here.
            (var document, var chunks) = await _ingestionService.IngestFileAsync(profileId, tempPath, progress);

            Log.Information("Document indexed: {DocId} ({FileName}) into profile {ProfileId}, {ChunkCount} chunks",
                document.Id, document.FileName, profileId, document.ChunkCount);

            return Created($"/profiles/{profileId}/documents/{document.Id}", new
            {
                id = document.Id,
                fileName = document.FileName,
                chunkCount = document.ChunkCount,
                status = document.Status.ToString(),
                sizeBytes = document.SizeBytes,
                pageCount = document.PageCount
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("DUPLICATE:"))
        {
            return Ok(new
            {
                duplicateSkipped = true,
                message = ex.Message.Replace("DUPLICATE: ", "")
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to ingest document {FileName} for profile {ProfileId}", file.FileName, profileId);
            return StatusCode(500, new { error = $"Ingestion failed: {ex.Message}" });
        }
        finally
        {
            // Clean up temp file
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// List all documents in a profile.
    /// </summary>
    [HttpGet]
    public IActionResult List(string profileId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var documents = _profileRepo.GetDocuments(profileId);
        return Ok(documents.Select(d => new
        {
            d.Id,
            d.ProfileId,
            d.FileName,
            d.Type,
            d.SizeBytes,
            d.PageCount,
            d.ChunkCount,
            d.IndexedAt,
            d.Status,
            d.ErrorMessage
        }));
    }

    /// <summary>
    /// Delete a document and remove its chunks from the retrieval index.
    /// </summary>
    [HttpDelete("{documentId}")]
    public IActionResult Delete(string profileId, string documentId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var document = _profileRepo.GetDocument(profileId, documentId);
        if (document == null)
            return NotFound(new { error = $"Document '{documentId}' not found in profile '{profileId}'" });

        // Remove from retrieval index
        _retrievalService.RemoveDocumentAsync(profileId, documentId).GetAwaiter().GetResult();

        // Delete from repository
        _profileRepo.DeleteDocument(profileId, documentId);

        // Document removed — cached answers may reference it, so invalidate.
        _cacheService.InvalidateProfile(profileId);

        Log.Information("Document deleted: {DocId} from profile {ProfileId}", documentId, profileId);
        return NoContent();
    }

    /// <summary>
    /// Re-index all documents in a profile (use after model swap).
    /// Returns 202 Accepted — runs in background.
    /// </summary>
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(string profileId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var documents = _profileRepo.GetDocuments(profileId);
        if (documents.Count == 0)
            return Ok(new { message = "No documents to reindex", documentsProcessed = 0 });

        _indexingStatus.Start(profileId, documents.Count);

        _ = Task.Run(async () =>
        {
            int processed = 0;
            try
            {
                foreach (var doc in documents)
                {
                    _indexingStatus.Report(profileId, doc.FileName, processed * 100.0 / documents.Count);
                    try
                    {
                        var progress = new Progress<(int step, string message, int percent)>(p =>
                        {
                            var overall = (processed + p.percent / 100.0) / documents.Count * 100.0;
                            _indexingStatus.Report(profileId, doc.FileName, overall);
                        });

                        // IngestFileAsync handles removal of stale chunks + persistence internally.
                        await _ingestionService.IngestFileAsync(profileId, doc.FilePath, progress);

                        processed++;
                        _indexingStatus.IncrementCompleted(profileId);
                        Log.Information("Reindexed document {DocId} ({Processed}/{Total})", doc.Id, processed, documents.Count);
                    }
                    catch (Exception ex)
                    {
                        _indexingStatus.IncrementFailed(profileId);
                        // IngestFileAsync already persisted Status=Error for the row.
                        Log.Error(ex, "Failed to reindex document {DocId}", doc.Id);
                    }
                }
                Log.Information("Reindex complete for profile {ProfileId}: {Processed}/{Total} succeeded",
                    profileId, processed, documents.Count);
            }
            finally
            {
                _indexingStatus.Finish(profileId);
            }
        });

        return Accepted(new { message = "Reindex started in background", documentCount = documents.Count });
    }

    /// <summary>
    /// Poll the background reindex status for a profile. Returns running flag,
    /// totals, the file currently being indexed, and overall percent.
    /// </summary>
    [HttpGet("/profiles/{profileId}/indexing-status")]
    public IActionResult IndexingStatus(string profileId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var snap = _indexingStatus.Get(profileId);
        return Ok(new
        {
            running = snap.Running,
            total = snap.Total,
            completed = snap.Completed,
            failed = snap.Failed,
            currentFile = snap.CurrentFile,
            percent = snap.Percent,
            updatedAt = snap.UpdatedAt
        });
    }
}