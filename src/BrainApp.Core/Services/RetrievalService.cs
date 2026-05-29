using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;

namespace BrainApp.Core.Services;

/// <summary>
/// Hybrid semantic + keyword vector retrieval backed by SQLite for persistence.
/// Chunks are loaded into memory per-profile for fast search, with write-through
/// to SQLite on add/remove for durability.
/// </summary>
public class RetrievalService
{
    private readonly LlamaService _llama;
    private readonly CacheService _cache;
    private readonly ProfileRepository _profileRepo;
    private readonly RetrievalSettings _settings;
    private readonly StorageSettings _storageSettings;
    private readonly LlamaSettings _llamaSettings;

    private readonly ConcurrentDictionary<string, List<DocumentChunk>> _index = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new();
    private readonly ConcurrentDictionary<string, bool> _embeddingMismatchWarned = new();
    // Per-profile IDF model for keyword scoring; rebuilt lazily after any chunk mutation.
    private readonly ConcurrentDictionary<string, IdfModel> _idfCache = new();

    private SemaphoreSlim GetLock(string profileId) =>
        _indexLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    public RetrievalService(
        LlamaService llama,
        CacheService cache,
        ProfileRepository profileRepo,
        IOptions<RetrievalSettings> settings,
        IOptions<StorageSettings> storageSettings,
        IOptions<LlamaSettings> llamaSettings)
    {
        _llama = llama;
        _cache = cache;
        _profileRepo = profileRepo;
        _settings = settings.Value;
        _storageSettings = storageSettings.Value;
        _llamaSettings = llamaSettings.Value;
    }

    /// <summary>
    /// Add chunks to the in-memory index and persist to SQLite.
    /// </summary>
    public async Task AddChunksAsync(string profileId, List<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var sem = GetLock(profileId);
        await sem.WaitAsync(ct);
        try
        {
            var list = _index.GetOrAdd(profileId, _ => new List<DocumentChunk>());
            list.AddRange(chunks);
            _profileRepo.SaveChunks(chunks);
            _idfCache.TryRemove(profileId, out _);
        }
        finally
        {
            sem.Release();
        }

        Log.Debug("Added {Count} chunks to index for profile {ProfileId}", chunks.Count, profileId);
    }

    /// <summary>
    /// Remove all chunks belonging to a document from both memory and SQLite.
    /// </summary>
    public async Task RemoveDocumentAsync(string profileId, string documentId, CancellationToken ct = default)
    {
        var sem = GetLock(profileId);
        await sem.WaitAsync(ct);
        try
        {
            if (_index.TryGetValue(profileId, out var list))
            {
                list.RemoveAll(c => c.DocumentId == documentId);
            }
            _profileRepo.DeleteChunksByDocument(documentId);
            _idfCache.TryRemove(profileId, out _);
        }
        finally
        {
            sem.Release();
        }

        Log.Information("Removed document {DocumentId} from index for profile {ProfileId}", documentId, profileId);
    }

    /// <summary>
    /// Clear all chunks for a profile from both memory and SQLite.
    /// </summary>
    public async Task ClearProfileAsync(string profileId, CancellationToken ct = default)
    {
        var sem = GetLock(profileId);
        await sem.WaitAsync(ct);
        try
        {
            _index.TryRemove(profileId, out _);
            _idfCache.TryRemove(profileId, out _);
            _profileRepo.DeleteChunksByProfile(profileId);
        }
        finally
        {
            sem.Release();
        }
        _cache.InvalidateProfile(profileId);
        Log.Information("Cleared index for profile {ProfileId}", profileId);
    }

    /// <summary>
    /// Get total chunk count for a profile.
    /// </summary>
    public int GetChunkCount(string profileId)
    {
        if (_index.TryGetValue(profileId, out var list) && list.Count > 0)
            return list.Count;
        return _profileRepo.GetChunkCountByProfile(profileId);
    }

    /// <summary>
    /// Retrieve the best chunks for a query.
    /// 1. Normalize (and optionally expand) the query
    /// 2. Hybrid scoring: semanticWeight * cosine + keywordWeight * IDF-weighted keyword overlap
    /// 3. Take TopK candidates, then select the final set via cross-encoder rerank
    ///    (if a reranker model is loaded) or MMR diversity selection as a fallback.
    /// </summary>
    public async Task<List<RetrievedChunk>> RetrieveAsync(
        string profileId,
        string query,
        int? topK = null,
        CancellationToken ct = default)
    {
        var chunks = LoadChunksForProfile(profileId);
        if (chunks.Count == 0)
            return new List<RetrievedChunk>();

        WarnIfEmbeddingModelMismatch(profileId, chunks);

        // The user's actual question — used for reranking and as the embedding-cache key.
        query = NormalizeQuery(query);
        // The text used for recall (embedding + keywords); may be widened by query expansion.
        var searchQuery = await MaybeExpandQueryAsync(query, ct);

        var queryEmbedding = await _llama.EmbedAsync(searchQuery, ct);

        var idf = GetIdf(profileId, chunks);
        var queryTokens = Tokenize(searchQuery);
        var scoredChunks = new List<RetrievedChunk>();
        int nullEmbeds = 0;

        foreach (var chunk in chunks)
        {
            if (chunk.Embedding == null || chunk.Embedding.Length == 0)
            {
                nullEmbeds++;
                continue;
            }

            double semanticScore = CosineSimilarity(queryEmbedding, chunk.Embedding);
            double keywordScore = CalculateKeywordScore(queryTokens, Tokenize(chunk.Text), idf);

            double finalScore = _settings.SemanticWeight * semanticScore +
                               _settings.KeywordWeight * keywordScore;

            scoredChunks.Add(new RetrievedChunk
            {
                Chunk = chunk,
                Score = finalScore,
                SemanticScore = semanticScore,
                KeywordScore = keywordScore
            });
        }

        var ordered = scoredChunks.OrderByDescending(c => c.Score).ToList();
        var aboveThreshold = ordered.Where(c => c.Score >= _settings.MinRelevanceScore).ToList();

        // Candidate pool to (re)rank, then how many to keep for the model.
        var candidates = (aboveThreshold.Count > 0 ? aboveThreshold : ordered).Take(_settings.TopK).ToList();
        int finalCount = topK ?? (_settings.EnableReranker && _settings.RerankTopN > 0
            ? _settings.RerankTopN
            : _settings.TopK);

        var result = await SelectChunksAsync(query, candidates, finalCount, ct);

        if (nullEmbeds == chunks.Count && chunks.Count > 0)
        {
            Log.Error(
                "Retrieval: all {Count} chunks for profile {ProfileId} have missing embeddings — re-index documents",
                chunks.Count, profileId);
        }

        Log.Information(
            "Retrieval: chunks={Chunks} nullEmbeddings={Null} scored={Scored} aboveThreshold={Pass} candidates={Cand} returned={Returned} topScore={Top:F3} minScore={Min:F3} reranker={Rr}",
            chunks.Count, nullEmbeds, ordered.Count, aboveThreshold.Count, candidates.Count, result.Count,
            ordered.Count > 0 ? ordered[0].Score : 0.0,
            _settings.MinRelevanceScore, _llama.HasReranker ? "cross-encoder" : "mmr");
        return result;
    }

    /// <summary>
    /// Select the final chunks from the candidate pool. Prefers a cross-encoder reranker
    /// (replacing each chunk's Score with the relevance score); falls back to MMR diversity
    /// selection over the embeddings we already computed. Both return a list sorted by Score
    /// descending so downstream ShapeChunks can rely on that ordering.
    /// </summary>
    private async Task<List<RetrievedChunk>> SelectChunksAsync(
        string query, List<RetrievedChunk> candidates, int finalCount, CancellationToken ct)
    {
        if (finalCount <= 0) return new List<RetrievedChunk>();
        if (candidates.Count <= finalCount && !_settings.EnableReranker)
            return candidates;
        if (!_settings.EnableReranker)
            return candidates.Take(finalCount).ToList();

        if (_llama.HasReranker && candidates.Count > 1)
        {
            var docs = candidates.Select(c => c.Chunk.Text ?? string.Empty).ToList();
            var scores = await _llama.RerankAsync(query, docs, ct);
            if (scores != null && scores.Count == candidates.Count)
            {
                for (int i = 0; i < candidates.Count; i++)
                    candidates[i].Score = scores[i]; // relevance score replaces hybrid score
                return candidates.OrderByDescending(c => c.Score).Take(finalCount).ToList();
            }
        }

        return SelectMmr(candidates, finalCount);
    }

    /// <summary>
    /// Maximal Marginal Relevance: greedily pick chunks balancing relevance (hybrid Score)
    /// against redundancy (max cosine similarity to already-selected chunks). λ=0.7.
    /// </summary>
    internal static List<RetrievedChunk> SelectMmr(List<RetrievedChunk> candidates, int finalCount)
    {
        const double lambda = 0.7;
        var selected = new List<RetrievedChunk>(Math.Min(finalCount, candidates.Count));
        var remaining = new List<RetrievedChunk>(candidates);

        while (selected.Count < finalCount && remaining.Count > 0)
        {
            RetrievedChunk best = remaining[0];
            double bestVal = double.NegativeInfinity;
            foreach (var cand in remaining)
            {
                double maxSim = 0;
                foreach (var s in selected)
                {
                    if (cand.Chunk.Embedding == null || s.Chunk.Embedding == null) continue;
                    double sim = CosineSimilarity(cand.Chunk.Embedding, s.Chunk.Embedding);
                    if (sim > maxSim) maxSim = sim;
                }
                double mmr = lambda * cand.Score - (1 - lambda) * maxSim;
                if (mmr > bestVal) { bestVal = mmr; best = cand; }
            }
            selected.Add(best);
            remaining.Remove(best);
        }

        return selected.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>Optionally widen the query with the chat model for better recall (off by default).</summary>
    private async Task<string> MaybeExpandQueryAsync(string query, CancellationToken ct)
    {
        if (!_settings.EnableQueryExpansion || string.IsNullOrWhiteSpace(query))
            return query;

        try
        {
            const string sys = "You expand a search query into a short list of keywords and synonyms in the SAME language as the query. Output only the keywords separated by spaces — no explanation, no punctuation.";
            var expanded = await _llama.ChatAsync(sys, new List<(MessageRole, string)>(), query, ct);
            expanded = expanded?.Trim() ?? string.Empty;
            return expanded.Length == 0 ? query : query + " " + expanded;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Query expansion failed; using original query.");
            return query;
        }
    }

    private List<DocumentChunk> LoadChunksForProfile(string profileId)
    {
        if (_index.TryGetValue(profileId, out var cached) && cached.Count > 0)
            return cached;

        if (_index.TryGetValue(profileId, out var empty) && empty.Count == 0)
            _index.TryRemove(profileId, out _);

        MigrateFromIndexBin(profileId, _storageSettings.ResolvedAppDataFolder);
        var chunks = _profileRepo.GetChunksByProfile(profileId);
        if (chunks.Count > 0)
            _index[profileId] = chunks;

        return chunks;
    }

    private void WarnIfEmbeddingModelMismatch(string profileId, List<DocumentChunk> chunks)
    {
        if (!_embeddingMismatchWarned.TryAdd(profileId, true))
            return;

        var stored = _profileRepo.GetProfileEmbeddingModel(profileId);
        var current = _llamaSettings.EmbeddingModelFile;
        if (string.IsNullOrEmpty(stored) || stored.Equals(current, StringComparison.OrdinalIgnoreCase))
            return;

        Log.Warning(
            "Profile {ProfileId}: chunks indexed with embedding model '{Stored}' but current model is '{Current}'. Re-index documents to restore semantic search.",
            profileId, stored, current);
    }

    /// <summary>
    /// Load chunks from SQLite into the in-memory index for a profile.
    /// Also performs one-time migration from legacy index.bin files.
    /// </summary>
    public Task LoadIndexAsync(string profileId, string appDataFolder)
    {
        if (_index.TryGetValue(profileId, out var existing) && existing.Count == 0)
            _index.TryRemove(profileId, out _);

        if (_index.ContainsKey(profileId))
            return Task.CompletedTask;

        MigrateFromIndexBin(profileId, appDataFolder);

        var chunks = _profileRepo.GetChunksByProfile(profileId);
        if (chunks.Count > 0)
        {
            _index[profileId] = chunks;
            Log.Information("Loaded index for profile {ProfileId}: {Count} chunks from SQLite", profileId, chunks.Count);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One-time migration: if a legacy index.bin exists, read it, insert into SQLite, then delete it.
    /// </summary>
    private void MigrateFromIndexBin(string profileId, string appDataFolder)
    {
        var indexPath = Path.Combine(appDataFolder, "profiles", profileId, "index.bin");
        if (!File.Exists(indexPath))
            return;

        try
        {
            Log.Information("Migrating legacy index.bin for profile {ProfileId} to SQLite...", profileId);

            using var stream = File.OpenRead(indexPath);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var count = reader.ReadInt32();
            var chunks = new List<DocumentChunk>(count);

            for (int i = 0; i < count; i++)
            {
                var textLen = reader.ReadInt32();
                var textBytes = reader.ReadBytes(textLen);
                var text = Encoding.UTF8.GetString(textBytes);

                var embedLen = reader.ReadInt32();
                var embedding = new float[embedLen];
                for (int j = 0; j < embedLen; j++)
                    embedding[j] = reader.ReadSingle();

                chunks.Add(new DocumentChunk
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Text = text,
                    Embedding = embedding,
                    DocumentId = reader.ReadString(),
                    FileName = reader.ReadString(),
                    ChunkIndex = reader.ReadInt32(),
                    PageNumber = reader.ReadInt32(),
                    ProfileId = profileId
                });
            }

            if (chunks.Count > 0)
            {
                _profileRepo.SaveChunks(chunks);
                Log.Information("Migrated {Count} chunks from index.bin to SQLite for profile {ProfileId}", chunks.Count, profileId);
            }

            File.Delete(indexPath);
            Log.Information("Deleted legacy index.bin for profile {ProfileId}", profileId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to migrate index.bin for profile {ProfileId}. File will be kept for manual recovery.", profileId);
        }
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return Math.Max(0, dot);
    }

    /// <summary>
    /// IDF-weighted keyword score in [0, 1]: the fraction of the query's total IDF mass
    /// that is matched in the chunk. Rare, content-bearing terms dominate over common ones.
    /// </summary>
    internal static double CalculateKeywordScore(List<string> queryTokens, List<string> chunkTokens, IdfModel idf)
    {
        if (queryTokens.Count == 0 || chunkTokens.Count == 0) return 0;

        var chunkSet = new HashSet<string>(chunkTokens);
        double matched = 0, total = 0;
        foreach (var q in new HashSet<string>(queryTokens))
        {
            double w = idf.Weight(q);
            total += w;
            if (chunkSet.Contains(q)) matched += w;
        }
        return total > 0 ? matched / total : 0;
    }

    private IdfModel GetIdf(string profileId, List<DocumentChunk> chunks) =>
        _idfCache.GetOrAdd(profileId, _ => BuildIdf(chunks));

    internal static IdfModel BuildIdf(List<DocumentChunk> chunks)
    {
        var df = new Dictionary<string, int>();
        foreach (var c in chunks)
            foreach (var term in new HashSet<string>(Tokenize(c.Text)))
                df[term] = df.TryGetValue(term, out var v) ? v + 1 : 1;

        int n = Math.Max(1, chunks.Count);
        var idf = new Dictionary<string, double>(df.Count);
        foreach (var kv in df)
            idf[kv.Key] = Math.Log((n + 1.0) / (kv.Value + 1.0)) + 1.0;

        return new IdfModel(idf, Math.Log(n + 1.0) + 1.0);
    }

    /// <summary>Per-profile inverse-document-frequency weights for keyword scoring.</summary>
    internal sealed class IdfModel
    {
        private readonly Dictionary<string, double> _idf;
        private readonly double _default;
        public IdfModel(Dictionary<string, double> idf, double defaultWeight) { _idf = idf; _default = defaultWeight; }
        public double Weight(string term) => _idf.TryGetValue(term, out var w) ? w : _default;
    }

    private static string NormalizeQuery(string query) =>
        string.IsNullOrWhiteSpace(query) ? string.Empty : Regex.Replace(query.Trim(), @"\s+", " ");

    // IT + EN function words that carry little retrieval signal.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        // English
        "the","and","for","are","but","not","you","with","this","that","from","have","has","had",
        "was","were","will","would","can","could","should","what","which","who","whom","whose","why",
        "how","when","where","there","here","then","than","into","onto","over","under","about","your",
        "our","their","its","his","her","they","them","these","those","such","also","may","might","must",
        // Italian
        "che","non","per","con","una","uno","del","della","dello","dei","degli","delle","nel","nella",
        "come","sono","essere","stato","stata","alla","allo","agli","alle","più",
        "quando","dove","perché","perche","cosa","quale","quali","questo","questa","questi","queste",
        "anche","ancora","molto","poco","tutto","tutti","tutte","loro","suo","sua","suoi","sue","nostro",
        // Italian articles / prepositions / common function words
        "il","lo","la","le","gli","di","da","in","su","al","dal","sul","tra","fra","ed","od",
        "se","ma","sia","mi","ti","ci","vi","ne","si","ho","hai","abbiamo","sono","era","erano"
    };

    internal static List<string> Tokenize(string text)
    {
        return Regex.Split(RemoveDiacritics(text.ToLowerInvariant()), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length > 1 && !Stopwords.Contains(t))
            .ToList();
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
