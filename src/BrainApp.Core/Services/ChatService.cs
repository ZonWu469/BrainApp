using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;
using BrainApp.Core.Skills;

// MAINTAINER NOTE: bump CacheService.PromptVersion whenever you change
// BuildSystemPrompt, BuildRagUserPrompt, the language-pin helpers below, or any
// chat-template builder in LlamaService. The query cache key incorporates that
// constant; without a bump, old answers generated under the previous prompts
// will keep being served until their 30-min TTL expires.
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

        // Cache scope: per (profileId, sessionId). Bypass entirely when there is no
        // session, or when the session already has prior turns — in a live chat we
        // always regenerate so answers reflect the evolved conversation.
        var cachedAnswer = CanReadCache(session)
            ? _cache.GetAnswer(profile.Id, session!.Id, normalized)
            : null;
        Log.Information("Chat: profile={ProfileId} session={SessionId} cacheHit={Hit} citations={Count}",
            profile.Id, session?.Id ?? "(none)", cachedAnswer != null, citations.Count);
        if (cachedAnswer != null)
        {
            sw.Stop();
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

        var rawHistory = BuildHistory(session);
        var enabledSkills = _skillCatalog.GetEnabledMethods(profile.Id);
        var systemPrompt = BuildSystemPrompt(profile.SystemPrompt, enabledSkills);
        var (history, ragContext) = PrepareContext(systemPrompt, rawHistory, retrieved, question);

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

        if (!skillUsed && session != null)
            _cache.SetAnswer(profile.Id, session.Id, normalized, answer);

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
        var indexedChunks = _retrieval.GetChunkCount(profile.Id);
        if (citations.Count == 0 && indexedChunks > 0)
            onStatus?.Invoke($"No matching passages ({indexedChunks} chunks indexed)");
        else
            onStatus?.Invoke($"Found {citations.Count} source{(citations.Count == 1 ? "" : "s")} ({indexedChunks} chunks indexed)");

        if (onCitations != null)
            await onCitations(citations);

        var cachedAnswer = CanReadCache(session)
            ? _cache.GetAnswer(profile.Id, session!.Id, normalized)
            : null;
        Log.Information("Chat: profile={ProfileId} session={SessionId} cacheHit={Hit} citations={Count}",
            profile.Id, session?.Id ?? "(none)", cachedAnswer != null, citations.Count);
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

        var rawHistory = BuildHistory(session);
        var enabledSkills = _skillCatalog.GetEnabledMethods(profile.Id);
        var systemPrompt = BuildSystemPrompt(profile.SystemPrompt, enabledSkills);
        var (history, ragContext) = PrepareContext(systemPrompt, rawHistory, retrieved, question);

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
                    if (!skillUsed && session != null)
                        _cache.SetAnswer(profile.Id, session.Id, normalized, routingResponse);
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

        var rawAccum = new System.Text.StringBuilder();
        var pending = new System.Text.StringBuilder();
        var cleanAccum = new System.Text.StringBuilder();
        bool inThink = false;
        const string OpenTag = "<think>";
        const string CloseTag = "</think>";
        int holdBack = Math.Max(OpenTag.Length, CloseTag.Length);

        await foreach (var token in _llama.ChatStreamAsync(systemPrompt, history, userPrompt, ct))
        {
            rawAccum.Append(token);
            outputTokenCount++;
            if (ContainsStopSignal(rawAccum))
                break;

            pending.Append(token);

            while (true)
            {
                var text = pending.ToString();
                if (inThink)
                {
                    var closeIdx = text.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        pending.Clear();
                        pending.Append(text[(closeIdx + CloseTag.Length)..]);
                        inThink = false;
                        continue;
                    }
                    if (text.Length > holdBack)
                    {
                        pending.Clear();
                        pending.Append(text[^holdBack..]);
                    }
                    break;
                }
                else
                {
                    var openIdx = text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                    if (openIdx >= 0)
                    {
                        var safe = text[..openIdx];
                        if (safe.Length > 0)
                        {
                            yield return safe;
                            cleanAccum.Append(safe);
                        }
                        pending.Clear();
                        pending.Append(text[(openIdx + OpenTag.Length)..]);
                        inThink = true;
                        continue;
                    }
                    if (text.Length > holdBack)
                    {
                        var safeLen = text.Length - holdBack;
                        var safe = text[..safeLen];
                        yield return safe;
                        cleanAccum.Append(safe);
                        pending.Clear();
                        pending.Append(text[safeLen..]);
                    }
                    break;
                }
            }

            onTokenStats?.Invoke(new TokenStats(inputTokens, outputTokenCount, inputTokens + outputTokenCount, _llamaSettings.ContextSize));
        }

        if (!inThink && pending.Length > 0)
        {
            var tail = pending.ToString();
            yield return tail;
            cleanAccum.Append(tail);
        }

        onTokenStats?.Invoke(new TokenStats(inputTokens, outputTokenCount, inputTokens + outputTokenCount, _llamaSettings.ContextSize));

        var finalAnswer = SanitizeAnswer(TruncateAtMarker(cleanAccum.ToString()));
        if (!skillUsed && session != null)
            _cache.SetAnswer(profile.Id, session.Id, normalized, finalAnswer);
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

    private const string LanguageDirectiveSuffix =
        "\n\nAlways respond in the same language as the user's most recent message. " +
        "Output the answer once, in one language only — never include translations or " +
        "restate it in another language.";

    private static string BuildSystemPrompt(string basePrompt, List<SkillMethodDefinition> enabledSkills)
    {
        var skillsBlock = SkillCatalogService.BuildSkillsPromptBlock(enabledSkills);
        var prefix = string.IsNullOrEmpty(skillsBlock) ? basePrompt : basePrompt + skillsBlock;
        return prefix + LanguageDirectiveSuffix;
    }

    private static readonly HashSet<string> ItalianMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "il", "la", "lo", "gli", "le", "di", "da", "del", "della", "dei", "degli", "delle",
        "che", "è", "sono", "non", "con", "per", "una", "uno", "sul", "sulla",
        "questo", "questa", "quale", "come", "dove", "quando", "perché", "perche",
        "ma", "anche", "molto", "qui", "qua", "cosa", "tutto", "essere", "fare",
        "riassumi", "spiega", "elenca", "descrivi"
    };

    private static string DetectLanguageDirective(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;

        if (question.IndexOfAny(new[] { 'à', 'è', 'é', 'ì', 'ò', 'ù', 'À', 'È', 'É', 'Ì', 'Ò', 'Ù' }) >= 0)
            return "Rispondi in italiano. ";

        var tokens = question.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '\'', '"', '(', ')' },
                   StringSplitOptions.RemoveEmptyEntries);
        int hits = 0;
        foreach (var t in tokens)
        {
            if (ItalianMarkers.Contains(t))
            {
                hits++;
                if (hits >= 2) return "Rispondi in italiano. ";
            }
        }
        return string.Empty;
    }

    private static string BuildRagUserPrompt(string ragContext, string question)
    {
        var langPin = DetectLanguageDirective(question);
        return $"{langPin}Answer using ONLY the context provided. Cite sources exactly as shown in the context header — either [filename, page N] or [filename, section N]. If the context does not contain enough information, say so clearly.\n\nContext:\n{ragContext}\n\nQuestion: {question}\n\n{langPin}Write your entire answer in the SAME language as the Question above. Do NOT translate or repeat the answer in any other language. Output the answer once, in one language only.";
    }

    private static string BuildRoutingUserPrompt(string ragContext, string question)
    {
        var hasUrl = ContainsHttpUrl(question);
        var langPin = DetectLanguageDirective(question);
        return
            $"Decide whether to call a skill or answer from documents and conversation history.\n" +
            $"STRICT RULE: emit a JSON skill call ONLY if the user's latest message above contains a full http:// or https:// URL.\n" +
            (hasUrl
                ? "The user message contains a URL — fetch_page is allowed if it would help.\n"
                : "The user message does NOT contain a URL — do NOT emit any JSON. Answer in natural language from the documents and conversation history.\n") +
            $"Do NOT call fetch_page if the user question can be answered from prior messages in this chat.\n" +
            $"If you do emit JSON, output ONLY the JSON object (no markdown fences, no commentary before or after).\n" +
            $"Otherwise answer using the context below and conversation history, citing document sources exactly as shown in the context header — either [filename, page N] or [filename, section N].\n\n" +
            $"Context:\n{ragContext}\n\nQuestion: {question}\n\n{langPin}";
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
        $"Use this result together with the document context to answer the user. Cite document sources exactly as shown in the context header — either [filename, page N] or [filename, section N] — where relevant.\n\n" +
        $"Context:\n{ragContext}\n\nOriginal question: {question}";

    // Cache reads are permitted only on the very first turn of a real session.
    // Sessionless callers (no ChatSession) never read the cache; ongoing chats
    // (history non-empty) always regenerate so the answer reflects the current
    // conversation. Writes are still allowed for any real session (see call sites).
    private static bool CanReadCache(ChatSession? session)
        => session != null && session.Messages.Count == 0;

    private List<(MessageRole, string)> BuildHistory(ChatSession? session)
    {
        var history = new List<(MessageRole, string)>();
        if (session == null) return history;

        // Walk newest -> oldest, accumulating an estimated token cost. Stop when the
        // running total would exceed HistoryTokenBudget. Always keep at least the most
        // recent message even if it alone blows the budget (layer B will trim further).
        var newestFirst = session.Messages
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryTurns * 2)
            .ToList();

        int budget = _llamaSettings.HistoryTokenBudget;
        int runningTokens = 0;
        var kept = new List<ChatMessage>();
        foreach (var msg in newestFirst)
        {
            int est = EstimateTokens(msg.Content);
            if (kept.Count > 0 && runningTokens + est > budget)
                break;
            kept.Add(msg);
            runningTokens += est;
        }

        kept.Reverse();
        foreach (var msg in kept)
            history.Add((msg.Role, msg.Content));

        return history;
    }

    // Fast char-based token estimate (~4 chars/token for typical text). Avoids the
    // per-call CreateContext cost of LlamaService.CountTokens during shaping; layer B
    // uses the real tokenizer for the final budget verification.
    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

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
                CitationUnit = r.Chunk.IsPaginated ? "page" : "section",
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
        var ordered = ReorderForEdges(chunks);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            var unit = r.Chunk.IsPaginated ? "page" : "section";
            sb.AppendLine($"[{i + 1}] [{r.Chunk.FileName}, {unit} {r.Chunk.PageNumber}]");
            sb.AppendLine(r.Chunk.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Mitigate "lost-in-the-middle": given chunks sorted by relevance (best first), place the
    /// most relevant at the start and the next-most at the very end, with the rest in between.
    /// Small models attend most strongly to the edges of the context.
    /// </summary>
    internal static List<RetrievedChunk> ReorderForEdges(List<RetrievedChunk> chunks)
    {
        if (chunks.Count <= 2) return chunks;
        var front = new List<RetrievedChunk>();
        var back = new List<RetrievedChunk>();
        for (int i = 0; i < chunks.Count; i++)
            (i % 2 == 0 ? front : back).Add(chunks[i]);
        back.Reverse();
        front.AddRange(back);
        return front;
    }

    // A.1 + A.2 + A.3 — always-on context shaping. Returns a copy of `retrieved` with
    // (a) low-score and below-dropoff chunks removed,
    // (b) near-duplicate chunks removed,
    // (c) each surviving chunk's Text truncated to MaxChunkTokens.
    // Original retrieved list is not mutated (we clone DocumentChunk for text trimming).
    private List<RetrievedChunk> ShapeChunks(List<RetrievedChunk> retrieved)
    {
        if (retrieved.Count == 0) return retrieved;

        // A.1 score filter + dropoff
        double topScore = retrieved[0].Score;
        double minAbsolute = _llamaSettings.MinChunkScore;
        double minRelative = topScore * _llamaSettings.ScoreDropoffRatio;
        var byScore = new List<RetrievedChunk>();
        foreach (var r in retrieved)
        {
            if (r.Score < minAbsolute) break;          // list is sorted desc
            if (byScore.Count > 0 && r.Score < minRelative) break;
            byScore.Add(r);
        }
        if (byScore.Count == 0) byScore.Add(retrieved[0]); // never produce empty context

        // A.2 near-duplicate dedup (same (DocId, Page) OR first-200-char prefix already kept)
        var deduped = new List<RetrievedChunk>();
        var keyFingerprints = new List<string>();
        foreach (var r in byScore)
        {
            string key = r.Chunk.DocumentId + "|" + r.Chunk.PageNumber;
            string head = NormalizeForDedup(r.Chunk.Text, 200);
            bool dup = false;
            for (int i = 0; i < deduped.Count; i++)
            {
                if (keyFingerprints[i].StartsWith(key + "||", StringComparison.Ordinal))
                {
                    dup = true; break;
                }
                var keptHead = keyFingerprints[i][(keyFingerprints[i].IndexOf("||", StringComparison.Ordinal) + 2)..];
                if (head.Length >= 40 && (keptHead.Contains(head, StringComparison.Ordinal) || head.Contains(keptHead, StringComparison.Ordinal)))
                {
                    dup = true; break;
                }
            }
            if (dup) continue;
            deduped.Add(r);
            keyFingerprints.Add(key + "||" + head);
        }

        // A.3 per-chunk token cap (estimate-based: ~4 chars/token)
        int maxChars = Math.Max(200, _llamaSettings.MaxChunkTokens * 4);
        var shaped = new List<RetrievedChunk>(deduped.Count);
        foreach (var r in deduped)
            shaped.Add(CapChunk(r, maxChars));

        return shaped;
    }

    private static RetrievedChunk CapChunk(RetrievedChunk r, int maxChars)
    {
        var text = r.Chunk.Text ?? string.Empty;
        if (text.Length <= maxChars) return r;

        // Truncate at the last sentence boundary before maxChars, else hard-cut.
        int cut = maxChars;
        int sentenceEnd = text.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, Math.Min(maxChars - 1, text.Length - 1));
        if (sentenceEnd > maxChars / 2) cut = sentenceEnd + 1;
        var truncated = text[..cut].TrimEnd() + " …[trimmed]";

        return new RetrievedChunk
        {
            Chunk = new DocumentChunk
            {
                Id = r.Chunk.Id,
                DocumentId = r.Chunk.DocumentId,
                ProfileId = r.Chunk.ProfileId,
                FileName = r.Chunk.FileName,
                Text = truncated,
                ChunkIndex = r.Chunk.ChunkIndex,
                PageNumber = r.Chunk.PageNumber,
                IsPaginated = r.Chunk.IsPaginated,
                Embedding = r.Chunk.Embedding
            },
            Score = r.Score,
            SemanticScore = r.SemanticScore
        };
    }

    private static string NormalizeForDedup(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var head = s.Length > max ? s[..max] : s;
        var sb = new System.Text.StringBuilder(head.Length);
        bool lastSpace = false;
        foreach (var c in head)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                lastSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    // Layer B — reactive trim. Input budget reserves room for generation via MaxTokens at
    // inference time; do not subtract MaxTokens from the input budget here.
    internal (List<(MessageRole, string)> History, List<RetrievedChunk> Chunks) TrimToBudget(
        string systemPrompt,
        List<(MessageRole, string)> history,
        List<RetrievedChunk> chunks,
        string question)
    {
        int budget = _llamaSettings.ContextSize - _llamaSettings.SafetyMargin;
        if (budget <= 0) return (history, chunks);

        int ragReservation = chunks.Count > 0
            ? Math.Min(_llamaSettings.RagTokenBudget, Math.Max(budget / 3, 400))
            : 0;

        int Measure(List<(MessageRole, string)> h, List<RetrievedChunk> c)
        {
            var rag = BuildContextBlock(c);
            var concat = systemPrompt + string.Join("\n", h.Select(t => t.Item2)) + rag + question;
            return _llama.CountTokens(concat);
        }

        int MeasureRagOnly(List<RetrievedChunk> c) =>
            _llama.CountTokens(BuildContextBlock(c));

        var workHist = new List<(MessageRole, string)>(history);
        var workChunks = new List<RetrievedChunk>(chunks);

        int total = Measure(workHist, workChunks);
        if (total <= budget) return (workHist, workChunks);

        int initialChunks = workChunks.Count;
        int initialHist = workHist.Count;

        // B.1 drop oldest history pairs (keep at least the last user+assistant pair)
        while (total > budget && workHist.Count > 2)
        {
            int drop = Math.Min(2, workHist.Count - 2);
            workHist.RemoveRange(0, drop);
            total = Measure(workHist, workChunks);
        }

        // B.2 drop lowest-score (trailing) chunks but keep enough RAG to meet reservation
        while (total > budget && workChunks.Count > 1)
        {
            if (ragReservation > 0 && workChunks.Count <= 1)
                break;
            if (ragReservation > 0 && MeasureRagOnly(workChunks) <= ragReservation && workChunks.Count <= 2)
                break;
            workChunks.RemoveAt(workChunks.Count - 1);
            total = Measure(workHist, workChunks);
        }

        // B.3 tighten per-chunk cap on remaining chunks (halve until fits or min reached)
        int tighterCap = Math.Max(200, _llamaSettings.MaxChunkTokens * 4);
        while (total > budget && tighterCap > 400)
        {
            if (ragReservation > 0 && MeasureRagOnly(workChunks) <= ragReservation)
                break;
            tighterCap /= 2;
            for (int i = 0; i < workChunks.Count; i++)
                workChunks[i] = CapChunk(workChunks[i], tighterCap);
            total = Measure(workHist, workChunks);
        }

        if (total > budget)
        {
            Log.Warning(
                "Reactive trim: prompt still ~{Tokens} tokens over budget {Budget} (hist {Hist}/{HistInit}, chunks {Chunks}/{ChunksInit}, ragReservation {RagRes})",
                total - budget, budget, workHist.Count, initialHist, workChunks.Count, initialChunks, ragReservation);
        }
        else
        {
            Log.Information(
                "Reactive trim: kept {Hist}/{HistInit} history msgs, {Chunks}/{ChunksInit} chunks (~{Tokens} tokens, budget {Budget})",
                workHist.Count, initialHist, workChunks.Count, initialChunks, total, budget);
        }

        return (workHist, workChunks);
    }

    // Combined entry: shape + reactive trim. Returns (history, ragContext) ready to feed.
    private (List<(MessageRole, string)> History, string RagContext) PrepareContext(
        string systemPrompt,
        List<(MessageRole, string)> history,
        List<RetrievedChunk> retrieved,
        string question)
    {
        var shaped = ShapeChunks(retrieved);
        Log.Information("Context shaped: {Kept}/{Total} chunks, {HistTurns} history msgs (~{Tokens} est tokens)",
            shaped.Count, retrieved.Count, history.Count,
            EstimateTokens(systemPrompt) + history.Sum(h => EstimateTokens(h.Item2)) + shaped.Sum(c => EstimateTokens(c.Chunk.Text)) + EstimateTokens(question));

        var (trimmedHist, trimmedChunks) = TrimToBudget(systemPrompt, history, shaped, question);
        return (trimmedHist, BuildContextBlock(trimmedChunks));
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
