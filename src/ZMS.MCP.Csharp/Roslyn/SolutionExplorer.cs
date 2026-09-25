using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>解决方案内的一个项目。</summary>
public sealed record SolutionProject(string Name, string AbsolutePath, string RelativePath);

/// <summary>
/// 磁盘上的一个解决方案：它描述的项目，以及客观在它所在文件夹下、却没被它描述的项目。
/// </summary>
public sealed class ScanSolutionGroup
{
    /// <summary>解决方案路径（相对扫描根；在扫描根之外时照实给绝对路径）。</summary>
    public string SolutionPath { get; }

    /// <summary>解决方案里描述的项目（相对扫描根，或照实的绝对路径）。</summary>
    public IReadOnlyList<string> Projects { get; }

    /// <summary>客观在解决方案所在文件夹下、但没被它描述的项目。</summary>
    public IReadOnlyList<string> ExtraProjects { get; }

    public ScanSolutionGroup(
        string solutionPath,
        IReadOnlyList<string> projects,
        IReadOnlyList<string> extraProjects)
    {
        SolutionPath = solutionPath;
        Projects = projects;
        ExtraProjects = extraProjects;
    }
}

/// <summary>扫盘结果：按解决方案分组 + 不属于任何解决方案的散装项目。</summary>
public sealed class ScanResult
{
    public IReadOnlyList<ScanSolutionGroup> Solutions { get; }

    public IReadOnlyList<string> LooseProjects { get; }

    public ScanResult(IReadOnlyList<ScanSolutionGroup> solutions, IReadOnlyList<string> looseProjects)
    {
        Solutions = solutions;
        LooseProjects = looseProjects;
    }
}

/// <summary>扫盘、解决方案读写（.sln / .slnx）与 dotnet CLI 调用。</summary>
public static class SolutionExplorer
{
    private const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static readonly Regex SlnProjectRegex = new(
        "Project\\(\"\\{(?<type>[0-9A-Fa-f-]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// 扫盘并按解决方案分组：
    /// 每个解决方案列出它描述的项目，再列出"客观在它所在文件夹下、却没被任何解决方案描述"的项目；
    /// 既没被描述、也不落在任何解决方案文件夹下的项目作为散装项目返回。
    /// 项目只归给包含它的、目录最深（最贴近）的那个解决方案。
    /// </summary>
    /// <param name="folder">扫描根目录。</param>
    /// <param name="depth">递归深度（小于等于 0 时按 4）；描述的项目不受此限制。</param>
    /// <param name="kinds">要包含的类型，逗号分隔：sln,slnx,csproj；空 = 全部。</param>
    public static ScanResult ScanTree(string folder, int depth, string kinds)
    {
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        if (depth <= 0)
        {
            depth = 4;
        }

        HashSet<string> extensions = ParseKinds(kinds);
        List<string> scanned = EnumerateFiles(root, depth, extensions);
        List<string> solutions = CollectSolutions(scanned);
        List<string> projectFiles = scanned
            .Where(file => string.Equals(Path.GetExtension(file), ".csproj", PathComparison.Comparison))
            .ToList();

        // 每个解决方案描述的项目；被任何解决方案描述过的项目都不再算"额外关系"或"散装"
        Dictionary<string, IReadOnlyList<SolutionProject>> describedBySolution = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> describedAnywhere = new(StringComparer.OrdinalIgnoreCase);
        foreach (string solution in solutions)
        {
            IReadOnlyList<SolutionProject> described = ReadProjectsSafe(solution);
            describedBySolution[solution] = described;
            foreach (SolutionProject project in described)
            {
                describedAnywhere.Add(project.AbsolutePath);
            }
        }

        // 项目归属：包含它的、目录最深的那个解决方案
        Dictionary<string, string> owner = new(StringComparer.OrdinalIgnoreCase);
        foreach (string project in projectFiles)
        {
            string? best = null;
            int bestLength = -1;
            foreach (string solution in solutions)
            {
                string? directory = Path.GetDirectoryName(solution);
                if (directory == null || !IsUnder(directory, project))
                {
                    continue;
                }

                if (directory.Length > bestLength)
                {
                    best = solution;
                    bestLength = directory.Length;
                }
            }

            if (best != null)
            {
                owner[project] = best;
            }
        }

        List<ScanSolutionGroup> groups = [];
        foreach (string solution in solutions)
        {
            List<string> describedPaths = describedBySolution[solution]
                .Select(project => Describe(root, project.AbsolutePath))
                .ToList();

            List<string> extra = projectFiles
                .Where(project => owner.TryGetValue(project, out string? ownerSolution) && ownerSolution == solution)
                .Where(project => !describedAnywhere.Contains(project))
                .Select(project => Describe(root, project))
                .OrderBy(path => path, PathComparison.Comparer)
                .ToList();

            groups.Add(new ScanSolutionGroup(Describe(root, solution), describedPaths, extra));
        }

        List<string> loose = projectFiles
            .Where(project => !describedAnywhere.Contains(project) && !owner.ContainsKey(project))
            .Select(project => Describe(root, project))
            .OrderBy(path => path, PathComparison.Comparer)
            .ToList();

        return new ScanResult(groups, loose);
    }

    /// <summary>找出解决方案文件；同一目录同名时 .slnx 优先于 .sln（只算一个解决方案）。</summary>
    private static List<string> CollectSolutions(List<string> scanned)
    {
        Dictionary<string, string> byKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in scanned)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not ".sln" and not ".slnx")
            {
                continue;
            }

            string directory = Path.GetDirectoryName(file) ?? "";
            string key = Path.Combine(directory, Path.GetFileNameWithoutExtension(file));
            if (!byKey.TryGetValue(key, out string? existing))
            {
                byKey[key] = file;
                continue;
            }

            if (Path.GetExtension(existing).ToLowerInvariant() == ".sln" && extension == ".slnx")
            {
                byKey[key] = file;
            }
        }

        return byKey.Values.OrderBy(file => file, PathComparison.Comparer).ToList();
    }

    private static IReadOnlyList<SolutionProject> ReadProjectsSafe(string solutionPath)
    {
        try
        {
            return ReadProjects(solutionPath);
        }
        catch (Exception)
        {
            // 坏掉的解决方案文件不阻塞扫描，当作没描述任何项目
            return [];
        }
    }

    /// <summary>相对扫描根描述路径；在扫描根之外时照实给绝对路径（正斜杠）。</summary>
    private static string Describe(string root, string absolutePath)
    {
        string relative = Path.GetRelativePath(root, absolutePath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return absolutePath.Replace('\\', '/');
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// <paramref name="file"/> 是否在 <paramref name="directory"/> 之下（含多级）。
    /// 前缀末尾保证有且只有一个分隔符 —— 盘根（<c>C:\</c>）本身已带分隔符，
    /// 不能再拼一个（<c>TrimEndingDirectorySeparator</c> 对盘根是保留而不是去掉）。
    /// </summary>
    internal static bool IsUnder(string directory, string file)
    {
        string normalized = Path.GetFullPath(directory);
        string prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(prefix, PathComparison.Comparison);
    }

    private static List<string> EnumerateFiles(string root, int depth, HashSet<string> extensions)
    {
        List<string> results = [];
        Stack<(string Directory, int Level)> pending = new();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (string current, int level) = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    results.Add(Path.GetFullPath(file));
                }
            }

            if (level >= depth)
            {
                continue;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                string name = Path.GetFileName(subdirectory);
                if (IsSkippedDirectory(name))
                {
                    continue;
                }

                pending.Push((subdirectory, level + 1));
            }
        }

        return results;
    }

    /// <summary>构建输出与工具目录：不区分大小写（Windows 上 Bin / OBJ 也要跳过）。</summary>
    private static bool IsSkippedDirectory(string name)
    {
        return name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<SolutionProject> ReadProjects(string solutionPath)
    {
        string fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Solution file not found: {fullPath}");
        }

        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        List<SolutionProject> projects = [];

        if (extension == ".slnx")
        {
            XDocument document = XDocument.Load(fullPath);
            if (document.Root == null)
            {
                return projects;
            }

            foreach (XElement element in document.Root.Descendants().Where(element => element.Name.LocalName == "Project"))
            {
                string? relative = element.Attribute("Path")?.Value;
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
                string absolute = Path.GetFullPath(Path.Combine(directory, normalized));
                projects.Add(new SolutionProject(Path.GetFileNameWithoutExtension(absolute), absolute, normalized));
            }
        }
        else
        {
            string content = File.ReadAllText(fullPath);
            foreach (Match match in SlnProjectRegex.Matches(content))
            {
                if (match.Groups["type"].Value.Equals(SolutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
                string absolute = Path.GetFullPath(Path.Combine(directory, relative));
                projects.Add(new SolutionProject(match.Groups["name"].Value, absolute, relative));
            }
        }

        return projects;
    }

    /// <summary>
    /// 把已有项目加入解决方案（<c>dotnet sln &lt;slnx&gt; add &lt;csproj&gt;</c>）。
    /// 编辑解决方案只支持 slnx（见需求文档），所以 .sln 会直接被拒绝；
    /// <paramref name="folder"/> 非空时用 <c>--solution-folder</c> 把项目放进 slnx 的虚拟文件夹。
    /// </summary>
    public static Task<(int ExitCode, string Output)> AddProjectToSolution(string solutionPath, string projectPath, string folder)
    {
        string solution = RequireSlnx(solutionPath);
        string project = Path.GetFullPath(projectPath);
        if (!File.Exists(project))
        {
            throw new FileNotFoundException($"要加入的项目不存在：{project}（创建项目不属于本工具，请自己跑命令行）。");
        }

        List<string> arguments = ["sln", solution, "add", project];
        if (!string.IsNullOrWhiteSpace(folder))
        {
            // slnx 里的虚拟文件夹路径用正斜杠（dotnet CLI 自己会补出 /src/ 这样的层级）
            arguments.Add("--solution-folder");
            arguments.Add(folder.Trim().Replace('\\', '/').Trim('/'));
        }

        return RunDotnetAsync([.. arguments]);
    }

    /// <summary>从解决方案移除项目（<c>dotnet sln &lt;slnx&gt; remove &lt;csproj&gt;</c>）；只支持 slnx。</summary>
    public static Task<(int ExitCode, string Output)> RemoveProjectFromSolution(string solutionPath, string projectPath)
    {
        string solution = RequireSlnx(solutionPath);
        string project = Path.GetFullPath(projectPath);
        return RunDotnetAsync("sln", solution, "remove", project);
    }

    /// <summary>编辑解决方案只对 slnx 进行（要改 .sln 先迁移）。</summary>
    internal static string RequireSlnx(string solutionPath)
    {
        string solution = Path.GetFullPath(solutionPath);
        if (!File.Exists(solution))
        {
            throw new FileNotFoundException($"解决方案不存在：{solution}");
        }

        if (!solution.EndsWith(".slnx", PathComparison.Comparison))
        {
            throw new InvalidOperationException(
                $"编辑解决方案只能对 .slnx 进行：{Path.GetFileName(solution)}。请先用「迁移解决方案为 slnx」把它迁过来。");
        }

        return solution;
    }

    public static async Task<(int ExitCode, string Output)> RunDotnetAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'dotnet'. Is the .NET SDK on PATH?");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出，忽略
            }

            return (-1, "dotnet command timed out after 120s.");
        }

        string output = (await stdout).Trim() + "\n" + (await stderr).Trim();
        return (process.ExitCode, output.Trim());
    }

    private static HashSet<string> ParseKinds(string kind)
    {
        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in kind.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = item.Trim().TrimStart('*', '.');
            if (trimmed.Length == 0)
            {
                continue;
            }

            extensions.Add("." + trimmed.ToLowerInvariant());
        }

        if (extensions.Count == 0)
        {
            extensions.Add(".sln");
            extensions.Add(".slnx");
            extensions.Add(".csproj");
        }

        return extensions;
    }
}
