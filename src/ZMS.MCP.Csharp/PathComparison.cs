namespace ZMS.MCP.Csharp;

/// <summary>
/// 路径比较的统一口径。文件系统的大小写敏感性**因平台而异**：
/// Windows 不区分（`A.cs` 与 `a.cs` 是同一个文件），Linux/macOS 区分。
/// 所以这里用条件编译按目标平台选，不能全仓库一律用不敏感比较 ——
/// 否则在 Linux 上会把两个不同文件当成同一个，路径去重、许可校验都会出错。
/// </summary>
/// <remarks>
/// NuGet 包 id、MSBuild 属性名、XML 名这些**与平台无关**的不敏感比较，
/// 仍应直接用 <see cref="StringComparer.OrdinalIgnoreCase"/>，不要走这里。
/// </remarks>
internal static class PathComparison
{
    /// <summary>给 HashSet/Dictionary 用的比较器。</summary>
    public static StringComparer Comparer
    {
        get
        {
#if WINDOWS
            return StringComparer.OrdinalIgnoreCase;
#else
            return StringComparer.Ordinal;
#endif
        }
    }

    /// <summary>给 Equals/StartsWith/EndsWith/OrderBy 用的比较方式。</summary>
    public static StringComparison Comparison
    {
        get
        {
#if WINDOWS
            return StringComparison.OrdinalIgnoreCase;
#else
            return StringComparison.Ordinal;
#endif
        }
    }
}