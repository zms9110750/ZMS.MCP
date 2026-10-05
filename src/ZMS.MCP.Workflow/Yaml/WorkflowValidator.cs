using System.Text.RegularExpressions;
using ZMS.MCP.Workflow.Models;
using WorkflowDocument = ZMS.MCP.Workflow.Models.Workflow;

namespace ZMS.MCP.Workflow.Yaml;

/// <summary>
/// 校验一棵工作流树。
///
/// 分两层：**这一层（schema）**查"这棵树本身立不立得住"——必填缺没缺、字段互相打不打架；
/// **语义提示**查"它在 GitHub 上跑得起来吗"——依赖指到不存在的作业、依赖成环之类。
/// 返回一句句人话（中文），没有毛病就是空表。
/// </summary>
public static partial class WorkflowValidator
{
    /// <summary>作业名的合法形态。</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex JobId();

    /// <summary>把一个触发器的名字映射到"它有没有给"。</summary>
    private static readonly string[] TriggerNames =
        ["push", "pull_request", "workflow_dispatch", "schedule", "workflow_call", "workflow_run"];

    /// <summary>查一遍，返回所有问题。</summary>
    public static IReadOnlyList<string> Check(WorkflowDocument workflow)
    {
        List<string> problems = [];

        if (Count(workflow.On) == 0)
        {
            problems.Add($"on：一个触发器都没给（可选：{string.Join(" / ", TriggerNames)}）。");
        }

        CheckPermissions(problems, "permissions", workflow.Permissions);

        if (workflow.Jobs.Count == 0)
        {
            problems.Add("jobs：至少要有一个作业。");
        }

        foreach ((string id, Job job) in workflow.Jobs)
        {
            if (!JobId().IsMatch(id))
            {
                problems.Add($"jobs.{id}：作业名只能用字母、数字、下划线、连字符，且不能以数字开头。");
            }

            if (string.IsNullOrWhiteSpace(job.RunsOn))
            {
                problems.Add($"jobs.{id}：缺 runs-on（跑在什么机器上）。");
            }

            if (job.Steps.Count == 0)
            {
                problems.Add($"jobs.{id}：一步都没有。");
            }

            CheckPermissions(problems, $"jobs.{id}.permissions", job.Permissions);

            if (job.Strategy is { } strategy
                && strategy.MatrixExpression is { Count: > 0 } expressions
                && strategy.Matrix is { Count: > 0 } values)
            {
                foreach (string name in expressions.Keys)
                {
                    if (values.ContainsKey(name))
                    {
                        problems.Add($"jobs.{id}.strategy.matrix：'{name}' 这一维既在 matrix 里列了取值、又在 matrixExpression 里给了表达式，只能给一种。");
                    }
                }
            }

            if (job.Container is { } container && string.IsNullOrWhiteSpace(container.Image))
            {
                problems.Add($"jobs.{id}.container：缺 image。");
            }

            for (int index = 0; index < job.Steps.Count; index++)
            {
                Step step = job.Steps[index];
                bool hasUses = !string.IsNullOrWhiteSpace(step.Uses);
                bool hasRun = !string.IsNullOrWhiteSpace(step.Run);

                if (hasUses && hasRun)
                {
                    problems.Add($"jobs.{id}.steps[{index}]：uses 与 run 只能给一个。");
                }
                else if (!hasUses && !hasRun)
                {
                    problems.Add($"jobs.{id}.steps[{index}]：uses 与 run 至少要给一个。");
                }

                if (hasUses && step.Uses!.Contains('@') == false && step.Uses.Contains("actions/", StringComparison.Ordinal))
                {
                    problems.Add($"jobs.{id}.steps[{index}]：uses 通常要带版本，例如 '{step.Uses}@v4'。");
                }
            }
        }

        foreach ((string id, Job job) in workflow.Jobs)
        {
            foreach (string need in job.Needs ?? [])
            {
                if (!workflow.Jobs.ContainsKey(need))
                {
                    problems.Add($"jobs.{id}.needs：依赖了一个不存在的作业 '{need}'。");
                }
            }
        }

        problems.AddRange(Cycles(workflow));
        return problems;
    }

    /// <summary>依赖成环：每个环报一次（把环上的名字按序写出来）。</summary>
    private static List<string> Cycles(WorkflowDocument workflow)
    {
        List<string> problems = [];
        HashSet<string> done = [];
        List<string> path = [];
        HashSet<string> onPath = [];

        void Visit(string id)
        {
            if (done.Contains(id) || !workflow.Jobs.TryGetValue(id, out Job? job))
            {
                return;
            }

            if (onPath.Contains(id))
            {
                int at = path.IndexOf(id);
                problems.Add($"jobs.{id}.needs：依赖成环 —— {string.Join(" → ", path[at..])} → {id}。");
                return;
            }

            onPath.Add(id);
            path.Add(id);
            foreach (string need in job.Needs ?? [])
            {
                Visit(need);
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(id);
            done.Add(id);
        }

        foreach (string id in workflow.Jobs.Keys)
        {
            Visit(id);
        }

        return problems;
    }

    private static void CheckPermissions(List<string> problems, string where, Permissions? permissions)
    {
        if (permissions == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(permissions.All) && permissions.Scopes is { Count: > 0 })
        {
            problems.Add($"{where}：整句（read-all / write-all）与逐个权限只能给一种。");
        }
    }

    private static int Count(Triggers triggers)
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
}
