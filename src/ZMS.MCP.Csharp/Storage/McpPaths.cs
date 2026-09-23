namespace ZMS.MCP.Csharp.Storage;

/// <summary>
/// MCP 自己的数据目录：拟定状态、漏洞索引缓存都放这里，**不放进被操作的项目**。
/// </summary>
public static class McpPaths
{
    /// <summary>数据目录：<c>%LOCALAPPDATA%\ZMS.MCP.Csharp</c>（拿不到就退回临时目录）。</summary>
    public static string DataDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        string directory = Path.Combine(root, "ZMS.MCP.Csharp");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>拟定状态的 sqlite 文件。</summary>
    public static string DraftDatabase()
    {
        return Path.Combine(DataDirectory(), "drafts.db");
    }

    /// <summary>漏洞索引的本地缓存目录。</summary>
    public static string VulnerabilityCacheDirectory()
    {
        string directory = Path.Combine(DataDirectory(), "vulnerabilities");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
