using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using BrainApp.Core.Config;

namespace BrainApp.Core.Services;

public class SkillFetchCache
{
    private readonly SkillsSettings _settings;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CacheEntry>> _bySession = new();

    public SkillFetchCache(IOptions<SkillsSettings> settings)
    {
        _settings = settings.Value;
    }

    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim().ToLowerInvariant();

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.ToString().TrimEnd('/').ToLowerInvariant();
    }

    public bool TryGet(string? sessionId, string url, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var key = NormalizeUrl(url);
        if (!_bySession.TryGetValue(sessionId, out var sessionCache))
            return false;

        if (!sessionCache.TryGetValue(key, out var entry))
            return false;

        if (IsExpired(entry))
        {
            sessionCache.TryRemove(key, out _);
            return false;
        }

        content = entry.Content;
        return true;
    }

    public void Set(string? sessionId, string url, string content)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(content))
            return;

        var sessionCache = _bySession.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, CacheEntry>());
        var key = NormalizeUrl(url);
        sessionCache[key] = new CacheEntry(content, DateTime.UtcNow, url);

        TrimSessionCache(sessionCache);
    }

    public bool HasCachedContent(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        if (!_bySession.TryGetValue(sessionId, out var sessionCache))
            return false;

        return sessionCache.Values.Any(e => !IsExpired(e));
    }

    public bool TryGetLatest(string? sessionId, out string content, out string url)
    {
        content = string.Empty;
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        if (!_bySession.TryGetValue(sessionId, out var sessionCache))
            return false;

        CacheEntry? latest = null;
        foreach (var entry in sessionCache.Values)
        {
            if (IsExpired(entry))
                continue;
            if (latest == null || entry.StoredAt > latest.StoredAt)
                latest = entry;
        }

        if (latest == null)
            return false;

        content = latest.Content;
        url = latest.Url;
        return !string.IsNullOrWhiteSpace(content);
    }

    private bool IsExpired(CacheEntry entry)
    {
        if (_settings.FetchCacheTtlMinutes <= 0)
            return false;
        return DateTime.UtcNow - entry.StoredAt > TimeSpan.FromMinutes(_settings.FetchCacheTtlMinutes);
    }

    private void TrimSessionCache(ConcurrentDictionary<string, CacheEntry> sessionCache)
    {
        var max = _settings.MaxFetchCacheEntriesPerSession;
        if (max <= 0 || sessionCache.Count <= max)
            return;

        var toRemove = sessionCache
            .OrderBy(kv => kv.Value.StoredAt)
            .Take(sessionCache.Count - max)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            sessionCache.TryRemove(key, out _);
    }

    private sealed record CacheEntry(string Content, DateTime StoredAt, string Url);
}
