using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BrainApp.Api.Controllers;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace BrainApp.Tests;

public class SkillsControllerTests
{
    private static T GetPropValue<T>(object obj, string propName)
    {
        var prop = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        Assert.NotNull(prop);
        return (T)(prop!.GetValue(obj) ?? default(T)!);
    }

    [Fact]
    public void SkillsController_List_DisabledSkill_IsReflected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "brainapp_skills_controller_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var storageSettings = Options.Create(new StorageSettings { AppDataFolder = tempRoot });
        var skillsFolder = Path.Combine(tempRoot, "skills");
        var skillsSettings = Options.Create(new SkillsSettings
        {
            SkillsFolder = skillsFolder,
            ExecutionTimeoutSeconds = 2,
            MaxSkillResultChars = 16000
        });

        var profileRepo = new ProfileRepository(storageSettings);
        var scriptEngine = new SkillScriptEngine(skillsSettings);
        var executor = new SkillExecutor(skillsSettings);
        var fetchCache = new SkillFetchCache(skillsSettings);
        var catalog = new SkillCatalogService(
            scriptEngine,
            executor,
            fetchCache,
            profileRepo,
            skillsSettings,
            storageSettings);

        var controller = new SkillsController(profileRepo, catalog);

        var profileId = "profile1";
        profileRepo.SaveProfile(new Profile { Id = profileId, Name = "Profile 1" });

        // Ensure list loads catalog and applies defaults
        var listResult1 = controller.List(profileId);
        var ok1 = Assert.IsType<OkObjectResult>(listResult1);
        Assert.NotNull(ok1.Value);

        var items1 = Assert.IsAssignableFrom<IEnumerable>(ok1.Value);
        var fetchItem1 = items1.Cast<object>()
            .FirstOrDefault(x => string.Equals(GetPropValue<string>(x, "FileName"), "fetch-page.txt", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fetchItem1);
        Assert.True(GetPropValue<bool>(fetchItem1!, "Enabled"));

        var setResult = controller.SetEnabled(profileId, "fetch-page.txt", new SetSkillEnabledRequest { Enabled = false });
        Assert.IsType<OkObjectResult>(setResult);

        var listResult2 = controller.List(profileId);
        var ok2 = Assert.IsType<OkObjectResult>(listResult2);
        var items2 = Assert.IsAssignableFrom<IEnumerable>(ok2.Value);
        var fetchItem2 = items2.Cast<object>()
            .FirstOrDefault(x => string.Equals(GetPropValue<string>(x, "FileName"), "fetch-page.txt", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fetchItem2);
        Assert.False(GetPropValue<bool>(fetchItem2!, "Enabled"));
    }

    [Fact]
    public void SkillsController_Refresh_ReturnsOk()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "brainapp_skills_controller_refresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var storageSettings = Options.Create(new StorageSettings { AppDataFolder = tempRoot });
        var skillsFolder = Path.Combine(tempRoot, "skills");
        var skillsSettings = Options.Create(new SkillsSettings { SkillsFolder = skillsFolder });

        var profileRepo = new ProfileRepository(storageSettings);
        var scriptEngine = new SkillScriptEngine(skillsSettings);
        var executor = new SkillExecutor(skillsSettings);
        var fetchCache = new SkillFetchCache(skillsSettings);
        var catalog = new SkillCatalogService(
            scriptEngine,
            executor,
            fetchCache,
            profileRepo,
            skillsSettings,
            storageSettings);

        var controller = new SkillsController(profileRepo, catalog);

        var profileId = "profile1";
        profileRepo.SaveProfile(new Profile { Id = profileId, Name = "Profile 1" });

        var result = controller.Refresh(profileId);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }
}

