namespace ZMS.MCP.Workflow.Models;

/// <summary>
/// GitHub Actions 工作流的强类型模型（最小可用集）。
///
/// 键序一律用 <see cref="SortedDictionary{TKey,TValue}"/> 或固定字段顺序表达 ——
/// **不依赖字典/反射的枚举顺序**，这样"同一棵树生成两次字节一致"才成立。
/// 以官方 <c>workflow-v1.0.json</c>（actions/languageservices）为参照，缺什么补什么。
/// </summary>
public sealed class Workflow
{
    public string? Name { get; set; }

    /// <summary><c>on</c>：触发器。必填（一个不写的工作流没有意义）。</summary>
    public Triggers On { get; set; } = new();

    public SortedDictionary<string, string>? Env { get; set; }

    public Permissions? Permissions { get; set; }

    public Defaults? Defaults { get; set; }

    public Concurrency? Concurrency { get; set; }

    /// <summary><c>jobs</c>：作业表。用有序字典 —— 顺序由键来定，不由插入次序定。</summary>
    public SortedDictionary<string, Job> Jobs { get; set; } = [];
}

/// <summary><c>on</c>：可用触发器。</summary>
public sealed class Triggers
{
    /// <summary>有 branches / paths / tags 三档过滤的那一个（形状最全，单独给）。</summary>
    public PushTrigger? Push { get; set; }

    /// <summary>比 push 多一档 types。</summary>
    public PullRequestTrigger? PullRequest { get; set; }

    /// <summary>手动触发，可以带输入。</summary>
    public WorkflowDispatchTrigger? WorkflowDispatch { get; set; }

    /// <summary>按 cron 定时。</summary>
    public List<string>? Schedule { get; set; }

    /// <summary>被别的流水线复用（自己的 inputs / secrets / outputs）。</summary>
    public WorkflowCallTrigger? WorkflowCall { get; set; }

    /// <summary>盯别的流水线跑完。</summary>
    public WorkflowRunTrigger? WorkflowRun { get; set; }

    /// <summary>
    /// 其余事件（<c>release</c> / <c>issues</c> / <c>label</c> / <c>check_run</c> / <c>discussion</c> /
    /// <c>deployment</c> / <c>merge_group</c> / <c>repository_dispatch</c> / <c>registry_package</c> /
    /// <c>fork</c> / <c>gollum</c> 等等）：形状都一样 —— 名字 → 它的过滤条件。
    /// **可以填哪些名字**由 <c>list_workflow_schema(trigger)</c> 给全表；这里故意做成开放字典，
    /// 免得官方每加一个事件就得改一次模型。
    /// </summary>
    public SortedDictionary<string, SimpleTrigger>? Other { get; set; }
}

/// <summary>通用触发器：只带过滤条件的那些事件都用它。</summary>
public sealed class SimpleTrigger
{
    /// <summary>动作类型，如 <c>opened</c> / <c>published</c> / <c>completed</c>。</summary>
    public List<string>? Types { get; set; }

    public List<string>? Branches { get; set; }

    public List<string>? BranchesIgnore { get; set; }

    public List<string>? Paths { get; set; }

    public List<string>? PathsIgnore { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? TagsIgnore { get; set; }
}

/// <summary><c>push</c>。</summary>
public sealed class PushTrigger
{
    public List<string>? Branches { get; set; }

    public List<string>? BranchesIgnore { get; set; }

    public List<string>? Paths { get; set; }

    public List<string>? PathsIgnore { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? TagsIgnore { get; set; }
}

/// <summary><c>pull_request</c>。</summary>
public sealed class PullRequestTrigger
{
    public List<string>? Types { get; set; }

    public List<string>? Branches { get; set; }

    public List<string>? BranchesIgnore { get; set; }

    public List<string>? Paths { get; set; }

    public List<string>? PathsIgnore { get; set; }
}

/// <summary><c>workflow_dispatch</c>：手动触发，可以带输入。</summary>
public sealed class WorkflowDispatchTrigger
{
    public SortedDictionary<string, DispatchInput>? Inputs { get; set; }
}

/// <summary>手动触发的一个输入。</summary>
public sealed class DispatchInput
{
    public string? Description { get; set; }

    public bool? Required { get; set; }

    /// <summary>默认值。写成字符串 —— yaml 里它可以是布尔/数字，交给序列化那一层决定引号。</summary>
    public string? Default { get; set; }

    /// <summary><c>string</c> / <c>choice</c> / <c>boolean</c> / <c>environment</c>。</summary>
    public string? Type { get; set; }

    /// <summary>只有 <c>type: choice</c> 用得上。</summary>
    public List<string>? Options { get; set; }
}

/// <summary><c>workflow_call</c>：可被别的流水线复用时，自己的输入/密钥/输出。</summary>
public sealed class WorkflowCallTrigger
{
    public SortedDictionary<string, CallInput>? Inputs { get; set; }

    public SortedDictionary<string, CallSecret>? Secrets { get; set; }

    public SortedDictionary<string, CallOutput>? Outputs { get; set; }
}

/// <summary>被复用时的一个输入。</summary>
public sealed class CallInput
{
    public string? Description { get; set; }

    public bool? Required { get; set; }

    public string? Default { get; set; }

    /// <summary><c>string</c> / <c>number</c> / <c>boolean</c>。</summary>
    public string Type { get; set; } = "string";
}

/// <summary>被复用时的一个密钥。</summary>
public sealed class CallSecret
{
    public string? Description { get; set; }

    public bool? Required { get; set; }
}

/// <summary>被复用时对外暴露的一个输出。</summary>
public sealed class CallOutput
{
    public string Description { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary><c>workflow_run</c>：被别的流水线跑完时触发。</summary>
public sealed class WorkflowRunTrigger
{
    /// <summary>要盯的那几个工作流（按名字）。</summary>
    public List<string>? Workflows { get; set; }

    /// <summary><c>completed</c> / <c>requested</c> / <c>in_progress</c>。</summary>
    public List<string>? Types { get; set; }

    public List<string>? Branches { get; set; }

    public List<string>? BranchesIgnore { get; set; }
}

/// <summary>一个作业。</summary>
public sealed class Job
{
    public string? Name { get; set; }

    /// <summary><c>runs-on</c>：必填（跑在什么机器上）。</summary>
    public string RunsOn { get; set; } = "";

    /// <summary>依赖哪些作业先完成。</summary>
    public List<string>? Needs { get; set; }

    public string? If { get; set; }

    public SortedDictionary<string, string>? Env { get; set; }

    public Permissions? Permissions { get; set; }

    public Strategy? Strategy { get; set; }

    public Container? Container { get; set; }

    public SortedDictionary<string, Container>? Services { get; set; }

    public Concurrency? Concurrency { get; set; }

    public SortedDictionary<string, string>? Outputs { get; set; }

    public int? TimeoutMinutes { get; set; }

    public bool? ContinueOnError { get; set; }

    public List<Step> Steps { get; set; } = [];
}

/// <summary><c>strategy</c>：矩阵与并发度。</summary>
public sealed class Strategy
{
    /// <summary>
    /// <c>matrix</c> 里**把取值列出来**的那些维度：每个键对应一串取值。
    /// 用有序字典 —— 矩阵的键序不能随插入次序变。
    /// </summary>
    public SortedDictionary<string, List<string>>? Matrix { get; set; }

    /// <summary>
    /// <c>matrix</c> 里**交给表达式算**的那些维度：值写成一个表达式，
    /// 例如 <c>${{ fromJson(needs.build.outputs.tfm_json) }}</c>。
    /// 与 <see cref="Matrix"/> 一起写进 yaml 的同一个 <c>matrix</c> 下，两边键不能重。
    /// </summary>
    public SortedDictionary<string, string>? MatrixExpression { get; set; }

    public bool? FailFast { get; set; }

    public int? MaxParallel { get; set; }
}

/// <summary><c>container</c>：作业跑在容器里。</summary>
public sealed class Container
{
    public string Image { get; set; } = "";

    public SortedDictionary<string, string>? Env { get; set; }

    public List<string>? Ports { get; set; }

    public List<string>? Volumes { get; set; }

    public string? Options { get; set; }

    public ContainerCredentials? Credentials { get; set; }
}

/// <summary>拉私有镜像用的凭据。</summary>
public sealed class ContainerCredentials
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}

/// <summary><c>defaults</c>：这一步/这一作业的默认值。</summary>
public sealed class Defaults
{
    public DefaultsRun? Run { get; set; }
}

/// <summary><c>defaults.run</c>。</summary>
public sealed class DefaultsRun
{
    public string? Shell { get; set; }

    public string? WorkingDirectory { get; set; }
}

/// <summary><c>concurrency</c>：同名的一组只跑一个。</summary>
public sealed class Concurrency
{
    public string Group { get; set; } = "";

    /// <summary>true = 排队的等前面跑完；false / 不写 = 新的把旧的取消掉。</summary>
    public bool? CancelInProgress { get; set; }
}

/// <summary><c>permissions</c>：<c>read-all</c> / <c>write-all</c>，或逐个给 <c>actions: read</c> 这种。</summary>
public sealed class Permissions
{
    /// <summary>整个写成一句时用它（<c>read-all</c> / <c>write-all</c> / <c>{}</c>）。</summary>
    public string? All { get; set; }

    /// <summary>逐个权限：键是权限名，值是 <c>read</c> / <c>write</c> / <c>none</c>。</summary>
    public SortedDictionary<string, string>? Scopes { get; set; }
}

/// <summary>作业里的一步。</summary>
public sealed class Step
{
    public string? Name { get; set; }

    public string? Id { get; set; }

    public string? If { get; set; }

    /// <summary>用现成的 action，如 <c>actions/checkout@v4</c>。</summary>
    public string? Uses { get; set; }

    /// <summary>跑一段命令。</summary>
    public string? Run { get; set; }

    /// <summary>给 <c>uses</c> 的输入。</summary>
    public SortedDictionary<string, string>? With { get; set; }

    public string? Shell { get; set; }

    public string? WorkingDirectory { get; set; }

    public SortedDictionary<string, string>? Env { get; set; }

    public bool? ContinueOnError { get; set; }

    public int? TimeoutMinutes { get; set; }
}
