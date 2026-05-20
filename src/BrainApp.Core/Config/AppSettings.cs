using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace BrainApp.Core.Config;

public class AppSettings
{
    public LlamaSettings LLama { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public RetrievalSettings Retrieval { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public ApiSettings Api { get; set; } = new();
    public SkillsSettings Skills { get; set; } = new();
}

public class LlamaSettings
{
    public string ModelsFolder { get; set; } = "models";
    public string ChatModelFile { get; set; } = "Qwen3-1.7B-Q8_0.gguf";
    public string EmbeddingModelFile { get; set; } = "nomic-embed-text-v1.5.Q4_K_M.gguf";
    public int ContextSize { get; set; } = 16384;
    public int GpuLayerCount { get; set; } = 99;
    public int Threads { get; set; } = 0;
    public int BatchSize { get; set; } = 512;
    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 1024;
    public List<string> AntiPrompts { get; set; } = new() { "<|end|>", "<|endoftext|>", "<|im_end|>" };
    public ChatTemplate ChatTemplate { get; set; } = ChatTemplate.Qwen;

    public string GetResolvedModelsFolder(StorageSettings storage)
    {
        // 1. CWD fallback (solution root during dev)
        var cwdModels = Path.Combine(Directory.GetCurrentDirectory(), ModelsFolder);
        if (Directory.Exists(cwdModels)) return cwdModels;

        // 2. App context base directory (build output bin/Debug/net8.0)
        var baseDir = Path.Combine(AppContext.BaseDirectory, ModelsFolder);
        if (Directory.Exists(baseDir)) return baseDir;

        // 3. AppData fallback
        var appDataModels = Path.Combine(storage.ResolvedAppDataFolder, "models");
        if (Directory.Exists(appDataModels)) return appDataModels;

        // 4. Return CWD even if it doesn't exist (let caller handle missing file)
        return cwdModels;
    }

    public string GetResolvedChatModelPath(StorageSettings storage) =>
        Path.Combine(GetResolvedModelsFolder(storage), ChatModelFile);

    public string GetResolvedEmbeddingModelPath(StorageSettings storage) =>
        Path.Combine(GetResolvedModelsFolder(storage), EmbeddingModelFile);
}

public enum ChatTemplate { Qwen, Llama3, Phi3, Gemma, Mistral, ChatML }

public class CacheSettings
{
    public int EmbeddingTtlMinutes { get; set; } = 1440;
    public int QueryTtlMinutes { get; set; } = 30;
    public int MaxEmbeddingEntries { get; set; } = 50000;
    public int MaxQueryEntries { get; set; } = 500;
    public bool EnableQueryCache { get; set; } = true;
    public bool EnableEmbeddingCache { get; set; } = true;
}

public class RetrievalSettings
{
    public int TopK { get; set; } = 6;
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 120;
    public int MinChunkLength { get; set; } = 60;
    public double SemanticWeight { get; set; } = 0.7;
    public double KeywordWeight { get; set; } = 0.3;
    public double MinRelevanceScore { get; set; } = 0.1;
    public string OcrLanguages { get; set; } = "eng+ita";
}

public class StorageSettings
{
    public string AppDataFolder { get; set; } = "";

    public string ResolvedAppDataFolder =>
        string.IsNullOrEmpty(AppDataFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrainApp")
            : AppDataFolder;

    public int MaxDocumentsPerProfile { get; set; } = 500;
    public int MaxFileSizeMb { get; set; } = 50;
}

public class ApiSettings
{
    public int Port { get; set; } = 5199;
    public bool EnableSwagger { get; set; } = true;
    public string ApiKey { get; set; } = "change-me-in-production";
    public int RateLimitPerMinute { get; set; } = 60;
}

public class SkillsSettings
{
    public string SkillsFolder { get; set; } = "";
    public int ExecutionTimeoutSeconds { get; set; } = 30;
    public int MaxSkillResultChars { get; set; } = 16000;
    public int FetchCacheTtlMinutes { get; set; } = 30;
    public int MaxFetchCacheEntriesPerSession { get; set; } = 10;

    public string GetResolvedSkillsFolder(StorageSettings storage) =>
        string.IsNullOrEmpty(SkillsFolder)
            ? Path.Combine(AppContext.BaseDirectory, "skills")
            : SkillsFolder;
}