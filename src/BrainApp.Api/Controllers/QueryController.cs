using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// Cross-profile query — asks the same question across multiple profiles simultaneously.
/// </summary>
[ApiController]
[Route("[controller]")]
[Tags("Automation")]
public class QueryController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly ChatService _chatService;

    public QueryController(ProfileRepository profileRepo, ChatService chatService)
    {
        _profileRepo = profileRepo;
        _chatService = chatService;
    }

    /// <summary>
    /// Query multiple profiles and return combined results.
    /// Useful for: "what do all client contracts say about liability?"
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> QueryCrossProfile([FromBody] CrossProfileQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required" });

        if (request.ProfileIds == null || request.ProfileIds.Length == 0)
            return BadRequest(new { error = "At least one profileId is required" });

        var answers = new List<CrossProfileAnswer>();
        var errors = new List<string>();

        foreach (var profileId in request.ProfileIds)
        {
            var profile = _profileRepo.GetProfile(profileId);
            if (profile == null)
            {
                errors.Add($"Profile '{profileId}' not found");
                continue;
            }

            try
            {
                var message = await _chatService.AskAsync(profile, null, request.Question);
                answers.Add(new CrossProfileAnswer
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Answer = message.Content,
                    Citations = message.Citations
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Cross-profile query failed for profile {ProfileId}", profileId);
                errors.Add($"Profile '{profileId}' failed: {ex.Message}");
            }
        }

        return Ok(new
        {
            answers,
            errors = errors.Count > 0 ? errors : null,
            question = request.Question,
            profileCount = request.ProfileIds.Length,
            answeredCount = answers.Count
        });
    }

    /// <summary>
    /// Quick test: ask a single profile a quick question.
    /// </summary>
    [HttpPost("ask")]
    public async Task<IActionResult> AskSingle([FromBody] CrossProfileQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required" });

        if (request.ProfileIds == null || request.ProfileIds.Length == 0)
            return BadRequest(new { error = "At least one profileId is required" });

        // Use first profile
        var profile = _profileRepo.GetProfile(request.ProfileIds[0]);
        if (profile == null)
            return NotFound(new { error = $"Profile '{request.ProfileIds[0]}' not found" });

        var message = await _chatService.AskAsync(profile, null, request.Question);
        return Ok(new
        {
            profileId = profile.Id,
            profileName = profile.Name,
            answer = message.Content,
            citations = message.Citations,
            fromCache = message.FromCache,
            latencyMs = message.LatencyMs
        });
    }
}

public class CrossProfileQueryRequest
{
    public string Question { get; set; } = "";
    public string[] ProfileIds { get; set; } = Array.Empty<string>();
    public bool UnionResults { get; set; } = false;
}

public class CrossProfileAnswer
{
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Answer { get; set; } = "";
    public List<ChunkCitation> Citations { get; set; } = new();
}