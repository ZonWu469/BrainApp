using BrainApp.Core.Services;
using BrainApp.Core.Skills;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BrainApp.Api.Controllers;

[ApiController]
[Route("profiles/{profileId}/skills")]
[Authorize]
[Tags("Skills")]
public class SkillsController : ControllerBase
{
    private readonly ProfileRepository _profileRepo;
    private readonly SkillCatalogService _skillCatalog;

    public SkillsController(ProfileRepository profileRepo, SkillCatalogService skillCatalog)
    {
        _profileRepo = profileRepo;
        _skillCatalog = skillCatalog;
    }

    [HttpGet]
    public IActionResult List(string profileId)
    {
        if (_profileRepo.GetProfile(profileId) == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        var catalog = _skillCatalog.GetCatalog();
        var profileSkills = _profileRepo.GetProfileSkills(profileId);
        var enabledLookup = profileSkills.ToDictionary(p => p.SkillFile, p => p.Enabled, StringComparer.OrdinalIgnoreCase);

        var items = catalog.Select(def => new
        {
            def.FileName,
            def.IsValid,
            def.CompileError,
            Methods = def.Methods.Select(m => new
            {
                m.SkillName,
                m.Description,
                m.FullName,
                Parameters = m.Parameters
            }),
            Enabled = !enabledLookup.TryGetValue(def.FileName, out var e) || e
        });

        return Ok(items);
    }

    [HttpPut("{fileName}")]
    public IActionResult SetEnabled(string profileId, string fileName, [FromBody] SetSkillEnabledRequest request)
    {
        if (_profileRepo.GetProfile(profileId) == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        _profileRepo.SetSkillEnabled(profileId, fileName, request.Enabled);
        return Ok(new { profileId, fileName, request.Enabled });
    }

    [HttpPost("refresh")]
    public IActionResult Refresh(string profileId)
    {
        if (_profileRepo.GetProfile(profileId) == null)
            return NotFound(new { error = $"Profile '{profileId}' not found" });

        _skillCatalog.Refresh();
        return Ok(new { message = "Skills catalog refreshed", count = _skillCatalog.GetCatalog().Count });
    }
}

public class SetSkillEnabledRequest
{
    public bool Enabled { get; set; }
}
