using Microsoft.AspNetCore.Mvc;
using Serilog;
using BrainApp.Core.Models;
using BrainApp.Core.Services;

namespace BrainApp.Api.Controllers;

/// <summary>
/// Profile CRUD endpoints and statistics.
/// </summary>
[ApiController]
[Route("[controller]")]
[Tags("Profiles")]
public class ProfilesController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly CacheService _cacheService;

    public ProfilesController(ProfileRepository profileRepo, CacheService cacheService)
    {
        _profileRepo = profileRepo;
        _cacheService = cacheService;
    }

    /// <summary>
    /// List all profiles.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var profiles = _profileRepo.GetAllProfiles();
        return Ok(profiles.Select(p =>
        {
            p.Stats = _profileRepo.GetProfileStats(p.Id);
            return p;
        }));
    }

    /// <summary>
    /// Get a single profile by ID.
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var profile = _profileRepo.GetProfile(id);
        if (profile == null)
            return NotFound(new { error = $"Profile '{id}' not found" });

        profile.Stats = _profileRepo.GetProfileStats(id);
        return Ok(profile);
    }

    /// <summary>
    /// Create a new profile.
    /// </summary>
    [HttpPost]
    public IActionResult Create([FromBody] ProfileCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Profile name is required" });

        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = request.Name.Trim(),
            Description = request.Description ?? "",
            Color = request.Color ?? "#534AB7",
            Icon = request.Icon ?? "brain",
            SystemPrompt = Profile.DefaultSystemPrompt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _profileRepo.SaveProfile(profile);
        Log.Information("Profile created: {ProfileId} ({ProfileName})", profile.Id, profile.Name);

        return Created($"/profiles/{profile.Id}", profile);
    }

    /// <summary>
    /// Update an existing profile.
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] ProfileUpdateRequest request)
    {
        var existing = _profileRepo.GetProfile(id);
        if (existing == null)
            return NotFound(new { error = $"Profile '{id}' not found" });

        // Apply updates
        if (!string.IsNullOrWhiteSpace(request.Name))
            existing.Name = request.Name.Trim();
        if (request.Description != null)
            existing.Description = request.Description;
        if (!string.IsNullOrWhiteSpace(request.Color))
            existing.Color = request.Color;
        if (!string.IsNullOrWhiteSpace(request.Icon))
            existing.Icon = request.Icon;
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            existing.SystemPrompt = request.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(request.ModelOverride))
            existing.ModelOverride = request.ModelOverride;

        existing.UpdatedAt = DateTime.UtcNow;
        _profileRepo.SaveProfile(existing);
        Log.Information("Profile updated: {ProfileId}", id);

        return Ok(existing);
    }

    /// <summary>
    /// Delete a profile and all its documents, sessions, and messages.
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var existing = _profileRepo.GetProfile(id);
        if (existing == null)
            return NotFound(new { error = $"Profile '{id}' not found" });

        // Delete all documents first (cascade)
        var documents = _profileRepo.GetDocuments(id);
        foreach (var doc in documents)
        {
            _profileRepo.DeleteDocument(id, doc.Id);
        }

        // Delete profile
        _profileRepo.DeleteProfile(id);

        // Invalidate cache
        _cacheService.InvalidateProfile(id);

        Log.Information("Profile deleted: {ProfileId}", id);
        return NoContent();
    }

    /// <summary>
    /// Get statistics for a profile (document count, chunk count, total size, etc.)
    /// </summary>
    [HttpGet("{id}/stats")]
    public IActionResult GetStats(string id)
    {
        var profile = _profileRepo.GetProfile(id);
        if (profile == null)
            return NotFound(new { error = $"Profile '{id}' not found" });

        var stats = _profileRepo.GetProfileStats(id);
        return Ok(stats);
    }
}

public class ProfileCreateRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
}

public class ProfileUpdateRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public string? SystemPrompt { get; set; }
    public string? ModelOverride { get; set; }
}