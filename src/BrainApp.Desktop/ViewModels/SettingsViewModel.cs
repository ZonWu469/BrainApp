using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BrainApp.Core.Config;
using BrainApp.Core.Services;

namespace BrainApp.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly LlamaService _llama;
    private readonly LlamaSettings _settings;
    private readonly StorageSettings _storageSettings;

    // Model tab
    [ObservableProperty]
    private string _chatModelFile = string.Empty;

    [ObservableProperty]
    private string _embeddingModelFile = string.Empty;

    [ObservableProperty]
    private string _modelsFolder = string.Empty;

    [ObservableProperty]
    private int _contextSize;

    [ObservableProperty]
    private int _gpuLayerCount;

    [ObservableProperty]
    private int _threads;

    [ObservableProperty]
    private string _selectedChatTemplate = string.Empty;

    [ObservableProperty]
    private string _modelInfoText = string.Empty;

    [ObservableProperty]
    private string _testResultText = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isReloading;

    // Cache tab
    [ObservableProperty]
    private bool _enableEmbeddingCache;

    [ObservableProperty]
    private bool _enableQueryCache;

    [ObservableProperty]
    private int _embeddingTtlMinutes;

    [ObservableProperty]
    private int _queryTtlMinutes;

    [ObservableProperty]
    private string _cacheStatsText = string.Empty;

    // Storage tab
    [ObservableProperty]
    private string _appDataFolder = string.Empty;

    [ObservableProperty]
    private int _maxFileSizeMb;

    [ObservableProperty]
    private int _maxDocumentsPerProfile;

    // API tab
    [ObservableProperty]
    private bool _apiEnabled;

    [ObservableProperty]
    private int _apiPort;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _swaggerEnabled;

    [ObservableProperty]
    private bool _isApiKeyVisible;

    // About tab
    [ObservableProperty]
    private string _versionText = "1.0.0";

    [ObservableProperty]
    private string _aboutText = string.Empty;

    [ObservableProperty]
    private string _modelCheckResult = string.Empty;

    public ObservableCollection<string> ChatTemplates { get; } = new()
    {
        "Qwen", "Llama3", "Phi3", "Gemma", "Mistral", "ChatML"
    };

    public SettingsViewModel(LlamaService llama, LlamaSettings settings, StorageSettings storageSettings)
    {
        _llama = llama;
        _settings = settings;
        _storageSettings = storageSettings;

        // Initialize from current settings
        ChatModelFile = settings.ChatModelFile;
        EmbeddingModelFile = settings.EmbeddingModelFile;
        ModelsFolder = settings.ModelsFolder;
        ContextSize = settings.ContextSize;
        GpuLayerCount = settings.GpuLayerCount;
        Threads = settings.Threads;
        SelectedChatTemplate = settings.ChatTemplate.ToString();

        AppDataFolder = storageSettings.ResolvedAppDataFolder;
        MaxFileSizeMb = storageSettings.MaxFileSizeMb;
        MaxDocumentsPerProfile = storageSettings.MaxDocumentsPerProfile;

        RefreshModelInfo();
        AboutText = $"BrainApp v{VersionText}\nLocal Knowledge Base powered by LLamaSharp\n\nGGUF models provide full offline AI inference with no network required.";
    }

    public void RefreshModelInfo()
    {
        var info = _llama.GetModelInfo();
        var sizeGb = info.FileSizeBytes / (1024.0 * 1024.0 * 1024.0);
        ModelInfoText = $"File: {info.ChatModelFile}\n" +
                       $"Size: {sizeGb:F2} GB\n" +
                       $"Context: {info.ContextSize}\n" +
                       $"GPU Layers: {info.GpuLayerCount}\n" +
                       $"Threads: {info.Threads}\n" +
                       $"Est. VRAM: {info.EstimatedVramMb} MB";
    }

    [RelayCommand]
    private async Task TestModelAsync()
    {
        IsTesting = true;
        TestResultText = "Testing...";
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _llama.ChatAsync(
                "You are a helpful assistant.",
                new(),
                "Reply with exactly: OK",
                CancellationToken.None);
            sw.Stop();
            TestResultText = $"Success in {sw.ElapsedMilliseconds}ms: {result.Trim()}";
        }
        catch (Exception ex)
        {
            sw.Stop();
            TestResultText = $"Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task ReloadModelAsync()
    {
        IsReloading = true;
        TestResultText = "Reloading model...";
        try
        {
            await _llama.ReloadAsync();
            TestResultText = "Model reloaded successfully";
            RefreshModelInfo();
        }
        catch (Exception ex)
        {
            TestResultText = $"Reload failed: {ex.Message}";
        }
        finally
        {
            IsReloading = false;
        }
    }

    [RelayCommand]
    private void ClearAllCaches(CacheService cacheService)
    {
        cacheService.ClearAll();
        CacheStatsText = "All caches cleared";
    }

    [RelayCommand]
    private void CheckModelFiles()
    {
        var chatPath = Path.Combine(ModelsFolder, ChatModelFile);
        var embedPath = Path.Combine(ModelsFolder, EmbeddingModelFile);

        var chatExists = File.Exists(chatPath);
        var embedExists = File.Exists(embedPath);

        var result = $"Chat model: {(chatExists ? "✓ Found" : "✗ Not found")} ({FormatSize(chatPath)})\n" +
                    $"Embedding model: {(embedExists ? "✓ Found" : "✗ Not found")} ({FormatSize(embedPath)})";

        ModelCheckResult = result;
    }

    [RelayCommand]
    private void ToggleApiKeyVisibility()
    {
        IsApiKeyVisible = !IsApiKeyVisible;
    }

    [RelayCommand]
    private void CopyApiKey()
    {
        // Will be handled by view
    }

    [RelayCommand]
    private async Task BrowseModelsFolderAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider == null) return;

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select models folder",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            ModelsFolder = result[0].Path.LocalPath;
        }
    }

    private static string FormatSize(string path)
    {
        if (!File.Exists(path)) return "N/A";
        var info = new FileInfo(path);
        var gb = info.Length / (1024.0 * 1024.0 * 1024.0);
        return $"{gb:F2} GB";
    }

    public void SaveSettings(
        out LlamaSettings llamaSettings,
        out StorageSettings storageSettings,
        out ApiSettings apiSettings)
    {
        llamaSettings = new LlamaSettings
        {
            ModelsFolder = ModelsFolder,
            ChatModelFile = ChatModelFile,
            EmbeddingModelFile = EmbeddingModelFile,
            ContextSize = ContextSize,
            GpuLayerCount = GpuLayerCount,
            Threads = Threads,
            BatchSize = _settings.BatchSize,
            Temperature = _settings.Temperature,
            MaxTokens = _settings.MaxTokens,
            AntiPrompts = _settings.AntiPrompts,
            ChatTemplate = Enum.TryParse<ChatTemplate>(SelectedChatTemplate, out var ct) ? ct : Core.Config.ChatTemplate.Qwen
        };

        storageSettings = new StorageSettings
        {
            AppDataFolder = _storageSettings.AppDataFolder,
            MaxDocumentsPerProfile = MaxDocumentsPerProfile,
            MaxFileSizeMb = MaxFileSizeMb
        };

        apiSettings = new ApiSettings
        {
            Port = ApiPort,
            EnableSwagger = SwaggerEnabled,
            ApiKey = ApiKey,
            RateLimitPerMinute = _settings.BatchSize // placeholder
        };
    }
}