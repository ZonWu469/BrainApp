using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// System endpoints: health, model info, cache management, model reload.
/// </summary>
[ApiController]
[Route("[controller]")]
[Tags("System")]
public class SystemController : ControllerBase
{
    private readonly LlamaService _llamaService;
    private readonly CacheService _cacheService;
    private readonly LlamaSettings _llamaSettings;
    private readonly CacheSettings _cacheSettings;
    private readonly ApiSettings _apiSettings;

    public SystemController(
        LlamaService llamaService,
        CacheService cacheService,
        IOptions<LlamaSettings> llamaSettings,
        IOptions<CacheSettings> cacheSettings,
        IOptions<ApiSettings> apiSettings)
    {
        _llamaService = llamaService;
        _cacheService = cacheService;
        _llamaSettings = llamaSettings.Value;
        _cacheSettings = cacheSettings.Value;
        _apiSettings = apiSettings.Value;
    }

    /// <summary>
    /// Health check — reports model status, GPU availability, initialization state.
    /// </summary>
    [HttpGet("/health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        var chatPath = _llamaSettings.GetResolvedChatModelPath(new StorageSettings());
        var embedPath = _llamaSettings.GetResolvedEmbeddingModelPath(new StorageSettings());

        var status = _llamaService.HealthCheck();

        return Ok(new
        {
            modelsFound = status.ModelsFound,
            chatModel = status.ChatModel,
            embedModel = status.EmbedModel,
            gpuAvailable = status.GpuAvailable,
            gpuLayers = status.GpuLayers,
            initialized = status.Initialized,
            modelSizeGb = status.ModelSizeGb
        });
    }

    /// <summary>
    /// Model info — detailed GGUF model configuration and resource estimates.
    /// </summary>
    [HttpGet("/model/info")]
    public IActionResult ModelInfo()
    {
        var info = _llamaService.GetModelInfo();
        return Ok(new
        {
            info.ChatModelFile,
            info.EmbedModelFile,
            info.ContextSize,
            info.GpuLayerCount,
            info.EstimatedVramMb,
            info.Threads
        });
    }

    /// <summary>
    /// Cache statistics — current cache configuration and entry counts.
    /// </summary>
    [HttpGet("/cache/stats")]
    public IActionResult CacheStats()
    {
        var (embeddingCount, queryCount, profileCount) = _cacheService.GetStats();
        return Ok(new
        {
            embeddingCacheEnabled = _cacheSettings.EnableEmbeddingCache,
            queryCacheEnabled = _cacheSettings.EnableQueryCache,
            embeddingTtlMinutes = _cacheSettings.EmbeddingTtlMinutes,
            queryTtlMinutes = _cacheSettings.QueryTtlMinutes,
            embeddingEntries = embeddingCount,
            queryEntries = queryCount,
            profileGenerations = profileCount
        });
    }

    /// <summary>
    /// Invalidate all cached answers for a specific profile.
    /// </summary>
    [HttpDelete("/cache/{profileId}")]
    public IActionResult InvalidateProfileCache(string profileId)
    {
        _cacheService.InvalidateProfile(profileId);
        Log.Information("Cache invalidated for profile: {ProfileId}", profileId);
        return Ok(new { message = $"Cache invalidated for profile {profileId}" });
    }

    /// <summary>
    /// Reload GGUF model weights from disk (hot-swap model files).
    /// </summary>
    [HttpPost("/model/reload")]
    public async Task<IActionResult> ReloadModel()
    {
        try
        {
            await _llamaService.ReloadAsync();
            Log.Information("Model reloaded successfully");
            return Ok(new { reloaded = true, message = "Model weights reloaded from disk" });
        }
        catch (FileNotFoundException ex)
        {
            Log.Warning(ex, "Model reload failed — file not found");
            return StatusCode(503, new { reloaded = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Model reload failed");
            return StatusCode(503, new { reloaded = false, error = ex.Message });
        }
    }

    /// <summary>
    /// API configuration info (non-sensitive).
    /// </summary>
    [HttpGet("/config")]
    public IActionResult Config()
    {
        return Ok(new
        {
            port = _apiSettings.Port,
            swaggerEnabled = _apiSettings.EnableSwagger,
            rateLimitPerMinute = _apiSettings.RateLimitPerMinute
        });
    }
}