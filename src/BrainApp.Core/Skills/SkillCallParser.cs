using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainApp.Core.Skills;

public static class SkillCallParser
{
    private static readonly Regex JsonBlockRegex = new(
        @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InlineJsonRegex = new(
        @"\{[\s]*""skill""[\s]*:[\s\S]*?\}",
        RegexOptions.Compiled);

    public static SkillInvocation? TryParse(string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return null;

        var candidates = new List<string>();

        foreach (Match match in JsonBlockRegex.Matches(llmResponse))
            candidates.Add(match.Groups[1].Value.Trim());

        var trimmed = llmResponse.Trim();
        if (trimmed.StartsWith('{') && trimmed.Contains("\"skill\""))
        {
            // Models often append trailing prose ("...} Here's why I called this skill...")
            // which makes JsonDocument.Parse reject the whole string. Walk the input to
            // extract the first balanced JSON object so we don't lose a real skill call.
            candidates.Add(trimmed);
            var balanced = ExtractFirstBalancedObject(trimmed);
            if (balanced != null) candidates.Add(balanced);
        }

        foreach (Match match in InlineJsonRegex.Matches(llmResponse))
            candidates.Add(match.Value.Trim());

        foreach (var json in candidates.Distinct())
        {
            var invocation = ParseJson(json);
            if (invocation != null)
                return invocation;
        }

        return null;
    }

    /// <summary>
    /// Returns true when the response *looks like* an attempted skill invocation —
    /// either starts with '{' or contains a "skill" key — even if it failed to parse.
    /// ChatService uses this to detect routing failures and fall back to a plain RAG
    /// answer instead of leaking the broken JSON to the user.
    /// </summary>
    public static bool LooksLikeSkillJson(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        var t = response.TrimStart();
        if (t.StartsWith('{')) return true;
        if (t.StartsWith("```")) return true;
        return SkillKeyHintRegex.IsMatch(response);
    }

    private static readonly Regex SkillKeyHintRegex = new(
        @"""skill""\s*:",
        RegexOptions.Compiled);

    /// <summary>
    /// Walks the string once tracking string-literal state and brace depth, returning
    /// the substring covering the first balanced '{...}'. Returns null if no balanced
    /// object is found.
    /// </summary>
    private static string? ExtractFirstBalancedObject(string s)
    {
        int start = s.IndexOf('{');
        if (start < 0) return null;

        bool inString = false;
        bool escape = false;
        int depth = 0;

        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static SkillInvocation? ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("skill", out var skillProp))
                return null;

            var skillKey = skillProp.GetString();
            if (string.IsNullOrWhiteSpace(skillKey))
                return null;

            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsProp.EnumerateObject())
                    args[prop.Name] = JsonElementToObject(prop.Value);
            }

            return new SkillInvocation
            {
                SkillKey = skillKey.Trim(),
                Arguments = args
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => element.GetRawText()
    };
}
