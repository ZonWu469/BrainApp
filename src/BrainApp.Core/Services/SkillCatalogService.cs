using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Skills;

namespace BrainApp.Core.Services;

public class SkillCatalogService
{
    private readonly SkillScriptEngine _scriptEngine;
    private readonly SkillExecutor _executor;
    private readonly SkillFetchCache _fetchCache;
    private readonly ProfileRepository _profileRepo;
    private readonly SkillsSettings _skillsSettings;
    private readonly StorageSettings _storageSettings;
    private readonly object _scanLock = new();
    private List<SkillDefinition> _catalog = new();

    private const string SampleSkillFileName = "fetch-page.txt";
    private const string SampleSkillContent = """
        public class FetchPageSkill
        {
            private readonly SkillContext _context;

            public FetchPageSkill(SkillContext context)
            {
                _context = context;
            }

            [Skill("fetch_page", Description = "Fetches readable text from a web page URL")]
            public async Task<string> FetchPage(string url)
            {
                if (string.IsNullOrWhiteSpace(url))
                    return "Error: URL is required.";

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "Error: URL must be http or https.";

                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BrainApp/1.0");

                using var response = await http.GetAsync(uri, _context.CancellationToken);
                if (!response.IsSuccessStatusCode)
                    return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {uri}";

                var html = await response.Content.ReadAsStringAsync(_context.CancellationToken);
                var text = SkillHelpers.ExtractTextFromHtml(html);

                return string.IsNullOrWhiteSpace(text)
                    ? "No readable text found on the page."
                    : text;
            }
        }
        """;

    public SkillCatalogService(
        SkillScriptEngine scriptEngine,
        SkillExecutor executor,
        SkillFetchCache fetchCache,
        ProfileRepository profileRepo,
        IOptions<SkillsSettings> skillsSettings,
        IOptions<StorageSettings> storageSettings)
    {
        _scriptEngine = scriptEngine;
        _executor = executor;
        _fetchCache = fetchCache;
        _profileRepo = profileRepo;
        _skillsSettings = skillsSettings.Value;
        _storageSettings = storageSettings.Value;
        EnsureSkillsFolder();
    }

    public string SkillsFolder => _skillsSettings.GetResolvedSkillsFolder(_storageSettings);

    public IReadOnlyList<SkillDefinition> GetCatalog()
    {
        lock (_scanLock)
        {
            if (_catalog.Count == 0)
                RefreshInternal();
            return _catalog;
        }
    }

    public void Refresh()
    {
        lock (_scanLock)
        {
            _scriptEngine.InvalidateCache();
            RefreshInternal();
        }
    }

    public List<SkillMethodDefinition> GetEnabledMethods(string profileId)
    {
        var enabledFiles = _profileRepo.GetEnabledSkillFiles(profileId);
        var methods = new List<SkillMethodDefinition>();

        foreach (var def in GetCatalog().Where(d => d.IsValid))
        {
            if (!enabledFiles.Contains(def.FileName, StringComparer.OrdinalIgnoreCase))
                continue;

            methods.AddRange(def.Methods);
        }

        return methods;
    }

    public SkillMethodDefinition? ResolveMethod(string profileId, string skillKey)
    {
        var enabled = GetEnabledMethods(profileId);
        return enabled.FirstOrDefault(m =>
            m.FullName.Equals(skillKey, StringComparison.OrdinalIgnoreCase) ||
            m.SkillName.Equals(skillKey, StringComparison.OrdinalIgnoreCase) ||
            $"{m.SkillName} ({m.FullName})".Contains(skillKey, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SkillExecutionResult> ExecuteAsync(
        string profileId,
        SkillInvocation invocation,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var method = ResolveMethod(profileId, invocation.SkillKey);
        if (method == null)
        {
            return new SkillExecutionResult
            {
                Success = false,
                Error = $"Skill '{invocation.SkillKey}' not found or not enabled for this profile"
            };
        }

        if (IsFetchPageSkill(method) && TryGetUrlArgument(invocation, out var url) &&
            _fetchCache.TryGet(sessionId, url, out var cached))
        {
            Log.Information("Skill fetch_page cache hit for session {SessionId}", sessionId);
            return new SkillExecutionResult { Success = true, Output = cached };
        }

        var context = new SkillContext
        {
            ProfileId = profileId,
            SessionId = sessionId ?? string.Empty,
            AppDataFolder = _storageSettings.ResolvedAppDataFolder,
            CancellationToken = ct
        };

        Log.Information("Executing skill {Skill} for profile {ProfileId}", method.FullName, profileId);
        var result = await _executor.ExecuteAsync(method, invocation, context, ct);

        if (result.Success && IsFetchPageSkill(method) && TryGetUrlArgument(invocation, out var fetchedUrl) &&
            !string.IsNullOrWhiteSpace(result.Output) && !result.Output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            _fetchCache.Set(sessionId, fetchedUrl, result.Output);
        }

        return result;
    }

    public bool HasCachedFetchContent(string? sessionId) =>
        _fetchCache.HasCachedContent(sessionId);

    public bool TryGetLatestCachedFetch(string? sessionId, out string content, out string url) =>
        _fetchCache.TryGetLatest(sessionId, out content, out url);

    private static bool IsFetchPageSkill(SkillMethodDefinition method) =>
        method.SkillName.Equals("fetch_page", StringComparison.OrdinalIgnoreCase) ||
        method.FullName.Contains("FetchPage", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetUrlArgument(SkillInvocation invocation, out string url)
    {
        url = string.Empty;
        if (invocation.Arguments.TryGetValue("url", out var raw) && raw != null)
        {
            url = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(url);
        }

        if (invocation.Arguments.Count == 1)
        {
            url = invocation.Arguments.Values.First()?.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(url);
        }

        return false;
    }

    public static string BuildSkillsPromptBlock(IEnumerable<SkillMethodDefinition> methods)
    {
        var list = methods.ToList();
        if (list.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Available skills (use only when the user request requires external data or an action you cannot do from documents alone):");
        sb.AppendLine("STRICT RULES for skill calls:");
        sb.AppendLine("- NEVER emit a JSON skill call unless the user's most recent message contains a full http:// or https:// URL.");
        sb.AppendLine("- If you are unsure whether a skill is needed, answer from documents — do NOT call a skill.");
        sb.AppendLine("- For fetch_page: do NOT call it if the answer is already in the conversation history from a prior turn.");
        sb.AppendLine("- When calling fetch_page, pass the exact full URL from the user message (never invent, shorten, or guess paths).");
        foreach (var m in list)
        {
            var paramList = m.Parameters.Count == 0
                ? "none"
                : string.Join(", ", m.Parameters.Select(p => $"{p.Name}:{p.TypeName}"));
            sb.AppendLine($"- {m.SkillName} (invoke as \"{m.FullName}\"): {m.Description}. Parameters: {paramList}");
        }
        sb.AppendLine();
        sb.AppendLine("To call a skill, respond with ONLY a single JSON object — no markdown fences, no prose before or after:");
        sb.AppendLine("{\"skill\":\"ClassName.MethodName\",\"arguments\":{\"paramName\":\"value\"}}");
        sb.AppendLine("Example: {\"skill\":\"FetchPageSkill.FetchPage\",\"arguments\":{\"url\":\"https://example.com\"}}");
        sb.AppendLine("If no skill is needed, answer in natural language. Do NOT emit JSON.");
        return sb.ToString();
    }

    private void EnsureSkillsFolder()
    {
        var folder = SkillsFolder;
        Directory.CreateDirectory(folder);

        var samplePath = Path.Combine(folder, SampleSkillFileName);
        var needsUpdate = !File.Exists(samplePath) ||
            !string.Equals(File.ReadAllText(samplePath), SampleSkillContent, StringComparison.Ordinal);

        if (needsUpdate)
        {
            File.WriteAllText(samplePath, SampleSkillContent);
            _scriptEngine.InvalidateCache();
            Log.Information("Synced sample skill: {Path}", samplePath);
        }
    }

    private void RefreshInternal()
    {
        EnsureSkillsFolder();
        var folder = SkillsFolder;
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        _catalog = files
            .Select(f => _scriptEngine.CompileFile(f))
            .OrderBy(d => d.FileName)
            .ToList();

        _profileRepo.EnsureSkillDefaultsForAllProfiles(
            _catalog.Where(c => c.IsValid).Select(c => c.FileName));

        Log.Information("Skills catalog refreshed: {Count} files, {Valid} valid",
            _catalog.Count, _catalog.Count(c => c.IsValid));
    }
}
