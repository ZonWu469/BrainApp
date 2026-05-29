using Microsoft.Extensions.Options;
using Xunit;
using BrainApp.Core.Config;
using BrainApp.Core.Services;

namespace BrainApp.Tests;

public class CacheServiceTests
{
    private CacheService CreateService(CacheSettings? settings = null)
    {
        var opts = Options.Create(settings ?? new CacheSettings
        {
            EmbeddingTtlMinutes = 60,
            QueryTtlMinutes = 30,
            EnableEmbeddingCache = true,
            EnableQueryCache = true
        });
        return new CacheService(opts);
    }

    [Fact]
    public void GetEmbedding_Miss_ReturnsNull()
    {
        var svc = CreateService();
        var result = svc.GetEmbedding("hello world");
        Assert.Null(result);
    }

    [Fact]
    public void GetEmbedding_Hit_ReturnsSameArray()
    {
        var svc = CreateService();
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };

        svc.SetEmbedding("hello world", embedding);
        var result = svc.GetEmbedding("hello world");

        Assert.NotNull(result);
        Assert.Equal(embedding.Length, result!.Length);
        Assert.Equal(embedding[0], result[0]);
        Assert.Equal(embedding[1], result[1]);
        Assert.Equal(embedding[2], result[2]);
    }

    [Fact]
    public void GetEmbedding_DifferentText_ReturnsNull()
    {
        var svc = CreateService();
        svc.SetEmbedding("hello world", new float[] { 0.1f, 0.2f });
        var result = svc.GetEmbedding("different text");
        Assert.Null(result);
    }

    [Fact]
    public void SetEmbedding_ThenMissAfterClear_ReturnsNull()
    {
        var svc = CreateService();
        svc.SetEmbedding("test", new float[] { 0.5f });
        svc.ClearAll();
        var result = svc.GetEmbedding("test");
        Assert.Null(result);
    }

    [Fact]
    public void GetAnswer_Miss_ReturnsNull()
    {
        var svc = CreateService();
        var result = svc.GetAnswer("profile1", "session1", "what is this");
        Assert.Null(result);
    }

    [Fact]
    public void GetAnswer_Hit_ReturnsCachedAnswer()
    {
        var svc = CreateService();
        svc.SetAnswer("profile1", "session1", "what is this", "cached answer");

        var result = svc.GetAnswer("profile1", "session1", "what is this");

        Assert.NotNull(result);
        Assert.Equal("cached answer", result);
    }

    [Fact]
    public void GetAnswer_DifferentSession_ReturnsMiss()
    {
        // A new chat in the same profile must NOT see another chat's cached answer.
        var svc = CreateService();
        svc.SetAnswer("profile1", "session1", "what is this", "cached answer");

        var result = svc.GetAnswer("profile1", "session2", "what is this");

        Assert.Null(result);
    }

    [Fact]
    public void GetAnswer_DifferentProfile_ReturnsMiss()
    {
        var svc = CreateService();
        svc.SetAnswer("profile1", "session1", "what is this", "cached answer");

        var result = svc.GetAnswer("profile2", "session1", "what is this");

        Assert.Null(result);
    }

    [Fact]
    public void InvalidateProfile_MakesAnswerUnreachable()
    {
        var svc = CreateService();

        svc.SetAnswer("profile1", "session1", "question", "answer1");
        Assert.Equal("answer1", svc.GetAnswer("profile1", "session1", "question"));

        svc.InvalidateProfile("profile1");

        var result = svc.GetAnswer("profile1", "session1", "question");
        Assert.Null(result);
    }

    [Fact]
    public void InvalidateProfile_InvalidatesAllSessionsOfThatProfile()
    {
        var svc = CreateService();
        svc.SetAnswer("profile1", "session1", "question", "answer-A");
        svc.SetAnswer("profile1", "session2", "question", "answer-B");
        svc.SetAnswer("profile2", "session1", "question", "answer-C");

        svc.InvalidateProfile("profile1");

        Assert.Null(svc.GetAnswer("profile1", "session1", "question"));
        Assert.Null(svc.GetAnswer("profile1", "session2", "question"));
        Assert.Equal("answer-C", svc.GetAnswer("profile2", "session1", "question"));
    }

    [Fact]
    public void InvalidateProfile_IncrementsGeneration()
    {
        var svc = CreateService();

        var gen1 = svc.GetProfileGeneration("profile1");
        svc.InvalidateProfile("profile1");
        var gen2 = svc.GetProfileGeneration("profile1");
        svc.InvalidateProfile("profile1");
        var gen3 = svc.GetProfileGeneration("profile1");

        Assert.Equal(0, gen1);
        Assert.Equal(1, gen2);
        Assert.Equal(2, gen3);
    }

    [Fact]
    public void NormalizeQuestion_StandardizesWhitespace()
    {
        var result = CacheService.NormalizeQuestion("  What  is   this?  ");
        Assert.Equal("what is this?", result);
    }

    [Fact]
    public void NormalizeQuestion_Lowercases()
    {
        var result = CacheService.NormalizeQuestion("HELLO WORLD");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void GetStats_ReturnsCounts()
    {
        var svc = CreateService();

        svc.SetEmbedding("text1", new float[] { 0.1f });
        svc.SetEmbedding("text2", new float[] { 0.2f });
        svc.SetAnswer("p1", "s1", "q1", "a1");
        svc.GetProfileGeneration("p1");
        svc.GetProfileGeneration("p2");

        var (embCount, queryCount, profileCount) = svc.GetStats();

        Assert.Equal(2, embCount);
        Assert.Equal(1, queryCount);
        Assert.Equal(2, profileCount);
    }

    [Fact]
    public void DisableEmbeddingCache_ReturnsNull()
    {
        var settings = new CacheSettings { EnableEmbeddingCache = false };
        var svc = CreateService(settings);

        svc.SetEmbedding("test", new float[] { 0.5f });
        var result = svc.GetEmbedding("test");

        Assert.Null(result);
    }

    [Fact]
    public void DisableQueryCache_ReturnsNull()
    {
        var settings = new CacheSettings { EnableQueryCache = false };
        var svc = CreateService(settings);

        svc.SetAnswer("p1", "s1", "q1", "a1");
        var result = svc.GetAnswer("p1", "s1", "q1");

        Assert.Null(result);
    }
}
