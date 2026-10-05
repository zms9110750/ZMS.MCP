using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ZMS.MCP.Workflow.Credentials;
using ZMS.MCP.Workflow.Models;
using ZMS.MCP.Workflow.Yaml;
using WorkflowDocument = ZMS.MCP.Workflow.Models.Workflow;

namespace ZMS.MCP.Workflow.Tools;

/// <summary>工作流：查可用结构、由树生成 yaml、校验。</summary>
[McpServerToolType]
public static class WorkflowTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List what a workflow tree may contain. kind picks which part: 'trigger' lists the supported triggers, " +
        "'field' lists the fields of root / job / step, 'action' lists the inputs of a few common official actions. " +
        "value narrows it to one node (for example value = 'push' shows what that trigger accepts, value = 'checkout' " +
        "shows that action's inputs). The tool input schema already describes the shape of the tree itself; this tool " +
        "fills in the value domain that a schema cannot express.")]
    public static string ListWorkflowSchema(
        [Description("Which part to ask about: 'trigger' / 'field' / 'action'.")] string kind,
        [Description("Narrow to one node, e.g. 'push' or 'checkout'. Empty = list the whole part.")] string? value = null)
    {
        string what = kind.Trim().ToLowerInvariant();
        string? node = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        return what switch
        {
            "trigger" => node == null ? TriggerList() : TriggerDetail(node),
            "field" => node == null ? FieldList() : FieldDetail(node),
            "action" => node == null ? ActionList() : ActionDetail(node),
            _ => throw new ArgumentException($"kind 只认 trigger / field / action，给的是：{kind}"),
        };
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Write a GitHub Actions workflow yaml from a tree. The tree is this tool's own 'tree' parameter - its JSON " +
        "schema (shown when the tools are listed) is the reference for what the tree may contain. The yaml is produced " +
        "deterministically: the same tree always yields the same bytes (2-space indent, LF line endings, key order " +
        "fixed by the writer). The tree is checked first: if it does not hold up, nothing is written and the problems " +
        "are returned instead. If the target file does not exist, it is created. If it DOES exist, this tool refuses " +
        "unless you pass the cookie that a previous call gave you for that exact file - that cookie is the proof you " +
        "know what you are replacing; a call without it returns what would be overwritten and writes nothing.")]
    public static string WriteWorkflow(
        [Description("Where to write the yaml. Absolute path of a .yml / .yaml file.")] string path,
        [Description("The workflow tree: name / on / env / permissions / jobs, where each job has runs-on and steps.")] WorkflowDocument tree,
        [Description("The cookie for the existing file. Only needed (and only honoured) when the target already exists.")] string? cookie = null)
    {
        IReadOnlyList<string> problems = WorkflowValidator.Check(tree);
        if (problems.Count > 0)
        {
            return "# 没有生成（树本身立不住）\n\n" + Bullet(problems);
        }

        string full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            string current = Cookie.Of(full);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                FileInfo existing = new(full);
                return "# 没有写（目标已存在，没给凭据）\n"
                    + $"- 目标：{full}\n"
                    + $"- 它现在的样子：{existing.Length} 字节，改于 {existing.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n"
                    + $"- 它的凭据是 `{current}`。要覆盖就把这个 cookie 带上再来一次；\n"
                    + "  这份内容会被**整份替换**（不可回滚，没有回收站可救）。\n";
            }

            if (!Cookie.Matches(cookie, current))
            {
                return "# 没有写（凭据对不上）\n"
                    + $"- 目标：{full}\n"
                    + $"- 你给的：`{cookie.Trim()}`\n"
                    + $"- 现在算出来：`{current}` —— 这期间它被改过，或者你给的是别的东西的凭据。\n";
            }
        }
        else if (!string.IsNullOrWhiteSpace(cookie) && !Cookie.Matches(cookie, Cookie.Of(full)))
        {
            return "# 没有写（凭据对不上）\n"
                + $"- 目标：{full}（现在不存在）\n"
                + $"- 你给的凭据不是「文件不存在」那一档的；那一档的凭据是 `{Cookie.Of(full)}`。\n";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, WorkflowWriter.Write(tree), new UTF8Encoding(false));

        StringBuilder builder = new();
        builder.AppendLine("# 已生成");
        builder.AppendLine($"- 文件：{full}");
        builder.AppendLine($"- 作业：{tree.Jobs.Count} 个（{string.Join(", ", tree.Jobs.Keys)}）");
        builder.AppendLine($"- 它的凭据（要再改一次就带上）：`{Cookie.Of(full)}`");
        builder.AppendLine("- 提示：内容与这棵树一一对应；改内容请改树再生成，不要手改 yaml。");
        return builder.ToString();
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Check a workflow. Give either path (read an existing yaml) or tree (check a tree directly), exactly one of " +
        "them. Reports what is wrong in plain words: schema problems (missing runs-on, a step with both uses and run, " +
        "an empty trigger list) and semantic ones (needs pointing at a job that does not exist, dependency cycles).")]
    public static string ValidateWorkflow(
        [Description("Path of an existing workflow yaml to read and check.")] string? path = null,
        [Description("A workflow tree to check directly.")] WorkflowDocument? tree = null)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(path);
        if (hasPath == (tree != null))
        {
            throw new ArgumentException("path 与 tree 给一个，而且只给一个。");
        }

        if (tree != null)
        {
            return Report(path: null, tree, WorkflowValidator.Check(tree));
        }

        string full = Path.GetFullPath(path!);
        if (!File.Exists(full))
        {
            throw new ArgumentException($"文件不存在：{full}");
        }

        WorkflowDocument read;
        try
        {
            read = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .Build()
                .Deserialize<WorkflowDocument>(File.ReadAllText(full, new UTF8Encoding(false)))
                ?? new WorkflowDocument();
        }
        catch (YamlException exception)
        {
            return $"# 读不了这份 yaml\n- {full}\n- {exception.Message}\n";
        }

        return Report(full, read, WorkflowValidator.Check(read));
    }

    private static string Report(string? path, WorkflowDocument workflow, IReadOnlyList<string> problems)
    {
        StringBuilder builder = new();
        builder.AppendLine(path == null ? "# 校验结果（树）" : $"# 校验结果（{path}）");
        builder.AppendLine($"- 触发器：{CountTriggers(workflow.On)} 个");
        builder.AppendLine($"- 作业：{workflow.Jobs.Count} 个");
        builder.AppendLine();

        if (problems.Count == 0)
        {
            builder.AppendLine("没有发现问题。");
            return builder.ToString();
        }

        builder.AppendLine($"发现 {problems.Count} 个问题：");
        builder.AppendLine();
        builder.AppendLine(Bullet(problems));
        return builder.ToString();
    }

    private static string Bullet(IReadOnlyList<string> lines)
    {
        StringBuilder builder = new();
        foreach (string line in lines)
        {
            builder.AppendLine($"- {line}");
        }

        return builder.ToString();
    }

    private static int CountTriggers(Triggers triggers)
    {
        int count = 0;
        count += triggers.Push != null ? 1 : 0;
        count += triggers.PullRequest != null ? 1 : 0;
        count += triggers.WorkflowDispatch != null ? 1 : 0;
        count += triggers.Schedule is { Count: > 0 } ? 1 : 0;
        count += triggers.WorkflowCall != null ? 1 : 0;
        count += triggers.WorkflowRun != null ? 1 : 0;
        return count;
    }

    /// <summary>通用形状的那批事件（与模型里的 <c>Triggers.Other</c> 对应）。</summary>
    private static readonly HashSet<string> GenericTriggers =
    [
        "branch_protection_rule", "check_run", "check_suite", "create", "delete", "deployment",
        "deployment_status", "discussion", "discussion_comment", "fork", "gollum", "issue_comment",
        "issues", "label", "merge_group", "milestone", "page_build", "project", "project_card",
        "project_column", "public", "pull_request_review", "pull_request_review_comment",
        "pull_request_target", "registry_package", "release", "repository_dispatch", "status", "watch",
    ];

    private static string TriggerList()
    {
        return """
            # 触发器（on）

            ## 单独建模的（形状不一样，各有各的写法）

            - `push` —— 推到分支或打标签
            - `pull_request` —— 开/更新 PR
            - `workflow_dispatch` —— 手动点一下（可以带输入）
            - `schedule` —— 按 cron 定时
            - `workflow_call` —— 被别的流水线复用（自己的 inputs / secrets / outputs）
            - `workflow_run` —— 盯别的流水线跑完

            ## 其余事件（形状一样：types / branches / paths / tags 几档过滤）

            它们都写进 `on.other.<事件名>`，能用的名字是：

            `branch_protection_rule` `check_run` `check_suite` `create` `delete` `deployment`
            `deployment_status` `discussion` `discussion_comment` `fork` `gollum` `issue_comment`
            `issues` `label` `merge_group` `milestone` `page_build` `project` `project_card`
            `project_column` `public` `pull_request_review` `pull_request_review_comment`
            `pull_request_target` `registry_package` `release` `repository_dispatch` `status` `watch`

            用 value 看某一个接受什么，例如 value = `release`。
            """;
    }

    private static string TriggerDetail(string name)
    {
        string lowered = name.Trim().ToLowerInvariant().Replace('-', '_');
        if (GenericTriggers.Contains(lowered))
        {
            return $"""
                # {lowered}

                这是通用形状的事件，写进 `on.other.{lowered}`：

                - `types` —— 动作类型（每个事件能用的取值不同，见 GitHub 的文档；例如 `release` 是
                  `published` / `created` / `edited` / `deleted` / `prereleased` / `released`，
                  `issues` 是 `opened` / `edited` / `closed` / `reopened` / `labeled` 等）
                - `branches` / `branches-ignore`
                - `paths` / `paths-ignore`
                - `tags` / `tags-ignore`
                """;
        }

        return lowered switch
        {
            "push" => """
                # push

                - `branches` / `branches-ignore` —— 只在这些分支上跑（两者不能同时给）
                - `paths` / `paths-ignore` —— 只在这些路径改动时跑（同上）
                - `tags` / `tags-ignore` —— 标签
                """,
            "pull_request" => """
                # pull_request

                - `types` —— `opened` / `synchronize` / `reopened` / `closed` / `labeled` …
                - `branches` / `branches-ignore` —— 目标分支
                - `paths` / `paths-ignore` —— 改动路径
                """,
            "workflow_dispatch" => """
                # workflow_dispatch

                - `inputs.<名>.description` —— 给人看的说明
                - `inputs.<名>.required` —— 必填否
                - `inputs.<名>.default` —— 默认值
                - `inputs.<名>.type` —— `string` / `choice` / `boolean` / `environment`
                - `inputs.<名>.options` —— 只有 `type: choice` 用
                """,
            "schedule" => """
                # schedule

                - 一个列表，每项写 `cron`，按 POSIX cron（UTC）。例如 `cron: '0 3 * * 1'` = 每周一 03:00。
                """,
            "workflow_call" => """
                # workflow_call

                - `inputs.<名>` —— `description` / `required` / `default` / `type`（`string` / `number` / `boolean`）
                - `secrets.<名>` —— `description` / `required`
                - `outputs.<名>` —— `description` / `value`
                """,
            "workflow_run" => """
                # workflow_run

                - `workflows` —— 盯哪几个工作流（按名字）
                - `types` —— `completed` / `requested` / `in_progress`
                - `branches` / `branches-ignore`
                """,
            _ => throw new ArgumentException($"认不出这个触发器：{name}（可选：push / pull_request / workflow_dispatch / schedule / workflow_call / workflow_run）"),
        };
    }

    private static string FieldList()
    {
        return """
            # 字段

            用 value 看哪一段：`root` / `job` / `step`。

            - `root` —— 工作流顶层
            - `job` —— 一个作业
            - `step` —— 作业里的一步
            """;
    }

    private static string FieldDetail(string name)
    {
        return name switch
        {
            "root" => """
                # root（工作流顶层）

                - `name` —— 工作流名（给人看的）
                - `on` —— 触发器，至少一个
                - `env` —— 全局环境变量
                - `permissions` —— 整句 `read-all` / `write-all`，或逐个权限；两者不能混
                - `defaults.run.shell` / `defaults.run.working-directory` —— 全局默认
                - `concurrency.group` / `concurrency.cancel-in-progress`
                - `jobs.<作业名>` —— 至少一个
                """,
            "job" => """
                # job（一个作业）

                - `name` —— 显示名
                - `runs-on` —— **必填**
                - `needs` —— 依赖哪些作业先跑完
                - `if` —— 条件
                - `env` —— 这个作业的环境变量
                - `permissions` —— 同顶层
                - `strategy.matrix` / `strategy.fail-fast` / `strategy.max-parallel`
                - `container.image` … 或 `services.<名>`
                - `outputs` —— 给下游作业用
                - `timeout-minutes` / `continue-on-error`
                - `steps` —— 至少一步
                """,
            "step" => """
                # step（一步）

                - `name` / `id` / `if`
                - `uses` —— 用现成的 action（与 `run` **只能给一个**）
                - `run` —— 跑命令（与 `uses` 只能给一个）
                - `with` —— 给 `uses` 的输入
                - `shell` / `working-directory` / `env`
                - `continue-on-error` / `timeout-minutes`
                """,
            _ => throw new ArgumentException($"认不出这一段：{name}（可选：root / job / step）"),
        };
    }

    private static string ActionList()
    {
        return """
            # 常用 action

            - `checkout` —— `actions/checkout`，取代码
            - `setup-dotnet` —— `actions/setup-dotnet`，装 .NET SDK
            - `upload-artifact` —— `actions/upload-artifact`，存产物
            - `download-artifact` —— `actions/download-artifact`，取产物

            用 value 看某一个的输入，例如 value = `checkout`。
            官方 action 的完整输入清单来自各自的 action.yml，这个工具只列常用的那几个。
            """;
    }

    private static string ActionDetail(string name)
    {
        return name switch
        {
            "checkout" => """
                # actions/checkout

                - `fetch-depth` —— 拉多少历史（`0` = 全部）
                - `ref` / `repository` / `token`
                - `path` —— 放到哪个子目录
                - `submodules` / `lfs`
                """,
            "setup-dotnet" => """
                # actions/setup-dotnet

                - `dotnet-version` —— 例如 `8.0.x`、`9.0.x`
                - `global-json-file` —— 用 global.json 里那个版本
                - `source-url` / `source-url` 的凭据 `nuget-source-username` / `nuget-source-password`
                """,
            "upload-artifact" => """
                # actions/upload-artifact

                - `name` —— 产物名
                - `path` —— 要存的东西
                - `if-no-files-found` —— `warn` / `error` / `ignore`
                - `retention-days` —— 留几天
                """,
            "download-artifact" => """
                # actions/download-artifact

                - `name` —— 取哪个产物
                - `path` —— 放到哪
                """,
            _ => throw new ArgumentException($"认不出这个 action：{name}（可选：checkout / setup-dotnet / upload-artifact / download-artifact）"),
        };
    }
}
