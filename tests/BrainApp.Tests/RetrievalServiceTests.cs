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
            TopK = 6,
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

        return new RetrievalService(llamaService, cacheService, profileRepo, retrievalOpts);
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