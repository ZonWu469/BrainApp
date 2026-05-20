using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Desktop.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ChatService _chatService;
    private readonly ProfileRepository _profileRepo;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private Profile? _currentProfile;

    [ObservableProperty]
    private ObservableCollection<ChatMessageViewModel> _messages = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _tokenInfo = string.Empty;

    public ChatViewModel(ChatService chatService, ProfileRepository profileRepo)
    {
        _chatService = chatService;
        _profileRepo = profileRepo;
    }

    public void LoadProfile(Profile profile)
    {
        CurrentProfile = profile;
        Messages.Clear();
        StatusText = string.Empty;

        // Load recent session
        var sessions = _profileRepo.GetSessionHistory(profile.Id, limit: 1);
        if (sessions.Count > 0)
        {
            var session = sessions[0];
            var msgs = _profileRepo.GetMessages(session.Id);
            foreach (var msg in msgs)
            {
                Messages.Add(new ChatMessageViewModel
                {
                    Content = msg.Content,
                    IsUser = msg.Role == MessageRole.User,
                    FromCache = msg.FromCache,
                    Citations = new ObservableCollection<ChunkCitation>(msg.Citations ?? new()),
                    Timestamp = msg.CreatedAt
                });
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || CurrentProfile == null || IsStreaming)
            return;

        var question = InputText.Trim();
        InputText = string.Empty;

        // Add user message
        var userMsg = new ChatMessageViewModel
        {
            Content = question,
            IsUser = true,
            Timestamp = DateTime.UtcNow
        };
        Messages.Add(userMsg);

        // Add placeholder for assistant
        var assistantMsg = new ChatMessageViewModel
        {
            Content = string.Empty,
            IsUser = false,
            Timestamp = DateTime.UtcNow
        };
        Messages.Add(assistantMsg);

        IsStreaming = true;
        StatusText = "Thinking...";
        TokenInfo = string.Empty;
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        try
        {
            var session = GetOrCreateSessionWithMessages(CurrentProfile.Id);
            var userMsgModel = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = question,
                CreatedAt = userMsg.Timestamp
            };
            if (session.Messages.Count == 0 ||
                session.Messages[^1].Role != MessageRole.User ||
                session.Messages[^1].Content != question)
            {
                session.Messages.Add(userMsgModel);
            }

            var lastCitations = new List<ChunkCitation>();
            TokenStats? lastTokenStats = null;

            await foreach (var token in _chatService.AskStreamAsync(
                CurrentProfile, session, question,
                onCitations: async c =>
                {
                    lastCitations = c;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        assistantMsg.Citations = new ObservableCollection<ChunkCitation>(c);
                    });
                },
                onStatus: status => Dispatcher.UIThread.Post(() => StatusText = status),
                onTokenStats: stats =>
                {
                    lastTokenStats = stats;
                    Dispatcher.UIThread.Post(() =>
                    {
                        TokenInfo = $"In: {stats.InputTokens:N0} · Out: {stats.OutputTokens:N0} · Total: {stats.TotalTokens:N0} / {stats.ContextLimit:N0} · {sw.Elapsed.TotalSeconds:F1}s";
                    });
                },
                ct: _cts.Token))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    assistantMsg.Content += token;
                    OnPropertyChanged(nameof(Messages));
                });
            }

            sw.Stop();
            if (lastTokenStats != null)
            {
                assistantMsg.TokenDisplay = $"In: {lastTokenStats.InputTokens:N0} · Out: {lastTokenStats.OutputTokens:N0} · {sw.Elapsed.TotalSeconds:F1}s";
            }

            // Save messages
            var assistantMsgModel = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                SessionId = session.Id,
                Role = MessageRole.Assistant,
                Content = assistantMsg.Content,
                Citations = lastCitations,
                FromCache = false,
                CreatedAt = assistantMsg.Timestamp
            };
            if (!session.Messages.Any(m => m.Id == userMsgModel.Id))
                session.Messages.Add(userMsgModel);
            _profileRepo.SaveMessages(session.Id, new[] { userMsgModel, assistantMsgModel });
        }
        catch (OperationCanceledException)
        {
            assistantMsg.Content += " [stopped]";
            StatusText = "Stopped";
        }
        finally
        {
            IsStreaming = false;
            StatusText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private ChatSession GetOrCreateSessionWithMessages(string profileId)
    {
        var latest = _profileRepo.GetSessionHistory(profileId, limit: 1).FirstOrDefault();
        if (latest != null)
        {
            var loaded = _profileRepo.GetSession(latest.Id);
            if (loaded != null)
                return loaded;
        }

        return _profileRepo.CreateSession(profileId);
    }

    private bool CanSend() => !IsStreaming && !string.IsNullOrWhiteSpace(InputText) && CurrentProfile != null;

    [RelayCommand]
    private void StopGeneration()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void NewChat()
    {
        if (CurrentProfile != null)
        {
            var session = _profileRepo.CreateSession(CurrentProfile.Id, "New chat");
            Messages.Clear();
            StatusText = "Started new chat";
        }
    }

    [RelayCommand]
    private async Task ExportChatAsync()
    {
        // Export will be handled by the view via file picker
    }

    [RelayCommand]
    private async Task GenerateDigestAsync()
    {
        if (CurrentProfile == null) return;

        IsLoading = true;
        StatusText = "Generating digest...";

        try
        {
            var digest = await _chatService.GenerateDigestAsync(CurrentProfile);
            var digestMsg = new ChatMessageViewModel
            {
                Content = digest,
                IsUser = false,
                IsDigest = true,
                Timestamp = DateTime.UtcNow
            };
            Messages.Add(digestMsg);
        }
        finally
        {
            IsLoading = false;
            StatusText = string.Empty;
        }
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
    }

    [RelayCommand]
    private void OpenCitation(ChunkCitation? citation)
    {
        if (citation == null || string.IsNullOrEmpty(citation.FilePath))
            return;

        if (!System.IO.File.Exists(citation.FilePath))
        {
            StatusText = $"File not found: {citation.FileName}";
            return;
        }

        try
        {
            if (citation.FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && citation.PageNumber > 0)
            {
                var uri = $"file:///{citation.FilePath.Replace('\\', '/')}#page={citation.PageNumber}";
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(citation.FilePath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Cannot open: {citation.FileName}";
            Serilog.Log.Warning(ex, "Failed to open citation file {FilePath}", citation.FilePath);
        }
    }
}

public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isUser;

    [ObservableProperty]
    private bool _fromCache;

    [ObservableProperty]
    private bool _isDigest;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private ObservableCollection<ChunkCitation> _citations = new();

    [ObservableProperty]
    private bool _isCitationsExpanded;

    [ObservableProperty]
    private string _tokenDisplay = string.Empty;
}