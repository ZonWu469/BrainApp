using Microsoft.Extensions.Options;
using Xunit;
using Moq;
using BrainApp.Core.Config;
using BrainApp.Core.Services;
using BrainApp.Core.Models;
using BrainApp.Core.Skills;
using System;

namespace BrainApp.Tests;

public class RetrievalServiceTests
{
    private RetrievalService CreateService(
        RetrievalSettings? settings = null)
    {
        var retrievalOpts = Options.Create(settings ?? new RetrievalSettings
        {
            TopK = 12,
            ChunkSize = 800,
            ChunkOverlap = 120,
            MinChunkLength = 60,
            SemanticWeight = 0.7,
            KeywordWeight = 0.3,
            MinRelevanceScore = 0.1
        });

        // Create mock services
        var llamaSettings = Options.Create(new LlamaSettings
        {
            ModelsFolder = "models",
            ChatModelFile = "test.gguf",
            EmbeddingModelFile = "test-embed.gguf"
        });
        var storageSettings = Options.Create(new StorageSettings
        {
            AppDataFolder = ""
        });
        var cacheSettings = Options.Create(new CacheSettings
        {
            EnableEmbeddingCache = false,
            EnableQueryCache = false
        });

        var cacheService = new CacheService(cacheSettings);
        var llamaService = new LlamaService(llamaSettings, storageSettings, cacheService);

        var tempDbFolder = Path.Combine(Path.GetTempPath(), "brainapp_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbFolder);
        var profileRepo = new ProfileRepository(Options.Create(new StorageSettings { AppDataFolder = tempDbFolder }));

        // Ensure foreign-key targets exist for chunk inserts.
        // chunks.profile_id -> profiles.id
        // chunks.document_id -> documents.id
        profileRepo.SaveProfile(new Profile { Id = "profile1", Name = "Profile 1" });
        profileRepo.SaveProfile(new Profile { Id = "profile2", Name = "Profile 2" });

        profileRepo.SaveDocument(new Document
        {
            Id = "doc1",
            ProfileId = "profile1",
            FileName = "doc1.txt",
            FilePath = "doc1.txt",
            FileHash = "hash-doc1",
            Type = DocumentType.Txt,
            SizeBytes = 1,
            PageCount = 1,
            ChunkCount = 0,
            IndexedAt = DateTime.UtcNow,
            Status = DocumentStatus.Ready
        });

        profileRepo.SaveDocument(new Document
        {
            Id = "doc2",
            ProfileId = "profile2",
            FileName = "doc2.txt",
            FilePath = "doc2.txt",
            FileHash = "hash-doc2",
            Type = DocumentType.Txt,
            SizeBytes = 1,
            PageCount = 1,
            ChunkCount = 0,
            IndexedAt = DateTime.UtcNow,
            Status = DocumentStatus.Ready
        });

        return new RetrievalService(
            llamaService,
            cacheService,
            profileRepo,
            retrievalOpts,
            Options.Create(new StorageSettings { AppDataFolder = tempDbFolder }),
            llamaSettings);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        // Use properly L2-normalized vectors: {1, 0} and {1, 0}
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 1.0f, 0.0f };

        var result = RetrievalService.CosineSimilarity(a, b);

        Assert.Equal(1.0, result, 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };

        var result = RetrievalService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result, 5);
    }

    [Fact]
    public void CosineSimilarity_NegativeVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { -1.0f, 0.0f };

        var result = RetrievalService.CosineSimilarity(a, b);

        Assert.Equal(0.0, result, 5); // Negative clamped to 0
    }

    [Fact]
    public async Task RetrieveAsync_EmptyIndex_ReturnsEmptyList()
    {
        var svc = CreateService();

        var result = await svc.RetrieveAsync("nonexistent-profile", "test query");

        Assert.Empty(result);
    }

    [Fact]
    public async Task AddChunks_IncreasesChunkCount()
    {
        var svc = CreateService();

        var chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = "c1",
                ProfileId = "profile1",
                DocumentId = "doc1",
                Text = "test chunk",
                ChunkIndex = 0,
                PageNumber = 1,
                Embedding = new float[] { 0.1f, 0.2f }
            }
        };

        await svc.AddChunksAsync("profile1", chunks);

        var count = svc.GetChunkCount("profile1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ClearProfile_RemovesChunks()
    {
        var svc = CreateService();

        var chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = "c1",
                ProfileId = "profile1",
                DocumentId = "doc1",
                Text = "test chunk",
                ChunkIndex = 0,
                PageNumber = 1,
                Embedding = new float[] { 0.1f, 0.2f }
            }
        };

        await svc.AddChunksAsync("profile1", chunks);
        Assert.Equal(1, svc.GetChunkCount("profile1"));

        await svc.ClearProfileAsync("profile1");

        Assert.Equal(0, svc.GetChunkCount("profile1"));
    }

    [Fact]
    public async Task RemoveDocument_RemovesChunks()
    {
        var svc = CreateService();

        var chunks = new List<DocumentChunk>
        {
            new()
            {
                Id = "c1",
                ProfileId = "profile1",
                DocumentId = "doc1",
                Text = "test chunk",
                ChunkIndex = 0,
                PageNumber = 1,
                Embedding = new float[] { 0.1f, 0.2f }
            }
        };

        await svc.AddChunksAsync("profile1", chunks);
        Assert.Equal(1, svc.GetChunkCount("profile1"));

        await svc.RemoveDocumentAsync("profile1", "doc1");

        Assert.Equal(0, svc.GetChunkCount("profile1"));
    }

    [Fact]
    public async Task GetChunkCount_NonExistentProfile_ReturnsZero()
    {
        var svc = CreateService();

        var result = svc.GetChunkCount("nonexistent");

        Assert.Equal(0, result);
    }

    private static DocumentChunk Chunk(string id, string text, float[] emb) => new()
    {
        Id = id, ProfileId = "p", DocumentId = "d", FileName = "f.txt",
        Text = text, ChunkIndex = 0, PageNumber = 1, Embedding = emb
    };

    [Fact]
    public void Tokenize_DropsStopwordsAndDiacritics()
    {
        var tokens = RetrievalService.Tokenize("Qual è la POLIZZA assicurativa più adatta?");

        Assert.Contains("polizza", tokens);
        Assert.Contains("assicurativa", tokens);
        Assert.Contains("adatta", tokens);
        Assert.Contains("piu", tokens);          // "più" -> "piu" (diacritics folded; not a stopword token)
        Assert.DoesNotContain("la", tokens);     // stopword removed
        Assert.DoesNotContain("è", tokens);       // length 1 after fold -> dropped
    }

    [Fact]
    public void KeywordScore_RareTermOutweighsCommonTerm()
    {
        // "report" appears in every chunk (common, low IDF); "fotosintesi" is rare (high IDF).
        var corpus = new List<DocumentChunk>
        {
            Chunk("1", "report report report", Array.Empty<float>()),
            Chunk("2", "report annuale vendite", Array.Empty<float>()),
            Chunk("3", "report sulla fotosintesi clorofilliana", Array.Empty<float>()),
        };
        var idf = RetrievalService.BuildIdf(corpus);

        var query = RetrievalService.Tokenize("report fotosintesi");
        double rareMatch = RetrievalService.CalculateKeywordScore(query, RetrievalService.Tokenize(corpus[2].Text), idf);
        double commonMatch = RetrievalService.CalculateKeywordScore(query, RetrievalService.Tokenize(corpus[1].Text), idf);

        // The chunk matching the rare term should score higher than one matching only the common term.
        Assert.True(rareMatch > commonMatch, $"rare={rareMatch} should exceed common={commonMatch}");
    }

    [Fact]
    public void SelectMmr_KeepsTopRelevanceAndDropsNearDuplicate()
    {
        var a = new[] { 1f, 0f };          // top relevance, unique direction
        var dupOfA = new[] { 1f, 0f };     // near-duplicate of A
        var b = new[] { 0f, 1f };          // lower relevance but diverse
        var cands = new List<RetrievedChunk>
        {
            new() { Chunk = Chunk("A", "a", a),       Score = 0.90 },
            new() { Chunk = Chunk("Adup", "adup", dupOfA), Score = 0.80 },
            new() { Chunk = Chunk("B", "b", b),       Score = 0.70 },
        };

        var picked = RetrievalService.SelectMmr(cands, 2);
        var ids = picked.Select(p => p.Chunk.Id).ToList();

        Assert.Contains("A", ids);     // highest relevance always kept
        Assert.Contains("B", ids);     // diverse chunk preferred over the near-duplicate
        Assert.DoesNotContain("Adup", ids);
    }

    [Fact]
    public void ReorderForEdges_PlacesTopChunksAtStartAndEnd()
    {
        // Input is sorted by relevance, best first.
        var sorted = Enumerable.Range(0, 5)
            .Select(i => new RetrievedChunk { Chunk = Chunk($"c{i}", $"t{i}", Array.Empty<float>()), Score = 1.0 - i * 0.1 })
            .ToList();

        var ordered = ChatService.ReorderForEdges(sorted);

        Assert.Equal("c0", ordered.First().Chunk.Id);  // most relevant first
        Assert.Equal("c1", ordered.Last().Chunk.Id);   // second-most relevant last
        Assert.Equal(5, ordered.Count);
    }

    [Fact]
    public async Task AddChunks_MultipleProfiles_StaySeparate()
    {
        var svc = CreateService();

        await svc.AddChunksAsync("profile1", new List<DocumentChunk>
        {
            new()
            {
                Id = "c1",
                ProfileId = "profile1",
                DocumentId = "doc1",
                Text = "chunk1",
                ChunkIndex = 0,
                PageNumber = 1,
                Embedding = new float[] { 0.1f, 0.2f }
            }
        });

        await svc.AddChunksAsync("profile2", new List<DocumentChunk>
        {
            new()
            {
                Id = "c2",
                ProfileId = "profile2",
                DocumentId = "doc2",
                Text = "chunk2",
                ChunkIndex = 0,
                PageNumber = 1,
                Embedding = new float[] { 0.3f, 0.4f }
            }
        });

        Assert.Equal(1, svc.GetChunkCount("profile1"));
        Assert.Equal(1, svc.GetChunkCount("profile2"));
    }
}