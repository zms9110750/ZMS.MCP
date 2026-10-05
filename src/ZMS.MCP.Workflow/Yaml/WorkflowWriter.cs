using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using ZMS.MCP.Workflow.Models;
using JobContainer = ZMS.MCP.Workflow.Models.Container;
using WorkflowDocument = ZMS.MCP.Workflow.Models.Workflow;

namespace ZMS.MCP.Workflow.Yaml;

/// <summary>
/// 把模型写成 yaml。
///
/// **确定性**是硬要求：同一棵树两次写出来**字节一致**。做法是**不碰反射**——
/// 键的顺序就是这里代码的顺序，缩进固定 2 格，标量一律交给 <see cref="Emitter"/> 按同一套规则决定要不要加引号。
/// </summary>
public static class WorkflowWriter
{
    /// <summary>缩进固定两格。</summary>
    public const int Indent = 2;

    /// <summary>写成一份完整的 yaml 文档。</summary>
    public static string Write(WorkflowDocument workflow)
    {
        // NewLine 显式钉成 "\n"：StringWriter 默认跟随 Environment.NewLine，
        // 那会让同一棵树在 Windows 与 Linux 上写出不同字节。
        using StringWriter output = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        Emitter emitter = new(output, bestIndent: Indent, bestWidth: int.MaxValue, isCanonical: false);

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());

        Map(emitter, () =>
        {
            Text(emitter, "name", workflow.Name);

            Key(emitter, "on");
            WriteTriggers(emitter, workflow.On);

            Dictionary(emitter, "env", workflow.Env);
            WritePermissions(emitter, workflow.Permissions);
            WriteDefaults(emitter, workflow.Defaults);
            WriteConcurrency(emitter, workflow.Concurrency);

            Key(emitter, "jobs");
            Map(emitter, () =>
            {
                foreach ((string id, Job job) in workflow.Jobs)
                {
                    Key(emitter, id);
                    WriteJob(emitter, job);
                }
            });
        });

        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());

        // Emitter 在 Windows 上按 CRLF 落行，不听 StringWriter.NewLine —— 这里归一成 LF，
        // 同一棵树在任何平台上写出来才是同一串字节。
        return output.ToString().Replace("\r\n", "\n");
    }

    private static void WriteTriggers(Emitter emitter, Triggers triggers)
    {
        Map(emitter, () =>
        {
            if (triggers.Push != null)
            {
                Key(emitter, "push");
                Map(emitter, () =>
                {
                    List(emitter, "branches", triggers.Push.Branches);
                    List(emitter, "branches-ignore", triggers.Push.BranchesIgnore);
                    List(emitter, "paths", triggers.Push.Paths);
                    List(emitter, "paths-ignore", triggers.Push.PathsIgnore);
                    List(emitter, "tags", triggers.Push.Tags);
                    List(emitter, "tags-ignore", triggers.Push.TagsIgnore);
                });
            }

            if (triggers.PullRequest != null)
            {
                Key(emitter, "pull_request");
                Map(emitter, () =>
                {
                    List(emitter, "types", triggers.PullRequest.Types);
                    List(emitter, "branches", triggers.PullRequest.Branches);
                    List(emitter, "branches-ignore", triggers.PullRequest.BranchesIgnore);
                    List(emitter, "paths", triggers.PullRequest.Paths);
                    List(emitter, "paths-ignore", triggers.PullRequest.PathsIgnore);
                });
            }

            if (triggers.WorkflowDispatch != null)
            {
                Key(emitter, "workflow_dispatch");
                Map(emitter, () =>
                {
                    if (triggers.WorkflowDispatch.Inputs is { Count: > 0 } inputs)
                    {
                        Key(emitter, "inputs");
                        Map(emitter, () =>
                        {
                            foreach ((string name, DispatchInput input) in inputs)
                            {
                                Key(emitter, name);
                                Map(emitter, () =>
                                {
                                    Text(emitter, "description", input.Description);
                                    Boolean(emitter, "required", input.Required);
                                    ScalarText(emitter, "default", input.Default);
                                    Text(emitter, "type", input.Type);
                                    List(emitter, "options", input.Options);
                                });
                            }
                        });
                    }
                });
            }

            if (triggers.Schedule is { Count: > 0 } schedule)
            {
                Key(emitter, "schedule");
                emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
                foreach (string cron in schedule)
                {
                    Map(emitter, () => ScalarText(emitter, "cron", cron));
                }

                emitter.Emit(new SequenceEnd());
            }

            if (triggers.WorkflowCall != null)
            {
                Key(emitter, "workflow_call");
                Map(emitter, () =>
                {
                    Maps(emitter, "inputs", triggers.WorkflowCall.Inputs, input => () =>
                    {
                        Text(emitter, "description", input.Description);
                        Boolean(emitter, "required", input.Required);
                        ScalarText(emitter, "default", input.Default);
                        Text(emitter, "type", input.Type);
                    });

                    Maps(emitter, "secrets", triggers.WorkflowCall.Secrets, secret => () =>
                    {
                        Text(emitter, "description", secret.Description);
                        Boolean(emitter, "required", secret.Required);
                    });

                    Maps(emitter, "outputs", triggers.WorkflowCall.Outputs, output => () =>
                    {
                        Text(emitter, "description", output.Description);
                        Text(emitter, "value", output.Value);
                    });
                });
            }

            if (triggers.WorkflowRun != null)
            {
                Key(emitter, "workflow_run");
                Map(emitter, () =>
                {
                    List(emitter, "workflows", triggers.WorkflowRun.Workflows);
                    List(emitter, "types", triggers.WorkflowRun.Types);
                    List(emitter, "branches", triggers.WorkflowRun.Branches);
                    List(emitter, "branches-ignore", triggers.WorkflowRun.BranchesIgnore);
                });
            }

            // 其余事件：形状一样，逐个写
            foreach ((string name, SimpleTrigger trigger) in triggers.Other ?? [])
            {
                Key(emitter, name);
                Map(emitter, () =>
                {
                    List(emitter, "types", trigger.Types);
                    List(emitter, "branches", trigger.Branches);
                    List(emitter, "branches-ignore", trigger.BranchesIgnore);
                    List(emitter, "paths", trigger.Paths);
                    List(emitter, "paths-ignore", trigger.PathsIgnore);
                    List(emitter, "tags", trigger.Tags);
                    List(emitter, "tags-ignore", trigger.TagsIgnore);
                });
            }
        });
    }

    private static void WriteJob(Emitter emitter, Job job)
    {
        Map(emitter, () =>
        {
            Text(emitter, "name", job.Name);
            List(emitter, "needs", job.Needs);
            Text(emitter, "if", job.If);
            Text(emitter, "runs-on", job.RunsOn);

            Dictionary(emitter, "env", job.Env);
            WritePermissions(emitter, job.Permissions);
            WriteConcurrency(emitter, job.Concurrency);

            if (job.Strategy != null)
            {
                Key(emitter, "strategy");
                Map(emitter, () =>
                {
                    if (job.Strategy.Matrix is { Count: > 0 } || job.Strategy.MatrixExpression is { Count: > 0 })
                    {
                        Key(emitter, "matrix");
                        Map(emitter, () =>
                        {
                            // 表达式那一档先写：它是整维交给表达式算的
                            foreach ((string name, string expression) in job.Strategy.MatrixExpression ?? [])
                            {
                                ScalarText(emitter, name, expression);
                            }

                            // 再写把取值列出来的那一档
                            foreach ((string name, List<string> values) in job.Strategy.Matrix ?? [])
                            {
                                List(emitter, name, values);
                            }
                        });
                    }

                    Boolean(emitter, "fail-fast", job.Strategy.FailFast);
                    Number(emitter, "max-parallel", job.Strategy.MaxParallel);
                });
            }

            if (job.Container != null)
            {
                Key(emitter, "container");
                WriteContainer(emitter, job.Container);
            }

            if (job.Services is { Count: > 0 } services)
            {
                Key(emitter, "services");
                Map(emitter, () =>
                {
                    foreach ((string name, JobContainer service) in services)
                    {
                        Key(emitter, name);
                        WriteContainer(emitter, service);
                    }
                });
            }

            Dictionary(emitter, "outputs", job.Outputs);
            Number(emitter, "timeout-minutes", job.TimeoutMinutes);
            Boolean(emitter, "continue-on-error", job.ContinueOnError);

            Key(emitter, "steps");
            emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
            foreach (Step step in job.Steps)
            {
                Map(emitter, () =>
                {
                    Text(emitter, "name", step.Name);
                    Text(emitter, "id", step.Id);
                    Text(emitter, "if", step.If);
                    Text(emitter, "uses", step.Uses);
                    Text(emitter, "run", step.Run);
                    Dictionary(emitter, "with", step.With);
                    Text(emitter, "shell", step.Shell);
                    Text(emitter, "working-directory", step.WorkingDirectory);
                    Dictionary(emitter, "env", step.Env);
                    Boolean(emitter, "continue-on-error", step.ContinueOnError);
                    Number(emitter, "timeout-minutes", step.TimeoutMinutes);
                });
            }

            emitter.Emit(new SequenceEnd());
        });
    }

    private static void WriteContainer(Emitter emitter, JobContainer container)
    {
        Map(emitter, () =>
        {
            Text(emitter, "image", container.Image);
            Dictionary(emitter, "env", container.Env);
            List(emitter, "ports", container.Ports);
            List(emitter, "volumes", container.Volumes);
            Text(emitter, "options", container.Options);

            if (container.Credentials != null)
            {
                Key(emitter, "credentials");
                Map(emitter, () =>
                {
                    Text(emitter, "username", container.Credentials.Username);
                    Text(emitter, "password", container.Credentials.Password);
                });
            }
        });
    }

    private static void WriteDefaults(Emitter emitter, Defaults? defaults)
    {
        if (defaults?.Run == null)
        {
            return;
        }

        Key(emitter, "defaults");
        Map(emitter, () =>
        {
            Key(emitter, "run");
            Map(emitter, () =>
            {
                Text(emitter, "shell", defaults.Run.Shell);
                Text(emitter, "working-directory", defaults.Run.WorkingDirectory);
            });
        });
    }

    private static void WriteConcurrency(Emitter emitter, Concurrency? concurrency)
    {
        if (concurrency == null)
        {
            return;
        }

        Key(emitter, "concurrency");
        Map(emitter, () =>
        {
            Text(emitter, "group", concurrency.Group);
            Boolean(emitter, "cancel-in-progress", concurrency.CancelInProgress);
        });
    }

    private static void WritePermissions(Emitter emitter, Permissions? permissions)
    {
        if (permissions == null)
        {
            return;
        }

        Key(emitter, "permissions");
        if (!string.IsNullOrEmpty(permissions.All))
        {
            Scalar(emitter, permissions.All);
            return;
        }

        Map(emitter, () =>
        {
            foreach ((string scope, string value) in permissions.Scopes ?? [])
            {
                ScalarText(emitter, scope, value);
            }
        });
    }

    private static void Maps<T>(Emitter emitter, string name, SortedDictionary<string, T>? items, Func<T, Action> body)
    {
        if (items is not { Count: > 0 })
        {
            return;
        }

        Key(emitter, name);
        Map(emitter, () =>
        {
            foreach ((string key, T item) in items)
            {
                Key(emitter, key);
                Map(emitter, body(item));
            }
        });
    }

    private static void Dictionary(Emitter emitter, string name, SortedDictionary<string, string>? items)
    {
        if (items is not { Count: > 0 })
        {
            return;
        }

        Key(emitter, name);
        Map(emitter, () =>
        {
            foreach ((string key, string value) in items)
            {
                ScalarText(emitter, key, value);
            }
        });
    }

    private static void List(Emitter emitter, string name, List<string>? items)
    {
        if (items is not { Count: > 0 })
        {
            return;
        }

        Key(emitter, name);
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        foreach (string item in items)
        {
            Scalar(emitter, item);
        }

        emitter.Emit(new SequenceEnd());
    }

    private static void Text(Emitter emitter, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ScalarText(emitter, name, value);
        }
    }

    private static void Number(Emitter emitter, string name, int? value)
    {
        if (value.HasValue)
        {
            ScalarText(emitter, name, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Boolean(Emitter emitter, string name, bool? value)
    {
        if (value.HasValue)
        {
            Key(emitter, name);
            Scalar(emitter, value.Value ? "true" : "false");
        }
    }

    private static void ScalarText(Emitter emitter, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Key(emitter, name);
        Scalar(emitter, value);
    }

    private static void Map(Emitter emitter, Action body)
    {
        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        body();
        emitter.Emit(new MappingEnd());
    }

    private static void Key(Emitter emitter, string name)
    {
        emitter.Emit(new Scalar(null, null, name, ScalarStyle.Plain, true, false));
    }

    /// <summary>
    /// 标量交给 emitter 决定引号 —— 同一套规则，所以同一个值两次的写法一样。
    /// 但**多行内容必须用块标量**（<c>|</c>）：折叠标量（<c>&gt;-</c>）会把换行折成空格，
    /// 那会让一段脚本从三行变成一行，语义就变了。
    /// </summary>
    private static void Scalar(Emitter emitter, string value)
    {
        ScalarStyle style = value.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.Any;
        emitter.Emit(new Scalar(null, null, value, style, true, false));
    }
}
