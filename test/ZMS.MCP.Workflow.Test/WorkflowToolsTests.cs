using Xunit;
using ZMS.MCP.Workflow.Models;
using ZMS.MCP.Workflow.Tools;
using WorkflowDocument = ZMS.MCP.Workflow.Models.Workflow;

namespace ZMS.MCP.Workflow.Test;

public class WorkflowToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zms-wf-" + Guid.NewGuid().ToString("N"));

    public WorkflowToolsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static WorkflowDocument Good()
    {
        return new WorkflowDocument
        {
            Name = "CI",
            On = new Triggers { Push = new PushTrigger { Branches = ["main"] } },
            Jobs = new SortedDictionary<string, Job>
            {
                ["build"] = new Job { RunsOn = "ubuntu-latest", Steps = [new Step { Run = "dotnet build" }] },
            },
        };
    }

    [Fact]
    public void 生成一份工作流并落盘()
    {
        string file = Path.Combine(_root, "ci.yml");

        string output = WorkflowTools.WriteWorkflow(file, Good());

        Assert.Contains("已生成", output, StringComparison.Ordinal);
        string written = File.ReadAllText(file);
        Assert.Contains("\njobs:\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void 目标已存在就不写()
    {
        string file = Path.Combine(_root, "有过了.yml");
        File.WriteAllText(file, "原样");

        string output = WorkflowTools.WriteWorkflow(file, Good());

        Assert.Contains("目标已存在", output, StringComparison.Ordinal);
        Assert.Contains("整份替换", output, StringComparison.Ordinal);
        Assert.Equal("原样", File.ReadAllText(file));
    }

    [Fact]
    public void 带对凭据就能覆盖已有文件()
    {
        string file = Path.Combine(_root, "改过.yml");
        File.WriteAllText(file, "原样");

        string refused = WorkflowTools.WriteWorkflow(file, Good());
        string cookie = ReadCookie(refused);

        string output = WorkflowTools.WriteWorkflow(file, Good(), cookie);

        Assert.Contains("已生成", output, StringComparison.Ordinal);
        Assert.Contains("\njobs:\n", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void 凭据对不上就拒写()
    {
        string file = Path.Combine(_root, "别人改过.yml");
        File.WriteAllText(file, "原样");

        string output = WorkflowTools.WriteWorkflow(file, Good(), "0123456789abcdef");

        Assert.Contains("凭据对不上", output, StringComparison.Ordinal);
        Assert.Equal("原样", File.ReadAllText(file));
    }

    /// <summary>把工具那句人话里反引号内的凭据抠出来（模拟"上一次调用给了我一个 cookie"）。</summary>
    private static string ReadCookie(string message)
    {
        int start = message.IndexOf('`');
        int end = message.IndexOf('`', start + 1);
        return start >= 0 && end > start ? message[(start + 1)..end] : "";
    }

    [Fact]
    public void 树立不住就不写并把问题摆出来()
    {
        string file = Path.Combine(_root, "坏的.yml");
        WorkflowDocument broken = new()
        {
            On = new Triggers { Push = new PushTrigger() },
            Jobs = new SortedDictionary<string, Job> { ["build"] = new Job() },
        };

        string output = WorkflowTools.WriteWorkflow(file, broken);

        Assert.Contains("树本身立不住", output, StringComparison.Ordinal);
        Assert.Contains("缺 runs-on", output, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void 校验要求_path_与_tree_给一个且只给一个()
    {
        Assert.Throws<ArgumentException>(() => WorkflowTools.ValidateWorkflow());
        Assert.Throws<ArgumentException>(() => WorkflowTools.ValidateWorkflow("a.yml", Good()));
    }

    [Fact]
    public void 校验一棵好树没有发现问题()
    {
        Assert.Contains("没有发现问题", WorkflowTools.ValidateWorkflow(tree: Good()), StringComparison.Ordinal);
    }

    [Fact]
    public void 校验读回来的_yaml_能认出_runson()
    {
        string file = Path.Combine(_root, "读.yml");
        WorkflowTools.WriteWorkflow(file, Good());

        string output = WorkflowTools.ValidateWorkflow(file);

        Assert.Contains("没有发现问题", output, StringComparison.Ordinal);
    }

    [Fact]
    public void list_workflow_schema_三种_kind_与定位()
    {
        Assert.Contains("push", WorkflowTools.ListWorkflowSchema("trigger"), StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch", WorkflowTools.ListWorkflowSchema("trigger", "workflow_dispatch"), StringComparison.Ordinal);
        Assert.Contains("runs-on", WorkflowTools.ListWorkflowSchema("field", "job"), StringComparison.Ordinal);
        Assert.Contains("checkout", WorkflowTools.ListWorkflowSchema("action", "checkout"), StringComparison.Ordinal);

        // 通用形状的那批事件也在表里，且定位时指向 on.other.<名字>
        Assert.Contains("release", WorkflowTools.ListWorkflowSchema("trigger"), StringComparison.Ordinal);
        Assert.Contains("on.other.release", WorkflowTools.ListWorkflowSchema("trigger", "release"), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => WorkflowTools.ListWorkflowSchema("nonsense"));
        Assert.Throws<ArgumentException>(() => WorkflowTools.ListWorkflowSchema("trigger", "nope"));
    }
}
