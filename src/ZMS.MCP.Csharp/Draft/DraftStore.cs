using Microsoft.Data.Sqlite;
using ZMS.MCP.Csharp.Storage;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>
/// 拟定里的一条编辑。存的是**意图**（要改哪个类型/成员、内容是什么）和**算好的结果内容**，
/// 以及该文件在拟定开始时的基线 hash（落盘前用来发现"被别人改过"）。
/// </summary>
public sealed record DraftEdit(
    long Id,
    int Sequence,
    string TypePath,
    string MemberName,
    string? RequestedContent,
    string FilePath,
    string BaselineHash,
    string ResultContent,
    string Action)
{
    public bool IsDelete => RequestedContent == null;
}

/// <summary>一个项目上的拟定。</summary>
public sealed record DraftRecord(string ProjectPath, string Cookit, string CreatedAt, IReadOnlyList<DraftEdit> Edits);

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
        string filePath,
        string baselineHash,
        string resultContent,
        string action)
    {
        string normalized = Normalize(projectPath);
        DraftRecord record = GetOrCreate(normalized);
        int sequence = record.Edits.Count == 0 ? 1 : record.Edits.Max(edit => edit.Sequence) + 1;

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO draft_edits (project_path, seq, type_path, member_name, content, file_path, baseline_hash, result_content, action)
            VALUES ($project, $seq, $type, $member, $content, $file, $hash, $result, $action);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$project", normalized);
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$type", typePath);
        command.Parameters.AddWithValue("$member", memberName);
        command.Parameters.AddWithValue("$content", requestedContent == null ? DBNull.Value : requestedContent);
        command.Parameters.AddWithValue("$file", filePath);
        command.Parameters.AddWithValue("$hash", baselineHash);
        command.Parameters.AddWithValue("$result", resultContent);
        command.Parameters.AddWithValue("$action", action);
        long id = Convert.ToInt64(command.ExecuteScalar());

        return new DraftEdit(id, sequence, typePath, memberName, requestedContent, filePath, baselineHash, resultContent, action);
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

    /// <summary>多文件落盘前记录"打算写什么"（不是真原子，崩溃后靠它前滚补齐）。</summary>
    public void RecordJournal(string cookit, IReadOnlyList<KeyValuePair<string, string>> files)
    {
        using SqliteConnection connection = Open();
        using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM write_journal WHERE cookit = $cookit;";
            clear.Parameters.AddWithValue("$cookit", cookit);
            clear.ExecuteNonQuery();
        }

        int sequence = 0;
        foreach (KeyValuePair<string, string> file in files)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO write_journal (cookit, seq, file_path, content) VALUES ($cookit, $seq, $file, $content);";
            command.Parameters.AddWithValue("$cookit", cookit);
            command.Parameters.AddWithValue("$seq", sequence++);
            command.Parameters.AddWithValue("$file", file.Key);
            command.Parameters.AddWithValue("$content", file.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>读回未完成的写前日志。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ReadJournal(string cookit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, content FROM write_journal WHERE cookit = $cookit ORDER BY seq;";
        command.Parameters.AddWithValue("$cookit", cookit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<KeyValuePair<string, string>> files = [];
        while (reader.Read())
        {
            files.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }

        return files;
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
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                project_path   TEXT NOT NULL,
                seq            INTEGER NOT NULL,
                type_path      TEXT NOT NULL,
                member_name    TEXT NOT NULL,
                content        TEXT NULL,
                file_path      TEXT NOT NULL,
                baseline_hash  TEXT NOT NULL,
                result_content TEXT NOT NULL,
                action         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS write_journal (
                cookit    TEXT NOT NULL,
                seq       INTEGER NOT NULL,
                file_path TEXT NOT NULL,
                content   TEXT NOT NULL
            );
            """;
        schema.ExecuteNonQuery();
        return connection;
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
                SELECT id, seq, type_path, member_name, content, file_path, baseline_hash, result_content, action
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
                    editReader.GetString(6),
                    editReader.GetString(7),
                    editReader.GetString(8)));
            }

            return new DraftRecord(projectPath, cookit, createdAt, list);
        }
    }
}
