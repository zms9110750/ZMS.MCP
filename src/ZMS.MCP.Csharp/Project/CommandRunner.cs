using System.Diagnostics;
using System.Text;

namespace ZMS.MCP.Csharp.Project;

/// <summary>一次命令行的结果。</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>命令行通道：参数用 <c>ArgumentList</c> 逐个传，避免自己拼引号出错。</summary>
public static class CommandRunner
{
    private const int DefaultTimeoutSeconds = 300;

    /// <summary>
    /// 累积输出的上限（字符数）。dotnet build/restore 的输出可能极大，而 MCP 服务是长驻进程：
    /// 不设上限就是无界内存增长。超了就停止累积，只在末尾标注一次。
    /// </summary>
    private const int OutputLimit = 200_000;

    private static void AppendCapped(StringBuilder output, string line, ref bool truncated)
    {
        if (output.Length >= OutputLimit)
        {
            if (!truncated)
            {
                truncated = true;
                output.AppendLine("（输出过长，后续已省略）");
            }

            return;
        }

        output.AppendLine(line);
    }

    /// <summary>
    /// 跑一个命令并等它结束。超时会连**整棵进程树**一起杀，避免留下孤儿进程卡住构建输出。
    /// </summary>
    public static CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();
        bool truncated = false;
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                lock (output)
                {
                    AppendCapped(output, eventArgs.Data, ref truncated);
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                lock (output)
                {
                    AppendCapped(output, eventArgs.Data, ref truncated);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // 进程已经退出或平台不支持整树杀，忽略
            }

            throw new TimeoutException($"命令超时（{timeoutSeconds}s）：{fileName} {string.Join(' ', arguments)}");
        }

        process.WaitForExit();
        lock (output)
        {
            return new CommandResult(process.ExitCode, output.ToString());
        }
    }
}
