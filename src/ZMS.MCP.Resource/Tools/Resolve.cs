using ZMS.MCP.Resource.Archive;
using ZMS.MCP.Resource.Targeting;

namespace ZMS.MCP.Resource.Tools;

/// <summary>
/// 工具层的寻址：<c>target</c> / <c>toTarget</c> 除了「空 / <c>ftp:</c> / 压缩包路径」，
/// **还可以是追踪 cookie** —— 它就代表「那个正在被追踪的压缩包」。
///
/// 压缩包**不追踪就是只读的**：查看直接给路径就行；要往里写东西，先 `track_archive`
/// 拿到 cookie，再把这个 cookie 填进 `target` / `toTarget`。
/// </summary>
internal static class Resolve
{
    /// <summary>解析一次寻址；<paramref name="target"/> 是追踪 cookie 时认出它对应的压缩包。</summary>
    public static Address Address(string? target, string path)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            TrackedArchive? tracked = DraftStore.Find(target.Trim());
            if (tracked != null)
            {
                return new ArchiveAddress(tracked.ArchivePath, path);
            }
        }

        return Targeting.Address.Parse(target, path);
    }
}
