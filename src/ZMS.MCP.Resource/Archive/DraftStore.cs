using Microsoft.Data.Sqlite;

namespace ZMS.MCP.Resource.Archive;

/// <summary>一条攒着的拟定：要往包里加一份，或从包里删一个。</summary>
/// <param name="Id">自增序号。</param>
/// <param name="ArchivePath">哪个压缩包。</param>
/// <param name="Kind"><c>add</c> 或 <c>delete</c>。</param>
/// <param name="InnerPath">包内路径。</param>
/// <param name="Content">加进去的内容（<c>delete</c> 时没有）。</param>
/// <param name="Cookie">**被涉及条目自己**的凭据。</param>
public sealed record ArchiveDraft(long Id, string ArchivePath, string Kind, string InnerPath, byte[]? Content, string Cookie)
{
    /// <summary>是加还是删。</summary>
    public bool IsAdd => Kind == "add";
}

/// <summary>被追踪的压缩包。</summary>
public sealed record TrackedArchive(string ArchivePath, string Format, string Cookie, long BaselineSize, long BaselineTime);

/// <summary>
/// 拟定的落点：一张 sqlite 表，**放在 MCP 自己的目录里**，绝不写进被操作的项目。
/// 里面两样东西：**被追踪的压缩包**（追踪 cookie + 落盘基线）与**攒着的拟定指令**。
/// 进程重启也还在 —— 拿 cookie 回来接着做。
/// </summary>
public static class DraftStore
{
    /// <summary>测试用：把库指到别处。</summary>
    public static string? OverrideLocation { get; set; }

    /// <summary>库文件在哪。</summary>
    public static string Location
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OverrideLocation))
            {
                return OverrideLocation!;
            }

            string? custom = Environment.GetEnvironmentVariable("ZMS_MCP_DRAFTS");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZMS.MCP.Resource",
                    "drafts.db")
                : custom!;
        }
    }

    /// <summary>开始追踪一个压缩包，返回追踪 cookie。</summary>
    public static string Track(string archivePath, string format, long baselineSize, long baselineTime)
    {
        string cookie = Guid.NewGuid().ToString("D");
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Tracking (ArchivePath, Format, Cookie, BaselineSize, BaselineTime)
            VALUES ($path, $format, $cookie, $size, $time)
            ON CONFLICT(ArchivePath) DO UPDATE SET
                Format = excluded.Format,
                Cookie = excluded.Cookie,
                BaselineSize = excluded.BaselineSize,
                BaselineTime = excluded.BaselineTime;
            """;
        command.Parameters.AddWithValue("$path", Key(archivePath));
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$cookie", cookie);
        command.Parameters.AddWithValue("$size", baselineSize);
        command.Parameters.AddWithValue("$time", baselineTime);
        command.ExecuteNonQuery();
        return cookie;
    }

    /// <summary>按追踪 cookie 找出是哪个包。</summary>
    public static TrackedArchive? Find(string cookie)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ArchivePath, Format, Cookie, BaselineSize, BaselineTime FROM Tracking WHERE Cookie = $cookie;";
        command.Parameters.AddWithValue("$cookie", cookie.Trim());

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new TrackedArchive(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4))
            : null;
    }

    /// <summary>这个包正在被追踪吗（按包路径问）。</summary>
    public static TrackedArchive? FindByPath(string archivePath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ArchivePath, Format, Cookie, BaselineSize, BaselineTime FROM Tracking WHERE ArchivePath = $path;";
        command.Parameters.AddWithValue("$path", Key(archivePath));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new TrackedArchive(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4))
            : null;
    }

    /// <summary>解除追踪，并把攒着的拟定一并清掉。返回清掉几条。</summary>
    public static int Untrack(TrackedArchive tracked)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        int removed;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM Drafts WHERE ArchivePath = $path;";
            count.Parameters.AddWithValue("$path", tracked.ArchivePath);
            removed = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DELETE FROM Drafts WHERE ArchivePath = $path;";
            drop.Parameters.AddWithValue("$path", tracked.ArchivePath);
            drop.ExecuteNonQuery();
        }

        using (SqliteCommand untrack = connection.CreateCommand())
        {
            untrack.Transaction = transaction;
            untrack.CommandText = "DELETE FROM Tracking WHERE ArchivePath = $path;";
            untrack.Parameters.AddWithValue("$path", tracked.ArchivePath);
            untrack.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    /// <summary>攒一条拟定。</summary>
    public static void Stage(string archivePath, string kind, string innerPath, byte[]? content, string cookie)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Drafts (ArchivePath, Kind, InnerPath, Content, Cookie) VALUES ($path, $kind, $inner, $content, $cookie);";
        command.Parameters.AddWithValue("$path", archivePath);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$inner", innerPath);
        command.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
        command.Parameters.AddWithValue("$cookie", cookie);
        command.ExecuteNonQuery();
    }

    /// <summary>这个包上攒了哪些。</summary>
    public static IReadOnlyList<ArchiveDraft> DraftsOf(string archivePath)
    {
        List<ArchiveDraft> drafts = [];
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ArchivePath, Kind, InnerPath, Content, Cookie FROM Drafts WHERE ArchivePath = $path ORDER BY Id;";
        command.Parameters.AddWithValue("$path", archivePath);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            drafts.Add(new ArchiveDraft(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : (byte[])reader[4],
                reader.GetString(5)));
        }

        return drafts;
    }

    /// <summary>落盘成功后清掉这个包的拟定。</summary>
    public static int ClearDrafts(string archivePath)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Drafts WHERE ArchivePath = $path;";
        command.Parameters.AddWithValue("$path", archivePath);
        return command.ExecuteNonQuery();
    }

    /// <summary>落盘之后把基线换成新的。</summary>
    public static void Rebaseline(TrackedArchive tracked, long size, long time)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE Tracking SET BaselineSize = $size, BaselineTime = $time WHERE ArchivePath = $path;";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$time", time);
        command.Parameters.AddWithValue("$path", tracked.ArchivePath);
        command.ExecuteNonQuery();
    }

    private static string Key(string archivePath)
    {
        return Path.GetFullPath(archivePath);
    }

    private static SqliteConnection Open()
    {
        string where = Location;
        Directory.CreateDirectory(Path.GetDirectoryName(where)!);
        SqliteConnection connection = new($"Data Source={where};Pooling=False");
        connection.Open();

        using SqliteCommand shape = connection.CreateCommand();
        shape.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Tracking (
                ArchivePath TEXT PRIMARY KEY,
                Format TEXT NOT NULL,
                Cookie TEXT NOT NULL,
                BaselineSize INTEGER NOT NULL,
                BaselineTime INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Drafts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ArchivePath TEXT NOT NULL,
                Kind TEXT NOT NULL,
                InnerPath TEXT NOT NULL,
                Content BLOB,
                Cookie TEXT NOT NULL
            );
            """;
        shape.ExecuteNonQuery();
        return connection;
    }
}
