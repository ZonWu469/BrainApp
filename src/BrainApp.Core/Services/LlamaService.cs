using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;

namespace BrainApp.Core.Services;

/// <summary>
/// LlamaService wraps LLamaSharp for in-process GGUF inference.
/// Handles both chat (interactive executor) and embedding (LLamaEmbedder).
/// Thread-safe via SemaphoreSlim — LLamaSharp contexts are not thread-safe.
/// </summary>
public class LlamaService : IAsyncDisposable
{
    private readonly LlamaSettings _settings;
    private readonly StorageSettings _storageSettings;
    private readonly CacheService _cache;
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private readonly SemaphoreSlim _embedLock = new(1, 1);
    private readonly SemaphoreSlim _rerankLock = new(1, 1);

    private LLamaWeights? _chatWeights;
    private LLamaWeights? _embedWeights;
    private LLamaWeights? _rerankWeights;
    private ModelParams? _chatParams;
    private ModelParams? _embedParams;
    private ModelParams? _rerankParams;
    private LLamaEmbedder? _embedder;
    private LLamaReranker? _reranker;
    private bool _initialized;

    /// <summary>True when a cross-encoder reranker model was loaded successfully.</summary>
    public bool HasReranker => _reranker != null;

    // NativeLibraryConfig must be configured exactly once, before the first native call.
    private static int _nativeConfigured;

    public bool IsInitialized => _initialized;

    public LlamaService(
        IOptions<LlamaSettings> settings,
        IOptions<StorageSettings> storage,
        CacheService cache)
    {
        _settings = settings.Value;
        _storageSettings = storage.Value;
        _cache = cache;
    }

    /// <summary>
    /// Initialize LLamaSharp — load both GGUF model files into memory.
    /// This is async to avoid blocking the UI thread during the 2-10s load time.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        ConfigureNativeBackend();

        var modelsFolder = _settings.GetResolvedModelsFolder(_storageSettings);
        var chatPath = _settings.GetResolvedChatModelPath(_storageSettings);
        var embedPath = _settings.GetResolvedEmbeddingModelPath(_storageSettings);

        // Validate model files exist
        if (!File.Exists(chatPath))
            throw new FileNotFoundException(
                $"Chat model not found: {chatPath}. " +
                "Download from https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF and place in the models/ folder.");

        if (!File.Exists(embedPath))
            throw new FileNotFoundException(
                $"Embedding model not found: {embedPath}. " +
                "Download from https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF and place in the models/ folder.");

        var threadCount = _settings.Threads == 0
            ? Environment.ProcessorCount
            : _settings.Threads;

        _chatParams = new ModelParams(chatPath)
        {
            ContextSize = (uint)_settings.ContextSize,
            GpuLayerCount = _settings.GpuLayerCount,
            Threads = (int)threadCount,
            BatchSize = (uint)_settings.BatchSize,
            UBatchSize = (uint)_settings.BatchSize
        };

        _embedParams = new ModelParams(embedPath)
        {
            ContextSize = 8192,
            BatchSize = 512,
            UBatchSize = 512,
            GpuLayerCount = 0,
            Embeddings = true,
            PoolingType = LLama.Native.LLamaPoolingType.Mean
        };

        Log.Information("Loading GGUF models: chat={Chat}, embed={Embed}",
            _settings.ChatModelFile, _settings.EmbeddingModelFile);

        _chatWeights = await Task.Run(() => LLamaWeights.LoadFromFile(_chatParams), ct);
        _embedWeights = await Task.Run(() => LLamaWeights.LoadFromFile(_embedParams), ct);
        _embedder = new LLamaEmbedder(_embedWeights, _embedParams);

        await TryLoadRerankerAsync(ct);

        _initialized = true;
        Log.Information("LLamaSharp initialized successfully. Chat layers: {Layers}, Embedding layers: {EmbedLayers}, Reranker: {Reranker}",
            _settings.GpuLayerCount, 0, HasReranker ? _settings.RerankerModelFile : "none (MMR fallback)");
    }

    /// <summary>
    /// Compute L2-normalized embedding for the given text using the embedding model.
    /// Results are cached for 24h to avoid re-computing identical texts.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("LlamaService not initialized. Call InitializeAsync first.");

        // Check cache first. Key includes the embedding model file so a model
        // swap doesn't return stale vectors (different dimension / different space).
        var modelKey = _settings.EmbeddingModelFile;
        var cached = _cache.GetEmbedding(text, modelKey);
        if (cached != null) return cached;

        await _embedLock.WaitAsync(ct);
        try
        {
            var input = text.Length > 6000 ? text[..6000] : text;
            var embeddingResult = await Task.Run(() => _embedder!.GetEmbeddings(input), ct);
            // LLamaEmbedder.GetEmbeddings returns float[][] for a batch; take first result
            var embedding = embeddingResult.FirstOrDefault() ?? throw new InvalidOperationException("No embedding returned");
            var result = embedding.ToArray();

            // L2-normalize for cosine similarity
            float mag = MathF.Sqrt(result.Sum(x => x * x));
            if (mag > 1e-6f)
                for (int i = 0; i < result.Length; i++)
                    result[i] /= mag;

            _cache.SetEmbedding(text, result, modelKey);
            return result;
        }
        finally
        {
            _embedLock.Release();
        }
    }

    /// <summary>
    /// Load the cross-encoder reranker model if one is configured and present on disk.
    /// Failure (missing file, unsupported model) is non-fatal — callers fall back to MMR.
    /// </summary>
    private async Task TryLoadRerankerAsync(CancellationToken ct)
    {
        var path = _settings.GetResolvedRerankerModelPath(_storageSettings);
        if (string.IsNullOrEmpty(path))
            return;

        if (!File.Exists(path))
        {
            Log.Warning("Reranker model '{File}' not found at {Path} — using MMR fallback for chunk selection.",
                _settings.RerankerModelFile, path);
            return;
        }

        try
        {
            _rerankParams = new ModelParams(path)
            {
                ContextSize = 8192,
                BatchSize = 512,
                UBatchSize = 512,
                GpuLayerCount = 0, // keep GPU free for the chat model
                PoolingType = LLama.Native.LLamaPoolingType.Rank
            };
            _rerankWeights = await Task.Run(() => LLamaWeights.LoadFromFile(_rerankParams), ct);
            _reranker = new LLamaReranker(_rerankWeights, _rerankParams);
            Log.Information("Reranker model loaded: {File}", _settings.RerankerModelFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load reranker model '{File}' — using MMR fallback.", _settings.RerankerModelFile);
            _rerankWeights?.Dispose();
            _rerankWeights = null;
            _reranker = null;
        }
    }

    /// <summary>
    /// Cross-encoder relevance scores for (query, document) pairs, in document order,
    /// normalized to (0, 1). Returns null if no reranker is loaded so the caller can
    /// fall back to MMR. Scores are not cached: they depend on the (query, doc) pair.
    /// </summary>
    public async Task<IReadOnlyList<float>?> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        if (_reranker == null || documents.Count == 0)
            return null;

        await _rerankLock.WaitAsync(ct);
        try
        {
            return await _reranker.GetRelevanceScores(query, documents, normalize: true, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reranking failed — caller will fall back to original ordering / MMR.");
            return null;
        }
        finally
        {
            _rerankLock.Release();
        }
    }

    /// <summary>
    /// Non-streaming chat — sends full prompt and waits for complete response.
    /// </summary>
    public async Task<string> ChatAsync(
        string systemPrompt,
        List<(MessageRole role, string content)> history,
        string userMessage,
        CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("LlamaService not initialized. Call InitializeAsync first.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var executor = new StatelessExecutor(_chatWeights!, _chatParams!);
            var inferParams = new InferenceParams
            {
                SamplingPipeline = BuildSampler(),
                MaxTokens = _settings.MaxTokens,
                AntiPrompts = _settings.AntiPrompts
            };

            var prompt = BuildChatPrompt(systemPrompt, history, userMessage);
            var sb = new StringBuilder();

            try
            {
                await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
                    sb.Append(token);
            }
            catch (LLama.Exceptions.LLamaDecodeError ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(ex, "llama_decode failed with NoKvSlot — prompt exceeded KV cache capacity");
                throw new InvalidOperationException(
                    "Prompt too large for context window — try shortening the question or starting a new session.", ex);
            }

            return CleanOutput(sb.ToString());
        }
        finally
        {
            _chatLock.Release();
        }
    }

    /// <summary>
    /// Streaming chat — yields tokens as they are generated.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        List<(MessageRole role, string content)> history,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("LlamaService not initialized. Call InitializeAsync first.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var executor = new StatelessExecutor(_chatWeights!, _chatParams!);
            var inferParams = new InferenceParams
            {
                SamplingPipeline = BuildSampler(),
                MaxTokens = _settings.MaxTokens,
                AntiPrompts = _settings.AntiPrompts
            };

            var prompt = BuildChatPrompt(systemPrompt, history, userMessage);
            var enumerator = executor.InferAsync(prompt, inferParams, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    string? token = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                            break;
                        token = enumerator.Current;
                    }
                    catch (LLama.Exceptions.LLamaDecodeError ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error(ex, "llama_decode failed with NoKvSlot — prompt exceeded KV cache capacity");
                        throw new InvalidOperationException(
                            "Prompt too large for context window — try shortening the question or starting a new session.", ex);
                    }
                    yield return token!;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            _chatLock.Release();
        }
    }

    /// <summary>
    /// Count the number of tokens in a text using the chat model's tokenizer.
    /// </summary>
    public int CountTokens(string text)
    {
        if (!_initialized || _chatWeights == null || string.IsNullOrEmpty(text))
            return 0;

        try
        {
            using var context = _chatWeights.CreateContext(_chatParams!);
            var tokens = context.Tokenize(text);
            return tokens.Length;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Token counting failed, returning estimate");
            return text.Length / 4;
        }
    }

    private static void ConfigureNativeBackend()
    {
        if (Interlocked.Exchange(ref _nativeConfigured, 1) == 1)
            return;

        void Forward(LLamaLogLevel level, string? msg)
        {
            var text = msg?.TrimEnd();
            if (string.IsNullOrEmpty(text)) return;
            switch (level)
            {
                case LLamaLogLevel.Error: Log.Error("[llama] {Msg}", text); break;
                case LLamaLogLevel.Warning: Log.Warning("[llama] {Msg}", text); break;
                default: Log.Information("[llama] {Msg}", text); break;
            }
        }

        try
        {
            // Prefer CUDA, fall back to Vulkan, then CPU as last resort.
            // GTX 1650 + driver 591.55 satisfies Vulkan via NVIDIA's bundled ICD;
            // CUDA12 native DLL only loads if cudart64_12/cublas64_12 are on PATH.
            NativeLibraryConfig.All
                .WithCuda(true)
                .WithVulkan(true)
                .WithAutoFallback(true)
                .WithLogCallback(Forward);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NativeLibraryConfig setup failed — backend may have been initialized already");
        }

        try
        {
            // llama.cpp's own logger — emits the verbose device/load/offload lines.
            NativeLogConfig.llama_log_set((level, msg) => Forward(level, msg));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NativeLogConfig hook failed");
        }
    }

    private DefaultSamplingPipeline BuildSampler() => new()
    {
        Temperature = (float)_settings.Temperature,
        RepeatPenalty = 1.1f
    };

    private static readonly string[] TruncateMarkers = {
        "<|im_start|>", "<|im_end|>", "<|end|>", "<|endoftext|>", "<|eot_id|>",
        "<start_of_turn>", "<end_of_turn>"
    };

    private static readonly Regex ThinkBlockRegex = new(
        @"<think>[\s\S]*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnclosedThinkRegex = new(
        @"<think>[\s\S]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CleanOutput(string raw)
    {
        var result = raw;

        result = ThinkBlockRegex.Replace(result, string.Empty);
        result = UnclosedThinkRegex.Replace(result, string.Empty);

        foreach (var marker in TruncateMarkers)
        {
            var idx = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                result = result[..idx];
        }

        return result.Trim();
    }

    /// <summary>
    /// Build chat prompt in the format expected by the configured model family.
    /// </summary>
    private string BuildChatPrompt(
        string system,
        List<(MessageRole role, string content)> history,
        string userMsg)
    {
        return _settings.ChatTemplate switch
        {
            ChatTemplate.Qwen => BuildQwenPrompt(system, history, userMsg),
            ChatTemplate.Llama3 => BuildLlama3Prompt(system, history, userMsg),
            ChatTemplate.Phi3 => BuildPhi3Prompt(system, history, userMsg),
            ChatTemplate.Gemma => BuildGemmaPrompt(system, history, userMsg),
            ChatTemplate.Mistral => BuildMistralPrompt(system, history, userMsg),
            ChatTemplate.ChatML => BuildChatMLPrompt(system, history, userMsg),
            _ => BuildQwenPrompt(system, history, userMsg)
        };
    }

    private static string BuildQwenPrompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<|im_start|>system\n{system} /no_think<|im_end|>\n");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "user" : "assistant";
            sb.Append($"<|im_start|>{r}\n{content}<|im_end|>\n");
        }
        sb.Append($"<|im_start|>user\n{userMsg}<|im_end|>\n<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static string BuildLlama3Prompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{system}<|eot_id|>");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "user" : "assistant";
            sb.Append($"<|start_header_id|>{r}<|end_header_id|>\n\n{content}<|eot_id|>");
        }
        sb.Append($"<|start_header_id|>user<|end_header_id|>\n\n{userMsg}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    private static string BuildPhi3Prompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<|system|>\n{system}<|end|>\n");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "user" : "assistant";
            sb.Append($"<|{r}|>\n{content}<|end|>\n");
        }
        sb.Append($"<|user|>\n{userMsg}<|end|>\n<|assistant|>\n");
        return sb.ToString();
    }

    private static string BuildGemmaPrompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<start_of_turn>model\n{system}\n");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "user" : "model";
            sb.Append($"<start_of_turn>{r}\n{content}\n");
        }
        sb.Append($"<start_of_turn>user\n{userMsg}\n<end_of_turn>\n<start_of_turn>model\n");
        return sb.ToString();
    }

    private static string BuildMistralPrompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<s>[INST] {system} [/INST]\n");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "[INST]" : "[/INST]";
            sb.Append($"{r}{content}</s>\n");
        }
        sb.Append($"[INST] {userMsg} [/INST]\n");
        return sb.ToString();
    }

    private static string BuildChatMLPrompt(string system, List<(MessageRole, string)> history, string userMsg)
    {
        var sb = new StringBuilder();
        sb.Append($"<|im_start|>system\n{system}<|im_end|>\n");
        foreach (var (role, content) in history)
        {
            var r = role == MessageRole.User ? "user" : "assistant";
            sb.Append($"<|im_start|>{r}\n{content}<|im_end|>\n");
        }
        sb.Append($"<|im_start|>user\n{userMsg}<|im_end|>\n<|im_start|>assistant\n");
        return sb.ToString();
    }

    /// <summary>
    /// Check model files exist and attempt GPU detection.
    /// </summary>
    public HealthStatus HealthCheck()
    {
        var chatPath = _settings.GetResolvedChatModelPath(_storageSettings);
        var embedPath = _settings.GetResolvedEmbeddingModelPath(_storageSettings);
        var chatExists = File.Exists(chatPath);
        var embedExists = File.Exists(embedPath);
        var modelsFound = chatExists && embedExists;

        bool gpuAvailable = _settings.GpuLayerCount > 0;

        return new HealthStatus(
            ModelsFound: modelsFound,
            ChatModel: _settings.ChatModelFile,
            EmbedModel: _settings.EmbeddingModelFile,
            GpuAvailable: gpuAvailable,
            GpuLayers: _settings.GpuLayerCount,
            Initialized: _initialized,
            ModelSizeGb: GetModelSizeGb(chatPath));
    }

    /// <summary>
    /// Get detailed model information.
    /// </summary>
    public ModelInfo GetModelInfo()
    {
        var chatPath = _settings.GetResolvedChatModelPath(_storageSettings);
        long chatSize = 0;
        if (File.Exists(chatPath))
            chatSize = new FileInfo(chatPath).Length;

        int threads = _settings.Threads == 0 ? Environment.ProcessorCount : _settings.Threads;

        return new ModelInfo(
            ChatModelFile: _settings.ChatModelFile,
            EmbedModelFile: _settings.EmbeddingModelFile,
            ContextSize: _settings.ContextSize,
            GpuLayerCount: _settings.GpuLayerCount,
            FileSizeBytes: chatSize,
            EstimatedVramMb: EstimateVramMb(chatSize, _settings.GpuLayerCount, _settings.ContextSize),
            Threads: threads);
    }

    private long GetModelSizeGb(string chatPath)
    {
        if (!File.Exists(chatPath)) return 0;
        var info = new FileInfo(chatPath);
        return info.Length / (1024 * 1024 * 1024);
    }

    /// <summary>
    /// Reload GGUF weights from disk (hot-swap model files).
    /// Disposes existing weights and re-initializes from current settings.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        Log.Information("Reloading GGUF model weights...");

        // Dispose existing weights
        _embedder?.Dispose();
        _embedder = null;
        _reranker?.Dispose();
        _reranker = null;
        _rerankWeights?.Dispose();
        _rerankWeights = null;
        _chatWeights?.Dispose();
        _chatWeights = null;
        _embedWeights?.Dispose();
        _embedWeights = null;
        _initialized = false;

        // Re-initialize
        await InitializeAsync(ct);

        Log.Information("Model reload completed successfully");
    }

    private static int EstimateVramMb(long fileSizeBytes, int gpuLayers, int contextSize)
    {
        if (gpuLayers == 0) return 0;
        double gbLoaded = fileSizeBytes / (double)(1024 * 1024 * 1024);
        double ratio = Math.Min(gpuLayers / 32.0, 1.0);
        return (int)(gbLoaded * ratio * 1024);
    }

    public async ValueTask DisposeAsync()
    {
        _embedder?.Dispose();
        _reranker?.Dispose();
        _rerankWeights?.Dispose();
        _chatWeights?.Dispose();
        _embedWeights?.Dispose();
        _chatLock.Dispose();
        _embedLock.Dispose();
        _rerankLock.Dispose();
        await Task.CompletedTask;
    }
}