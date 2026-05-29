using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Models;

namespace BrainApp.Core.Services;

public class ProfileSkillRow
{
    public string SkillFile { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

/// <summary>
/// SQLite-backed repository for profiles, documents, and chat sessions.
/// DB stored at {AppDataFolder}/brain.db
/// </summary>
public class ProfileRepository : IDisposable
{
    public const string LastSelectedProfileKey = "last_selected_profile_id";

    public static string ActiveSessionKey(string profileId) => $"active_session:{profileId}";
    public static string EmbeddingModelKey(string profileId) => $"embedding_model:{profileId}";

    private readonly string _dbPath;
    private SqliteConnection? _connection;

    // SqliteConnection is not thread-safe. All callers must take this lock for
    // any operation that creates a command or reads from a DataReader. This
    // serializes DB access — fine because SQLite itself is single-writer and
    // our reads are short.
    private readonly object _dbLock = new();

    public ProfileRepository(IOptions<StorageSettings> settings)
    {
        var appData = settings.Value.ResolvedAppDataFolder;
        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, "brain.db");
        Initialize();
    }

    private void Initialize()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        // Throughput tuning. WAL allows concurrent readers while a write is
        // in progress; synchronous=NORMAL is safe with WAL and a lot faster
        // than the default FULL.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size=-20000;
                PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        // Create tables
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT DEFAULT '',
                color TEXT DEFAULT '#534AB7',
                icon TEXT DEFAULT 'brain',
                system_prompt TEXT NOT NULL,
                model_override TEXT DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                type INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL,
                page_count INTEGER DEFAULT 0,
                chunk_count INTEGER DEFAULT 0,
                indexed_at TEXT NOT NULL,
                status INTEGER NOT NULL,
                error_message TEXT,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_documents_profile ON documents(profile_id);
            CREATE INDEX IF NOT EXISTS idx_documents_hash ON documents(file_hash);

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_profile ON sessions(profile_id);

            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                role INTEGER NOT NULL,
                content TEXT NOT NULL,
                citations TEXT DEFAULT '[]',
                created_at TEXT NOT NULL,
                latency_ms REAL,
                from_cache INTEGER DEFAULT 0,
                FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id);

            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                text TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                page_number INTEGER NOT NULL,
                is_paginated INTEGER NOT NULL DEFAULT 1,
                embedding BLOB NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
                FOREIGN KEY (document_id) REFERENCES documents(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_chunks_profile ON chunks(profile_id);
            CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id);

            CREATE TABLE IF NOT EXISTS profile_skills (
                profile_id TEXT NOT NULL,
                skill_file TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (profile_id, skill_file),
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_profile_skills_profile ON profile_skills(profile_id);

            CREATE TABLE IF NOT EXISTS app_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();

        MigrateChunksIsPaginated();

        Log.Information("ProfileRepository initialized. DB: {DbPath}", _dbPath);
    }

    /// <summary>
    /// One-time schema and data migration for the is_paginated column. Adds the column
    /// to legacy DBs and backfills page_number = chunk_index + 1 for rows that came in
    /// with page_number = 0 (every non-PDF chunk before this change). Non-PDF rows are
    /// also marked is_paginated = 0 so the UI renders "section N" instead of "page N".
    /// Safe to call on every startup: the ALTER is guarded and the UPDATE is a no-op once applied.
    /// </summary>
    private void MigrateChunksIsPaginated()
    {
        // Check whether the column already exists. PRAGMA table_info returns one row per column.
        bool hasColumn = false;
        using (var pragma = _connection!.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(chunks)";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "is_paginated", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            using var alter = _connection!.CreateCommand();
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN is_paginated INTEGER NOT NULL DEFAULT 1";
            alter.ExecuteNonQuery();
            Log.Information("Added is_paginated column to chunks table");
        }

        // Backfill: chunks that came in with page_number = 0 are all non-paginated formats
        // (DOCX/PPTX/HTML/MD/TXT/images). Rewrite their page_number to chunk_index + 1 and
        // flag them as non-paginated so the UI shows "section N".
        using var update = _connection!.CreateCommand();
        update.CommandText = @"
            UPDATE chunks
            SET page_number = chunk_index + 1,
                is_paginated = 0
            WHERE page_number = 0";
        var affected = update.ExecuteNonQuery();
        if (affected > 0)
            Log.Information("Backfilled {Count} legacy chunks: page_number = chunk_index + 1, is_paginated = 0", affected);
    }

    // ==================== PROFILE CRUD ====================

    public List<Profile> GetAllProfiles()
    {
        var profiles = new List<Profile>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM profiles ORDER BY created_at DESC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public Profile? GetProfile(string id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM profiles WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return ReadProfile(reader);
        return null;
    }

    public void SaveProfile(Profile profile)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO profiles (id, name, description, color, icon, system_prompt, model_override, created_at, updated_at)
            VALUES (@id, @name, @description, @color, @icon, @system_prompt, @model_override, @created_at, @updated_at)";

        cmd.Parameters.AddWithValue("@id", profile.Id);
        cmd.Parameters.AddWithValue("@name", profile.Name);
        cmd.Parameters.AddWithValue("@description", profile.Description ?? "");
        cmd.Parameters.AddWithValue("@color", profile.Color ?? "#534AB7");
        cmd.Parameters.AddWithValue("@icon", profile.Icon ?? "brain");
        cmd.Parameters.AddWithValue("@system_prompt", profile.SystemPrompt ?? Profile.DefaultSystemPrompt);
        cmd.Parameters.AddWithValue("@model_override", profile.ModelOverride ?? "");
        cmd.Parameters.AddWithValue("@created_at", profile.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O"));

        cmd.ExecuteNonQuery();
        Log.Debug("Saved profile: {ProfileId} - {ProfileName}", profile.Id, profile.Name);
    }

    public void DeleteProfile(string id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        Log.Information("Deleted profile: {ProfileId}", id);
    }

    public ProfileStats GetProfileStats(string profileId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) as doc_count,
                COALESCE(SUM(chunk_count), 0) as chunk_count,
                COALESCE(SUM(size_bytes), 0) as total_size,
                MAX(indexed_at) as last_indexed
            FROM documents
            WHERE profile_id = @profileId";
        cmd.Parameters.AddWithValue("@profileId", profileId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new ProfileStats
            {
                DocumentCount = reader.GetInt32(0),
                ChunkCount = reader.GetInt32(1),
                TotalSizeBytes = reader.GetInt64(2),
                LastIndexed = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3))
            };
        }
        return new ProfileStats();
    }

    // ==================== DOCUMENT CRUD ====================

    public List<Document> GetDocuments(string profileId)
    {
        lock (_dbLock)
        {
            var docs = new List<Document>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM documents WHERE profile_id = @profileId ORDER BY indexed_at DESC";
            cmd.Parameters.AddWithValue("@profileId", profileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                docs.Add(ReadDocument(reader));
            }
            return docs;
        }
    }

    public Document? GetDocument(string profileId, string documentId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM documents WHERE profile_id = @profileId AND id = @id";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.Parameters.AddWithValue("@id", documentId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return ReadDocument(reader);
            return null;
        }
    }

    public Document? GetDocumentByHash(string profileId, string fileHash)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM documents WHERE profile_id = @profileId AND file_hash = @hash";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.Parameters.AddWithValue("@hash", fileHash);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return ReadDocument(reader);
            return null;
        }
    }

    public void SaveDocument(Document document)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO documents (id, profile_id, file_name, file_path, file_hash, type, size_bytes, page_count, chunk_count, indexed_at, status, error_message)
                VALUES (@id, @profile_id, @file_name, @file_path, @file_hash, @type, @size_bytes, @page_count, @chunk_count, @indexed_at, @status, @error_message)";

            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.AddWithValue("@profile_id", document.ProfileId);
            cmd.Parameters.AddWithValue("@file_name", document.FileName);
            cmd.Parameters.AddWithValue("@file_path", document.FilePath);
            cmd.Parameters.AddWithValue("@file_hash", document.FileHash);
            cmd.Parameters.AddWithValue("@type", (int)document.Type);
            cmd.Parameters.AddWithValue("@size_bytes", document.SizeBytes);
            cmd.Parameters.AddWithValue("@page_count", document.PageCount);
            cmd.Parameters.AddWithValue("@chunk_count", document.ChunkCount);
            cmd.Parameters.AddWithValue("@indexed_at", document.IndexedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@status", (int)document.Status);
            cmd.Parameters.AddWithValue("@error_message", document.ErrorMessage ?? (object)DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteDocument(string profileId, string documentId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE profile_id = @profileId AND id = @id";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.Parameters.AddWithValue("@id", documentId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteDocumentsByProfile(string profileId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE profile_id = @profileId";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.ExecuteNonQuery();
        }
        Log.Information("Deleted all documents for profile {ProfileId}", profileId);
    }

    /// <summary>
    /// Reset documents stuck in the Indexing status to Error. Call on startup
    /// to recover after a crash mid-ingestion — without this the rows stay in
    /// Indexing forever and the file's hash blocks re-upload.
    /// </summary>
    public int ResetStuckDocuments()
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                UPDATE documents
                SET status = @errorStatus,
                    error_message = COALESCE(error_message, 'Interrupted — process exited mid-ingestion')
                WHERE status = @indexingStatus";
            cmd.Parameters.AddWithValue("@errorStatus", (int)DocumentStatus.Error);
            cmd.Parameters.AddWithValue("@indexingStatus", (int)DocumentStatus.Indexing);
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0)
                Log.Information("Reset {Count} stuck Indexing document(s) to Error on startup", affected);
            return affected;
        }
    }

    // ==================== APP STATE ====================

    public string? GetAppState(string key)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_state WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : (string)result;
        }
    }

    public void SetAppState(string key, string value)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO app_state (key, value) VALUES (@key, @value)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetLastSelectedProfileId() => GetAppState(LastSelectedProfileKey);

    public void SetLastSelectedProfileId(string profileId) =>
        SetAppState(LastSelectedProfileKey, profileId);

    public string? GetActiveSessionId(string profileId) =>
        GetAppState(ActiveSessionKey(profileId));

    public void SetActiveSessionId(string profileId, string sessionId) =>
        SetAppState(ActiveSessionKey(profileId), sessionId);

    public string? GetProfileEmbeddingModel(string profileId) =>
        GetAppState(EmbeddingModelKey(profileId));

    public void SetProfileEmbeddingModel(string profileId, string embeddingModelFile) =>
        SetAppState(EmbeddingModelKey(profileId), embeddingModelFile);

    /// <summary>Most recent session that has at least one message (skips empty "New chat" orphans).</summary>
    public ChatSession? GetLatestSessionWithMessages(string profileId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT s.* FROM sessions s
            WHERE s.profile_id = @profileId
              AND EXISTS (SELECT 1 FROM messages m WHERE m.session_id = s.id)
            ORDER BY s.updated_at DESC
            LIMIT 1";
        cmd.Parameters.AddWithValue("@profileId", profileId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var session = ReadSession(reader);
        session.Messages = GetMessages(session.Id);
        return session;
    }

    public void DeleteEmptySessions(string profileId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM sessions
                WHERE profile_id = @profileId
                  AND id NOT IN (SELECT DISTINCT session_id FROM messages)";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                Log.Debug("Removed {Count} empty session(s) for profile {ProfileId}", deleted, profileId);
        }
    }

    // ==================== SESSION CRUD ====================

    public List<ChatSession> GetSessionHistory(string profileId, int limit = 20)
    {
        var sessions = new List<ChatSession>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE profile_id = @profileId ORDER BY updated_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }
        return sessions;
    }

    public ChatSession? GetSession(string sessionId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var session = ReadSession(reader);
            session.Messages = GetMessages(sessionId);
            return session;
        }
        return null;
    }

    public void SaveSession(ChatSession session)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO sessions (id, profile_id, title, created_at, updated_at)
            VALUES (@id, @profile_id, @title, @created_at, @updated_at)";

        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@profile_id", session.ProfileId);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@created_at", session.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O"));

        cmd.ExecuteNonQuery();
    }

    public List<ChatMessage> GetMessages(string sessionId)
    {
        var messages = new List<ChatMessage>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE session_id = @sessionId ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(ReadMessage(reader));
        }
        return messages;
    }

    public void SaveMessages(string sessionId, IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO messages (id, session_id, role, content, citations, created_at, latency_ms, from_cache)
                VALUES (@id, @session_id, @role, @content, @citations, @created_at, @latency_ms, @from_cache)";

            cmd.Parameters.AddWithValue("@id", msg.Id);
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            cmd.Parameters.AddWithValue("@role", (int)msg.Role);
            cmd.Parameters.AddWithValue("@content", msg.Content);
            cmd.Parameters.AddWithValue("@citations", JsonSerializer.Serialize(msg.Citations));
            cmd.Parameters.AddWithValue("@created_at", msg.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@latency_ms", msg.LatencyMs ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@from_cache", msg.FromCache ? 1 : 0);

            cmd.ExecuteNonQuery();
        }

        // Update session updated_at
        using var cmd2 = _connection!.CreateCommand();
        cmd2.CommandText = "UPDATE sessions SET updated_at = @updated WHERE id = @id";
        cmd2.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("O"));
        cmd2.Parameters.AddWithValue("@id", sessionId);
        cmd2.ExecuteNonQuery();
    }

    public List<ChatSession> SearchSessions(string profileId, string query)
    {
        var sessions = new List<ChatSession>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT s.* FROM sessions s
            INNER JOIN messages m ON s.id = m.session_id
            WHERE s.profile_id = @profileId AND m.content LIKE @query
            ORDER BY s.updated_at DESC LIMIT 20";
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@query", $"%{query}%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }
        return sessions;
    }

    public ChatSession CreateSession(string profileId, string title = "New chat")
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ProfileId = profileId,
            Title = title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        SaveSession(session);
        return session;
    }

    // ==================== CHUNK CRUD ====================

    public void SaveChunks(List<DocumentChunk> chunks)
    {
        if (chunks.Count == 0) return;

        lock (_dbLock)
        {
            using var transaction = _connection!.BeginTransaction();
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO chunks (id, profile_id, document_id, file_name, text, chunk_index, page_number, is_paginated, embedding)
                    VALUES (@id, @profile_id, @document_id, @file_name, @text, @chunk_index, @page_number, @is_paginated, @embedding)";

                var pId = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
                var pProfile = cmd.Parameters.Add("@profile_id", Microsoft.Data.Sqlite.SqliteType.Text);
                var pDoc = cmd.Parameters.Add("@document_id", Microsoft.Data.Sqlite.SqliteType.Text);
                var pFile = cmd.Parameters.Add("@file_name", Microsoft.Data.Sqlite.SqliteType.Text);
                var pText = cmd.Parameters.Add("@text", Microsoft.Data.Sqlite.SqliteType.Text);
                var pChunkIdx = cmd.Parameters.Add("@chunk_index", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pPage = cmd.Parameters.Add("@page_number", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pPaginated = cmd.Parameters.Add("@is_paginated", Microsoft.Data.Sqlite.SqliteType.Integer);
                var pEmbed = cmd.Parameters.Add("@embedding", Microsoft.Data.Sqlite.SqliteType.Blob);

                foreach (var chunk in chunks)
                {
                    pId.Value = chunk.Id;
                    pProfile.Value = chunk.ProfileId;
                    pDoc.Value = chunk.DocumentId;
                    pFile.Value = chunk.FileName;
                    pText.Value = chunk.Text;
                    pChunkIdx.Value = chunk.ChunkIndex;
                    pPage.Value = chunk.PageNumber;
                    pPaginated.Value = chunk.IsPaginated ? 1 : 0;
                    pEmbed.Value = EmbeddingToBlob(chunk.Embedding!);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
                Log.Debug("Saved {Count} chunks for document {DocumentId}", chunks.Count, chunks[0].DocumentId);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public List<DocumentChunk> GetChunksByProfile(string profileId)
    {
        lock (_dbLock)
        {
            var chunks = new List<DocumentChunk>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT * FROM chunks WHERE profile_id = @profileId";
            cmd.Parameters.AddWithValue("@profileId", profileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                chunks.Add(ReadChunk(reader));
            }
            return chunks;
        }
    }

    public int GetChunkCountByProfile(string profileId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE profile_id = @profileId";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void DeleteChunksByDocument(string documentId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM chunks WHERE document_id = @documentId";
            cmd.Parameters.AddWithValue("@documentId", documentId);
            cmd.ExecuteNonQuery();
        }
        Log.Debug("Deleted chunks for document {DocumentId}", documentId);
    }

    public void DeleteChunksByProfile(string profileId)
    {
        lock (_dbLock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM chunks WHERE profile_id = @profileId";
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.ExecuteNonQuery();
        }
        Log.Debug("Deleted chunks for profile {ProfileId}", profileId);
    }

    // ==================== PROFILE SKILLS ====================

    public HashSet<string> GetEnabledSkillFiles(string profileId)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT skill_file FROM profile_skills WHERE profile_id = @profileId AND enabled = 1";
        cmd.Parameters.AddWithValue("@profileId", profileId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            enabled.Add(reader.GetString(0));

        return enabled;
    }

    public List<ProfileSkillRow> GetProfileSkills(string profileId)
    {
        var rows = new List<ProfileSkillRow>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT skill_file, enabled FROM profile_skills WHERE profile_id = @profileId ORDER BY skill_file";
        cmd.Parameters.AddWithValue("@profileId", profileId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProfileSkillRow
            {
                SkillFile = reader.GetString(0),
                Enabled = reader.GetInt32(1) == 1
            });
        }
        return rows;
    }

    public bool IsSkillEnabled(string profileId, string skillFile)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT enabled FROM profile_skills WHERE profile_id = @profileId AND skill_file = @skillFile";
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@skillFile", skillFile);

        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return true;
        return Convert.ToInt32(result) == 1;
    }

    public void SetSkillEnabled(string profileId, string skillFile, bool enabled)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO profile_skills (profile_id, skill_file, enabled)
            VALUES (@profileId, @skillFile, @enabled)";
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@skillFile", skillFile);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void EnsureSkillDefaultsForAllProfiles(IEnumerable<string> skillFiles)
    {
        var files = skillFiles.ToList();
        if (files.Count == 0) return;

        var profiles = GetAllProfiles();
        foreach (var profile in profiles)
        {
            foreach (var file in files)
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO profile_skills (profile_id, skill_file, enabled)
                    VALUES (@profileId, @skillFile, 1)";
                cmd.Parameters.AddWithValue("@profileId", profile.Id);
                cmd.Parameters.AddWithValue("@skillFile", file);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static byte[] EmbeddingToBlob(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToEmbedding(byte[] blob)
    {
        var embedding = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
        return embedding;
    }

    // ==================== READERS ====================

    private static Profile ReadProfile(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Description = reader.IsDBNull(reader.GetOrdinal("description")) ? "" : reader.GetString(reader.GetOrdinal("description")),
        Color = reader.IsDBNull(reader.GetOrdinal("color")) ? "#534AB7" : reader.GetString(reader.GetOrdinal("color")),
        Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? "brain" : reader.GetString(reader.GetOrdinal("icon")),
        SystemPrompt = reader.IsDBNull(reader.GetOrdinal("system_prompt")) ? Profile.DefaultSystemPrompt : reader.GetString(reader.GetOrdinal("system_prompt")),
        ModelOverride = reader.IsDBNull(reader.GetOrdinal("model_override")) ? "" : reader.GetString(reader.GetOrdinal("model_override")),
        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static Document ReadDocument(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("error_message");
        string? errorMsg = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        return new Document
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            ProfileId = reader.GetString(reader.GetOrdinal("profile_id")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            FileHash = reader.GetString(reader.GetOrdinal("file_hash")),
            Type = (DocumentType)reader.GetInt32(reader.GetOrdinal("type")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            PageCount = reader.GetInt32(reader.GetOrdinal("page_count")),
            ChunkCount = reader.GetInt32(reader.GetOrdinal("chunk_count")),
            IndexedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("indexed_at"))),
            Status = (DocumentStatus)reader.GetInt32(reader.GetOrdinal("status")),
            ErrorMessage = errorMsg
        };
    }

    private static ChatSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ProfileId = reader.GetString(reader.GetOrdinal("profile_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static DocumentChunk ReadChunk(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ProfileId = reader.GetString(reader.GetOrdinal("profile_id")),
        DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
        FileName = reader.GetString(reader.GetOrdinal("file_name")),
        Text = reader.GetString(reader.GetOrdinal("text")),
        ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
        PageNumber = reader.GetInt32(reader.GetOrdinal("page_number")),
        IsPaginated = reader.GetInt32(reader.GetOrdinal("is_paginated")) == 1,
        Embedding = BlobToEmbedding((byte[])reader.GetValue(reader.GetOrdinal("embedding")))
    };

    private static ChatMessage ReadMessage(SqliteDataReader reader)
    {
        var citationsJson = reader.IsDBNull(reader.GetOrdinal("citations"))
            ? "[]"
            : reader.GetString(reader.GetOrdinal("citations"));
        var citations = JsonSerializer.Deserialize<List<ChunkCitation>>(citationsJson) ?? new();

        return new ChatMessage
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            SessionId = reader.GetString(reader.GetOrdinal("session_id")),
            Role = (MessageRole)reader.GetInt32(reader.GetOrdinal("role")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            Citations = citations,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            LatencyMs = reader.IsDBNull(reader.GetOrdinal("latency_ms")) ? null : reader.GetDouble(reader.GetOrdinal("latency_ms")),
            FromCache = reader.GetInt32(reader.GetOrdinal("from_cache")) == 1
        };
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}