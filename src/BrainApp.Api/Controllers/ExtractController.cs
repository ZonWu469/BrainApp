using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// JSON extraction from documents using a schema.
/// </summary>
[ApiController]
[Route("profiles/{profileId}/[controller]")]
[Tags("Extraction")]
public class ExtractController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly ChatService _chatService;

    public ExtractController(ProfileRepository profileRepo, ChatService chatService)
    {
        _profileRepo = profileRepo;
        _chatService = chatService;
    }

    /// <summary>
    /// Extract structured JSON from documents based on a schema.
    /// Provide a natural language question and a JSON schema (array of field definitions).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Extract(string profileId, [FromBody] ExtractRequest request)
    {
        var profile = _profileRepo.GetProfile(profileId);
        if (profile == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question is required" });

        if (string.IsNullOrWhiteSpace(request.JsonSchema))
            return BadRequest(new { error = "JsonSchema is required (e.g. '[{\"name\":\"string\",\"value\":\"number\"}]')" });

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _chatService.ExtractJsonAsync(profile, request.Question, request.JsonSchema);
            sw.Stop();

            return Ok(new
            {
                jsonOutput = result.JsonOutput,
                sources = result.Sources.Select(s => new
                {
                    s.FileName,
                    s.PageNumber,
                    s.Excerpt,
                    s.RelevanceScore
                }),
                latencyMs = sw.Elapsed.TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Extraction failed for profile {ProfileId}", profileId);
            return StatusCode(500, new { error = $"Extraction failed: {ex.Message}" });
        }
    }
}

public class ExtractRequest
{
    public string Question { get; set; } = "";
    public string JsonSchema { get; set; } = "";
}