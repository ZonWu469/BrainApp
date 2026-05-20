namespace BrainApp.Core.Skills;

public static class SkillPreamble
{
    public const string NamespaceName = "BrainApp.RuntimeSkills";

    public static string BuildFullScript(string userCode) =>
        Usings + "\n\n" +
        $"namespace {NamespaceName}\n" +
        "{\n" +
        userCode + "\n" +
        "}\n";

    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Net.Http;
        using System.Text;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using BrainApp.Core.Skills;
        """;
}
