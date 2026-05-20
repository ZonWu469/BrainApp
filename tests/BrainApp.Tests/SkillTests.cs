using BrainApp.Core.Config;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;
using Microsoft.Extensions.Options;
using Xunit;

namespace BrainApp.Tests;

public class SkillTests
{
    [Fact]
    public void GetResolvedSkillsFolder_DefaultsToExecutableSkillsDirectory()
    {
        var settings = new SkillsSettings();
        var storage = new StorageSettings { AppDataFolder = "ignored" };

        var path = settings.GetResolvedSkillsFolder(storage);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "skills"), path);
    }

    [Fact]
    public void ContainsHttpUrl_DetectsAbsoluteUrls()
    {
        Assert.True(ChatService.ContainsHttpUrl("Read https://en.wikipedia.org/wiki/ESA please"));
        Assert.False(ChatService.ContainsHttpUrl("What is the European Space Agency?"));
    }

    [Fact]
    public void SkillCallParser_ParsesJsonBlock()
    {
        var response = """
            ```json
            {"skill":"FetchPageSkill.FetchPage","arguments":{"url":"https://example.com"}}
            ```
            """;

        var invocation = SkillCallParser.TryParse(response);

        Assert.NotNull(invocation);
        Assert.Equal("FetchPageSkill.FetchPage", invocation!.SkillKey);
        Assert.Equal("https://example.com", invocation.Arguments["url"]?.ToString());
    }

    [Fact]
    public void SkillCallParser_ParsesInlineJson()
    {
        var response = """{"skill":"fetch_page","arguments":{"url":"https://test.org"}}""";

        var invocation = SkillCallParser.TryParse(response);

        Assert.NotNull(invocation);
        Assert.Equal("fetch_page", invocation!.SkillKey);
    }

    [Fact]
    public void SkillCallParser_ToleratesTrailingProse()
    {
        // Models sometimes append commentary after the JSON object. Previously this
        // made JsonDocument.Parse reject the whole string and TryParse return null,
        // leaking the JSON to the user via ChatService's else branch.
        var response = """
            {"skill":"FetchPageSkill.FetchPage","arguments":{"url":"https://example.com"}} Here's why I called this skill.
            """;

        var invocation = SkillCallParser.TryParse(response);

        Assert.NotNull(invocation);
        Assert.Equal("FetchPageSkill.FetchPage", invocation!.SkillKey);
        Assert.Equal("https://example.com", invocation.Arguments["url"]?.ToString());
    }

    [Fact]
    public void SkillCallParser_LooksLikeSkillJson_DetectsBareJsonStart()
    {
        Assert.True(SkillCallParser.LooksLikeSkillJson("""{"skill":"x"}"""));
        Assert.True(SkillCallParser.LooksLikeSkillJson("   {  \"skill\":  \"x\"  }"));
        Assert.True(SkillCallParser.LooksLikeSkillJson("```json\n{\"skill\":\"x\"}\n```"));
    }

    [Fact]
    public void SkillCallParser_LooksLikeSkillJson_DetectsInlineSkillKey()
    {
        Assert.True(SkillCallParser.LooksLikeSkillJson("Here is the call: {\"skill\": \"foo\"}"));
    }

    [Fact]
    public void SkillCallParser_LooksLikeSkillJson_IgnoresNormalProse()
    {
        Assert.False(SkillCallParser.LooksLikeSkillJson("The European Space Agency is..."));
        Assert.False(SkillCallParser.LooksLikeSkillJson(""));
        Assert.False(SkillCallParser.LooksLikeSkillJson("   "));
    }

    [Fact]
    public void SkillScriptEngine_CompilesSampleSkill()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "brainapp_skills_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var skillPath = Path.Combine(tempDir, "echo.txt");
            File.WriteAllText(skillPath, """
                public class EchoSkill
                {
                    [Skill("echo", Description = "Echoes input back")]
                    public string Echo(string text) => "echo:" + text;
                }
                """);

            var settings = Options.Create(new SkillsSettings());
            var engine = new SkillScriptEngine(settings);
            var def = engine.CompileFile(skillPath);

            Assert.True(def.IsValid, def.CompileError);
            Assert.Single(def.Methods);
            Assert.Equal("echo", def.Methods[0].SkillName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SkillExecutor_InvokesCompiledMethod()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "brainapp_skills_exec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var skillPath = Path.Combine(tempDir, "echo.txt");
            File.WriteAllText(skillPath, """
                public class EchoSkill
                {
                    [Skill("echo", Description = "Echoes input back")]
                    public string Echo(string text) => "echo:" + text;
                }
                """);

            var engine = new SkillScriptEngine(Options.Create(new SkillsSettings()));
            var def = engine.CompileFile(skillPath);
            var method = def.Methods[0];

            var executor = new SkillExecutor(Options.Create(new SkillsSettings()));
            var result = await executor.ExecuteAsync(
                method,
                new SkillInvocation { SkillKey = "echo", Arguments = new() { ["text"] = "hello" } },
                new SkillContext());

            Assert.True(result.Success, result.Error);
            Assert.Equal("echo:hello", result.Output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
