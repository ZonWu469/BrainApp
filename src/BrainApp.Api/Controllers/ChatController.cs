using System.Text;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// Chat endpoints: ask questions, streaming responses, session management.
/// </summary>
[ApiController]
[Route("profiles/{profileId}/[controller]")]
[Tags("Chat")]
public class ChatController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly ChatService _chatService;

    public ChatController(ProfileRepository profileRepo, ChatService chatService)
    {
        _profileRepo = profileRepo;
        _chatService = chatService;
    }

    /// <summary>
    /// Ask a question to a profile's knowledge base.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Ask(string profileId, [FromBody] ChatRequest request)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required" });

        ChatSession? session = null;
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            session = _profileRepo.GetSession(request.SessionId);
            if (session != null && session.ProfileId != profileId)
                session = null; // Session belongs to different profile
        }

        try
        {
            var message = await _chatService.AskAsync(profile, session, request.Question);

            // Save session if new
            if (session == null)
            {
                session = _profileRepo.CreateSession(profileId, request.Question.Length > 50
                    ? request.Question[..50] + "..."
                    : request.Question);
            }

            return Ok(new
            {
                answer = message.Content,
                citations = message.Citations.Select(c => new
                {
                    c.FileName,
                    c.PageNumber,
                    c.Excerpt,
                    c.RelevanceScore
                }),
                fromCache = message.FromCache,
                latencyMs = message.LatencyMs,
                sessionId = session.Id,
                model = "Qwen3-VL-8B-Instruct-Q3_K_M"
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not initialized"))
        {
            return StatusCode(503, new { error = "AI model not initialized. Ensure GGUF model file is present in models/ folder." });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chat error for profile {ProfileId}", profileId);
            return StatusCode(500, new { error = $"Chat failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Streaming ask — Server-Sent Events (SSE).
    /// First event: citations. Then per-token events. Final event: done=true.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(string profileId, [FromQuery] string question, [FromQuery] string? sessionId = null)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
        {
            Response.StatusCode = 404;
            await SendJsonEvent(new { error = $"Profile '{profileId}' not found" });
            return;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            Response.StatusCode = 400;
            await SendJsonEvent(new { error = "Question is required" });
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.StatusCode = 200;

        try
        {
            var session = !string.IsNullOrWhiteSpace(sessionId) ? _profileRepo.GetSession(sessionId) : null;

            // Citations callback fires before first token
            List<ChunkCitation>? capturedCitations = null;
            Task SendCitations(List<ChunkCitation> citations)
            {
                capturedCitations = citations;
                return SendJsonEvent(new
                {
                    citations = citations.Select(c => new
                    {
                        c.FileName,
                        c.PageNumber,
                        c.Excerpt,
                        c.RelevanceScore
                    })
                });
            }

            var fullAnswer = new StringBuilder();
            BrainApp.Core.Models.TokenStats? lastTokenStats = null;

            await foreach (var token in _chatService.AskStreamAsync(
                profile, session, question, SendCitations,
                onTokenStats: stats => lastTokenStats = stats))
            {
                fullAnswer.Append(token);
                await SendJsonEvent(new { token });
            }

            await SendJsonEvent(new
            {
                done = true,
                answer = fullAnswer.ToString(),
                tokens = lastTokenStats != null ? new
                {
                    input = lastTokenStats.InputTokens,
                    output = lastTokenStats.OutputTokens,
                    total = lastTokenStats.TotalTokens,
                    contextLimit = lastTokenStats.ContextLimit
                } : null
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Streaming error for profile {ProfileId}", profileId);
            await SendJsonEvent(new { error = ex.Message, done = true });
        }
    }

    /// <summary>
    /// Get session history (last 20 sessions).
    /// </summary>
    [HttpGet("sessions")]
    public IActionResult GetSessions(string profileId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var sessions = _profileRepo.GetSessionHistory(profileId, 20);
        return Ok(sessions.Select(s => new
        {
            s.Id,
            s.ProfileId,
            s.Title,
            s.CreatedAt,
            s.UpdatedAt,
            messageCount = s.Messages.Count
        }));
    }

    /// <summary>
    /// Get a specific session with all its messages.
    /// </summary>
    [HttpGet("sessions/{sessionId}")]
    public IActionResult GetSession(string profileId, string sessionId)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var session = _profileRepo.GetSession(sessionId);
        if (session == null)
            return NotFound(new { error = $"Session '{sessionId}' not found" });

        if (session.ProfileId != profileId)
            return NotFound(new { error = $"Session '{sessionId}' not found in profile '{profileId}'" });

        var messages = _profileRepo.GetMessages(sessionId);
        return Ok(new
        {
            session.Id,
            session.ProfileId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            messages = messages.Select(m => new
            {
                m.Id,
                m.SessionId,
                m.Role,
                m.Content,
                m.Citations,
                m.CreatedAt,
                m.LatencyMs,
                m.FromCache
            })
        });
    }

    private async Task SendJsonEvent(object data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }
}

public class ChatRequest
{
    public string Question { get; set; } = "";
    public string? SessionId { get; set; }
    public string? OutputFormat { get; set; } // "text" | "json" | "draft_reply"
}