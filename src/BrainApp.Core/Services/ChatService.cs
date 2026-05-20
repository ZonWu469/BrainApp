using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Skills;

namespace BrainApp.Core.Services;

/// <summary>
/// Chat service implementing the full RAG pipeline with optional skill invocation.
/// </summary>
public class ChatService
{
    private readonly LlamaService _llama;
    private readonly CacheService _cache;
    private readonly RetrievalService _retrieval;
    private readonly ProfileRepository _profileRepo;
    private readonly SkillCatalogService _skillCatalog;
    private readonly LlamaSettings _llamaSettings;
    private const int MaxHistoryTurns = 12;

    public ChatService(
        LlamaService llama,
        CacheService cache,
        RetrievalService retrieval,
        ProfileRepository profileRepo,
        SkillCatalogService skillCatalog,
        IOptions<LlamaSettings> llamaSettings)
    {
        _llama = llama;
        _cache = cache;
        _retrieval = retrieval;
        _profileRepo = profileRepo;
        _skillCatalog = skillCatalog;
        _llamaSettings = llamaSettings.Value;
    }

    public async Task<ChatMessage> AskAsync(
        Profile profile,
        ChatSession? session,
        string question,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var normalized = CacheService.NormalizeQuestion(question);

        var retrieved = await _retrieval.RetrieveAsync(profile.Id, question, ct: ct);
        var citations = BuildCitations(profile.Id, retrieved);

        var cachedAnswer = _cache.GetAnswer(profile.Id, normalized);
        if (cachedAnswer != null)
        {
            sw.Stop();
            Log.Information("Query cache hit for profile {ProfileId}", profile.Id);
            return new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                SessionId = session?.Id ?? "",
                Role = MessageRole.Assistant,
                Content = cachedAnswer,
                Citations = citations,
                FromCache = true,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }

        var history = BuildHistory(session);
        var ragContext = BuildContextBlock(retrieved);
        var enabledSkills = _skillCatalog.GetEnabledMethods(profile.Id);
        var systemPrompt = BuildSystemPrompt(profile.SystemPrompt, enabledSkills);

        string answer;
        bool skillUsed = false;

        if (enabledSkills.Count > 0 && ContainsHttpUrl(question))
        {
            if (TryUseCachedFetchForFollowUp(enabledSkills, question, session?.Id, out var cachedFetch))
            {
                skillUsed = true;
                var finalPrompt = BuildFinalUserPrompt(ragContext, question, cachedFetch);
                answer = await _llama.ChatAsync(profile.SystemPrompt, history, finalPrompt, ct);
            }
            else
            {
                var routingPrompt = BuildRoutingUserPrompt(ragContext, question);
                var routingResponse = await _llama.ChatAsync(systemPrompt, history, routingPrompt, ct);
                var skillCall = SkillCallParser.TryParse(routingResponse);

                if (skillCall != null && IsKnownSkill(skillCall.SkillKey, enabledSkills))
                {
                    skillUsed = true;
                    var skillResult = await _skillCatalog.ExecuteAsync(profile.Id, skillCall, session?.Id, ct);
                    var resultText = skillResult.Success
                        ? skillResult.Output
                        : $"Skill error: {skillResult.Error}";

                    var finalPrompt = BuildFinalUserPrompt(ragContext, question, resultText);
                    answer = await _llama.ChatAsync(profile.SystemPrompt, history, finalPrompt, ct);
                }
                else if (SkillCallParser.LooksLikeSkillJson(routingResponse))
                {
                    Log.Warning("Skill routing produced unusable JSON; falling back to plain RAG. Head: {Head}",
                        Head(routingResponse));
                    var ragPrompt = BuildRagUserPrompt(ragContext, question);
                    answer = await _llama.ChatAsync(profile.SystemPrompt, history, ragPrompt, ct);
                }
                else
                {
                    answer = routingResponse;
                }
            }
        }
        else
        {
            var userPrompt = BuildRagUserPrompt(ragContext, question);
            answer = await _llama.ChatAsync(systemPrompt, history, userPrompt, ct);
        }

        // Final-answer sanitizer: even after the fallback, if something looks like
        // skill JSON slipped through, replace it with a user-friendly message rather
        // than persisting JSON noise into the session.
        answer = SanitizeAnswer(answer);

        sw.Stop();

        var fullPrompt = systemPrompt + string.Join("", history.Select(h => h.Item2)) + question;
        var inputTokens = _llama.CountTokens(fullPrompt);
        var outputTokens = _llama.CountTokens(answer);
        var tokenStats = new TokenStats(inputTokens, outputTokens, inputTokens + outputTokens, _llamaSettings.ContextSize);

        if (!skillUsed)
            _cache.SetAnswer(profile.Id, normalized, answer);

        var message = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            SessionId = session?.Id ?? "",
            Role = MessageRole.Assistant,
            Content = answer,
            Citations = citations,
            FromCache = false,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            Tokens = tokenStats
        };

        if (session != null)
        {
            var userMessage = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = question
            };
            _profileRepo.SaveMessages(session.Id, new[] { userMessage, message });
        }

        Log.Information("Ask completed for profile {ProfileId} in {LatencyMs}ms (skillUsed={SkillUsed})",
            profile.Id, sw.Elapsed.TotalMilliseconds, skillUsed);
        return message;
    }

    public async IAsyncEnumerable<string> AskStreamAsync(
        Profile profile,
        ChatSession? session,
        string question,
        Func<List<ChunkCitation>, Task>? onCitations = null,
        Action<TokenStats>? onTokenStats = null,
        Action<string>? onStatus = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalized = CacheService.NormalizeQuestion(question);

        onStatus?.Invoke("Searching documents...");
        var retrieved = await _retrieval.RetrieveAsync(profile.Id, question, ct: ct);
        var citations = BuildCitations(profile.Id, retrieved);
        onStatus?.Invoke($"Found {citations.Count} source{(citations.Count == 1 ? "" : "s")}");

        if (onCitations != null)
            await onCitations(citations);

        var cachedAnswer = _cache.GetAnswer(profile.Id, normalized);
        if (cachedAnswer != null)
        {
            onStatus?.Invoke("Answer from cache");
            foreach (var word in cachedAnswer.Split(' '))
            {
                yield return word + " ";
                if (ct.IsCancellationRequested) yield break;
            }
            yield break;
        }

        var history = BuildHistory(session);
        var ragContext = BuildContextBlock(retrieved);
        var enabledSkills = _skillCatalog.GetEnabledMethods(profile.Id);
        var systemPrompt = BuildSystemPrompt(profile.SystemPrompt, enabledSkills);

        string userPrompt;
        bool skillUsed = false;

        if (enabledSkills.Count > 0 && ContainsHttpUrl(question))
        {
            if (TryUseCachedFetchForFollowUp(enabledSkills, question, session?.Id, out var cachedFetch))
            {
                skillUsed = true;
                userPrompt = BuildFinalUserPrompt(ragContext, question, cachedFetch);
                systemPrompt = profile.SystemPrompt;
            }
            else
            {
                onStatus?.Invoke("Routing to skill...");
                var routingPrompt = BuildRoutingUserPrompt(ragContext, question);
                var routingResponse = await _llama.ChatAsync(systemPrompt, history, routingPrompt, ct);
                var skillCall = SkillCallParser.TryParse(routingResponse);

                if (skillCall != null && IsKnownSkill(skillCall.SkillKey, enabledSkills))
                {
                    skillUsed = true;
                    onStatus?.Invoke($"Using skill: {skillCall.SkillKey}...");
                    var skillResult = await _skillCatalog.ExecuteAsync(profile.Id, skillCall, session?.Id, ct);
                    var resultText = skillResult.Success
                        ? skillResult.Output
                        : $"Skill error: {skillResult.Error}";
                    userPrompt = BuildFinalUserPrompt(ragContext, question, resultText);
                    systemPrompt = profile.SystemPrompt;
                }
                else if (SkillCallParser.LooksLikeSkillJson(routingResponse))
                {
                    Log.Warning("Skill routing produced unusable JSON; streaming plain RAG fallback. Head: {Head}",
                        Head(routingResponse));
                    userPrompt = BuildRagUserPrompt(ragContext, question);
                    systemPrompt = profile.SystemPrompt;
                }
                else
                {
                    foreach (var word in routingResponse.Split(' '))
                    {
                        yield return word + " ";
                        if (ct.IsCancellationRequested) yield break;
                    }
                    if (!skillUsed)
                        _cache.SetAnswer(profile.Id, normalized, routingResponse);
                    yield break;
                }
            }
        }
        else
        {
            userPrompt = BuildRagUserPrompt(ragContext, question);
        }

        onStatus?.Invoke("Generating answer...");
        var fullPrompt = systemPrompt + string.Join("", history.Select(h => h.Item2)) + userPrompt;
        var inputTokens = _llama.CountTokens(fullPrompt);
        int outputTokenCount = 0;

        var sb = new System.Text.StringBuilder();
        await foreach (var token in _llama.ChatStreamAsync(systemPrompt, history, userPrompt, ct))
        {
            sb.Append(token);
            outputTokenCount++;
            if (ContainsStopSignal(sb))
                break;
            yield return token;
            onTokenStats?.Invoke(new TokenStats(inputTokens, outputTokenCount, inputTokens + outputTokenCount, _llamaSettings.ContextSize));
        }

        onTokenStats?.Invoke(new TokenStats(inputTokens, outputTokenCount, inputTokens + outputTokenCount, _llamaSettings.ContextSize));

        var finalAnswer = SanitizeAnswer(TruncateAtMarker(sb.ToString()));
        if (!skillUsed)
            _cache.SetAnswer(profile.Id, normalized, finalAnswer);
    }

    public async Task<ExtractionResult> ExtractJsonAsync(
        Profile profile,
        string question,
        string jsonSchema,
        CancellationToken ct = default)
    {
        var retrieved = await _retrieval.RetrieveAsync(profile.Id, question, ct: ct);
        var ragContext = BuildContextBlock(retrieved);

        var systemPrompt = profile.SystemPrompt;
        var userPrompt = $"Return ONLY a valid JSON object matching this schema: {jsonSchema}. No explanation, no markdown, just the raw JSON. Use the same language as the extraction task for string values.\n\nContext:\n{ragContext}\n\nExtraction task: {question}";

        var jsonOutput = await _llama.ChatAsync(systemPrompt, new(), userPrompt, ct);

        jsonOutput = jsonOutput.Trim();
        if (jsonOutput.StartsWith("```json"))
            jsonOutput = jsonOutput[7..];
        if (jsonOutput.StartsWith("```"))
            jsonOutput = jsonOutput[3..];
        if (jsonOutput.EndsWith("```"))
            jsonOutput = jsonOutput[..^3];
        jsonOutput = jsonOutput.Trim();

        return new ExtractionResult
        {
            ProfileId = profile.Id,
            Query = question,
            JsonSchema = jsonSchema,
            JsonOutput = jsonOutput,
            Sources = BuildCitations(profile.Id, retrieved)
        };
    }

    public async Task<string> GenerateDigestAsync(Profile profile, CancellationToken ct = default)
    {
        var retrieved = await _retrieval.RetrieveAsync(profile.Id, "Provide a comprehensive summary of all topics covered in the documents", ct: ct);
        var ragContext = BuildContextBlock(retrieved);

        var systemPrompt = profile.SystemPrompt;
        var userPrompt = $"Generate a concise weekly digest summarizing the key topics, findings, and insights from the following documents. Highlight any important updates, action items, or decisions. Respond in the same language as the documents.\n\nContext:\n{ragContext}\n\nProvide a well-structured summary with sections for: Highlights, Key Topics, Action Items, and Questions to Explore.";

        return await _llama.ChatAsync(systemPrompt, new(), userPrompt, ct);
    }

    public async Task<string> GenerateProjectStatusAsync(Profile profile, CancellationToken ct = default)
    {
        var retrieved = await _retrieval.RetrieveAsync(profile.Id, "project status update milestones deliverables blockers", ct: ct);
        var ragContext = BuildContextBlock(retrieved);

        var systemPrompt = profile.SystemPrompt;
        var userPrompt = $"Based on the documents provided, generate a project status report with sections: Overall Status, Completed Milestones, Current Work, Blockers/Issues, and Next Steps. Respond in the same language as the documents.\n\nContext:\n{ragContext}";

        return await _llama.ChatAsync(systemPrompt, new(), userPrompt, ct);
    }

    public async Task<string> GenerateDraftReplyAsync(
        Profile profile,
        string originalEmail,
        string? contextOverride = null,
        CancellationToken ct = default)
    {
        string ragContext;
        if (!string.IsNullOrEmpty(contextOverride))
        {
            ragContext = contextOverride;
        }
        else
        {
            var retrieved = await _retrieval.RetrieveAsync(profile.Id, originalEmail, ct: ct);
            ragContext = BuildContextBlock(retrieved);
        }

        var systemPrompt = profile.SystemPrompt;
        var userPrompt = $"Using the context from the documents, draft a professional reply to this email:\n\n{originalEmail}\n\nThe reply should be concise, reference specific facts from the documents where relevant, and maintain a professional tone. Respond in the same language as the email.\n\nContext:\n{ragContext}";

        return await _llama.ChatAsync(systemPrompt, new(), userPrompt, ct);
    }

    private static string BuildSystemPrompt(string basePrompt, List<SkillMethodDefinition> enabledSkills)
    {
        var skillsBlock = SkillCatalogService.BuildSkillsPromptBlock(enabledSkills);
        if (string.IsNullOrEmpty(skillsBlock))
            return basePrompt;
        return basePrompt + skillsBlock;
    }

    private static string BuildRagUserPrompt(string ragContext, string question) =>
        $"Answer using ONLY the context provided. Always cite sources as [filename, page N]. If the context does not contain enough information, say so clearly. Respond in the same language as the question.\n\nContext:\n{ragContext}\n\nQuestion: {question}";

    private static string BuildRoutingUserPrompt(string ragContext, string question)
    {
        var hasUrl = ContainsHttpUrl(question);
        return
            $"Decide whether to call a skill or answer from documents and conversation history.\n" +
            $"STRICT RULE: emit a JSON skill call ONLY if the user's latest message above contains a full http:// or https:// URL.\n" +
            (hasUrl
                ? "The user message contains a URL — fetch_page is allowed if it would help.\n"
                : "The user message does NOT contain a URL — do NOT emit any JSON. Answer in natural language from the documents and conversation history.\n") +
            $"Do NOT call fetch_page if the user question can be answered from prior messages in this chat.\n" +
            $"If you do emit JSON, output ONLY the JSON object (no markdown fences, no commentary before or after).\n" +
            $"Otherwise answer using the context below and conversation history, citing document sources as [filename, page N].\n\n" +
            $"Context:\n{ragContext}\n\nQuestion: {question}";
    }

    private static bool IsKnownSkill(string skillKey, List<SkillMethodDefinition> enabledSkills)
    {
        if (string.IsNullOrWhiteSpace(skillKey)) return false;
        return enabledSkills.Any(m =>
            string.Equals(m.FullName, skillKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.SkillName, skillKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string Head(string s) => s.Length > 200 ? s[..200] : s;

    // If a response still looks like raw skill JSON (or is dominated by it), replace
    // with a friendly fallback so the user never sees the protocol leak.
    private static string SanitizeAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return answer;
        var trimmed = answer.Trim();
        if (SkillCallParser.LooksLikeSkillJson(trimmed))
        {
            // Only intervene if the JSON is essentially the whole message — a short
            // answer that merely mentions "skill" shouldn't get nuked.
            if (trimmed.StartsWith('{') || trimmed.StartsWith("```"))
            {
                return "I couldn't produce a useful answer for that. Try rephrasing — " +
                       "and if you wanted me to fetch a URL, paste the full address.";
            }
        }
        return answer;
    }

    private static string BuildFinalUserPrompt(string ragContext, string question, string skillResult) =>
        $"A skill was executed and returned the following result:\n\n{skillResult}\n\n" +
        $"Use this result together with the document context to answer the user. Cite document sources as [filename, page N] where relevant.\n\n" +
        $"Context:\n{ragContext}\n\nOriginal question: {question}";

    private static List<(MessageRole, string)> BuildHistory(ChatSession? session)
    {
        var history = new List<(MessageRole, string)>();
        if (session == null) return history;

        var recentMessages = session.Messages
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryTurns * 2)
            .Reverse()
            .ToList();

        foreach (var msg in recentMessages)
            history.Add((msg.Role, msg.Content));

        return history;
    }

    private List<ChunkCitation> BuildCitations(string profileId, List<RetrievedChunk> retrieved)
    {
        var documents = _profileRepo.GetDocuments(profileId);
        var docPathLookup = documents.ToDictionary(d => d.Id, d => d.FilePath);

        return retrieved.Select(r =>
        {
            docPathLookup.TryGetValue(r.Chunk.DocumentId, out var filePath);
            return new ChunkCitation
            {
                FileName = r.Chunk.FileName,
                FilePath = filePath ?? string.Empty,
                PageNumber = r.Chunk.PageNumber,
                Excerpt = r.Chunk.Text.Length > 120 ? r.Chunk.Text[..120] + "..." : r.Chunk.Text,
                RelevanceScore = r.Score
            };
        }).ToList();
    }

    private bool TryUseCachedFetchForFollowUp(
        List<SkillMethodDefinition> enabledSkills,
        string question,
        string? sessionId,
        out string cachedContent)
    {
        cachedContent = string.Empty;
        if (ContainsHttpUrl(question))
            return false;

        var hasFetchPage = enabledSkills.Any(m =>
            m.SkillName.Equals("fetch_page", StringComparison.OrdinalIgnoreCase) ||
            m.FullName.Contains("FetchPage", StringComparison.OrdinalIgnoreCase));

        if (!hasFetchPage)
            return false;

        return _skillCatalog.TryGetLatestCachedFetch(sessionId, out cachedContent, out _);
    }

    public static bool ContainsHttpUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var httpIdx = text.IndexOf("http", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0)
                return false;

            var end = httpIdx;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ')' && text[end] != ']')
                end++;

            var candidate = text[httpIdx..end].TrimEnd('.', ',', ';', '"', '\'');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return true;

            searchFrom = end;
        }

        return false;
    }

    private static string BuildContextBlock(List<RetrievedChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var r = chunks[i];
            sb.AppendLine($"[{i + 1}] [{r.Chunk.FileName}, page {r.Chunk.PageNumber}]");
            sb.AppendLine(r.Chunk.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static readonly string[] StopMarkers = {
        "<|im_start|>", "<|im_end|>", "<|end|>", "<|endoftext|>", "<|eot_id|>"
    };

    private static bool ContainsStopSignal(System.Text.StringBuilder sb)
    {
        var text = sb.ToString();
        foreach (var marker in StopMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string TruncateAtMarker(string raw)
    {
        var result = raw;
        foreach (var marker in StopMarkers)
        {
            var idx = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                result = result[..idx];
        }
        return result.Trim();
    }
}
