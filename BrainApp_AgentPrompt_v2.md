# BrainApp — AI Coding Agent Prompt v2
## Cross-Platform Desktop Knowledge Base — Fully Local, No Network, No Ollama

---

## YOUR ROLE

You are a senior C# software engineer and architect. You will build **BrainApp** —
a cross-platform desktop application for 100% offline, local document Q&A using
multiple isolated "brain" profiles. The LLM runs directly in-process via
**LLamaSharp**, loading GGUF model files from disk. There is no Ollama, no HTTP
inference server, no network dependency of any kind after the initial model download.

You work autonomously: write code, run `dotnet build`, fix all errors, iterate until
each phase is complete and all tests pass before moving to the next phase.

**Never ask for clarification mid-phase.** When a decision is ambiguous, make the most
reasonable engineering choice, document it in a comment, and continue.

---

## PROJECT OVERVIEW

| Attribute        | Value                                                      |
|------------------|------------------------------------------------------------|
| App name         | BrainApp                                                   |
| Target platforms | Windows (primary), macOS, Linux                            |
| UI framework     | **Avalonia UI 11** (cross-platform, never WPF/WinForms)    |
| Runtime          | .NET 8                                                     |
| LLM runtime      | **LLamaSharp** — loads GGUF files directly, in-process     |
| Default model    | Qwen 2.5 3B Q4_K_M (`qwen2.5-3b-instruct-q4_k_m.gguf`)    |
| Embedding model  | `nomic-embed-text-v1.5.Q4_K_M.gguf` (same LLamaSharp)     |
| Storage          | SQLite via Microsoft.Data.Sqlite                           |
| Architecture     | 3-project solution: Core · API · Desktop                   |
| Network          | **Zero** — all inference is in-process, file-based         |

### What the app does

- Users create named **profiles** ("brains"), each an isolated knowledge base
- Each profile ingests documents (PDF, DOCX, TXT, HTML, MD, images via OCR)
- Documents are chunked, embedded, and stored in a per-profile vector index
- Users ask natural-language questions; the app retrieves relevant chunks,
  feeds them to the local LLM, returns cited answers with source references
- A REST API exposes all functionality for external integrations and automation
- **Everything runs locally** — GGUF model files on disk, inference in-process

### Business features (implement all)

- **Project intelligence:** status lookup, open action items, deadline extraction,
  decision audit trail — answers sourced from emails + meeting notes + contracts
- **Contract intelligence:** clause finder, expiry alerts, contract comparison,
  compliance gap detection across a portfolio of contracts
- **Email intelligence:** thread summariser, unanswered question tracker,
  client sentiment shift detection, context-aware draft reply generation
- **Finance intelligence:** overdue invoice tracker, revenue by client/project,
  cost and margin analysis across invoices and contracts
- **Agent features:** structured JSON extraction, scheduled digest via API,
  cross-profile query via API, multilingual Q&A (Italian, French, German, Spanish)
- **Per-profile personas:** each brain has a custom system prompt that shapes
  the AI's tone and output format (formal legal, casual support, numeric finance)

### Cross-platform constraints (enforce throughout)

- Use **Avalonia UI 11** — never WPF, WinForms, or MAUI
- Use `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`
  for all user data paths — never hardcode OS-specific paths
- Use `Path.Combine()` everywhere — never string-concatenate paths
- File dialogs: use `StorageProvider.OpenFilePickerAsync` — never `Microsoft.Win32`
- Fonts: Avalonia system font fallback — never Windows-specific font APIs
- LLamaSharp backend: select automatically per OS (CUDA on Windows/Linux with
  compatible GPU, Metal on macOS, CPU fallback everywhere)

---

## SOLUTION STRUCTURE

```
BrainApp/
├── BrainApp.sln
├── models/                              ← GGUF files live here (gitignored)
│   ├── qwen2.5-3b-instruct-q4_k_m.gguf ← default chat model
│   ├── nomic-embed-text-v1.5.Q4_K_M.gguf ← default embedding model
│   └── .gitkeep
├── appsettings.json                     ← shared config, copied to all outputs
├── MODELS.md                            ← download instructions for GGUF files
├── README.md
├── publish.sh
├── publish.ps1
├── src/
│   ├── BrainApp.Core/
│   │   ├── BrainApp.Core.csproj
│   │   ├── Config/
│   │   │   └── AppSettings.cs
│   │   ├── Models/
│   │   │   └── Models.cs
│   │   └── Services/
│   │       ├── CacheService.cs
│   │       ├── ChatService.cs
│   │       ├── IngestionService.cs
│   │       ├── LlamaService.cs          ← LLamaSharp wrapper (replaces OllamaService)
│   │       ├── ProfileRepository.cs
│   │       └── RetrievalService.cs
│   ├── BrainApp.Api/
│   │   ├── BrainApp.Api.csproj
│   │   └── Program.cs
│   └── BrainApp.Desktop/
│       ├── BrainApp.Desktop.csproj
│       ├── App.axaml
│       ├── App.axaml.cs
│       ├── Assets/
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs
│       │   ├── ChatViewModel.cs
│       │   ├── DocumentsViewModel.cs
│       │   └── SettingsViewModel.cs
│       └── Views/
│           ├── MainWindow.axaml
│           ├── MainWindow.axaml.cs
│           ├── ProfilesPanel.axaml
│           ├── ChatView.axaml
│           ├── DocumentsView.axaml
│           └── SettingsWindow.axaml
└── tests/
    └── BrainApp.Tests/
        ├── BrainApp.Tests.csproj
        ├── CacheServiceTests.cs
        └── RetrievalServiceTests.cs
```

---

## SHARED CONFIGURATION — appsettings.json

```json
{
  "LLama": {
    "ModelsFolder": "models",
    "ChatModelFile": "qwen2.5-3b-instruct-q4_k_m.gguf",
    "EmbeddingModelFile": "nomic-embed-text-v1.5.Q4_K_M.gguf",
    "ContextSize": 8192,
    "GpuLayerCount": 99,
    "Threads": 0,
    "BatchSize": 512,
    "Temperature": 0.1,
    "MaxTokens": 1024,
    "AntiPrompts": ["User:", "Human:", "<|end|>", "<|endoftext|>"]
  },
  "Cache": {
    "EmbeddingTtlMinutes": 1440,
    "QueryTtlMinutes": 30,
    "MaxEmbeddingEntries": 50000,
    "MaxQueryEntries": 500,
    "EnableQueryCache": true,
    "EnableEmbeddingCache": true
  },
  "Retrieval": {
    "TopK": 6,
    "ChunkSize": 800,
    "ChunkOverlap": 120,
    "MinChunkLength": 60,
    "SemanticWeight": 0.7,
    "KeywordWeight": 0.3,
    "MinRelevanceScore": 0.1
  },
  "Storage": {
    "AppDataFolder": "",
    "MaxDocumentsPerProfile": 500,
    "MaxFileSizeMb": 50
  },
  "Api": {
    "Port": 5199,
    "EnableSwagger": true,
    "ApiKey": "change-me-in-production",
    "RateLimitPerMinute": 60
  }
}
```

**Config resolution rules:**

- `LLama.ModelsFolder` when relative → resolve against `AppContext.BaseDirectory`
  then fall back to `Path.Combine(AppDataFolder, "models")`
- `LLama.GpuLayerCount = 99` → offload all layers to GPU; set to `0` for CPU-only
- `LLama.Threads = 0` → auto-detect (use `Environment.ProcessorCount`)
- `Storage.AppDataFolder` when empty → `Environment.GetFolderPath(
  Environment.SpecialFolder.ApplicationData) + "/BrainApp"`

**Changing the model (document prominently in README and in-app settings):**

To swap models, edit `appsettings.json → LLama → ChatModelFile`. No recompile.
Place the new `.gguf` file in the `models/` folder.

Recommended models for 2–4 GB VRAM:

| File name                                  | Size   | Quality | Speed (4GB GPU) |
|--------------------------------------------|--------|---------|-----------------|
| `qwen2.5-3b-instruct-q4_k_m.gguf`          | 2.0 GB | 73/100  | ~55 tok/s ← DEFAULT |
| `qwen2.5-3b-instruct-q8_0.gguf`            | 3.3 GB | 77/100  | ~35 tok/s       |
| `llama-3.2-3b-instruct-q4_k_m.gguf`        | 2.0 GB | 70/100  | ~48 tok/s       |
| `gemma-3-1b-it-q4_k_m.gguf`                | 0.8 GB | 58/100  | ~90 tok/s       |
| `phi-3.5-mini-instruct-q4_k_m.gguf`        | 2.4 GB | 72/100  | ~45 tok/s       |

For 8+ GB VRAM (better quality):

| File name                                  | Size   | Quality | Speed (8GB GPU) |
|--------------------------------------------|--------|---------|-----------------|
| `qwen2.5-7b-instruct-q4_k_m.gguf`          | 4.5 GB | 84/100  | ~30 tok/s       |
| `mistral-nemo-instruct-2407-q4_k_m.gguf`   | 7.1 GB | 88/100  | ~20 tok/s       |
| `llama-3.1-8b-instruct-q4_k_m.gguf`        | 4.9 GB | 85/100  | ~22 tok/s       |

Download source: https://huggingface.co — search for the filename, pick a trusted
repo (bartowski, TheBloke, lmstudio-community are reliable quantization sources).

---

## NUGET PACKAGES (authoritative list)

### BrainApp.Core
```xml
<!-- LLamaSharp — local GGUF inference, no network required -->
<PackageReference Include="LLamaSharp" Version="0.18.0" />

<!-- LLamaSharp backends — include all three; LLamaSharp selects at runtime -->
<PackageReference Include="LLamaSharp.Backend.Cpu"      Version="0.18.0" />
<PackageReference Include="LLamaSharp.Backend.Cuda11"   Version="0.18.0" />
<PackageReference Include="LLamaSharp.Backend.Cuda12"   Version="0.18.0" />

<!-- Document parsing -->
<PackageReference Include="UglyToad.PdfPig"             Version="0.1.9" />
<PackageReference Include="DocumentFormat.OpenXml"      Version="3.0.2" />
<PackageReference Include="Tesseract"                   Version="5.2.0" />
<PackageReference Include="HtmlAgilityPack"             Version="1.11.61" />
<PackageReference Include="Markdig"                     Version="0.37.0" />

<!-- Storage and config -->
<PackageReference Include="Microsoft.Data.Sqlite"                    Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Caching.Memory"      Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json"  Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Options"             Version="8.0.0" />

<!-- Logging -->
<PackageReference Include="Serilog"                Version="4.0.1" />
<PackageReference Include="Serilog.Sinks.File"     Version="6.0.0" />
<PackageReference Include="Serilog.Sinks.Console"  Version="6.0.0" />
```

> **macOS note:** LLamaSharp.Backend.Metal is separate. Add conditionally:
> ```xml
> <PackageReference Include="LLamaSharp.Backend.Metal" Version="0.18.0"
>   Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
> ```

### BrainApp.Api
```xml
<PackageReference Include="Swashbuckle.AspNetCore"    Version="6.7.3" />
<PackageReference Include="AspNetCoreRateLimit"       Version="5.0.0" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
```

### BrainApp.Desktop
```xml
<PackageReference Include="Avalonia"                  Version="11.1.3" />
<PackageReference Include="Avalonia.Desktop"          Version="11.1.3" />
<PackageReference Include="Avalonia.Themes.Fluent"    Version="11.1.3" />
<PackageReference Include="Avalonia.Fonts.Inter"      Version="11.1.3" />
<PackageReference Include="CommunityToolkit.Mvvm"     Version="8.3.2" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
```

### BrainApp.Tests
```xml
<PackageReference Include="xunit"                           Version="2.9.0" />
<PackageReference Include="xunit.runner.visualstudio"       Version="2.8.2" />
<PackageReference Include="Microsoft.NET.Test.Sdk"          Version="17.11.1" />
<PackageReference Include="Moq"                             Version="4.20.70" />
<PackageReference Include="Microsoft.Extensions.Options"    Version="8.0.0" />
```

---

## DOMAIN MODELS

```csharp
public class Profile {
    public string Id { get; set; }            // 8-char hex
    public string Name { get; set; }
    public string Description { get; set; }
    public string Color { get; set; }         // hex, e.g. "#534AB7"
    public string Icon { get; set; }          // Tabler icon name
    public string SystemPrompt { get; set; }
    public string ModelOverride { get; set; } // filename override, empty = global config
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ProfileStats Stats { get; set; }

    public const string DefaultSystemPrompt =
        "You are a precise knowledge base assistant. Answer questions using ONLY " +
        "the provided document excerpts. Always cite sources as [filename, page N]. " +
        "If the documents do not contain enough information, say so clearly. " +
        "Never invent facts not present in the provided context.";
}

public class ProfileStats {
    public int DocumentCount { get; set; }
    public int ChunkCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastIndexed { get; set; }
    public int QuestionsAnswered { get; set; }
}

public class Document {
    public string Id { get; set; }
    public string ProfileId { get; set; }
    public string FileName { get; set; }
    public string FilePath { get; set; }
    public string FileHash { get; set; }      // SHA-256 first 16 chars
    public DocumentType Type { get; set; }
    public long SizeBytes { get; set; }
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime IndexedAt { get; set; }
    public DocumentStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum DocumentType { Pdf, Docx, Doc, Txt, Markdown, Html, Image, Unknown }
public enum DocumentStatus { Pending, Indexing, Ready, Error }

public class DocumentChunk {
    public string Id { get; set; }
    public string DocumentId { get; set; }
    public string ProfileId { get; set; }
    public string FileName { get; set; }
    public string Text { get; set; }
    public int ChunkIndex { get; set; }
    public int PageNumber { get; set; }
    public float[]? Embedding { get; set; }
}

public class ChatSession {
    public string Id { get; set; }
    public string ProfileId { get; set; }
    public string Title { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
}

public class ChatMessage {
    public string Id { get; set; }
    public string SessionId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; }
    public List<ChunkCitation> Citations { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public double? LatencyMs { get; set; }
    public bool FromCache { get; set; }
}

public enum MessageRole { User, Assistant, System }

public class ChunkCitation {
    public string FileName { get; set; }
    public int PageNumber { get; set; }
    public string Excerpt { get; set; }       // first 120 chars of chunk
    public double RelevanceScore { get; set; }
}

public class RetrievedChunk {
    public DocumentChunk Chunk { get; set; }
    public double Score { get; set; }
    public double SemanticScore { get; set; }
    public double KeywordScore { get; set; }
}

// Extraction result for JSON API feature
public class ExtractionResult {
    public string ProfileId { get; set; }
    public string Query { get; set; }
    public string JsonSchema { get; set; }    // requested schema
    public string JsonOutput { get; set; }    // LLM-produced JSON
    public List<ChunkCitation> Sources { get; set; } = new();
}
```

---

## PHASE 1 — Foundation, LLamaSharp Integration & Core Services

**Goal:** Solution compiles. LLamaSharp loads a GGUF model and produces output.
All services implemented and unit-tested.

### 1.1 — AppSettings.cs

Strongly-typed config binding. Key rules:

- `LlamaSettings.ResolvedModelsFolder`: if `ModelsFolder` is relative, resolve
  first against `AppContext.BaseDirectory`, then against `AppDataFolder/models`.
  Return the first path that exists. If neither exists, return the
  `AppContext.BaseDirectory`-relative path (let startup validation report the error).
- `LlamaSettings.ResolvedChatModelPath`: `Path.Combine(ResolvedModelsFolder, ChatModelFile)`
- `LlamaSettings.ResolvedEmbeddingModelPath`: same pattern for `EmbeddingModelFile`
- `StorageSettings.ResolvedAppDataFolder`: expand `""` → runtime AppData path

### 1.2 — LlamaService.cs (core service, replaces OllamaService)

This is the most important service. It wraps LLamaSharp and runs entirely in-process.

**Model loading strategy:**
```csharp
// Load chat model once at startup, keep alive for app lifetime
// Use IDisposable — dispose on app shutdown
private LLamaWeights _chatWeights;
private LLamaWeights _embedWeights;      // separate weights for embedding model
private ModelParams _chatParams;
private ModelParams _embedParams;
```

**Initialization (`InitializeAsync`):**
```csharp
public async Task InitializeAsync(CancellationToken ct = default)
{
    // Validate model files exist — throw FileNotFoundException with helpful message
    // if not found, including the download URL from MODELS.md

    _chatParams = new ModelParams(settings.ResolvedChatModelPath)
    {
        ContextSize    = (uint)settings.ContextSize,    // 8192
        GpuLayerCount  = settings.GpuLayerCount,        // 99 = all layers to GPU
        Threads        = settings.Threads == 0
                         ? (uint)Environment.ProcessorCount
                         : (uint)settings.Threads,
        BatchSize      = (uint)settings.BatchSize
    };

    _embedParams = new ModelParams(settings.ResolvedEmbeddingModelPath)
    {
        ContextSize   = 512,   // embedding model needs less context
        GpuLayerCount = settings.GpuLayerCount,
        EmbeddingMode = true   // CRITICAL — must be true for embedding model
    };

    // Load weights asynchronously (can take 2–10s on first load)
    _chatWeights  = await Task.Run(() => LLamaWeights.LoadFromFile(_chatParams), ct);
    _embedWeights = await Task.Run(() => LLamaWeights.LoadFromFile(_embedParams), ct);

    _initialized = true;
    Log.Information("LLamaSharp loaded: chat={Chat}, embed={Embed}",
        settings.ChatModelFile, settings.EmbeddingModelFile);
}
```

**Embedding (`EmbedAsync`):**
```csharp
public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
{
    // 1. Check embedding cache (SHA-256 keyed, 24h TTL) → return if hit
    var cached = _cache.GetEmbedding(text);
    if (cached is not null) return cached;

    // 2. Compute embedding in-process
    using var context = _embedWeights.CreateContext(_embedParams);
    var embedder = new LLamaEmbedder(_embedWeights, _embedParams);
    var embedding = await Task.Run(() => embedder.GetEmbeddings(text), ct);
    var result = embedding.Select(d => (float)d).ToArray();

    // 3. L2-normalize (important for cosine similarity to work correctly)
    float mag = MathF.Sqrt(result.Sum(x => x * x));
    if (mag > 1e-6f) for (int i = 0; i < result.Length; i++) result[i] /= mag;

    // 4. Cache and return
    _cache.SetEmbedding(text, result);
    return result;
}
```

**Chat — non-streaming (`ChatAsync`):**
```csharp
public async Task<string> ChatAsync(
    string systemPrompt,
    List<(MessageRole role, string content)> history,
    string userMessage,
    CancellationToken ct = default)
{
    using var context = _chatWeights.CreateContext(_chatParams);
    var executor = new InteractiveExecutor(context);

    var inferParams = new InferenceParams
    {
        Temperature     = (float)settings.Temperature,
        MaxTokens       = settings.MaxTokens,
        AntiPrompts     = settings.AntiPrompts
    };

    // Build prompt in Qwen 2.5 chat format:
    // <|im_start|>system\n{system}<|im_end|>\n
    // <|im_start|>user\n{msg}<|im_end|>\n
    // <|im_start|>assistant\n
    var prompt = BuildChatPrompt(systemPrompt, history, userMessage);

    var sb = new StringBuilder();
    await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        sb.Append(token);

    return sb.ToString().Trim();
}
```

**Chat — streaming (`ChatStreamAsync`):**
```csharp
public async IAsyncEnumerable<string> ChatStreamAsync(
    string systemPrompt,
    List<(MessageRole role, string content)> history,
    string userMessage,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var context = _chatWeights.CreateContext(_chatParams);
    var executor = new InteractiveExecutor(context);
    var inferParams = new InferenceParams
    {
        Temperature = (float)settings.Temperature,
        MaxTokens   = settings.MaxTokens,
        AntiPrompts = settings.AntiPrompts
    };
    var prompt = BuildChatPrompt(systemPrompt, history, userMessage);
    await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
        yield return token;
}
```

**Chat prompt builder — Qwen 2.5 format:**
```csharp
private static string BuildChatPrompt(
    string system,
    List<(MessageRole role, string content)> history,
    string userMsg)
{
    var sb = new StringBuilder();
    sb.Append($"<|im_start|>system\n{system}<|im_end|>\n");
    foreach (var (role, content) in history)
    {
        var r = role == MessageRole.User ? "user" : "assistant";
        sb.Append($"<|im_start|>{r}\n{content}<|im_end|>\n");
    }
    sb.Append($"<|im_start|>user\n{userMsg}<|im_end|>\n<|im_start|>assistant\n");
    return sb.ToString();
}
```

> **Important:** the chat template changes per model family. Include a
> `ChatTemplate` enum in `AppSettings` with values:
> `Qwen` (default), `Llama3`, `Phi3`, `Gemma`, `Mistral`, `ChatML`.
> `BuildChatPrompt` switches on this enum so swapping models works correctly.
> Document all templates in `MODELS.md`.

**Additional methods:**
- `HealthCheckAsync()` → `(bool modelsFound, string chatModel, string embedModel,
  bool gpuAvailable, int gpuLayers)`
  Check that both GGUF files exist on disk. Attempt to detect GPU availability
  using `NativeLibraryConfig.Instance.WithCuda()` or check CUDA/Metal availability.
- `GetModelInfo()` → `ModelInfo` record with file name, size bytes, context size,
  gpu layers, estimated VRAM usage (`params * bytes_per_weight * gpu_layer_ratio`)
- `IsInitialized` property — false until `InitializeAsync` completes
- Implement `IAsyncDisposable` — dispose weights and contexts on app shutdown
- Thread safety: wrap inference calls in `SemaphoreSlim(1,1)` — LLamaSharp
  contexts are not thread-safe; queue concurrent requests

### 1.3 — CacheService.cs

Two-layer in-memory cache (identical logic to v1 prompt):

**Layer 1 — Embedding cache**
- Key: `"emb:" + SHA256(text)[..16]`
- TTL: `CacheSettings.EmbeddingTtlMinutes` (1440 = 24h)
- Rationale: same text always produces same vector — skip in-process embedding entirely

**Layer 2 — Query answer cache**
- Key: `"q:" + SHA256(profileId + generation + normalizedQuestion)[..20]`
- TTL: `CacheSettings.QueryTtlMinutes` (30 min)
- `InvalidateProfile(profileId)`: bump generation counter
- Rationale: repeated identical questions return from memory in ~5ms vs 8–30s

Expose: `GetEmbedding`, `SetEmbedding`, `GetAnswer`, `SetAnswer`,
`InvalidateProfile`, `GetStats()`.

### 1.4 — IngestionService.cs

`IngestFileAsync(profileId, filePath, progress, ct)` → `(Document, List<DocumentChunk>)`

**Parsers:**
- PDF: PdfPig — text per page, prefix `[Page N]`
- DOCX: DocumentFormat.OpenXml — paragraphs + heading structure
- HTML: HtmlAgilityPack — strip script/style, get InnerText
- Markdown: Markdig → HTML → HtmlAgilityPack
- TXT: `File.ReadAllTextAsync`
- Image: Tesseract.NET — graceful fallback if tessdata missing

**Chunking:** fixed-size (800 chars) with overlap (120 chars), snap to sentence
boundary by scanning back for `.!?\n` from chunk end.

**Embedding:** call `LlamaService.EmbedAsync` per chunk. Cache hits make
re-ingestion of unchanged documents nearly instant.

**Duplicate detection:** `ComputeHash(filePath)` → SHA-256 hex first 16 chars.
Check `ProfileRepository.DocumentExists(profileId, hash)` before ingesting.

### 1.5 — RetrievalService.cs

In-memory index: `Dictionary<string, List<DocumentChunk>>`.

- `AddChunksAsync`, `RemoveDocumentAsync`, `ClearProfileAsync`, `ChunkCount`
- `RetrieveAsync(profileId, query, topK, ct)`:
  1. Embed query via `LlamaService.EmbedAsync` (cache hit if query repeated)
  2. Cosine similarity for semantic score (L2-normalized → dot product suffices)
  3. Token overlap BM25-style for keyword score
  4. Final = `SemanticWeight * semantic + KeywordWeight * keyword`
  5. Filter `MinRelevanceScore`, sort desc, take `topK`
- `SaveIndexAsync(profileId)` → serialize to `{profileFolder}/index.bin`
  using `BinaryWriter`: `[int chunkCount][per chunk: textLen, text bytes,
  embLen, float[] embedding, docId, fileName, chunkIndex, pageNumber]`
- `LoadIndexAsync(profileId)` → deserialize from `index.bin` on profile load

### 1.6 — ChatService.cs

**`AskAsync(profile, session, question, ct)` → `ChatMessage`:**
1. Check query cache → return `FromCache = true` if hit (instant, ~5ms)
2. `RetrievalService.RetrieveAsync`
3. Build RAG prompt — system from `profile.SystemPrompt`, numbered context block,
   instruction: `Answer using ONLY the context. Cite as [filename, page N].`
4. Trim history to last 8 turns (fit context window)
5. `LlamaService.ChatAsync`
6. Cache answer → `CacheService.SetAnswer`
7. Return `ChatMessage` with citations and latency

**`AskStreamAsync(profile, session, question, onCitations, ct)`
  → `IAsyncEnumerable<string>`:**
- Cache check first — if hit, simulate streaming word-by-word (consistent UX,
  do NOT skip streaming for cache hits, users expect the animation)
- Call `LlamaService.ChatStreamAsync`
- Cache completed answer on stream end (accumulate in `StringBuilder`)
- `onCitations` callback fired before first token with retrieved citations

**`ExtractJsonAsync(profile, question, jsonSchema, ct)` → `ExtractionResult`:**
  Special mode for the JSON extraction API endpoint. Appends to the prompt:
  `"Return ONLY a valid JSON object matching this schema: {jsonSchema}. No explanation,
  no markdown, just the raw JSON."` Strips any accidental markdown fences from output.

**`GenerateDraftReplyAsync(profile, originalEmail, context, ct)` → `string`:**
  Builds a prompt that includes the original email text + retrieved project context,
  asks the model to draft a professional reply. Returns draft text.

### 1.7 — ProfileRepository.cs

SQLite-backed. DB at `{AppDataFolder}/brain.db`.

Tables (same schema as v1): `profiles`, `documents`, `sessions` with JSON `data`
columns. Indexes on profile_id and file_hash.

**Additional methods vs v1:**
- `GetSessionHistory(profileId, limit)` → last N sessions with message counts
- `GetDocumentByHash(profileId, hash)` → `Document?` for duplicate check
- `SearchSessions(profileId, query)` → sessions containing query string in messages

### 1.8 — Unit Tests

`CacheServiceTests`:
- Embedding hit returns same array; miss returns null
- Query hit after set; InvalidateProfile makes answer unreachable
- Generation counter increments on each InvalidateProfile call

`RetrievalServiceTests`:
- Empty index returns empty list
- High keyword-match chunk scores higher than zero-match chunk
- CosineSimilarity of identical L2-normalized vectors ≈ 1.0

### Phase 1 completion check
```bash
dotnet build BrainApp.sln          # zero errors
dotnet test                        # all tests pass
# Manual: place a GGUF file in models/ and run a quick smoke test:
dotnet run --project src/BrainApp.Api
curl http://localhost:5199/health
```

---

## PHASE 2 — REST API

**Goal:** All endpoints functional, Swagger at `/swagger`, model loaded from file.

### Startup — `Program.cs`

```csharp
// Build host exactly as Phase 1 services, plus web layer
var app = builder.Build();

// Initialize LLamaSharp before accepting requests
var llama = app.Services.GetRequiredService<LlamaService>();
await llama.InitializeAsync();    // loads GGUF from disk, takes 2–10s
// Log loading time and model info
```

### Endpoints

#### System
```
GET  /health
     → { modelsFound, chatModel, embedModel, gpuAvailable,
          gpuLayers, initialized, modelSizeGb }

GET  /model/info
     → { chatModelFile, embedModelFile, contextSize, gpuLayerCount,
          estimatedVramMb, threads }

GET  /cache/stats
     → { embeddingCacheEnabled, queryCacheEnabled, embeddingTtlMinutes,
          queryTtlMinutes }

DELETE /cache/{profileId}
     → { message }

POST /model/reload
     → reloads GGUF weights from disk (for hot-swapping model files)
     → 200 { reloaded: true } | 503 if model file not found
```

#### Profiles
```
GET    /profiles                     → Profile[]
GET    /profiles/{id}                → Profile | 404
POST   /profiles                     → 201 Profile
PUT    /profiles/{id}                → Profile | 404
DELETE /profiles/{id}                → 204
GET    /profiles/{id}/stats          → ProfileStats
```

#### Documents
```
POST   /profiles/{id}/documents      multipart/form-data field "file"
       → { id, fileName, chunkCount, status, duplicateSkipped? }

GET    /profiles/{id}/documents      → Document[]
DELETE /profiles/{id}/documents/{docId} → 204

POST   /profiles/{id}/documents/reindex
       → re-embeds all documents in the profile (use after model swap)
       → returns 202 Accepted, runs in background
```

#### Chat
```
POST /profiles/{id}/chat
     body: { question, sessionId?, outputFormat? }
     outputFormat: "text" (default) | "json" | "draft_reply"
     → { answer, citations, fromCache, latencyMs, sessionId, model }

GET  /profiles/{id}/chat/stream?question=...&sessionId=...
     Server-Sent Events:
       data: {"token":"..."}\n\n        (per token)
       data: {"citations":[...]}\n\n    (before first token)
       data: {"done":true,"answer":"..."}\n\n

GET  /profiles/{id}/sessions          → ChatSession[] (last 20)
GET  /profiles/{id}/sessions/{sid}    → ChatSession with messages
```

#### Extraction (JSON output mode)
```
POST /profiles/{id}/extract
     body: { question, jsonSchema }
     → { jsonOutput, sources, latencyMs }
     Example body:
       { "question": "Extract all contract values and end dates",
         "jsonSchema": "[{\"client\":\"string\",\"value\":\"number\",\"endDate\":\"string\"}]" }
```

#### Cross-profile query
```
POST /query
     body: { question, profileIds: string[], unionResults: bool }
     → { answers: [{ profileId, profileName, answer, citations }] }
     Queries multiple profiles in sequence, returns combined results.
     Use for: "what do all client contracts say about liability?"
```

#### Digest / automation
```
POST /profiles/{id}/digest
     body: { prompt?: string }
     Default prompt: "Summarise what needs attention this week:
       overdue items, approaching deadlines, unanswered questions,
       unresolved risks. Be concise and actionable."
     → { digest, generatedAt, model }
     Designed to be called on a schedule (cron, Task Scheduler, etc.)
```

### Swagger
Tags: System, Profiles, Documents, Chat, Extraction, Automation
Include `X-Api-Key` security definition.

### Phase 2 completion check
```bash
dotnet run --project src/BrainApp.Api
# Swagger at http://localhost:5199/swagger
# POST /profiles → create profile
# POST /profiles/{id}/documents → upload a .txt
# POST /profiles/{id}/chat → get cited answer
# GET /health → { modelsFound: true, initialized: true }
```

---

## PHASE 3 — Avalonia Desktop UI

**Goal:** App runs on Windows, macOS, Linux. Full chat + document management.

### App.axaml.cs — DI host

```csharp
protected override async void OnStartup(StartupEventArgs e)
{
    _host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c => c.AddJsonFile("appsettings.json"))
        .ConfigureServices((ctx, s) => {
            s.Configure<AppSettings>(ctx.Configuration);
            s.AddMemoryCache();
            s.AddSingleton<CacheService>();
            s.AddSingleton<LlamaService>();       // loads GGUF in-process
            s.AddSingleton<RetrievalService>();
            s.AddSingleton<IngestionService>();
            s.AddSingleton<ChatService>();
            s.AddSingleton<ProfileRepository>();
            s.AddTransient<MainWindowViewModel>();
            s.AddTransient<MainWindow>();
        }).Build();

    await _host.StartAsync();

    // Show splash / loading screen while GGUF loads (can take 5–10s)
    var splash = new LoadingWindow("Loading AI model...");
    splash.Show();

    var llama = _host.Services.GetRequiredService<LlamaService>();
    await llama.InitializeAsync();

    splash.Close();

    var main = _host.Services.GetRequiredService<MainWindow>();
    main.Show();
    MainWindow = main;
}
```

### MainWindow layout

```
┌─────────────────────────────────────────────────────────────────┐
│ Toolbar: [BrainApp logo] [Profile name] [Model badge] [Settings]│
├──────────────┬──────────────────────────────┬───────────────────┤
│   Profile    │        Chat area             │   Documents       │
│   sidebar    │                              │   panel           │
│   200px      │       flex (fills)           │   280px           │
│              │                              │                   │
│  [+ New]     │  [message bubbles]           │  [+ Add docs]     │
│              │                              │  [doc list]       │
│  [profile    │  [input row]                 │  [ingest bar]     │
│   list]      │                              │                   │
│              │                              │                   │
│  ── ──  ──   │                              │                   │
│  Model info  │                              │                   │
│  Status dot  │                              │                   │
└──────────────┴──────────────────────────────┴───────────────────┘
```

### Loading screen (LoadingWindow.axaml)

Show while GGUF weights load from disk. Display:
- App name + version
- Progress message ("Loading Qwen 2.5 3B…")
- Indeterminate progress bar
- Model file name and size
- Tip: "Tip: First load reads model from disk (~2–8s). Subsequent starts are faster."

### Profile sidebar

- Scrollable list — color badge dot + name + doc count + chunk count
- Selected profile highlighted with info-color background
- "+" button creates new profile (opens `ProfileEditDialog`)
- Right-click menu: Rename, Edit, Change Color, Delete (with confirmation)
- Bottom section:
  - Model chip: `[■ Qwen 2.5 3B]` — click opens Settings
  - Status indicator: green dot "Ready" | yellow "Loading" | red "Model not found"
  - RAM/VRAM usage if available via `GC.GetTotalMemory` approximation

### Chat view

- Message list: user bubbles right-aligned (info-color fill), assistant left-aligned
  (surface bg, subtle border)
- Each assistant message:
  - Content (streamed token by token)
  - `[⚡ cached]` badge when `FromCache = true` — amber pill
  - Collapsible citations panel: `[▸ 3 sources]` → expands to show filename chips
    with page numbers; clicking a chip shows excerpt in a tooltip
- Input row:
  - Multiline `TextBox` — Enter sends, Shift+Enter inserts newline
  - Send button (disabled while streaming)
  - Stop button (visible only while streaming — cancels via `CancellationTokenSource`)
- Header bar:
  - Profile name + icon
  - `[New chat]` button, `[Export]` button
  - `[Digest ▾]` dropdown button → calls `ChatService` with the digest prompt,
    shows result as a special "digest" message with amber left border
- Empty state: show example questions relevant to loaded document types
  e.g. "Try: What are my open invoices?" / "Which contracts expire this quarter?"
- Streaming: update `ChatMessageViewModel.Content` token-by-token via
  `Dispatcher.UIThread.InvokeAsync` — never block the UI thread

### Documents panel

- Header: "Documents" + count badge + total size
- `[+ Add documents]` button → `StorageProvider.OpenFilePickerAsync` (cross-platform)
  Supported: `*.pdf;*.docx;*.doc;*.txt;*.html;*.htm;*.md;*.png;*.jpg;*.jpeg;*.webp`
- Drag-and-drop: accept files via Avalonia `DragDrop` events
- Document list per item:
  - Type icon (by extension)
  - Filename (truncated with tooltip for long names)
  - Size + chunk count
  - Status badge: Pending (gray) | Indexing (blue + spinner) | Ready (green) | Error (red)
  - Delete button → confirmation dialog → removes from index + DB
- Ingest progress: per-file progress bar + step label ("Embedding chunk 12/47…")
- Duplicate detection: show toast "Already indexed: {filename}" and skip
- "Reindex all" button in panel header (for after model swap)
- Empty state: drag-and-drop illustration + hint text

### Settings window (SettingsWindow.axaml)

**Model tab:**
- Chat model file: text field (editable) + `[Browse…]` button → file picker for `.gguf`
- Embedding model file: same
- Models folder: text field + `[Browse…]` folder picker + `[Open folder]` button
- Context size: numeric input (1024–131072)
- GPU layers: slider 0–99 (0 = CPU only, 99 = all to GPU)
- Threads: numeric input (0 = auto)
- Chat template: dropdown (Qwen, Llama3, Phi3, Gemma, Mistral, ChatML)
- `[Test model]` button → runs a quick inference ("Hello"), shows latency
- `[Reload model]` button → calls `LlamaService.InitializeAsync` again
- Current model info card: file size, context, estimated VRAM, GPU layers active

**Cache tab:**
- Toggle embedding cache on/off
- Toggle query cache on/off
- Embedding TTL slider (minutes)
- Query TTL slider (minutes)
- `[Clear all caches]` button

**Storage tab:**
- AppData folder (read-only) + `[Open in explorer]`
- Max file size (MB)
- Max documents per profile

**API tab:**
- Enable API toggle
- Port input
- API key field (masked + show/hide)
- Enable Swagger toggle
- `[Copy API key]` button

**About tab:**
- Version, build date
- Link to MODELS.md for download instructions
- Link to GitHub
- `[Check model files]` → validates both GGUF files exist and shows file sizes

### MODELS.md download guide (must generate this file)

```markdown
# Downloading GGUF Model Files

BrainApp requires GGUF model files placed in the `models/` folder.

## Default models (recommended for 2–4 GB GPU)

### Chat model
File: `qwen2.5-3b-instruct-q4_k_m.gguf`
Size: ~2.0 GB
Download: https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF
Direct: [filename link on HuggingFace]

### Embedding model
File: `nomic-embed-text-v1.5.Q4_K_M.gguf`
Size: ~274 MB
Download: https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF

## Alternative chat models
[table from appsettings.json section above]

## Chat templates
When changing models, set ChatTemplate in appsettings.json:
- Qwen 2.5 → "Qwen"
- Llama 3.x → "Llama3"
- Phi-3.5 → "Phi3"
- Gemma 3 → "Gemma"
- Mistral → "Mistral"
Wrong template = garbled or empty responses.

## Placing model files
Copy .gguf files to: {AppBaseDirectory}/models/
Or set a custom path in Settings → Model tab.
```

### Phase 3 completion check
```bash
# Place both GGUF files in models/
dotnet run --project src/BrainApp.Desktop
# Verify:
# - Loading screen shows while model loads
# - Main window appears with 3-column layout
# - Can create profile, add a .txt file, ask a question
# - Streaming answer appears token by token
# - Citations panel shows correct source file
# - Settings window opens and shows model info
```

---

## PHASE 4 — Business Features, Polish & Packaging

**Goal:** All business-specific features working. App is production-ready.

### 4.1 — Business intelligence prompts

Create `BusinessPrompts.cs` in Core — a static class with named prompt templates:

```csharp
public static class BusinessPrompts
{
    public static string ProjectStatus(string projectName) =>
        $"What is the current status of '{projectName}'? Include: last known milestone, " +
        $"any pending approvals or blockers, last communication date. Cite sources.";

    public static string OpenActionItems() =>
        "List all open, unresolved, or pending action items, tasks, or to-dos found " +
        "across the documents. For each item include: description, responsible person if " +
        "mentioned, source document and date. Format as a numbered list.";

    public static string UpcomingDeadlines(int days = 90) =>
        $"Extract all dates, deadlines, and milestones mentioned in the documents that " +
        $"fall within the next {days} days or have no date but appear urgent. " +
        $"Sort chronologically. Cite the source document for each.";

    public static string ContractClauseSearch(string clauseType) =>
        $"Find all documents that contain clauses related to '{clauseType}'. " +
        $"Quote the relevant clause text and cite the document name and section number.";

    public static string OverdueInvoices() =>
        "Which invoices are overdue, unpaid, or have unresolved disputes? " +
        "List invoice number, client, amount, due date, and current status. " +
        "Cite the source document for each.";

    public static string RevenueByClient() =>
        "Summarise the total billed amount per client based on invoices and contracts. " +
        "Return as a list: client name, total amount, currency, number of invoices.";

    public static string UnansweredQuestions() =>
        "Find questions asked in emails or meeting notes that appear to have no response " +
        "or follow-up in the documents. List each question, who asked it, the date, " +
        "and the source document.";

    public static string WeeklyDigest() =>
        "Summarise what needs attention. Include: overdue invoices, approaching deadlines " +
        "(next 14 days), open action items, unanswered client questions, contract renewals " +
        "due within 90 days. Be concise and actionable. Use bullet points.";

    public static string DraftReply(string originalEmail) =>
        $"Using the context from the documents, draft a professional reply to this email:\n\n" +
        $"{originalEmail}\n\nThe reply should be concise, reference specific facts from " +
        $"the documents where relevant, and maintain a professional tone.";

    public static string SentimentShift() =>
        "Analyse the tone and sentiment of communications over time. Has the tone become " +
        "more urgent, frustrated, or positive recently compared to earlier communications? " +
        "Cite specific phrases or emails that indicate a shift.";
}
```

Expose these as **Quick Actions** in the chat UI — a row of chips above the input:
`[Project status] [Open tasks] [Deadlines] [Overdue invoices] [Weekly digest]`
Clicking a chip pre-fills the input or sends immediately.

### 4.2 — JSON extraction in the UI

Add an "Extract" mode toggle in the chat header (next to normal Q&A mode).
In Extract mode:
- Text area for the question/instruction
- Text area for the JSON schema (pre-filled with a useful example)
- Output rendered as formatted JSON with syntax highlighting
- Copy-to-clipboard button

### 4.3 — Profile edit dialog (ProfileEditDialog.axaml)

Fields:
- Name (required, max 50 chars, validated)
- Description (optional)
- Color picker: 12 preset swatches + custom hex input
- System prompt (multiline, 8 rows) + `[Reset to default]` button
- Model override: dropdown listing `.gguf` files found in the models folder,
  plus "(use global config)" as first option
- Chat template override (if model override selected)

### 4.4 — Export features

**Export chat session** (chat header button):
Format: Markdown
```
# BrainApp Export: {session.Title}
Profile: {profile.Name}
Date: {exportDate}
Model: {modelFile}

---

**You:** {question}

**BrainApp:** {answer}
Sources: {citations}

---
```
Save via `StorageProvider.SaveFilePickerAsync`.

**Export profile** (sidebar context menu):
- Zip: `{profileFolder}/docs/` + SQLite rows as `profile_export.json`
- Note in README: embeddings are NOT exported (model-specific), re-index on import

**Import profile**:
- Pick `.brainzip` → extract docs → re-ingest with current model
- Show progress (may take several minutes for large profiles)

### 4.5 — Global hotkey overlay

- Default: `Ctrl+Shift+B`
- Windows: `RegisterHotKey` P/Invoke wrapped in `#if WINDOWS`
- macOS: Carbon API or skip with user notification
- Opens compact floating window:
  - Profile selector dropdown
  - Question input
  - Answer displayed inline (streaming)
  - `Escape` to dismiss
- If hotkey registration fails: show one-time notification in status bar

### 4.6 — Notification service

Use Avalonia `WindowNotificationManager`:
- Ingestion complete: "Indexed {n} chunks from {filename}"
- Duplicate skipped: "Already indexed: {filename}"
- Model not found: persistent error banner with link to MODELS.md
- Cache cleared: "Cache cleared for {profile}"
- API errors: toast with error message

### 4.7 — Publish scripts

**publish.sh:**
```bash
#!/usr/bin/env bash
set -e
dotnet publish src/BrainApp.Desktop -c Release -r win-x64   --self-contained true -o dist/win-x64
dotnet publish src/BrainApp.Desktop -c Release -r osx-arm64 --self-contained true -o dist/osx-arm64
dotnet publish src/BrainApp.Desktop -c Release -r osx-x64   --self-contained true -o dist/osx-x64
dotnet publish src/BrainApp.Desktop -c Release -r linux-x64  --self-contained true -o dist/linux-x64
dotnet publish src/BrainApp.Api     -c Release -r win-x64   --self-contained true -o dist/api-win-x64
echo "Build complete. Copy GGUF model files to dist/{platform}/models/ before distributing."
```

**publish.ps1:** same targets using PowerShell syntax.

### 4.8 — README.md

Must cover:
1. Prerequisites: .NET 8 only if not using self-contained build
2. **Model setup** (prominent section): place GGUF files in `models/`,
   links to download, file naming requirements
3. How to change the model: edit `appsettings.json → LLama → ChatModelFile`,
   set correct `ChatTemplate`, click "Reload model" in Settings
4. Quick start: `dotnet run --project src/BrainApp.Desktop`
5. API reference table (all endpoints)
6. Business use cases: project brain, contracts brain, email brain examples
7. Troubleshooting:
   - "Model not found" → check models/ folder, check file name matches config
   - "Out of memory" → reduce GpuLayerCount, use smaller model
   - "Garbled output" → wrong ChatTemplate for model family
   - "Slow responses" → set GpuLayerCount to 0 and use CPU, or use smaller model
   - "OCR not working" → download `eng.traineddata` to `tessdata/` folder
8. Supported model families and chat templates table

### Phase 4 completion check
```bash
dotnet build BrainApp.sln           # zero errors
dotnet test                         # all tests pass
dotnet publish src/BrainApp.Desktop -r win-x64 --self-contained
# Test: app launches, loads model, indexes a PDF,
#       Quick Actions chips work, digest generates correctly,
#       Settings shows model info, export produces valid Markdown
```

---

## GENERAL ENGINEERING RULES

### LLamaSharp-specific rules
- **One context per inference call** — create with `using`, dispose immediately after
- **Never share a context between calls** — LLamaSharp contexts are stateful and not thread-safe
- **Serialize all inference** — `SemaphoreSlim(1,1)` in `LlamaService` for both chat and embed
- **EmbeddingMode = true** on the embedding model params — without this, embeddings are wrong
- **L2-normalize all embeddings** — required for cosine similarity to work correctly
- **AntiPrompts** — always set to prevent the model from generating "User:" and continuing
- **Context window budget** — history + context chunks + user message must fit in `ContextSize`;
  trim history and reduce `TopK` if approaching the limit
- On first startup if model file missing: show a friendly error with exact download
  instructions from `MODELS.md`, not a raw exception dialog

### Error handling
- Never swallow exceptions — log with Serilog at appropriate level
- User-facing errors: `NotificationService` toast, never raw exception dialogs
- Model loading failure: show persistent error banner in MainWindow, allow rest of
  app to function (browse profiles, view docs) even without inference

### Logging
- Info: model loaded, inference started/completed with latency
- Debug: cache hit/miss, chunk count retrieved, token count
- Warning: OCR fallback, duplicate skip, context window near limit
- Error: model file missing, inference failure, DB errors

### Performance
- All inference calls are `async` — never `.Result` or `.Wait()`
- Embedding batch: process sequentially (LLamaSharp is single-threaded per context)
- Index persistence: `SaveIndexAsync` on profile change and app shutdown
- Model cold start: show loading UI while `InitializeAsync` runs — never block the UI thread
- Context reuse consideration: `InteractiveExecutor` maintains KV cache between turns
  for multi-turn chat — use this for streaming sessions to avoid re-processing history

### Cross-platform paths
```csharp
// CORRECT
var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
var dataDir   = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "BrainApp");

// NEVER
var modelsDir = @"C:\BrainApp\models";
```

### Tesseract cross-platform
```csharp
var tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
// If missing: log warning, return "[Image: {name} — install tessdata for OCR]"
// Never throw — OCR is optional
```

---

## PHASE EXECUTION INSTRUCTIONS

Execute phases strictly in order. Before starting each phase:
1. Re-read this entire prompt
2. State the phase number and list files to create/modify
3. Write all code — no placeholder TODO methods
4. Run `dotnet build BrainApp.sln` — fix all errors before continuing
5. Run `dotnet test` — fix all failures
6. State "Phase N complete" and list what was built

If a NuGet package API has changed since training data, use what compiles correctly
and document the substitution in `NOTES.md`.

Do not truncate file output. Write complete files.

---

## DELIVERABLE SUMMARY

| Phase | Key outputs                                                                    |
|-------|--------------------------------------------------------------------------------|
| 1     | LLamaSharp in-process inference, all Core services, passing unit tests         |
| 2     | REST API — chat, extraction, cross-profile query, digest, model reload         |
| 3     | Avalonia UI — loading screen, 3-column layout, streaming chat, doc management  |
| 4     | Business features, Quick Actions, JSON extract, export, publish scripts        |

**Final state:** a fully self-contained desktop app that loads GGUF model files
directly from disk, runs all inference in-process with no network dependency,
supports Qwen 2.5 3B as default (swappable via config), handles business documents
with project/contract/email/finance intelligence, exposes a REST API for automation,
and runs identically on Windows, macOS, and Linux.
