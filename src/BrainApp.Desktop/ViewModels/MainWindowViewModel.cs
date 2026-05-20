using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Services;
using Microsoft.Extensions.Options;

namespace BrainApp.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly LlamaService _llama;
    private readonly ProfileRepository _profileRepo;
    private readonly RetrievalService _retrieval;
    private readonly LlamaSettings _settings;
    private readonly StorageSettings _storageSettings;

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<Profile> _profiles = new();

    [ObservableProperty]
    private string _modelStatusText = "Loading...";

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private ChatViewModel? _chatViewModel;

    [ObservableProperty]
    private DocumentsViewModel? _documentsViewModel;

    [ObservableProperty]
    private SkillsViewModel? _skillsViewModel;

    [ObservableProperty]
    private SettingsViewModel? _settingsViewModel;

    public MainWindowViewModel(
        LlamaService llama,
        ProfileRepository profileRepo,
        RetrievalService retrieval,
        IOptions<LlamaSettings> settings,
        IOptions<StorageSettings> storageSettings)
    {
        _llama = llama;
        _profileRepo = profileRepo;
        _retrieval = retrieval;
        _settings = settings.Value;
        _storageSettings = storageSettings.Value;
    }

    public void Initialize()
    {
        LoadProfiles();
        UpdateModelStatus();
    }

    private void LoadProfiles()
    {
        var profiles = _profileRepo.GetAllProfiles();
        Profiles = new ObservableCollection<Profile>(profiles);
        if (Profiles.Count > 0 && SelectedProfile == null)
            SelectedProfile = Profiles[0];
    }

    private void UpdateModelStatus()
    {
        var health = _llama.HealthCheck();
        if (!health.ModelsFound)
        {
            ModelStatusText = "Model not found";
            IsModelReady = false;
        }
        else if (!health.Initialized)
        {
            ModelStatusText = "Loading...";
            IsModelReady = false;
        }
        else
        {
            ModelStatusText = "Ready";
            IsModelReady = true;
        }
    }

    public void SelectProfile(Profile profile)
    {
        SelectedProfile = profile;
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value != null)
        {
            _ = _retrieval.LoadIndexAsync(value.Id, _storageSettings.ResolvedAppDataFolder);
            ChatViewModel?.LoadProfile(value);
            DocumentsViewModel?.LoadProfile(value.Id);
            SkillsViewModel?.LoadProfile(value.Id);
        }
    }

    [RelayCommand]
    private void CreateNewProfile()
    {
        var name = $"New Profile {Profiles.Count + 1}";
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Color = "#7C3AED",
            CreatedAt = DateTime.UtcNow,
            SystemPrompt = "You are a helpful AI assistant. Answer questions based only on the provided documents. Always cite sources as [filename, page N]."
        };
        _profileRepo.SaveProfile(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(Profile? profile)
    {
        if (profile == null) return;
        await _retrieval.ClearProfileAsync(profile.Id);
        _profileRepo.DeleteDocumentsByProfile(profile.Id);
        _profileRepo.DeleteProfile(profile.Id);
        Profiles.Remove(profile);
        if (SelectedProfile == profile)
            SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsViewModel == null)
            SettingsViewModel = new SettingsViewModel(_llama, _settings, _storageSettings);
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    public void UpdateModelStatusFromHealth(HealthStatus health)
    {
        if (!health.ModelsFound)
        {
            ModelStatusText = "Model not found";
            IsModelReady = false;
        }
        else if (!health.Initialized)
        {
            ModelStatusText = "Loading...";
            IsModelReady = false;
        }
        else
        {
            ModelStatusText = "Ready";
            IsModelReady = true;
        }
    }
}