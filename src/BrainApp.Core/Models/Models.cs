namespace BrainApp.Core.Models;

public class Profile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#534AB7";
    public string Icon { get; set; } = "brain";
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string ModelOverride { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ProfileStats? Stats { get; set; }

    public const string DefaultSystemPrompt =
        "You are a precise knowledge base assistant. Answer questions using ONLY " +
        "the provided document excerpts. Cite sources exactly as shown in the context " +
        "header — either [filename, page N] or [filename, section N]. " +
        "If the documents do not contain enough information, say so clearly. " +
        "Never invent facts not present in the provided context. " +
        "Always respond in the same language as the user's question.";
}

public class ProfileStats
{
    public int DocumentCount { get; set; }
    public int ChunkCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastIndexed { get; set; }
    public int QuestionsAnswered { get; set; }
}

public class Document
{
    public string Id { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DocumentType Type { get; set; } = DocumentType.Unknown;
    public long SizeBytes { get; set; }
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? ErrorMessage { get; set; }
}

public enum DocumentType { Pdf, Docx, Doc, Pptx, Txt, Markdown, Html, Image, Unknown }
public enum DocumentStatus { Pending, Indexing, Ready, Error }

public class DocumentChunk
{
    public string Id { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public int PageNumber { get; set; }
    public bool IsPaginated { get; set; } = true;
    public float[]? Embedding { get; set; }
}

public class ChatSession
{
    public string Id { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
}

public class ChatMessage
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public MessageRole Role { get; set; } = MessageRole.User;
    public string Content { get; set; } = string.Empty;
    public List<ChunkCitation> Citations { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public double? LatencyMs { get; set; }
    public bool FromCache { get; set; }
    public TokenStats? Tokens { get; set; }
}

public record TokenStats(int InputTokens, int OutputTokens, int TotalTokens, int ContextLimit);

public enum MessageRole { User, Assistant, System }

public class ChunkCitation
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string CitationUnit { get; set; } = "page";
    public string Excerpt { get; set; } = string.Empty;
    public double RelevanceScore { get; set; }
}

public class RetrievedChunk
{
    public DocumentChunk Chunk { get; set; } = null!;
    public double Score { get; set; }
    public double SemanticScore { get; set; }
    public double KeywordScore { get; set; }
}

public class ExtractionResult
{
    public string ProfileId { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
    public string JsonOutput { get; set; } = string.Empty;
    public List<ChunkCitation> Sources { get; set; } = new();
}

public record ModelInfo(
    string ChatModelFile,
    string EmbedModelFile,
    int ContextSize,
    int GpuLayerCount,
    long FileSizeBytes,
    int EstimatedVramMb,
    int Threads);

public record HealthStatus(
    bool ModelsFound,
    string ChatModel,
    string EmbedModel,
    bool GpuAvailable,
    int GpuLayers,
    bool Initialized,
    long ModelSizeGb);