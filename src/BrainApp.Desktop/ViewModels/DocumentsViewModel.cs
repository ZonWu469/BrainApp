using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Desktop.ViewModels;

public partial class DocumentsViewModel : ObservableObject
{
    private readonly IngestionService _ingestionService;
    private readonly ProfileRepository _profileRepo;
    private readonly RetrievalService _retrievalService;
    private string? _currentProfileId;

    [ObservableProperty]
    private string? _profileId;

    [ObservableProperty]
    private ObservableCollection<DocumentItemViewModel> _documents = new();

    [ObservableProperty]
    private bool _isIngesting;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressOverallText = string.Empty;

    [ObservableProperty]
    private string _progressDetailText = string.Empty;

    [ObservableProperty]
    private int _totalDocuments;

    [ObservableProperty]
    private string _totalSize = "0 KB";

    // Single CTS for the whole batch; bound to the Cancel button.
    private CancellationTokenSource? _batchCts;

    // How many files we ingest in parallel. Per-file chunk embedding is still
    // serialized by LlamaService._embedLock, so the win is overlapping parse/IO
    // of file N+1 with embedding of file N. 3 is a safe default on most boxes.
    private const int MaxParallelIngest = 3;

    public event EventHandler<string>? ToastRequested;

    public DocumentsViewModel(
        IngestionService ingestionService,
        ProfileRepository profileRepo,
        RetrievalService retrievalService)
    {
        _ingestionService = ingestionService;
        _profileRepo = profileRepo;
        _retrievalService = retrievalService;
    }

    public void LoadProfile(string profileId)
    {
        _currentProfileId = profileId;
        RefreshDocuments();
    }

    public void RefreshDocuments()
    {
        if (string.IsNullOrEmpty(_currentProfileId)) return;

        var docs = _profileRepo.GetDocuments(_currentProfileId);
        Documents = new ObservableCollection<DocumentItemViewModel>(
            docs.Select(d => new DocumentItemViewModel
            {
                Id = d.Id,
                FileName = d.FileName,
                FileType = d.Type.ToString(),
                FileSize = FormatFileSize(d.SizeBytes),
                ChunkCount = d.ChunkCount,
                Status = "Ready",
                IngestedAt = d.IndexedAt
            }));

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        TotalDocuments = Documents.Count;
        var totalBytes = Documents.Sum(d =>
        {
            if (long.TryParse(d.FileSize.Replace(" KB", "").Replace(" MB", "").Replace(" GB", ""), out var size))
            {
                if (d.FileSize.EndsWith(" GB")) size *= 1024 * 1024 * 1024;
                else if (d.FileSize.EndsWith(" MB")) size *= 1024 * 1024;
                else size *= 1024;
                return size;
            }
            return 0L;
        });
        TotalSize = FormatFileSize(totalBytes);
    }

    public async Task IngestFilesAsync(IStorageProvider storageProvider)
    {
        if (string.IsNullOrEmpty(_currentProfileId)) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select documents to add",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Documents")
                {
                    Patterns = new[] { "*.pdf", "*.docx", "*.doc", "*.pptx", "*.txt", "*.html", "*.htm", "*.md" }
                },
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }
                },
                new FilePickerFileType("All files")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files.Count == 0) return;

        var profile = _profileRepo.GetProfile(_currentProfileId);
        if (profile == null) return;

        var filePaths = files.Select(f => (f.Name, Path: f.Path.LocalPath)).ToList();
        await IngestPathsAsync(filePaths);
    }

    /// <summary>
    /// Run ingestion across the given file paths with per-file parallelism, a single
    /// shared CancellationTokenSource, and aggregated progress reported to the UI.
    /// </summary>
    private async Task IngestPathsAsync(List<(string Name, string Path)> filePaths)
    {
        if (string.IsNullOrEmpty(_currentProfileId) || filePaths.Count == 0) return;

        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        IsIngesting = true;
        ProgressPercent = 0;
        ProgressOverallText = $"0 / {filePaths.Count} files";
        ProgressDetailText = "Starting...";
        StatusText = ProgressOverallText;

        int completed = 0;
        int success = 0;
        int skipped = 0;
        int failed = 0;
        var failedFiles = new System.Collections.Concurrent.ConcurrentBag<string>();
        // perFilePercent[i] is the current 0-100 progress of file i; used to compute
        // overall percent across N parallel workers.
        var perFilePercent = new double[filePaths.Count];

        void RefreshOverall(string? detail = null)
        {
            double sum = 0;
            for (int i = 0; i < perFilePercent.Length; i++) sum += perFilePercent[i];
            var overall = sum / filePaths.Count;
            Dispatcher.UIThread.Post(() =>
            {
                ProgressPercent = overall;
                ProgressOverallText = $"{completed} / {filePaths.Count} files";
                if (detail != null) ProgressDetailText = detail;
                StatusText = ProgressOverallText;
            });
        }

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, filePaths.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelIngest, CancellationToken = ct },
                async (idx, token) =>
                {
                    var (name, path) = filePaths[idx];
                    var progress = new Progress<(int step, string message, int percent)>(p =>
                    {
                        perFilePercent[idx] = p.percent;
                        RefreshOverall($"{name}: {p.message}");
                    });

                    try
                    {
                        var (document, chunks) = await _ingestionService.IngestFileAsync(
                            _currentProfileId!, path, progress, token);

                        perFilePercent[idx] = 100;
                        Interlocked.Increment(ref completed);
                        Interlocked.Increment(ref success);

                        // Marshal the ObservableCollection mutation back to the UI thread.
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var existing = Documents.FirstOrDefault(d => d.Id == document.Id);
                            if (existing != null) Documents.Remove(existing);
                            Documents.Add(new DocumentItemViewModel
                            {
                                Id = document.Id,
                                FileName = document.FileName,
                                FileType = document.Type.ToString(),
                                FileSize = FormatFileSize(document.SizeBytes),
                                ChunkCount = chunks.Count,
                                Status = "Ready",
                                IngestedAt = document.IndexedAt
                            });
                        });

                        RefreshOverall($"{name}: done");
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is a batch-level intent; just stop reporting this file.
                        throw;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.StartsWith("DUPLICATE:"))
                    {
                        perFilePercent[idx] = 100;
                        Interlocked.Increment(ref completed);
                        Interlocked.Increment(ref skipped);
                        ToastRequested?.Invoke(this, $"Already indexed: {name}");
                        RefreshOverall($"{name}: already indexed");
                    }
                    catch (Exception ex)
                    {
                        perFilePercent[idx] = 100;
                        Interlocked.Increment(ref completed);
                        Interlocked.Increment(ref failed);
                        failedFiles.Add($"{name} ({ex.Message})");
                        Log.Error(ex, "Ingestion failed for {File}", name);
                        RefreshOverall($"{name}: failed — {ex.Message}");
                    }
                });
        }
        catch (OperationCanceledException)
        {
            ToastRequested?.Invoke(this, "Indexing cancelled");
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsIngesting = false;
            ProgressPercent = 0;
            ProgressOverallText = string.Empty;
            ProgressDetailText = string.Empty;
            StatusText = string.Empty;
            RefreshDocuments();
        }

        if (success > 0) ToastRequested?.Invoke(this, $"Indexed {success} document(s)");
        if (skipped > 0) ToastRequested?.Invoke(this, $"Skipped {skipped} duplicate(s)");
        if (failed > 0) ToastRequested?.Invoke(this, $"Failed: {failed} — see logs");
    }

    [RelayCommand]
    private void CancelIngestion() => _batchCts?.Cancel();

    [RelayCommand]
    private async Task ReindexAllAsync()
    {
        if (string.IsNullOrEmpty(_currentProfileId)) return;

        // Clear existing chunks from memory and SQLite.
        await _retrievalService.ClearProfileAsync(_currentProfileId);

        var docs = _profileRepo.GetDocuments(_currentProfileId);

        // Reset status from Ready → Indexing so IngestFileAsync doesn't treat
        // these files as duplicates (it only skips Status=Ready documents).
        foreach (var doc in docs)
        {
            doc.Status = DocumentStatus.Indexing;
            _profileRepo.SaveDocument(doc);
        }

        var paths = docs
            .Where(d => !string.IsNullOrEmpty(d.FilePath) && File.Exists(d.FilePath))
            .Select(d => (Name: d.FileName, Path: d.FilePath))
            .ToList();

        if (paths.Count == 0)
        {
            ToastRequested?.Invoke(this, "No document files found on disk to reindex");
            return;
        }

        await IngestPathsAsync(paths);
    }

    [RelayCommand]
    private async Task DeleteDocumentAsync(DocumentItemViewModel doc)
    {
        if (string.IsNullOrEmpty(_currentProfileId)) return;

        try
        {
            await _ingestionService.RemoveDocumentAsync(_currentProfileId, doc.Id);
            _profileRepo.DeleteDocument(_currentProfileId, doc.Id);
            Documents.Remove(doc);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            ToastRequested?.Invoke(this, $"Delete failed: {ex.Message}");
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024 * 1024)} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024 * 1024)} MB";
        return $"{bytes / 1024} KB";
    }
}

public partial class DocumentItemViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _fileType = string.Empty;

    [ObservableProperty]
    private string _fileSize = "0 KB";

    [ObservableProperty]
    private int _chunkCount;

    [ObservableProperty]
    private string _status = "Pending";

    [ObservableProperty]
    private bool _isIngesting;

    [ObservableProperty]
    private DateTime _ingestedAt;

    public void SetCts(CancellationTokenSource cts) => _cts = cts;

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}