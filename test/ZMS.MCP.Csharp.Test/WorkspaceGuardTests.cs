using Xunit;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 工作空间边界的判定。
///
/// 它**不是**安全边界（宿主自己就能绕过，MCP 的 roots 也不被强制）—— 它要保证的是
/// "每次调用的路径参数都落在本次会话的工作区里"，越界时说清"你给的是哪、边界在哪、怎么改"。
/// </summary>
public sealed class WorkspaceGuardTests
{
    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zms-workspace-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return Path.GetFullPath(directory);
    }

    [Fact]
    public void A_path_inside_the_workspace_passes()
    {
        string root = NewDirectory();
        string inside = Path.Combine(root, "sub", "Demo.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);

        Assert.True(WorkspaceGuard.IsInside(inside, [root], out string full, out _));
        Assert.Equal(Path.GetFullPath(inside), full);
    }

    [Fact]
    public void A_path_outside_the_workspace_is_refused_with_a_message_that_says_what_to_do()
    {
        string root = NewDirectory();
        string outside = Path.Combine(NewDirectory(), "Demo.csproj");

        Assert.False(WorkspaceGuard.IsInside(outside, [root], out _, out string message));
        Assert.Contains("不在本次会话的工作区里", message, StringComparison.Ordinal);
        Assert.Contains(outside, message, StringComparison.Ordinal);                    // 你给的是哪
        Assert.Contains(root, message, StringComparison.Ordinal);                       // 边界在哪
        Assert.Contains("ZMS_MCP_WORKSPACE", message, StringComparison.Ordinal);        // 怎么改
    }

    [Fact]
    public void A_sibling_directory_with_the_same_prefix_does_not_count_as_inside()
    {
        // "C:\a" 不能匹配 "C:\ab" —— 前缀比较必须带上目录分隔符
        string root = NewDirectory();
        string sibling = root + "x";
        Directory.CreateDirectory(sibling);

        Assert.False(WorkspaceGuard.IsInside(Path.Combine(sibling, "Demo.csproj"), [root], out _, out _));
    }

    [Fact]
    public void Going_up_with_dot_dot_leaves_the_workspace()
    {
        string root = NewDirectory();
        string inside = Path.Combine(root, "a");
        Directory.CreateDirectory(inside);

        Assert.False(WorkspaceGuard.IsInside(Path.Combine(inside, "..", "..", "Demo.csproj"), [root], out _, out _));
    }

    [Fact]
    public void No_workspace_means_no_restriction()
    {
        Assert.True(WorkspaceGuard.IsInside(Path.Combine(Path.GetTempPath(), "anything.csproj"), [], out _, out _));
        Assert.Contains("未设置边界", WorkspaceGuard.Describe([]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_root_itself_counts_as_inside()
    {
        string root = NewDirectory();

        Assert.True(WorkspaceGuard.IsInside(root, [root], out _, out _));
    }

    [Fact]
    public void Several_workspaces_all_count()
    {
        string first = NewDirectory();
        string second = NewDirectory();
        IReadOnlyList<string> roots = [first, second];

        Assert.True(WorkspaceGuard.IsInside(Path.Combine(first, "a.csproj"), roots, out _, out _));
        Assert.True(WorkspaceGuard.IsInside(Path.Combine(second, "b.csproj"), roots, out _, out _));
        Assert.False(WorkspaceGuard.IsInside(Path.Combine(NewDirectory(), "c.csproj"), roots, out _, out _));
    }
}
