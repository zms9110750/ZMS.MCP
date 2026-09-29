using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZMS.MCP.Csharp.Project;
using ZMS.MCP.Csharp.Roslyn;

namespace ZMS.MCP.Csharp.Draft;

/// <summary>一条诊断的配对键：**错误码 + 消息 + 文件**（行号不参与配对，改动会挪行号）。</summary>
public sealed record DiagnosticKey(string Code, string Message, string File, int Line)
{
    public string PairingKey => $"{Code}\u0001{Message}\u0001{File}";

    public override string ToString()
    {
        return $"{Code} {Message}（{File}:{Line}）";
    }
}

/// <summary>一次诊断快照。</summary>
public sealed record DiagnosticSnapshot(IReadOnlyList<DiagnosticKey> Errors, IReadOnlyList<DiagnosticKey> Warnings)
{
    public static DiagnosticSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// 「拟定新增或修改或删除符号」与「拟定修改确认」。
///
/// 拟定可以累积：多次调用依次叠加在同一个拟定上；
/// 拟定状态存 sqlite（放在 MCP 自己的目录）；MCP 重启从库里恢复，agent 重启靠「列出拟定」拿 cookit。
/// </summary>
public static class DraftService
{
    /// <summary>变更分类与顺序（需求文档写的是"增加，删除，修改"）。</summary>
    private static readonly string[] Categories = ["增加", "删除", "修改"];

    /// <summary>
    /// 一条改动属于哪一类。CodeEditor 的 action 是 removed / replaced / added 这类英文标记，
    /// 新建类型、新建内部类、补 partial 都归入"增加"。
    /// </summary>
    internal static string CategoryOf(DraftEdit edit)
    {
        return edit.Action switch
        {
            "removed" => "删除",
            "replaced" => "修改",
            _ => "增加",
        };
    }

    /// <summary>
    /// 前滚：多文件落盘不是真原子（见需求文档），中途崩溃会留下"写前日志"。
    /// 每发现一份未清掉的日志，就按它记下的内容重写一遍并清掉 —— 下次启动即补齐。
    /// </summary>
    /// <param name="databasePath">拟定库路径；空 = MCP 自己的目录（测试可注入临时库）。</param>
    public static string RecoverPendingWrites(string? databasePath = null)
    {
        DraftStore store = new(databasePath);
        List<string> messages = [];
        foreach (string cookit in store.JournalCookits())
        {
            int written = 0;
            int skipped = 0;
            List<string> held = [];
            foreach (DraftStore.JournalEntry entry in store.ReadJournal(cookit))
            {
                if (!File.Exists(entry.FilePath))
                {
                    // 崩在"新建文件"之前 → 补齐
                    FileWriter.WriteAtomic(entry.FilePath, entry.NewContent);
                    written++;
                    continue;
                }

                if (string.Equals(File.ReadAllText(entry.FilePath), entry.NewContent, StringComparison.Ordinal))
                {
                    // 崩在"写完之后、清日志之前" → 已经是新内容，别动（也不搅动时间戳）
                    skipped++;
                    continue;
                }

                if (entry.PreviousHash.Length > 0
                    && string.Equals(
                        FileWriter.ComputeHash(entry.FilePath),
                        entry.PreviousHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 还是"写之前"那一份 → 覆盖成拟定内容
                    FileWriter.WriteAtomic(entry.FilePath, entry.NewContent);
                    written++;
                    continue;
                }

                // 第三种：既不是新内容、也不是写前内容 —— 有人在崩溃窗口里改过 → **只报告，绝不覆盖**
                held.Add($"  - {entry.FilePath}（保持原样，未改动）");
            }

            if (held.Count > 0)
            {
                // 有拿不准的文件 → 日志**留着**，别静默丢掉唯一的判断依据
                messages.Add($"- {cookit}：{written} 个补齐、{skipped} 个已是新内容；以下文件被手改过，保持原样：");
                messages.AddRange(held);
                continue;
            }

            store.ClearJournal(cookit);
            messages.Add($"- {cookit}（{written} 个补齐、{skipped} 个已是新内容）");
        }

        if (messages.Count == 0)
        {
            return "";
        }

        return "# 前滚：处理上次未写完的落盘" + Environment.NewLine + string.Join(Environment.NewLine, messages);
    }

    /// <summary>
    /// 拟定编辑：**只记符号 + 意图 + 首次快照**，不算文件路径、不算整文件内容（那些留到预检/落盘现场做）；
    /// 同一符号只保留一条生效条目。
    /// 参数是 `track_project` 拿到的**追踪 cookie**（同一个 cookie 既用于拟定编写，也用于解除追踪）。
    /// 重新拟定同时是「解决冲突」的手段：该符号的 hash 会被更新为现在的 hash。
    /// </summary>
    public static string Stage(string cookie, string typePath, string memberName, string? content)
    {
        DraftStore store = new();
        string projectPath = RequireProjectByCookie(store, cookie, "stage_draft");

        if (content != null)
        {
            // 语法检查：新内容必须是合法的 C# 成员声明
            CodeEditor.EnsureMembersParse(content);
        }

        LoadedProject project = LoadedProject.Load(projectPath);
        (string symbolKey, string snapshot, string action) = DescribeSymbol(project, typePath, memberName, content);

        // 同一个符号只保留一条生效条目（替换，不是追加）
        store.ReplaceSymbol(projectPath, typePath, memberName, content, symbolKey, snapshot, action);
        // 拟定变了 → 落盘许可作废
        PermitStore.Invalidate(projectPath);
        // 重新拟定 = 解决冲突：这个符号的 hash 更新为现在的 hash（其它未解决冲突不受影响）
        ConflictService.AcceptCurrent(projectPath, symbolKey, SymbolBaseline.Capture(project.Compilation));

        StringBuilder builder = new();
        builder.AppendLine("# 拟定已更新");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- 符号：{symbolKey}");
        builder.AppendLine($"- 本次：{(content == null ? "删除" : "写入")}（{action}）");
        builder.AppendLine($"- 拟定条数：{store.Find(projectPath)?.Edits.Count ?? 0}");
        if (project.ModeNotice().Length > 0)
        {
            builder.AppendLine($"- {project.ModeNotice()}");
        }

        builder.AppendLine("- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。");
        return builder.ToString();
    }

    /// <summary>把 cookie 换成项目路径；cookie 无效就报「先 track_project」。</summary>
    private static string RequireProjectByCookie(DraftStore store, string cookie, string tool)
    {
        TrackingRecord? tracking = store.GetTrackingByCookie(cookie);
        if (tracking == null)
        {
            throw new InvalidOperationException(
                $"{tool} 需要 track_project 返回的追踪 cookie（没有 cookie / cookie 无效：先 track_project 拿 cookie）。");
        }

        return tracking.ProjectPath;
    }

    /// <summary>解析这次编辑对应的符号身份、快照文本与动作。</summary>
    private static (string SymbolKey, string Snapshot, string Action) DescribeSymbol(
        LoadedProject project,
        string typePath,
        string memberName,
        string? content)
    {
        string verb = content == null ? "removed" : "replaced";
        INamedTypeSymbol? type = SymbolLocator.FindType(project.Compilation, typePath);
        if (type == null || !IsExactTypePath(type, typePath))
        {
            // 两种都算"新建"，但符号身份不同：
            // - typePath 是**已存在的命名空间** → 在它下面加一个类（类名写在 content 里）
            // - 否则 typePath 就是新类型的全名（也涵盖"typePath 指嵌套类型、FindType 只解到外层"）
            // 快照为空 = 当时不存在。
            if (SymbolLocator.IsNamespace(project.Compilation, typePath))
            {
                return ($"{typePath}.{NewTypeNameOf(content)}", "", "新建类型");
            }

            return (typePath, "", content == null ? "removed" : "新建类型");
        }

        if (memberName.Length == 0)
        {
            return (SymbolBaseline.Key(type), SymbolBaseline.DeclaredText(type), verb);
        }

        IReadOnlyList<ISymbol> members = SymbolLocator.FindMembers(type, memberName);
        if (members.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{memberName}' 匹配到 {members.Count} 个重载，请带上参数列表消歧：{Environment.NewLine}"
                + MemberListRendering.DescribeOverloads(members));
        }

        if (members.Count == 0)
        {
            // 带了参数表的 memberName 是一条**精确路径**（"我要的就是这个签名"）—— 匹配不上就报错。
            // 当成新增会写出第二份同名成员（CS0111），而那要等预检诊断才看得出来。
            // 裸名字才是"新成员的名字"，匹配不上就是新增。
            if (memberName.Contains('(', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"定位不到符号：{typePath}.{memberName}"
                    + Nearby(type, memberName)
                    + " —— 参数表写错了？要是本来就想新增，就别带参数表。");
            }

            return ($"{SymbolBaseline.Key(type)}.{memberName}", "", content == null ? "removed" : "added");
        }

        ISymbol member = members[0];
        return (SymbolBaseline.Key(member), SymbolBaseline.DeclaredText(member), verb);
    }

    /// <summary>名字对得上、但签名对不上的那些成员（提示"是不是想写它"）。</summary>
    private static string Nearby(INamedTypeSymbol type, string memberName)
    {
        int bracket = memberName.IndexOf('(', StringComparison.Ordinal);
        string bare = (bracket > 0 ? memberName[..bracket] : memberName).Trim();
        List<ISymbol> same = [.. type.GetMembers(bare).Where(member => !member.IsImplicitlyDeclared)];
        if (same.Count == 0)
        {
            return "";
        }

        StringBuilder builder = new();
        builder.Append($"；{type.Name} 里叫 {bare} 的有这些：");
        foreach (ISymbol item in same)
        {
            builder.Append(Environment.NewLine).Append("  - ").Append(CodeEditor.MemberSignature(item));
        }

        return builder.ToString();
    }

    /// <summary>从一段类型声明里取类型名（给"在命名空间下加一个类"用）。</summary>
    private static string NewTypeNameOf(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "NewType";
        }

        foreach (SyntaxNode node in CSharpSyntaxTree.ParseText(content).GetRoot().DescendantNodes())
        {
            if (node is BaseTypeDeclarationSyntax declaration)
            {
                return declaration.Identifier.Text;
            }
        }

        return "NewType";
    }

    /// <summary>
    /// <paramref name="typePath"/> 是否**恰好**指向这个类型本身。
    /// <c>SymbolLocator.FindType</c> 会宽容地返回最长前缀（<c>Demo.Class1.Inner2</c> 解不到时会返回
    /// <c>Demo.Class1</c>），所以"这是不是一次新建"只能靠尾段名字自己判，不能只看 FindType 有没有返回。
    /// </summary>
    private static bool IsExactTypePath(INamedTypeSymbol type, string typePath)
    {
        string tail = typePath[(typePath.LastIndexOf('.') + 1)..];

        // 泛型的两种写法都要接受：`Ns.Box<T>` 与 `Ns.Box`1`
        int angle = tail.IndexOf('<');
        if (angle >= 0)
        {
            tail = tail[..angle];
        }

        int tick = tail.IndexOf('`');
        if (tick >= 0)
        {
            tail = tail[..tick];
        }

        return type.Name.Equals(tail.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 解决冲突（见审查意见第 6 / 7 条）：choice = `keep`（保持拟定）/ `drop`（移除拟定），
    /// 已经没有「还原为快照」这条路。两者都把该符号的 hash 更新为**现在的 hash**；`drop` 还会删掉该符号的拟定条目。
    /// </summary>
    public static string Select(string csprojPath, string memberPath, string selectCookie, string choice)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord? record = store.Find(projectPath);

        string decision = (choice ?? "").Trim().ToLowerInvariant();
        if (decision is not ("keep" or "drop"))
        {
            throw new ArgumentException("choice 只能是 keep（保持拟定）或 drop（移除拟定）。");
        }

        List<DraftEdit> matches = record == null
            ? []
            :
            [
                .. record.Edits.Where(item =>
                    item.SymbolKey.Equals(memberPath, StringComparison.Ordinal)
                    || EditLabel(item).Equals(memberPath, StringComparison.Ordinal)
                    || item.MemberName.Equals(memberPath, StringComparison.Ordinal)),
            ];
        if (matches.Count > 1)
        {
            // 不猜（同 §七：命中多个就报出来），让调用方用完整符号键消歧
            throw new InvalidOperationException(
                $"'{memberPath}' 命中 {matches.Count} 条拟定，请用完整符号键指定：{Environment.NewLine}"
                + string.Join(Environment.NewLine, matches.Select(item => "  - " + item.SymbolKey)));
        }

        DraftEdit? edit = matches.Count == 1 ? matches[0] : null;
        string symbolKey = edit?.SymbolKey ?? (memberPath ?? "").Trim();
        if (symbolKey.Length == 0)
        {
            throw new InvalidOperationException("要指定一个符号路径。");
        }

        if (edit == null && decision == "keep")
        {
            throw new InvalidOperationException($"拟定里没有这个符号：{memberPath}（keep 需要该符号有拟定；没有拟定就用 drop 接受现状）。");
        }

        SelectPermit? permit = PermitStore.TakeSelect(projectPath, symbolKey, selectCookie);
        if (permit == null)
        {
            throw new InvalidOperationException(
                "选择 cookie 无效（可能已被用掉、或已被新的查看替换）：重新查看符号（symbols）拿新的。");
        }

        string description;
        if (decision == "drop")
        {
            if (edit != null)
            {
                store.RemoveSymbol(projectPath, edit.SymbolKey);
            }

            description = "移除拟定（放弃本次写法、接受现状）";
        }
        else
        {
            description = "保持拟定（坚持本次写法）";
        }

        // 解决过的冲突：把这个符号的 hash 更新为现在的 hash
        ConflictService.AcceptCurrent(projectPath, symbolKey);
        PermitStore.Invalidate(projectPath);
        return $"# 选择已应用{Environment.NewLine}"
            + $"- 符号：{symbolKey}{Environment.NewLine}"
            + $"- 选择：{description}{Environment.NewLine}"
            + $"- 该符号的追踪 hash 已更新为现在的 hash（冲突解除）{Environment.NewLine}"
            + $"- 落盘许可已作废：要落盘请重新 confirm_draft（不带 applyCookie）做预检。";
    }

    /// <summary>
    /// 启动维护（v3 文档第六节）：① 前滚未完成的落盘 → ② 清理孤儿拟定（S0 却有拟定条目）。
    /// 这里**不**全量刷新追踪基线 —— 那会把"track 之后的外部改动"静默吞掉；
    /// 基线只在每次落盘之后按现状重算（见 RefreshBaseline）。
    /// </summary>
    public static string StartupMaintenance()
    {
        StringBuilder builder = new();
        try
        {
            string recovery = RecoverPendingWrites();
            if (recovery.Length > 0)
            {
                builder.AppendLine(recovery);
            }
        }
        catch (Exception exception)
        {
            // 前滚失败：写前日志是唯一依据，必须留着，孤儿清理也别做
            builder.AppendLine($"# 前滚失败（未完成，已保留写前日志）：{exception.Message}");
            return builder.ToString();
        }

        DraftStore store = new();
        int orphans = 0;
        foreach (DraftRecord record in store.ListAll())
        {
            if (store.GetTracking(record.ProjectPath) != null || store.HasPendingJournal(record.ProjectPath))
            {
                continue;
            }

            try
            {
                store.Clear(record.ProjectPath);
                orphans++;
            }
            catch (Exception exception)
            {
                builder.AppendLine($"# 孤儿拟定清理失败：{record.ProjectPath} —— {exception.Message}");
            }
        }

        if (orphans > 0)
        {
            builder.AppendLine($"# 孤儿拟定清理：{orphans} 个项目没有追踪记录，其拟定条目已清理（它们来自更早的版本）");
        }

        return builder.ToString();
    }

    /// <summary>列出当前拟定（只列符号与分类 —— 拟定里没有文件路径；文件在预检时才定位）。</summary>
    public static string List(string csprojPath)
    {
        string projectPath = ProjectViewer.ResolveProjectFile(csprojPath);
        DraftStore store = new();
        DraftRecord? record = store.Find(projectPath);
        if (record == null || record.Edits.Count == 0)
        {
            return $"# 拟定{Environment.NewLine}{projectPath}{Environment.NewLine}没有未完成的拟定。";
        }

        StringBuilder builder = new();
        builder.AppendLine("# 拟定");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine($"- 起始时间：{record.CreatedAt}");
        builder.AppendLine($"- 条数：{record.Edits.Count}");
        builder.AppendLine();
        foreach (DraftEdit edit in record.Edits)
        {
            builder.AppendLine($"- [{edit.Sequence}] {CategoryOf(edit)} {EditLabel(edit)}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 拟定确认：
    /// **不带 applyCookie** = 重建符号树 → 现场定位文件 → 预检（占用 / 符号冲突 / 字节）→ 无未解决冲突才发**内存** applyCookie 并记许可快照；
    /// **带 applyCookie** = 校验 cookie 与「路径 + 文件字节」→ 现场生成整文件新文本 → 落盘事务。
    /// 参数是 `track_project` 拿到的**追踪 cookie**（不再要 csprojPath）。
    /// </summary>
    public static string Confirm(string cookie, string applyCookie, bool apply)
    {
        DraftStore store = new();
        string projectPath = RequireProjectByCookie(store, cookie, "confirm_draft");
        return ConfirmProject(store, projectPath, applyCookie, apply);
    }

    /// <summary>按已解析的项目路径确认拟定（工具层只给 cookie；这里保留路径入参给内部复用与测试）。</summary>
    internal static string ConfirmProject(DraftStore store, string projectPath, string applyCookie, bool apply)
    {
        DraftRecord? record = store.Find(projectPath);
        if (record == null || record.Edits.Count == 0)
        {
            return $"# 拟定确认{Environment.NewLine}{projectPath}{Environment.NewLine}没有未完成的拟定。";
        }

        LoadedProject project = LoadedProject.Load(projectPath);
        DraftPlanner.Plan plan = DraftPlanner.Compute(project, record.Edits);
        DiagnosticSnapshot before = Analyze(project.Compilation);
        // 诊断对比必须基于**现场算出来的**拟定结果
        DiagnosticSnapshot after = Analyze(plan.Projected);

        bool cookieGiven = !string.IsNullOrWhiteSpace(applyCookie);
        if (!apply || !cookieGiven)
        {
            return Precheck(store, projectPath, project, record, plan, before, after);
        }

        return ApplyWithPermit(store, projectPath, record, plan, applyCookie, before, after);
    }

    /// <summary>预检：报告「本次改动涉及」与问题；**没有未解决冲突才发**（内存）applyCookie。</summary>
    private static string Precheck(
        DraftStore store,
        string projectPath,
        LoadedProject project,
        DraftRecord record,
        DraftPlanner.Plan plan,
        DiagnosticSnapshot before,
        DiagnosticSnapshot after)
    {
        StringBuilder builder = new();
        AppendChangeSummary(builder, projectPath, record, plan, before, after);

        List<string> problems = [];
        List<string> notices = [];
        foreach (string missing in plan.Missing)
        {
            problems.Add($"⛔ 定位不到符号：{missing} —— 可能被改名/删除；查看符号确认，或重新 stage_draft 重新拟定");
        }

        // 未解决冲突 = 追踪里保存的符号 hash 与当前源码不对齐（新增的、没有拟定的符号也算）
        TrackingRecord? tracking = store.GetTracking(projectPath);
        IReadOnlyDictionary<string, string> currentBaseline = SymbolBaseline.Capture(project.Compilation);
        if (tracking == null)
        {
            problems.Add("⛔ 这个项目已经不在追踪里：请重新 track_project 拿新的 cookie。");
        }
        else
        {
            IReadOnlyList<SymbolConflict> conflicts = ConflictService.Unresolved(tracking.Baseline, currentBaseline, record.Edits);
            if (conflicts.Count > 0)
            {
                problems.Add($"⛔ 还有 {conflicts.Count} 个未解决的冲突（没有未解决冲突才发落盘 cookie）：");
                foreach (SymbolConflict conflict in conflicts.Take(50))
                {
                    problems.Add($"  - {conflict.Describe()}");
                }

                if (conflicts.Count > 50)
                {
                    problems.Add($"  - …（还有 {conflicts.Count - 50} 个）");
                }

                problems.Add("  解决路径：查看符号（拿 selectCookie）→ select_draft（keep 保持拟定 / drop 移除拟定）→ 或重新 stage_draft 重新拟定。");
            }
        }

        Dictionary<string, SymbolPermitSnapshot> snapshot = new(StringComparer.Ordinal);
        foreach (DraftPlanner.PlannedFile file in plan.Files)
        {
            // 只有"这次要新建这个文件"时才怕撞车：往既有文件里加成员是正常路径（N1 回归的教训）
            bool createsNewFile = file.Actions.Any(action => action.Contains("新建", StringComparison.Ordinal));
            if (createsNewFile && File.Exists(file.FilePath))
            {
                problems.Add($"⛔ 新建目标文件已存在：{file.FilePath} —— 这次是新建，整文件会被覆盖，请确认或换个类型名");
            }

            WriteProbe probe = FileWriter.Probe(file.FilePath);
            if (probe == WriteProbe.DirectoryWillBeCreated)
            {
                // 提示级：不影响能不能落盘（目录会被建出来），但 agent 该知道推导出的路径会引入新层级
                notices.Add($"⚠ 会新建目录：{file.FilePath} —— 这个目录现在不存在，落盘时会建出来");
            }
            else if (probe != WriteProbe.Writable)
            {
                problems.Add($"⛔ 不可写：{file.FilePath} —— {DescribeProbe(probe)}");
                continue;
            }

            // 编码无法判定就在**预检这里**拒绝（不进落盘事务）：需求「写文件通则」要求"不猜、拒绝写并报告"
            if (File.Exists(file.FilePath))
            {
                try
                {
                    FileWriter.DetectEncoding(file.FilePath);
                }
                catch (InvalidOperationException exception)
                {
                    problems.Add($"⛔ {exception.Message}");
                    continue;
                }
            }

            string hash = FileWriter.ComputeHash(file.FilePath);

            // 文件级登记：这样"补 partial"这类不带符号归属的附带改动也在许可轨里，
            // 否则它在"发许可 → 传许可"窗口里被外部改过时既不报冲突也不作废许可
            snapshot[file.FilePath] = new SymbolPermitSnapshot(file.FilePath, hash);
            foreach (string symbol in file.SymbolKeys)
            {
                snapshot[symbol] = new SymbolPermitSnapshot(file.FilePath, hash);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 预检查");
        foreach (string notice in notices)
        {
            builder.AppendLine($"- {notice}");
        }

        if (problems.Count == 0)
        {
            string cookie = PermitStore.GrantApply(projectPath, snapshot);
            builder.AppendLine("- ✅ 无占用、无冲突");
            builder.AppendLine();
            builder.AppendLine($"- 落盘 cookie：`{cookie}`（传回来才落盘；每次预检换新，旧 cookie 立刻失效）");
            return builder.ToString();
        }

        PermitStore.Invalidate(projectPath);
        foreach (string problem in problems)
        {
            builder.AppendLine($"- {problem}");
        }

        builder.AppendLine();
        builder.AppendLine("（预检查未通过，不发放 applyCookie：解决上面列出的问题后重新预检。）");
        return builder.ToString();
    }

    /// <summary>带 cookie 落盘：判定顺序写死（无许可 → cookie 不符 → 逐项比对），通过才写。</summary>
    private static string ApplyWithPermit(
        DraftStore store,
        string projectPath,
        DraftRecord record,
        DraftPlanner.Plan plan,
        string applyCookie,
        DiagnosticSnapshot before,
        DiagnosticSnapshot after)
    {
        if (!PermitStore.HasApply(projectPath))
        {
            throw new InvalidOperationException("没有有效的落盘许可：请先不带 cookie 调用一次做预检。");
        }

        IReadOnlyDictionary<string, SymbolPermitSnapshot>? snapshot = PermitStore.CheckApply(projectPath, applyCookie);
        if (snapshot == null)
        {
            throw new InvalidOperationException(
                "cookie 已失效（可能被新的预检替换，或进程重启过）：请重新预检。");
        }

        if (plan.Missing.Count > 0)
        {
            // 符号在"发许可 → 传许可"窗口里被改名/删除 → 拒绝，绝不静默少写一部分
            PermitStore.Invalidate(projectPath);
            throw new InvalidOperationException(
                "落盘前有符号已经定位不到，许可已作废：\n"
                + string.Join("\n", plan.Missing.Select(item => "  - " + item)));
        }

        List<string> mismatched = [];
        foreach (DraftPlanner.PlannedFile file in plan.Files)
        {
            // 文件级：涵盖"补 partial"这类没有符号归属的附带改动
            if (!snapshot.TryGetValue(file.FilePath, out SymbolPermitSnapshot? filePermit))
            {
                mismatched.Add($"{file.FilePath}：许可里没有这个文件（本次改动范围变了）");
            }
            else if (!string.Equals(filePermit.FileHash, FileWriter.ComputeHash(file.FilePath), PathComparison.Comparison))
            {
                mismatched.Add($"{file.FilePath} 在发许可之后被外部改过");
            }

            foreach (string symbol in file.SymbolKeys)
            {
                if (!snapshot.TryGetValue(symbol, out SymbolPermitSnapshot? permit))
                {
                    continue;
                }

                if (!string.Equals(permit.FilePath, file.FilePath, PathComparison.Comparison))
                {
                    mismatched.Add($"{symbol}：文件从 {permit.FilePath} 变成 {file.FilePath}（移动/改名）");
                    continue;
                }

                if (!string.Equals(permit.FileHash, FileWriter.ComputeHash(file.FilePath), PathComparison.Comparison))
                {
                    mismatched.Add($"{symbol}：{file.FilePath} 在发许可之后被外部改过");
                }
            }
        }

        if (mismatched.Count > 0)
        {
            PermitStore.Invalidate(projectPath);
            throw new InvalidOperationException(
                "落盘目标状态与发许可时不一致，已作废许可：\n"
                + string.Join("\n", mismatched.Select(item => "  - " + item)));
        }

        List<KeyValuePair<string, string>> files =
        [
            .. plan.Files.Select(file => new KeyValuePair<string, string>(file.FilePath, file.NewContent)),
        ];

        // 写前日志要带"写之前的 hash 与编码"：前滚靠它区分「已经写好 / 还是写前状态 / 被手改过」
        List<DraftStore.JournalEntry> entries =
        [
            .. plan.Files.Select(file => new DraftStore.JournalEntry(
                file.FilePath,
                file.NewContent,
                FileWriter.ComputeHash(file.FilePath),
                FileWriter.DetectEncoding(file.FilePath).Encoding.WebName)),
        ];

        // 落盘事务：写前日志 → 原子写 → 清日志 → 清拟定与许可 → format → 整体重算基线
        store.RecordJournal(record.Cookit, entries);

        // 写：内容没变的文件会被跳过（不搅动时间戳），如实报出来
        List<string> skipped = [];
        foreach (DraftStore.JournalEntry entry in entries)
        {
            if (!FileWriter.WriteAtomic(entry.FilePath, entry.NewContent))
            {
                skipped.Add(entry.FilePath);
            }
        }

        MsBuildEvaluator.ClearCache();

        // format 在**清理之前**：失败就保留拟定与写前日志（可直接重试），并如实报出它额外改了什么
        Dictionary<string, string> beforeFormat = files.ToDictionary(
            file => file.Key,
            file => FileWriter.ComputeHash(file.Key),
            StringComparer.OrdinalIgnoreCase);
        (bool formatted, string formatLog) = FormatRunner(projectPath, [.. files.Select(file => file.Key)]);
        List<string> reformatted =
        [
            .. files
                .Select(file => file.Key)
                .Where(path => !string.Equals(beforeFormat[path], FileWriter.ComputeHash(path), PathComparison.Comparison)),
        ];

        if (!formatted)
        {
            StringBuilder failure = new();
            failure.AppendLine("# 落盘未完成（文件已写入，但格式化失败）");
            failure.AppendLine($"- 已写入 {files.Count} 个文件：{string.Join("、", files.Select(file => file.Key))}");
            failure.AppendLine($"- {formatLog}");
            failure.AppendLine("- 拟定与写前日志**已保留**：处理完之后重新 confirm_draft（不带 cookie）预检即可重试。");
            return failure.ToString();
        }

        store.ClearJournal(record.Cookit);
        store.Clear(projectPath);
        PermitStore.Invalidate(projectPath);
        RefreshBaseline(projectPath);

        StringBuilder builder = new();
        AppendChangeSummary(builder, projectPath, record, plan, before, after);
        builder.AppendLine();
        builder.AppendLine("## 已落盘");
        foreach (KeyValuePair<string, string> file in files)
        {
            builder.AppendLine(skipped.Contains(file.Key, PathComparison.Comparer)
                ? $"- {file.Key}（内容未变，跳过）"
                : $"- {file.Key}");
        }

        builder.AppendLine("- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）");
        builder.AppendLine($"- 格式化：{formatLog}");
        builder.AppendLine(reformatted.Count == 0
            ? "- format 额外改动：无"
            : $"- format 额外改动：{string.Join("、", reformatted)}（hash 变化）");
        builder.AppendLine("- 追踪继续，基线已按落盘后的现状整体重算。");
        return builder.ToString();
    }

    /// <summary>落盘后按当前现状**整体重算**追踪基线（否则文件里其它符号的变化会被当成"未追踪更改"）。</summary>
    private static void RefreshBaseline(string projectPath)
    {
        DraftStore store = new();
        TrackingRecord? tracking = store.GetTracking(projectPath);
        if (tracking == null)
        {
            return;
        }

        LoadedProject project = LoadedProject.Load(projectPath);
        store.SaveTracking(projectPath, tracking.TrackingCookie, SymbolBaseline.Capture(project.Compilation));
    }

    /// <summary>变更分类 + 本次改动涉及 + 诊断对比（预检与落盘共用）。</summary>
    private static void AppendChangeSummary(
        StringBuilder builder,
        string projectPath,
        DraftRecord record,
        DraftPlanner.Plan plan,
        DiagnosticSnapshot before,
        DiagnosticSnapshot after)
    {
        builder.AppendLine("# 拟定确认");
        builder.AppendLine($"- 项目：{projectPath}");
        builder.AppendLine();
        builder.AppendLine("## 变更分类");
        foreach (string category in Categories)
        {
            List<DraftEdit> group = [.. record.Edits.Where(edit => CategoryOf(edit) == category)];
            if (group.Count == 0)
            {
                continue;
            }

            builder.AppendLine($"### {category}（{group.Count}）");
            foreach (DraftEdit edit in group)
            {
                builder.AppendLine($"- {EditLabel(edit)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 本次改动涉及");
        if (plan.Files.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (DraftPlanner.PlannedFile file in plan.Files)
            {
                builder.AppendLine($"- {file.FilePath}（{string.Join("、", file.SymbolKeys)}）");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）");
        AppendDiagnostics(builder, "新增", after, before);
        AppendDiagnostics(builder, "消失", before, after);
    }

    /// <summary>把一条拟定算成若干次文件改动（改成员 / 删成员 / 新增成员 / 新建类型都可能牵扯多个文件）。</summary>
    internal static List<CodeChange> BuildChanges(
        LoadedProject project,
        CSharpCompilation compilation,
        string typePath,
        string memberName,
        string? content,
        out string description)
    {
        INamedTypeSymbol? type = SymbolLocator.FindType(compilation, typePath);
        if (type == null || !IsExactTypePath(type, typePath))
        {
            // typePath 指向**已存在的命名空间** = 在它下面加一个类：类名取自 content，
            // 于是真正的类型全名是「命名空间 + "." + 类名」。
            string fullPath = SymbolLocator.IsNamespace(compilation, typePath)
                ? $"{typePath}.{NewTypeNameOf(content)}"
                : typePath;
            description = $"新建类型 {fullPath}";
            List<CodeChange> created = [];

            // 新建嵌套类型时外层类型必须标 partial：新文件里外层也要带 partial，否则两边对不上
            // （见测试 Stage_creates_a_nested_type_and_marks_the_outer_type_partial）。
            int lastDot = fullPath.LastIndexOf('.');
            if (lastDot > 0)
            {
                INamedTypeSymbol? outer = SymbolLocator.FindType(compilation, fullPath[..lastDot]);
                if (outer != null)
                {
                    created.AddRange(EnsurePartial(outer));
                }
            }

            created.Add(CreateNewType(project.Info, compilation, fullPath, content));
            return created;
        }

        string member = (memberName ?? "").Trim();
        IReadOnlyList<ISymbol> members = member.Length == 0 ? [] : SymbolLocator.FindMembers(type, member);
        if (members.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{member}' 匹配到 {members.Count} 个重载，请带上参数列表消歧：{Environment.NewLine}"
                + MemberListRendering.DescribeOverloads(members));
        }

        List<CodeChange> changes = [];
        if (members.Count == 1)
        {
            ISymbol symbol = members[0];
            if (content == null)
            {
                description = $"删除成员 {typePath}.{member}";
                changes.Add(CodeEditor.ComputeRemove(symbol, format: true));
            }
            else
            {
                description = $"修改成员 {typePath}.{member}";
                changes.Add(CodeEditor.ComputeReplace(symbol, content, format: true));
            }

            return changes;
        }

        if (content == null)
        {
            throw new InvalidOperationException($"要删除的成员不存在：{typePath}.{member}");
        }

        description = $"在 {typePath} 新增成员 {member}";
        List<CodeChange> partials = EnsurePartial(type);
        CodeChange added = CodeEditor.ComputeAdd(type, content, before: "", format: true);

        // 落盘时每个文件只取**最后一条**改动，所以"同一文件里既要补 partial 又要加成员"
        // 必须合成一条：在补完 partial 的内容上再加成员，否则前一条会被后一条盖掉。
        CodeChange? sameFile = partials.FirstOrDefault(
            change => change.FilePath.Equals(added.FilePath, PathComparison.Comparison));
        if (sameFile != null)
        {
            partials.Remove(sameFile);
            added = CodeEditor.ComputeAddToSource(sameFile.FilePath, sameFile.NewContent, typePath, content, format: true);
        }

        changes.AddRange(partials);
        changes.Add(added);
        return changes;
    }

    /// <summary>新增成员要求**所有**分部声明都带 <c>partial</c>；缺的补上（每个文件一条改动）。</summary>
    internal static List<CodeChange> EnsurePartial(INamedTypeSymbol type)
    {
        List<CodeChange> changes = [];
        Dictionary<string, List<SyntaxNode>> perFile = new(PathComparison.Comparer);
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            SyntaxNode node = reference.GetSyntax();
            string file = node.SyntaxTree.FilePath;
            if (!perFile.TryGetValue(file, out List<SyntaxNode>? nodes))
            {
                nodes = [];
                perFile[file] = nodes;
            }

            nodes.Add(node);
        }

        foreach (KeyValuePair<string, List<SyntaxNode>> pair in perFile)
        {
            // 同一个文件里可能有**多处**该类型的声明（分部类）→ 必须一次替换多处：
            // 逐个 ReplaceNode 的话，第二个节点已经不在替换后的新树里了
            List<TypeDeclarationSyntax> pending =
            [
                .. pair.Value.OfType<TypeDeclarationSyntax>().Where(declaration => !HasPartial(declaration)),
            ];
            if (pending.Count == 0)
            {
                continue;
            }

            SyntaxNode updated = pair.Value[0].SyntaxTree.GetRoot()
                .ReplaceNodes(pending, (original, _) => AddPartial((TypeDeclarationSyntax)original));
            changes.Add(new CodeChange(pair.Key, updated.ToFullString(), 0, 0, "补 partial"));
        }

        return changes;
    }

    /// <summary>新建类型：外部类按命名空间 + 根命名空间定路径；内部类用 <c>Outer.Inner.cs</c>。</summary>
    internal static CodeChange CreateNewType(
        ProjectFileInfo info,
        CSharpCompilation compilation,
        string typePath,
        string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"类型不存在，必须在同一次拟定里给出 content 才能新建：{typePath}");
        }

        string[] segments = typePath.Split('.');
        string typeName = segments[^1];

        for (int index = segments.Length - 1; index > 0; index--)
        {
            string candidate = string.Join('.', segments[..index]);
            INamedTypeSymbol? outer = SymbolLocator.FindType(compilation, candidate);
            if (outer?.DeclaringSyntaxReferences.FirstOrDefault() is { } reference)
            {
                string outerFile = reference.SyntaxTree.FilePath;
                string nestedDirectory = Path.GetDirectoryName(outerFile) ?? info.ProjectDirectory;
                // 需求：内部类用 Outer.Inner.cs 作文件名
                string nestedPath = Path.Combine(nestedDirectory, $"{outer.Name}.{typeName}.cs");
                return new CodeChange(nestedPath, BuildNestedTypeSource(outer, content), 0, 0, "新建内部类");
            }
        }

        string rootNamespace = ReadRootNamespace(info.ProjectPath);
        string namespaceName = string.Join('.', segments[..^1]);
        string relative = namespaceName;
        if (rootNamespace.Length > 0 && namespaceName.StartsWith(rootNamespace, StringComparison.Ordinal))
        {
            relative = namespaceName[rootNamespace.Length..].Trim('.');
        }

        string targetDirectory = relative.Length == 0
            ? info.ProjectDirectory
            : Path.Combine(info.ProjectDirectory, relative.Replace('.', Path.DirectorySeparatorChar));

        return new CodeChange(
            Path.Combine(targetDirectory, typeName + ".cs"),
            BuildTypeSource(namespaceName, content),
            0,
            0,
            "新建类型");
    }

    /// <summary>
    /// 拼出新建类型的文件内容：文件级命名空间 + <c>content</c> 本身。
    /// <c>content</c> 是**完整的类型声明**（含修饰符、特性、基类、<c>partial</c>），
    /// 所以这里不再套一层 <c>public class</c> —— 否则
    /// <c>[McpServerToolType] public static class X</c> 这种声明会被包成一团拼不出来的东西。
    /// </summary>
    private static string BuildTypeSource(string namespaceName, string content)
    {
        StringBuilder builder = new();
        if (namespaceName.Length > 0)
        {
            builder.AppendLine($"namespace {namespaceName};");
            builder.AppendLine();
        }

        builder.AppendLine(content);
        return builder.ToString();
    }

    /// <summary>
    /// 内部类：新文件里从最外层到宿主类都写一遍（都带 <c>partial</c>），
    /// 这样「外部类用 partial 定义」的规则在新文件里也成立；修饰符照抄原声明（访问级别必须一致）。
    /// </summary>
    private static string BuildNestedTypeSource(INamedTypeSymbol outer, string content)
    {
        List<INamedTypeSymbol> chain = [];
        for (INamedTypeSymbol? current = outer; current != null; current = current.ContainingType)
        {
            chain.Insert(0, current);
        }

        string namespaceName = chain[0].ContainingNamespace is { IsGlobalNamespace: false } container
            ? container.ToDisplayString()
            : "";

        StringBuilder builder = new();
        if (namespaceName.Length > 0)
        {
            builder.AppendLine($"namespace {namespaceName};");
            builder.AppendLine();
        }

        string indent = "";
        foreach (INamedTypeSymbol link in chain)
        {
            string modifiers = ModifiersOf(link);
            string head = modifiers.Length == 0 ? "partial" : modifiers + " partial";
            builder.AppendLine($"{indent}{head} class {link.Name}");
            builder.AppendLine($"{indent}{{");
            indent += "    ";
        }

        // content 是这一层的**完整类型声明**（含修饰符、特性），直接内缩进来，不再套 public class
        builder.AppendLine(Indent(content, indent));

        for (int index = chain.Count - 1; index >= 0; index--)
        {
            indent = indent[..^4];
            builder.AppendLine($"{indent}}}");
        }

        return builder.ToString();
    }

    /// <summary>照抄原声明的修饰符（同一类型的分部声明访问级别必须一致）。</summary>
    private static string ModifiersOf(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not TypeDeclarationSyntax declaration)
        {
            return "public";
        }

        return string.Join(' ', declaration.Modifiers.Select(token => token.Text));
    }

    private static string Indent(string text, string prefix)
    {
        return string.Join(
            Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? line : prefix + line));
    }

    private static bool HasPartial(TypeDeclarationSyntax declaration)
    {
        return declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
    }

    /// <summary>
    /// 在**所有修饰符之后**、类型关键字之前插入 <c>partial</c>。
    /// C# 要求 <c>partial</c> 紧邻类型关键字，所以 <c>public sealed class</c> 必须变成
    /// <c>public sealed partial class</c>（如果插在访问修饰符后面就会得到
    /// <c>public partial sealed class</c>，那是 CS0267）。
    /// </summary>
    private static TypeDeclarationSyntax AddPartial(TypeDeclarationSyntax declaration)
    {
        List<SyntaxToken> modifiers = [.. declaration.Modifiers];

        // 插在末尾 —— 即紧邻 class / struct / interface / record；
        // 带上尾随空格，避免拼出 "partialclass"
        SyntaxToken partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        modifiers.Insert(modifiers.Count, partial);
        return declaration.WithModifiers(SyntaxFactory.TokenList(modifiers));
    }

    private static bool IsAccessModifier(SyntaxToken token)
    {
        return token.IsKind(SyntaxKind.PublicKeyword)
            || token.IsKind(SyntaxKind.PrivateKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword);
    }

    internal static DiagnosticSnapshot Analyze(Compilation compilation)
    {
        List<DiagnosticKey> errors = [];
        List<DiagnosticKey> warnings = [];
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                continue;
            }

            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            DiagnosticKey key = new(
                diagnostic.Id,
                diagnostic.GetMessage(),
                span.Path,
                span.StartLinePosition.Line + 1);
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(key);
            }
            else
            {
                warnings.Add(key);
            }
        }

        return new DiagnosticSnapshot(errors, warnings);
    }

    private static void AppendDiagnostics(
        StringBuilder builder,
        string label,
        DiagnosticSnapshot current,
        DiagnosticSnapshot previous)
    {
        HashSet<string> existing =
        [
            .. previous.Errors.Select(item => item.PairingKey),
            .. previous.Warnings.Select(item => item.PairingKey),
        ];
        List<DiagnosticKey> addedErrors =
        [
            .. current.Errors.Where(item => !existing.Contains(item.PairingKey)),
        ];
        List<DiagnosticKey> addedWarnings =
        [
            .. current.Warnings.Where(item => !existing.Contains(item.PairingKey)),
        ];
        int total = addedErrors.Count + addedWarnings.Count;
        // 数量要显式报出来（错误 / 警告分开），否则"新增/消失"是空的还是若干条要靠数
        builder.AppendLine($"- {label}：{total} 条（错误 {addedErrors.Count} / 警告 {addedWarnings.Count}）");
        if (total == 0)
        {
            return;
        }

        foreach (DiagnosticKey item in addedErrors.Concat(addedWarnings).Take(50))
        {
            builder.AppendLine($"  - {item}");
        }

        if (total > 50)
        {
            builder.AppendLine($"  - …（还有 {total - 50} 条）");
        }
    }

    /// <summary>跑 dotnet format；返回「是否成功」与给 agent 看的说明（失败不抛，交给调用方决定保留拟定）。</summary>
    /// <summary>
    /// 跑 dotnet format 的入口：**测试可替换成 no-op**（端到端用例验的是落盘语义，
    /// 不该为一次真实格式化等几十秒）。生产默认就是真跑。
    /// </summary>
    internal static Func<string, IReadOnlyList<string>, (bool Succeeded, string Log)> FormatRunner = RunFormat;

    private static (bool Succeeded, string Log) RunFormat(string projectPath, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return (true, "（没有文件需要格式化）");
        }

        List<string> arguments = ["format", projectPath, "--no-restore", "--include"];
        arguments.AddRange(files);
        try
        {
            CommandResult result = CommandRunner.Run("dotnet", arguments, Path.GetDirectoryName(projectPath) ?? ".", 300);
            return result.Succeeded
                ? (true, "已对本次改动的文件跑 dotnet format")
                : (false, $"dotnet format 未成功（退出码 {result.ExitCode}）：{FirstLine(result.Output)}");
        }
        catch (Exception exception) when (exception is TimeoutException or System.ComponentModel.Win32Exception)
        {
            return (false, $"dotnet format 未执行：{exception.Message}");
        }
    }

    private static string FirstLine(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }

    /// <summary>
    /// 项目的根命名空间：先看 csproj 里的 <c>RootNamespace</c>；**没写就用项目名**
    /// （MSBuild 的默认值就是项目名），否则新建类型的文件会多出一层目录（<c>Demo\Demo\X.cs</c>）。
    /// </summary>
    private static string ReadRootNamespace(string projectPath)
    {
        try
        {
            XElement? root = XDocument.Load(projectPath).Root;
            string declared = root == null ? "" : ProjectFileInfo.ReadProperty(root, "RootNamespace");
            if (declared.Length > 0)
            {
                return declared;
            }
        }
        catch (System.Xml.XmlException)
        {
            // 读不动就当没写，走下面的回退
        }

        return Path.GetFileNameWithoutExtension(projectPath);
    }

    /// <summary>拟定条目的显示名：新建类型没有成员名，别显示成 <c>Demo.NewType.</c>。</summary>
    internal static string EditLabel(DraftEdit edit)
    {
        return edit.MemberName.Length == 0 ? edit.TypePath : $"{edit.TypePath}.{edit.MemberName}";
    }

    /// <summary>预检查结论的中文说明（工具输出，供调用方判断"这次落盘会不会卡在占用上"）。</summary>
    private static string DescribeProbe(WriteProbe probe)
    {
        return probe switch
        {
            WriteProbe.Writable => "✅ 可写",
            WriteProbe.Busy => "⚠ 被占用（编辑器 / 杀软 / 索引服务可能持有它；落盘会退避重试，仍失败则报错）",
            WriteProbe.ReadOnly => "⛔ 只读属性（去掉只读才能写）",
            WriteProbe.Denied => "⛔ 没有写权限",
            WriteProbe.DirectoryNotWritable => "⛔ 目录不可写（新建文件需要可写目录）",
            _ => "⚠ 未知状态",
        };
    }
}
