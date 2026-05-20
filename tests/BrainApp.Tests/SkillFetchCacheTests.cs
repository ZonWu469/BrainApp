using BrainApp.Core.Config;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;
using Microsoft.Extensions.Options;
using Xunit;

namespace BrainApp.Tests;

public class SkillFetchCacheTests
{
    [Fact]
    public void NormalizeUrl_StripsFragmentAndLowercasesHost()
    {
        var a = SkillFetchCache.NormalizeUrl("https://Example.com/Page#section");
        var b = SkillFetchCache.NormalizeUrl("https://example.com/Page");
        Assert.Equal(a, b);
    }

    [Fact]
    public void TryGet_ReturnsCachedContentForSameSessionAndUrl()
    {
        var cache = new SkillFetchCache(Options.Create(new SkillsSettings
        {
            FetchCacheTtlMinutes = 30,
            MaxFetchCacheEntriesPerSession = 10
        }));

        const string sessionId = "sess1";
        const string url = "https://example.com/wiki/ESA";
        const string content = "European Space Agency content";

        cache.Set(sessionId, url, content);

        Assert.True(cache.TryGet(sessionId, url, out var cached));
        Assert.Equal(content, cached);
    }

    [Fact]
    public void TryGet_DifferentSession_ReturnsFalse()
    {
        var cache = new SkillFetchCache(Options.Create(new SkillsSettings()));
        cache.Set("sess1", "https://example.com", "content");

        Assert.False(cache.TryGet("sess2", "https://example.com", out _));
    }

    [Fact]
    public void TryGetLatest_ReturnsNewestEntry()
    {
        var cache = new SkillFetchCache(Options.Create(new SkillsSettings
        {
            FetchCacheTtlMinutes = 30,
            MaxFetchCacheEntriesPerSession = 10
        }));

        const string sessionId = "sess1";
        cache.Set(sessionId, "https://example.com/old", "old content");
        Thread.Sleep(5);
        cache.Set(sessionId, "https://example.com/new", "new content");

        Assert.True(cache.TryGetLatest(sessionId, out var content, out var url));
        Assert.Equal("new content", content);
        Assert.Equal("https://example.com/new", url);
    }

    [Fact]
    public void HasCachedContent_ReturnsTrueWhenSessionHasEntry()
    {
        var cache = new SkillFetchCache(Options.Create(new SkillsSettings()));
        cache.Set("sess1", "https://example.com", "content");

        Assert.True(cache.HasCachedContent("sess1"));
        Assert.False(cache.HasCachedContent("sess2"));
    }

    [Fact]
    public void SkillScriptEngine_CompilesFetchPageSkillWithContext()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var skillPath = Path.Combine(repoRoot, "skills", "fetch-page.txt");
        if (!File.Exists(skillPath))
            return;

        var engine = new SkillScriptEngine(Options.Create(new SkillsSettings()));
        var def = engine.CompileFile(skillPath);

        Assert.True(def.IsValid, def.CompileError);
        Assert.Contains(def.Methods, m => m.SkillName == "fetch_page");

        var source = File.ReadAllText(skillPath);
        Assert.Contains("GetAsync", source);
        Assert.DoesNotContain("GetStringAsync", source);
    }
}
