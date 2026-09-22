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
    /// 每个解决方案列出它描述的项目，再列出"客观在它所在文件夹下、却没被它描述"的项目；
    /// 没被任何解决方案描述（也没落在某个解决方案文件夹下）的项目作为散装项目返回。
    /// </summary>
    /// <param name="folder">扫描根目录。</param>
    /// <param name="depth">递归深度（小于等于 0 时按 4）。</param>
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

        List<string> solutions = scanned
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".sln" or ".slnx")
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> projectFiles = scanned
            .Where(file => string.Equals(Path.GetExtension(file), ".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        List<ScanSolutionGroup> groups = [];
        foreach (string solution in solutions)
        {
            IReadOnlyList<SolutionProject> described;
            try
            {
                described = ReadProjects(solution);
            }
            catch (Exception)
            {
                // 坏掉的解决方案文件不阻塞扫描，当作没描述任何项目
                described = [];
            }

            string? solutionDirectory = Path.GetDirectoryName(solution);
            List<string> describedPaths = [];
            foreach (SolutionProject project in described)
            {
                describedPaths.Add(Describe(root, project.AbsolutePath));
                claimed.Add(project.AbsolutePath);
            }

            List<string> extra = [];
            if (solutionDirectory != null)
            {
                foreach (string project in projectFiles)
                {
                    if (!IsUnder(solutionDirectory, project))
                    {
                        continue;
                    }

                    // 落在解决方案文件夹下的项目（含被描述的）都不再算散装
                    claimed.Add(project);
                    bool alreadyDescribed = described.Any(
                        entry => string.Equals(entry.AbsolutePath, project, StringComparison.OrdinalIgnoreCase));
                    if (!alreadyDescribed)
                    {
                        extra.Add(Describe(root, project));
                    }
                }
            }

            groups.Add(new ScanSolutionGroup(Describe(root, solution), describedPaths, extra));
        }

        List<string> loose = projectFiles
            .Where(project => !claimed.Contains(project))
            .Select(project => Describe(root, project))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult(groups, loose);
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

    private static bool IsUnder(string directory, string file)
    {
        string prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
                if (name is "bin" or "obj" or ".git" or ".vs" or "node_modules")
                {
                    continue;
                }

                pending.Push((subdirectory, level + 1));
            }
        }

        return results;
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

    public static Task<(int ExitCode, string Output)> AddProjectToSolution(string solutionPath, string projectPath)
    {
        string solution = Path.GetFullPath(solutionPath);
        string project = Path.GetFullPath(projectPath);
        return RunDotnetAsync("sln", solution, "add", project);
    }

    public static Task<(int ExitCode, string Output)> RemoveProjectFromSolution(string solutionPath, string projectPath)
    {
        string solution = Path.GetFullPath(solutionPath);
        string project = Path.GetFullPath(projectPath);
        return RunDotnetAsync("sln", solution, "remove", project);
    }

    public static Task<(int ExitCode, string Output)> CreateProject(string folder, string name, string template)
    {
        string target = Path.GetFullPath(folder);
        Directory.CreateDirectory(target);
        return RunDotnetAsync("new", template, "-n", name, "-o", target);
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
