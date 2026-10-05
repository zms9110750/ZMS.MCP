using Microsoft.VisualBasic.FileIO;
using ZMS.MCP.Resource.Credentials;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Local;

/// <summary>
/// 删本地对象（文件或目录，连同里面）。
///
/// **一律走回收站**；进不去回收站（过大装不进、网络盘、可移动盘、只读卷）就**不执行**，
/// 交用户手动处理 —— 这个高危操作不让 agent 干。远端同理（远端根本没有回收站，那是工具层拦的）。
/// 凭据是必须的：删目录时它是"读整棵树"那一份。
/// </summary>
public static class DeleteService
{
    /// <summary>删掉它。</summary>
    public static string Run(LocalAddress address, string cookie)
    {
        string path = address.InnerPath;
        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new ArgumentException($"不存在：{path}");
        }

        string current = isDirectory
            ? Cookie.Of(path, Size(path), Directory.GetLastWriteTimeUtc(path))
            : Cookie.Of(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));

        if (!Cookie.Matches(cookie, current))
        {
            return $"# 没执行：凭据对不上\n- {path}\n"
                + $"- 你给的：`{cookie.Trim()}`\n"
                + $"- 现在算出来：`{current}`\n"
                + "- 重新读一次（`read` / `list`）拿新凭据，再来。\n";
        }

        try
        {
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                $"进不了回收站，所以没有删：{exception.Message} —— 这个操作交给用户手动处理（不做硬删）。");
        }

        return $"# 已删除（回收站）\n- {path}\n- 去回收站能找回来。\n";
    }

    private static long Size(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", System.IO.SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }
}
