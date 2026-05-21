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

    private readonly ConcurrentDictionary<string, List<DocumentChunk>> _index = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new();

    private SemaphoreSlim GetLock(string profileId) =>
        _indexLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    public RetrievalService(
        LlamaService llama,
        CacheService cache,
        ProfileRepository profileRepo,
        IOptions<RetrievalSettings> settings)
    {
        _llama = llama;
        _cache = cache;
        _profileRepo = profileRepo;
        _settings = settings.Value;
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
        return _index.TryGetValue(profileId, out var list)
            ? list.Count
            : 0;
    }

    /// <summary>
    /// Retrieve top-K relevant chunks for a query using hybrid scoring.
    /// 1. Embed query (cached if repeated)
    /// 2. Cosine similarity (dot product of L2-normalized vectors)
    /// 3. BM25-style keyword overlap
    /// 4. Combined score: semanticWeight * semantic + keywordWeight * keyword
    /// </summary>
    public async Task<List<RetrievedChunk>> RetrieveAsync(
        string profileId,
        string query,
        int? topK = null,
        CancellationToken ct = default)
    {
        var k = topK ?? _settings.TopK;

        if (!_index.TryGetValue(profileId, out var chunks) || chunks.Count == 0)
        {
            chunks = _profileRepo.GetChunksByProfile(profileId);
            if (chunks.Count > 0)
                _index[profileId] = chunks;
            else
                return new List<RetrievedChunk>();
        }

        var queryEmbedding = await _llama.EmbedAsync(query, ct);

        var queryTokens = Tokenize(query);
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
            double keywordScore = CalculateKeywordScore(queryTokens, Tokenize(chunk.Text));

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
        var result = (aboveThreshold.Count > 0 ? aboveThreshold : ordered).Take(k).ToList();
        Log.Information(
            "Retrieval: chunks={Chunks} nullEmbeddings={Null} scored={Scored} aboveThreshold={Pass} returned={Returned} topScore={Top:F3} minScore={Min:F3}",
            chunks.Count, nullEmbeds, ordered.Count, aboveThreshold.Count, result.Count,
            ordered.Count > 0 ? ordered[0].Score : 0.0,
            _settings.MinRelevanceScore);
        return result;
    }

    /// <summary>
    /// Load chunks from SQLite into the in-memory index for a profile.
    /// Also performs one-time migration from legacy index.bin files.
    /// </summary>
    public Task LoadIndexAsync(string profileId, string appDataFolder)
    {
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

    private static double CalculateKeywordScore(List<string> queryTokens, List<string> chunkTokens)
    {
        if (queryTokens.Count == 0 || chunkTokens.Count == 0) return 0;

        var querySet = new HashSet<string>(queryTokens);
        var chunkSet = new HashSet<string>(chunkTokens);
        var overlap = querySet.Intersect(chunkSet).Count();

        var avgLen = (queryTokens.Count + chunkTokens.Count) / 2.0;
        return overlap / (avgLen + 5.0);
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length > 1)
            .ToList();
    }
}
