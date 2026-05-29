using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;

namespace BrainApp.Core.Services;

/// <summary>
/// Two-layer in-memory cache for embeddings and query answers.
/// Embedding cache: keyed by SHA256(text)[..16], 24h TTL
/// Query cache: keyed by SHA256(profileId+sessionId+generation+promptVersion+normalizedQuestion)[..20], 30min TTL.
/// The sessionId scopes answers to a specific chat so a new chat in the same
/// profile never serves another chat's cached answer.
/// Per-profile generation counter for cache invalidation across all sessions of that profile.
/// </summary>
public class CacheService
{
    private readonly CacheSettings _settings;

    // Embedding cache: "emb:" + SHA256(text)[..16] -> float[]
    private readonly ConcurrentDictionary<string, (float[] embedding, DateTime expiresAt)> _embeddingCache = new();

    // Query answer cache: "q:" + SHA256(profileId+generation+normalizedQuestion)[..20] -> (answer, DateTime expiresAt)
    private readonly ConcurrentDictionary<string, (string answer, DateTime expiresAt)> _queryCache = new();

    // Per-profile generation counters for cache invalidation
    private readonly ConcurrentDictionary<string, int> _profileGenerations = new();

    public CacheService(IOptions<CacheSettings> settings)
    {
        _settings = settings.Value;
    }

    /// <summary>
    /// Get cached embedding if available and not expired.
    /// modelKey should identify the embedding model (e.g. its filename) so
    /// switching models doesn't return stale vectors of the wrong dimension.
    /// </summary>
    public float[]? GetEmbedding(string text, string modelKey = "")
    {
        if (!_settings.EnableEmbeddingCache) return null;

        var key = GetEmbeddingKey(text, modelKey);
        if (_embeddingCache.TryGetValue(key, out var entry))
        {
            if (entry.expiresAt > DateTime.UtcNow)
            {
                Log.Debug("Embedding cache HIT for text hash: {KeyHash}", key[..16]);
                return entry.embedding;
            }
            // Expired — remove
            _embeddingCache.TryRemove(key, out _);
        }
        return null;
    }

    /// <summary>
    /// Store embedding in cache with configured TTL. modelKey scopes the entry
    /// to a specific embedding model.
    /// </summary>
    public void SetEmbedding(string text, float[] embedding, string modelKey = "")
    {
        if (!_settings.EnableEmbeddingCache) return;
        if (_embeddingCache.Count >= _settings.MaxEmbeddingEntries)
        {
            // Evict oldest expired entries
            EvictExpiredEmbeddings();
        }

        var key = GetEmbeddingKey(text, modelKey);
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.EmbeddingTtlMinutes);
        _embeddingCache[key] = (embedding, expiresAt);
        Log.Debug("Embedding cached: {KeyHash}, TTL: {TTL}m", key[..16], _settings.EmbeddingTtlMinutes);
    }

    /// <summary>
    /// Get cached answer for a question in a specific session if available and not expired.
    /// sessionId scopes the cache: a different chat (different sessionId) in the same
    /// profile will not see this entry.
    /// </summary>
    public string? GetAnswer(string profileId, string sessionId, string normalizedQuestion)
    {
        if (!_settings.EnableQueryCache) return null;

        var key = GetQueryKey(profileId, sessionId, normalizedQuestion);
        if (_queryCache.TryGetValue(key, out var entry))
        {
            if (entry.expiresAt > DateTime.UtcNow)
            {
                Log.Debug("Query cache HIT for profile: {ProfileId}, session: {SessionId}", profileId, sessionId);
                return entry.answer;
            }
            _queryCache.TryRemove(key, out _);
        }
        return null;
    }

    /// <summary>
    /// Store answer in cache with configured TTL, scoped to a specific session.
    /// </summary>
    public void SetAnswer(string profileId, string sessionId, string normalizedQuestion, string answer)
    {
        if (!_settings.EnableQueryCache) return;
        if (_queryCache.Count >= _settings.MaxQueryEntries)
        {
            EvictExpiredQueries();
        }

        var key = GetQueryKey(profileId, sessionId, normalizedQuestion);
        var expiresAt = DateTime.UtcNow.AddMinutes(_settings.QueryTtlMinutes);
        _queryCache[key] = (answer, expiresAt);
        Log.Debug("Query answer cached for profile: {ProfileId}, session: {SessionId}, TTL: {TTL}m",
            profileId, sessionId, _settings.QueryTtlMinutes);
    }

    /// <summary>
    /// Invalidate all cached answers for a profile by incrementing its generation counter.
    /// </summary>
    public void InvalidateProfile(string profileId)
    {
        var generation = _profileGenerations.AddOrUpdate(profileId, 1, (_, g) => g + 1);
        Log.Information("Invalidated cache for profile: {ProfileId}, new generation: {Generation}", profileId, generation);
    }

    /// <summary>
    /// Get the current generation counter for a profile.
    /// </summary>
    public int GetProfileGeneration(string profileId)
    {
        return _profileGenerations.GetOrAdd(profileId, 0);
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    public (int embeddingCount, int queryCount, int profileCount) GetStats()
    {
        return (_embeddingCache.Count, _queryCache.Count, _profileGenerations.Count);
    }

    /// <summary>
    /// Clear all caches (for settings UI).
    /// </summary>
    public void ClearAll()
    {
        _embeddingCache.Clear();
        _queryCache.Clear();
        Log.Information("All caches cleared");
    }

    /// <summary>
    /// Clear cache for a specific profile (query cache only).
    /// </summary>
    public void ClearProfileCache(string profileId)
    {
        // Remove all query cache entries for this profile
        // Since keys include generation, we just increment the generation counter
        InvalidateProfile(profileId);
    }

    private static string GetEmbeddingKey(string text, string modelKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(modelKey + "\0" + text));
        return "emb:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // Bump PromptVersion whenever BuildSystemPrompt, BuildRagUserPrompt, or any
    // chat-template builder in LlamaService changes — otherwise stale cached
    // answers generated under the old prompts will keep being served.
    // v4 introduces sessionId scoping; entries written under v3 (profile-scoped only)
    // must not be served under the new keying.
    // v5: reranking + IDF keyword scoring + edge-ordered context change which chunks/order
    // reach the model, so answers differ from v4 and must not be served from the old cache.
    public const string PromptVersion = "v5-2026-05";

    private string GetQueryKey(string profileId, string sessionId, string normalizedQuestion)
    {
        var generation = GetProfileGeneration(profileId);
        var combined = $"{profileId}:{sessionId}:{generation}:{PromptVersion}:{normalizedQuestion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return "q:" + Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private void EvictExpiredEmbeddings()
    {
        var now = DateTime.UtcNow;
        var toRemove = _embeddingCache
            .Where(kvp => kvp.Value.expiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            _embeddingCache.TryRemove(key, out _);

        if (toRemove.Count > 0)
            Log.Debug("Evicted {Count} expired embedding cache entries", toRemove.Count);
    }

    private void EvictExpiredQueries()
    {
        var now = DateTime.UtcNow;
        var toRemove = _queryCache
            .Where(kvp => kvp.Value.expiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            _queryCache.TryRemove(key, out _);

        if (toRemove.Count > 0)
            Log.Debug("Evicted {Count} expired query cache entries", toRemove.Count);
    }

    /// <summary>
    /// Normalize a question for consistent cache key generation.
    /// Lowercases, trims whitespace, removes extra spaces.
    /// </summary>
    public static string NormalizeQuestion(string question)
    {
        return string.Join(" ",
            question.ToLowerInvariant()
                    .Split(null as char[], StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()))
            .Trim();
    }
}