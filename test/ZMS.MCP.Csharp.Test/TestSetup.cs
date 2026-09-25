using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ZMS.MCP.Csharp.Draft;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Test;

/// <summary>
/// 测试进程启动时跑一次：把落盘流程里的 <c>dotnet format</c>、以及 MSBuild 求值都换成轻量实现。
///
/// 理由：这两样都是**别人的命令** —— 一次冷启动就是几秒、而且与文件数无关。
/// 端到端用例要验的是我们自己的语义（拟定、冲突判定、落盘、写前日志、引用计数），
/// 不该为别人命令的启动时间与稳定性买单（"知道用法就行"）。
/// 生产代码默认仍然真跑 <c>dotnet format</c> / <c>dotnet msbuild</c>。
/// </summary>
internal static class TestSetup
{
    private const string SkipFormatNote = "（测试：已跳过 dotnet format）";

    [ModuleInitializer]
    internal static void Initialize()
    {
        DraftService.FormatRunner = SkipFormat;
        MsBuildEvaluator.Override = EvaluateWithoutMsBuild;
    }

    private static (bool Succeeded, string Log) SkipFormat(string projectPath, IReadOnlyList<string> files)
    {
        return (true, SkipFormatNote);
    }

    /// <summary>
    /// 直接从 csproj 的 XML 读属性、扫源文件、拿当前进程已加载的程序集当引用 —— 不启动 MSBuild。
    /// （测试项目的 csproj 是最小形态，这些信息足够装配 Compilation。）
    /// </summary>
    private static MsBuildEvaluation EvaluateWithoutMsBuild(string projectPath)
    {
        string directory = Path.GetDirectoryName(projectPath) ?? ".";
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        XDocument document = XDocument.Load(projectPath);
        foreach (XElement element in document.Descendants())
        {
            if (element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements)
            {
                properties[element.Name.LocalName] = element.Value.Trim();
            }
        }

        properties.TryAdd("AssemblyName", Path.GetFileNameWithoutExtension(projectPath));
        properties.TryAdd("TargetFramework", "net11.0");
        properties.TryAdd("Nullable", "enable");
        properties.TryAdd("LangVersion", "latest");
        properties.TryAdd("DefineConstants", "TRACE;DEBUG");

        string separator = Path.DirectorySeparatorChar.ToString();
        List<string> compileItems = [];
        foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            // obj/bin 下的是生成文件，真实 MSBuild 也不会当编译项
            if (file.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase)
                || file.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            compileItems.Add(file);
        }

        // 引用集：运行时框架目录里的程序集（**带可空注释元数据**，签名渲染要靠它）+ 进程已加载的其它程序集。
        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        {
            references.Add(file);
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(assembly.Location);
            }
        }

        return new MsBuildEvaluation(properties, compileItems, [.. references]);
    }
}
