using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;

namespace BrainApp.Desktop.ViewModels;

public partial class SkillsViewModel : ObservableObject
{
    private readonly SkillCatalogService _skillCatalog;
    private readonly ProfileRepository _profileRepo;
    private string? _currentProfileId;

    [ObservableProperty]
    private ObservableCollection<SkillItemViewModel> _skills = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    public SkillsViewModel(SkillCatalogService skillCatalog, ProfileRepository profileRepo)
    {
        _skillCatalog = skillCatalog;
        _profileRepo = profileRepo;
    }

    public void LoadProfile(string? profileId)
    {
        _currentProfileId = profileId;
        RefreshSkills();
    }

    [RelayCommand]
    private void RefreshSkills()
    {
        _skillCatalog.Refresh();
        var catalog = _skillCatalog.GetCatalog();
        var profileSkills = string.IsNullOrEmpty(_currentProfileId)
            ? new List<ProfileSkillRow>()
            : _profileRepo.GetProfileSkills(_currentProfileId);

        var enabledLookup = profileSkills.ToDictionary(
            p => p.SkillFile,
            p => p.Enabled,
            StringComparer.OrdinalIgnoreCase);

        Skills = new ObservableCollection<SkillItemViewModel>(
            catalog.Select(def =>
            {
                var enabled = string.IsNullOrEmpty(_currentProfileId)
                    || !enabledLookup.TryGetValue(def.FileName, out var e)
                    || e;

                var methodSummary = def.IsValid
                    ? string.Join(", ", def.Methods.Select(m => m.SkillName))
                    : string.Empty;

                return new SkillItemViewModel
                {
                    FileName = def.FileName,
                    DisplayName = def.IsValid && def.Methods.Count > 0
                        ? def.Methods[0].SkillName
                        : Path.GetFileNameWithoutExtension(def.FileName),
                    Description = def.IsValid && def.Methods.Count > 0
                        ? def.Methods[0].Description
                        : def.CompileError ?? "Invalid",
                    MethodSummary = methodSummary,
                    IsValid = def.IsValid,
                    CompileError = def.CompileError ?? string.Empty,
                    IsEnabled = enabled,
                    MethodCount = def.Methods.Count
                };
            }));

        StatusText = $"{Skills.Count(s => s.IsValid)} valid / {Skills.Count} total · {_skillCatalog.SkillsFolder}";
    }

    [RelayCommand]
    private void OpenSkillsFolder()
    {
        var folder = _skillCatalog.SkillsFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    public void PersistSkillEnabled(SkillItemViewModel item)
    {
        if (string.IsNullOrEmpty(_currentProfileId))
            return;
        _profileRepo.SetSkillEnabled(_currentProfileId, item.FileName, item.IsEnabled);
    }
}

public partial class SkillItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _methodSummary = string.Empty;

    [ObservableProperty]
    private bool _isValid;

    [ObservableProperty]
    private string _compileError = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private int _methodCount;
}
