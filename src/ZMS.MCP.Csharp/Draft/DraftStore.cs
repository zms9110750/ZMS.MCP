using Microsoft.Data.Sqlite;
using ZMS.MCP.Csharp.Storage;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>
/// 拟定里的一条编辑：**符号 + 意图 + 首次编辑时该符号的文本**。
/// 不存文件路径、不存整文件内容、更不存文件快照 —— 路径与整文件新文本都在预检/落盘现场算。
/// </summary>
public sealed record DraftEdit(
    long Id,
    int Sequence,
    string TypePath,
    string MemberName,
    string? RequestedContent,
    string Action,
    string SymbolKey = "",
    string SymbolSnapshot = "")
{
    public bool IsDelete => RequestedContent == null;
}

/// <summary>一个项目上的拟定。</summary>
public sealed record DraftRecord(string ProjectPath, string Cookit, string CreatedAt, IReadOnlyList<DraftEdit> Edits);

/// <summary>一个项目的追踪记录：追踪 cookie + 符号级基线。</summary>
public sealed record TrackingRecord(
    string ProjectPath,
    string TrackingCookie,
    IReadOnlyDictionary<string, string> Baseline,
    string CreatedAt);

/// <summary>
/// 拟定状态的持久化：**sqlite 放在 MCP 自己的目录**（<see cref="McpPaths.DataDirectory"/>），不进项目。
/// MCP 重启后从同一个库里恢复同一个拟定；agent 重启后靠「列出拟定」重新拿到 cookit。
/// </summary>
public sealed class DraftStore
{
    private readonly string _databasePath;

    public DraftStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? McpPaths.DraftDatabase();
    }

    public string DatabasePath => _databasePath;

    /// <summary>取这个项目现有的拟定；没有就建一个（带新的 cookit）。</summary>
    public DraftRecord GetOrCreate(string projectPath)
    {
        string normalized = Normalize(projectPath);
        using SqliteConnection connection = Open();
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO drafts (project_path, cookit, created_at)
                VALUES ($project, $cookit, $created)
                ON CONFLICT(project_path) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$project", normalized);
            insert.Parameters.AddWithValue("$cookit", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        return Read(connection, normalized)
            ?? throw new InvalidOperationException($"拟定创建失败：{normalized}");
    }

    /// <summary>追加一条编辑（可以累积：多次调用依次叠加在同一个拟定上）。</summary>
    public DraftEdit Append(
        string projectPath,
        string typePath,
        string memberName,
        string? requestedContent,
        string action,
        string symbolKey = "",
        string symbolSnapshot = "")
    {
        string normalized = Normalize(projectPath);
        DraftRecord record = GetOrCreate(normalized);
        int sequence = record.Edits.Count == 0 ? 1 : record.Edits.Max(edit => edit.Sequence) + 1;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$project", normalized);
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$type", typePath);
        command.Parameters.AddWithValue("$member", memberName);
        command.Parameters.AddWithValue("$content", requestedContent == null ? DBNull.Value : requestedContent);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$symbol", symbolKey);
        command.Parameters.AddWithValue("$snapshot", symbolSnapshot);
        long id = Convert.ToInt64(command.ExecuteScalar());

        return new DraftEdit(id, sequence, typePath, memberName, requestedContent, action, symbolKey, symbolSnapshot);
    }

    /// <summary>
    /// 同一个符号**只保留一条生效条目**：先删它的旧条目，再插新的一条。
    /// `keepFirstSnapshot`：`stage` 传 true（沿用首次快照，冲突检测才有效）；
    /// 选择器（select_draft）传 false（用选中内容与**当前**文本当快照，否则冲突永远消不掉）。
    /// </summary>
    public DraftEdit ReplaceSymbol(
        string projectPath,
        string typePath,
        string memberName,
        string? requestedContent,
        string symbolKey,
        string symbolSnapshot,
        string action,
        bool keepFirstSnapshot = true)
    {
        string normalized = Normalize(projectPath);

        // 沿用**首次**快照：同符号第二次 stage 若把快照刷成"当下"，冲突检测就永远看不出来了
        string snapshot = symbolSnapshot;
        if (keepFirstSnapshot && symbolSnapshot.Length > 0)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText =
                "SELECT symbol_snapshot FROM draft_edits WHERE project_path = $project AND symbol_key = $symbol "
                + "ORDER BY rowid DESC LIMIT 1;";
            read.Parameters.AddWithValue("$project", normalized);
            read.Parameters.AddWithValue("$symbol", symbolKey);
            if (read.ExecuteScalar() is string existing && existing.Length > 0)
            {
                snapshot = existing;
            }
        }

        // 删旧 + 插新必须在**同一连接同一事务**里：否则删成功、插失败会静默丢掉这个符号的拟定条目
        DraftRecord record = GetOrCreate(normalized);
        int sequence = record.Edits.Count == 0 ? 1 : record.Edits.Max(edit => edit.Sequence) + 1;

        using (SqliteConnection connection = Open())
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM draft_edits WHERE project_path = $project AND symbol_key = $symbol;";
                delete.Parameters.AddWithValue("$project", normalized);
                delete.Parameters.AddWithValue("$symbol", symbolKey);
                delete.ExecuteNonQuery();
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = InsertSql;
            insert.Parameters.AddWithValue("$project", normalized);
            insert.Parameters.AddWithValue("$seq", sequence);
            insert.Parameters.AddWithValue("$type", typePath);
            insert.Parameters.AddWithValue("$member", memberName);
            insert.Parameters.AddWithValue("$content", requestedContent == null ? DBNull.Value : requestedContent);
            insert.Parameters.AddWithValue("$action", action);
            insert.Parameters.AddWithValue("$symbol", symbolKey);
            insert.Parameters.AddWithValue("$snapshot", snapshot);
            long id = Convert.ToInt64(insert.ExecuteScalar());

            transaction.Commit();
            return new DraftEdit(id, sequence, typePath, memberName, requestedContent, action, symbolKey, snapshot);
        }
    }

    /// <summary>列出所有追踪记录（启动维护要按项目刷新基线）。</summary>
    public IReadOnlyList<TrackingRecord> ListTrackings()
    {
        List<TrackingRecord> records = [];
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT project_path, tracking_cookie, symbol_baseline, created_at FROM trackings;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new TrackingRecord(
                reader.GetString(0),
                reader.GetString(1),
                SymbolBaseline.Deserialize(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                reader.GetString(3)));
        }

        return records;
    }

    /// <summary>拟定条目的 INSERT（`Append` 与 `ReplaceSymbol` 共用，保证两处列与参数一致）。</summary>
    private const string InsertSql =
        """
        INSERT INTO draft_edits (project_path, seq, type_path, member_name, content, action, symbol_key, symbol_snapshot)
        VALUES ($project, $seq, $type, $member, $content, $action, $symbol, $snapshot);
        SELECT last_insert_rowid();
        """;

    /// <summary>删掉某个符号的拟定条目（选择器的 <c>drop</c>）；返回删了几条。</summary>
    public int RemoveSymbol(string projectPath, string symbolKey)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM draft_edits WHERE project_path = $project AND symbol_key = $symbol;";
        delete.Parameters.AddWithValue("$project", Normalize(projectPath));
        delete.Parameters.AddWithValue("$symbol", symbolKey);
        return delete.ExecuteNonQuery();
    }

    /// <summary>读这个项目的拟定（没有返回 null）。</summary>
    public DraftRecord? Find(string projectPath)
    {
        using SqliteConnection connection = Open();
        return Read(connection, Normalize(projectPath));
    }

    /// <summary>清掉这个项目的拟定（落盘成功后调用）。</summary>
    public void Clear(string projectPath)
    {
        string normalized = Normalize(projectPath);
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM draft_edits WHERE project_path = $project; DELETE FROM drafts WHERE project_path = $project;";
        command.Parameters.AddWithValue("$project", normalized);
        command.ExecuteNonQuery();
    }

    // ───────── 追踪记录（符号级基线） ─────────

    /// <summary>读这个项目的追踪记录（没有返回 null）。</summary>
    public TrackingRecord? GetTracking(string projectPath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT project_path, tracking_cookie, symbol_baseline, created_at FROM trackings WHERE project_path = $project;";
        command.Parameters.AddWithValue("$project", Normalize(projectPath));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new TrackingRecord(
            reader.GetString(0),
            reader.GetString(1),
            SymbolBaseline.Deserialize(reader.GetString(2)),
            reader.GetString(3));
    }

    /// <summary>
    /// 按追踪 cookie 反查项目（`stage_draft` / `confirm_draft` 只拿 cookie，不拿 csprojPath）。
    /// cookie 对不上任何项目时返回 null。
    /// </summary>
    public TrackingRecord? GetTrackingByCookie(string cookie)
    {
        string trimmed = (cookie ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT project_path, tracking_cookie, symbol_baseline, created_at FROM trackings WHERE tracking_cookie = $cookie;";
        command.Parameters.AddWithValue("$cookie", trimmed);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new TrackingRecord(
            reader.GetString(0),
            reader.GetString(1),
            SymbolBaseline.Deserialize(reader.GetString(2)),
            reader.GetString(3));
    }

    /// <summary>
    /// 建追踪记录；已存在时**只刷新基线**（"已经在追踪就不重记"由调用方判断）。
    /// </summary>
    public TrackingRecord SaveTracking(string projectPath, string cookie, IReadOnlyDictionary<string, string> baseline)
    {
        string normalized = Normalize(projectPath);
        using SqliteConnection connection = Open();
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO trackings (project_path, tracking_cookie, symbol_baseline, created_at)
                VALUES ($project, $cookie, $baseline, $created)
                ON CONFLICT(project_path) DO UPDATE SET symbol_baseline = excluded.symbol_baseline;
                """;
            insert.Parameters.AddWithValue("$project", normalized);
            insert.Parameters.AddWithValue("$cookie", cookie);
            insert.Parameters.AddWithValue("$baseline", SymbolBaseline.Serialize(baseline));
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        return GetTracking(normalized) ?? throw new InvalidOperationException($"追踪记录写入失败：{normalized}");
    }

    /// <summary>取消追踪：**一个事务里**删掉追踪记录 + 该项目的全部拟定（写前日志另行处理，不在这里静默删）。</summary>
    public void ClearTracking(string projectPath)
    {
        string normalized = Normalize(projectPath);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM draft_edits WHERE project_path = $project;
                DELETE FROM drafts WHERE project_path = $project;
                DELETE FROM trackings WHERE project_path = $project;
                """;
            command.Parameters.AddWithValue("$project", normalized);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>这个项目有没有未完成的写前日志（靠 <c>drafts</c> 里的 cookit 关联）。</summary>
    public bool HasPendingJournal(string projectPath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM write_journal
            WHERE cookit IN (SELECT cookit FROM drafts WHERE project_path = $project);
            """;
        command.Parameters.AddWithValue("$project", Normalize(projectPath));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>写前日志的一条：要写的文件、新内容、**写之前**的文件 hash 与编码（前滚靠它判三态）。</summary>
    public sealed record JournalEntry(string FilePath, string NewContent, string PreviousHash, string Encoding);

    /// <summary>多文件落盘前记录"打算写什么"（不是真原子，崩溃后靠它前滚补齐）。</summary>
    public void RecordJournal(string cookit, IReadOnlyList<JournalEntry> entries)
    {
        using SqliteConnection connection = Open();
        using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM write_journal WHERE cookit = $cookit;";
            clear.Parameters.AddWithValue("$cookit", cookit);
            clear.ExecuteNonQuery();
        }

        int sequence = 0;
        foreach (JournalEntry entry in entries)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO write_journal (cookit, seq, file_path, content, previous_hash, encoding) "
                + "VALUES ($cookit, $seq, $file, $content, $hash, $encoding);";
            command.Parameters.AddWithValue("$cookit", cookit);
            command.Parameters.AddWithValue("$seq", sequence++);
            command.Parameters.AddWithValue("$file", entry.FilePath);
            command.Parameters.AddWithValue("$content", entry.NewContent);
            command.Parameters.AddWithValue("$hash", entry.PreviousHash);
            command.Parameters.AddWithValue("$encoding", entry.Encoding);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>所有还没清掉的写前日志的 cookit（启动时用它前滚补齐未写完的落盘）。</summary>
    public IReadOnlyList<string> JournalCookits()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT cookit FROM write_journal ORDER BY cookit;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> cookits = [];
        while (reader.Read())
        {
            cookits.Add(reader.GetString(0));
        }

        return cookits;
    }

    /// <summary>读回未完成的写前日志（含写前 hash 与编码，供三态判断）。</summary>
    public IReadOnlyList<JournalEntry> ReadJournal(string cookit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT file_path, content, previous_hash, encoding FROM write_journal WHERE cookit = $cookit ORDER BY seq;";
        command.Parameters.AddWithValue("$cookit", cookit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<JournalEntry> entries = [];
        while (reader.Read())
        {
            entries.Add(new JournalEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }

        return entries;
    }

    /// <summary>删掉某个项目名下拟定对应的写前日志（清除追踪时用；调用方已先试过前滚）。</summary>
    public int ClearJournalsForProject(string projectPath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM write_journal WHERE cookit IN (SELECT cookit FROM drafts WHERE project_path = $project);";
        command.Parameters.AddWithValue("$project", Normalize(projectPath));
        return command.ExecuteNonQuery();
    }

    /// <summary>清理写前日志（落盘完成后调用）。</summary>
    public void ClearJournal(string cookit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM write_journal WHERE cookit = $cookit;";
        command.Parameters.AddWithValue("$cookit", cookit);
        command.ExecuteNonQuery();
    }

    /// <summary>所有未完成的拟定（启动时可以提示调用方"上次还有没落完的"）。</summary>
    public IReadOnlyList<DraftRecord> ListAll()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT project_path FROM drafts ORDER BY project_path;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> projects = [];
        while (reader.Read())
        {
            projects.Add(reader.GetString(0));
        }

        List<DraftRecord> records = [];
        foreach (string project in projects)
        {
            DraftRecord? record = Read(connection, project);
            if (record != null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static string Normalize(string projectPath)
    {
        return Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
    }

    private SqliteConnection Open()
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnection connection = new($"Data Source={_databasePath}");
        connection.Open();
        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText =
            """
            CREATE TABLE IF NOT EXISTS drafts (
                project_path TEXT PRIMARY KEY,
                cookit       TEXT NOT NULL,
                created_at   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS draft_edits (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path    TEXT NOT NULL,
                seq             INTEGER NOT NULL,
                type_path       TEXT NOT NULL,
                member_name     TEXT NOT NULL,
                content         TEXT NULL,
                action          TEXT NOT NULL,
                symbol_key      TEXT NOT NULL DEFAULT '',
                symbol_snapshot TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS write_journal (
                cookit        TEXT NOT NULL,
                seq           INTEGER NOT NULL,
                file_path     TEXT NOT NULL,
                content       TEXT NOT NULL,
                previous_hash TEXT NOT NULL DEFAULT '',
                encoding      TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS trackings (
                project_path    TEXT PRIMARY KEY,
                tracking_cookie TEXT NOT NULL,
                symbol_baseline TEXT NOT NULL,
                created_at      TEXT NOT NULL
            );
            """;
        schema.ExecuteNonQuery();

        // 旧库补列：sqlite 没有 ADD COLUMN IF NOT EXISTS，先问 PRAGMA table_info（幂等）
        EnsureColumn(connection, "draft_edits", "symbol_snapshot", "TEXT NULL");
        EnsureColumn(connection, "draft_edits", "symbol_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "write_journal", "previous_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "write_journal", "encoding", "TEXT NOT NULL DEFAULT ''");

        // 旧库去列：拟定只存"符号 + 意图"，file_path / baseline_hash / result_content 已废弃
        DropLegacyDraftEditColumns(connection);
        return connection;
    }

    /// <summary>旧库补列：缺了才 <c>ALTER TABLE</c>，已经有了就直接返回。</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        if (HasColumn(connection, table, column))
        {
            return;
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 旧库迁移：把 <c>draft_edits</c> 里已废弃的 <c>file_path</c> / <c>baseline_hash</c> /
    /// <c>result_content</c> 三列**物理去掉**（sqlite 不一定支持 DROP COLUMN，所以走"建新表 → 搬数据 → 换名"）。
    /// </summary>
    private static void DropLegacyDraftEditColumns(SqliteConnection connection)
    {
        if (!HasColumn(connection, "draft_edits", "file_path"))
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText =
                """
                CREATE TABLE draft_edits_rebuilt (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_path    TEXT NOT NULL,
                    seq             INTEGER NOT NULL,
                    type_path       TEXT NOT NULL,
                    member_name     TEXT NOT NULL,
                    content         TEXT NULL,
                    action          TEXT NOT NULL,
                    symbol_key      TEXT NOT NULL DEFAULT '',
                    symbol_snapshot TEXT NULL
                );
                INSERT INTO draft_edits_rebuilt (id, project_path, seq, type_path, member_name, content, action, symbol_key, symbol_snapshot)
                SELECT id, project_path, seq, type_path, member_name, content, action,
                       ifnull(symbol_key, ''), symbol_snapshot
                FROM draft_edits;
                DROP TABLE draft_edits;
                ALTER TABLE draft_edits_rebuilt RENAME TO draft_edits;
                """;
            rebuild.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static DraftRecord? Read(SqliteConnection connection, string projectPath)
    {
        using (SqliteCommand head = connection.CreateCommand())
        {
            head.CommandText = "SELECT cookit, created_at FROM drafts WHERE project_path = $project;";
            head.Parameters.AddWithValue("$project", projectPath);
            using SqliteDataReader reader = head.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            string cookit = reader.GetString(0);
            string createdAt = reader.GetString(1);
            reader.Close();

            using SqliteCommand edits = connection.CreateCommand();
            edits.CommandText =
                """
                SELECT id, seq, type_path, member_name, content, action, symbol_key, symbol_snapshot
                FROM draft_edits WHERE project_path = $project ORDER BY seq;
                """;
            edits.Parameters.AddWithValue("$project", projectPath);
            using SqliteDataReader editReader = edits.ExecuteReader();
            List<DraftEdit> list = [];
            while (editReader.Read())
            {
                list.Add(new DraftEdit(
                    editReader.GetInt64(0),
                    editReader.GetInt32(1),
                    editReader.GetString(2),
                    editReader.GetString(3),
                    editReader.IsDBNull(4) ? null : editReader.GetString(4),
                    editReader.GetString(5),
                    editReader.IsDBNull(6) ? "" : editReader.GetString(6),
                    editReader.IsDBNull(7) ? "" : editReader.GetString(7)));
            }

            return new DraftRecord(projectPath, cookit, createdAt, list);
        }
    }
}
