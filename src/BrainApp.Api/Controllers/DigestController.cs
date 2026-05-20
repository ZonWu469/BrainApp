using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// Digest / automation endpoints — scheduled summary generation.
/// </summary>
[ApiController]
[Route("profiles/{profileId}/[controller]")]
[Tags("Automation")]
public class DigestController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly ChatService _chatService;

    private const string DefaultDigestPrompt = @"Summarise what needs attention this week:
overdue items, approaching deadlines, unanswered questions,
unresolved risks. Be concise and actionable.";

    public DigestController(ProfileRepository profileRepo, ChatService chatService)
    {
        _profileRepo = profileRepo;
        _chatService = chatService;
    }

    /// <summary>
    /// Generate a digest — AI-powered summary of what needs attention in a profile.
    /// Designed to be called on a schedule (cron, Task Scheduler, etc.)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateDigest(string profileId, [FromBody] DigestRequest? request = null)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var prompt = request?.Prompt ?? DefaultDigestPrompt;
        var sw = Stopwatch.StartNew();

        try
        {
            var message = await _chatService.AskAsync(profile, null, prompt);
            sw.Stop();

            Log.Information("Digest generated for profile {ProfileId} in {ElapsedMs}ms",
                profileId, sw.Elapsed.TotalMilliseconds);

            return Ok(new
            {
                digest = message.Content,
                generatedAt = DateTime.UtcNow,
                model = "Qwen3-VL-8B-Instruct-Q3_K_M",
                latencyMs = sw.Elapsed.TotalMilliseconds,
                promptUsed = prompt
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Digest generation failed for profile {ProfileId}", profileId);
            return StatusCode(500, new { error = $"Digest failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Generate a project status digest.
    /// </summary>
    [HttpPost("project-status")]
    public async Task<IActionResult> ProjectStatusDigest(string profileId)
    {
        return await GenerateDigest(profileId, new DigestRequest
        {
            Prompt = @"Provide a project status summary covering:
1. Overall health (green/amber/red)
2. Key accomplishments this week
3. Blockers or risks
4. Upcoming milestones in the next 2 weeks
5. Action items requiring immediate attention

Be specific and actionable. Format as a clean status report."
        });
    }

    /// <summary>
    /// Generate a contract review digest (highlights clauses needing attention).
    /// </summary>
    [HttpPost("contract-review")]
    public async Task<IActionResult> ContractReviewDigest(string profileId, [FromBody] ContractReviewRequest? request)
    {
        var prompt = request?.FocusArea != null
            ? $@"Review all contracts in this profile. Identify:
1. Contracts with approaching renewal dates (next 60 days)
2. Key obligations not yet fulfilled
3. Risk clauses (indemnification, liability caps, termination rights)
4. Any contracts mentioning '{request.FocusArea}'

Be specific with contract names, parties, and relevant clause excerpts."
            : @"Review all contracts in this profile. Identify:
1. Contracts with approaching renewal dates (next 60 days)
2. Key obligations not yet fulfilled
3. Risk clauses (indemnification, liability caps, termination rights)

Be specific with contract names, parties, and relevant clause excerpts.";

        return await GenerateDigest(profileId, new DigestRequest { Prompt = prompt });
    }
}

public class DigestRequest
{
    public string? Prompt { get; set; }
}

public class ContractReviewRequest
{
    public string? FocusArea { get; set; }
}