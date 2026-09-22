using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZMS.MCP.Csharp.Roslyn;

/// <summary>解决方案内的一个项目。</summary>
public sealed record SolutionProject(string Name, string AbsolutePath, string RelativePath);

/// <summary>扫盘、解决方案读写（.sln / .slnx）与 dotnet CLI 调用。</summary>
public static class SolutionExplorer
{
    private const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static readonly Regex SlnProjectRegex = new(
        "Project\\(\"\\{(?<type>[0-9A-Fa-f-]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> Scan(string folder, int depth, string kind)
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

        HashSet<string> extensions = ParseKinds(kind);
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
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(extension))
                {
                    continue;
                }

                results.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
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

        results.Sort(StringComparer.OrdinalIgnoreCase);
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
