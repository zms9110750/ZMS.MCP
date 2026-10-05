using Xunit;
using ZMS.MCP.Workflow.Models;
using ZMS.MCP.Workflow.Yaml;
using WorkflowDocument = ZMS.MCP.Workflow.Models.Workflow;

namespace ZMS.MCP.Workflow.Test;

public class WorkflowWriterTests
{
    private static WorkflowDocument Sample()
    {
        return new WorkflowDocument
        {
            Name = "CI",
            On = new Triggers
            {
                Push = new PushTrigger { Branches = ["main", "release/*"] },
                WorkflowDispatch = new WorkflowDispatchTrigger
                {
                    Inputs = new SortedDictionary<string, DispatchInput>
                    {
                        ["target"] = new DispatchInput
                        {
                            Description = "要构建的目标",
                            Required = true,
                            Type = "choice",
                            Options = ["win", "linux"],
                        },
                    },
                },
            },
            Permissions = new Permissions { All = "read-all" },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job
                {
                    RunsOn = "windows-latest",
                    Steps =
                    [
                        new Step { Uses = "actions/checkout@v4" },
                        new Step { Name = "构建", Run = "dotnet build" },
                    ],
                },
                ["test"] = new Job
                {
                    RunsOn = "windows-latest",
                    Needs = ["build"],
                    Steps = [new Step { Run = "dotnet test" }],
                },
            },
        };
    }

    [Fact]
    public void 同一棵树两次生成字节一致()
    {
        Assert.Equal(WorkflowWriter.Write(Sample()), WorkflowWriter.Write(Sample()));
    }

    [Fact]
    public void 缩进固定两格且键序按代码定()
    {
        string yaml = WorkflowWriter.Write(Sample());

        string escaped = yaml.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        Assert.True(yaml.Contains("\njobs:\n", StringComparison.Ordinal), "A: " + escaped);
        Assert.True(yaml.Contains("\n  build:\n", StringComparison.Ordinal), "B: " + escaped);

        // jobs 用有序字典：build 在前、test 在后，不随插入次序变
        Assert.True(yaml.IndexOf("  build:", StringComparison.Ordinal) < yaml.IndexOf("  test:", StringComparison.Ordinal), "C: " + escaped);

        // 触发的顺序也是代码里的顺序：push 在 workflow_dispatch 之前
        Assert.True(yaml.IndexOf("  push:", StringComparison.Ordinal) < yaml.IndexOf("  workflow_dispatch:", StringComparison.Ordinal), "D: " + escaped);
    }

    [Fact]
    public void matrix_的表达式那一维也能写出来()
    {
        WorkflowDocument workflow = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job
                {
                    RunsOn = "ubuntu-latest",
                    Strategy = new Strategy
                    {
                        MatrixExpression = new SortedDictionary<string, string>
                        {
                            ["fw"] = "${{ fromJson(needs.build.outputs.tfm_json) }}",
                        },
                        Matrix = new SortedDictionary<string, List<string>>
                        {
                            ["rid"] = ["win-x64", "linux-x64"],
                        },
                        FailFast = false,
                    },
                    Steps = [new Step { Run = "echo" }],
                },
            },
        };

        string yaml = WorkflowWriter.Write(workflow);

        Assert.Contains("fw: ${{ fromJson(needs.build.outputs.tfm_json) }}", yaml, StringComparison.Ordinal);
        Assert.Contains("rid:", yaml, StringComparison.Ordinal);
        Assert.Contains("fail-fast: false", yaml, StringComparison.Ordinal);
        Assert.Empty(WorkflowValidator.Check(workflow));
    }

    [Fact]
    public void matrix_同一维两种写法会被校验拒掉()
    {
        WorkflowDocument workflow = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job
                {
                    RunsOn = "ubuntu-latest",
                    Strategy = new Strategy
                    {
                        Matrix = new SortedDictionary<string, List<string>> { ["fw"] = ["a"] },
                        MatrixExpression = new SortedDictionary<string, string> { ["fw"] = "${{ fromJson('[]') }}" },
                    },
                    Steps = [new Step { Run = "echo" }],
                },
            },
        };

        Assert.Contains(
            WorkflowValidator.Check(workflow),
            problem => problem.Contains("只能给一种", StringComparison.Ordinal));
    }

    [Fact]
    public void 多行内容用块标量_换行不被折成空格()
    {
        WorkflowDocument workflow = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job
                {
                    RunsOn = "ubuntu-latest",
                    Steps = [new Step { Run = "第一行\n第二行\n第三行" }],
                },
            },
        };

        string yaml = WorkflowWriter.Write(workflow);

        // 块标量：换行原样留着；折叠标量会把三行折成一行，那就改了脚本语义
        Assert.Contains("run: |-", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(">-", yaml, StringComparison.Ordinal);
        Assert.Contains("第一行\n", yaml, StringComparison.Ordinal);
        Assert.Contains("第二行\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 空触发器与空作业会被校验挑出来()
    {
        IReadOnlyList<string> problems = WorkflowValidator.Check(new WorkflowDocument());

        Assert.Contains(problems, problem => problem.Contains("一个触发器都没给", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("至少要有一个作业", StringComparison.Ordinal));
    }

    [Fact]
    public void 缺_runs_on_会被挑出来()
    {
        WorkflowDocument workflow = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job { RunsOn = "", Steps = [new Step { Run = "echo hi" }] },
            },
        };

        Assert.Contains(WorkflowValidator.Check(workflow), problem => problem.Contains("缺 runs-on", StringComparison.Ordinal));
    }

    [Fact]
    public void uses_与_run_不能同时给也不能都空()
    {
        WorkflowDocument workflow = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job
                {
                    RunsOn = "ubuntu-latest",
                    Steps =
                    [
                        new Step { Uses = "actions/checkout@v4", Run = "echo hi" },
                        new Step(),
                    ],
                },
            },
        };

        IReadOnlyList<string> problems = WorkflowValidator.Check(workflow);

        Assert.Contains(problems, problem => problem.Contains("只能给一个", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("至少要给一个", StringComparison.Ordinal));
    }

    [Fact]
    public void 依赖不存在与依赖成环都会被挑出来()
    {
        WorkflowDocument missing = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job { RunsOn = "ubuntu-latest", Needs = ["nope"], Steps = [new Step { Run = "echo" }] },
            },
        };

        Assert.Contains(WorkflowValidator.Check(missing), problem => problem.Contains("不存在的作业", StringComparison.Ordinal));

        WorkflowDocument cyclic = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job>
            {
                ["a"] = new Job { RunsOn = "ubuntu-latest", Needs = ["b"], Steps = [new Step { Run = "echo" }] },
                ["b"] = new Job { RunsOn = "ubuntu-latest", Needs = ["a"], Steps = [new Step { Run = "echo" }] },
            },
        };

        Assert.Contains(WorkflowValidator.Check(cyclic), problem => problem.Contains("成环", StringComparison.Ordinal));
    }
}
