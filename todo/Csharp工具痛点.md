# 用 ZMS.MCP.Csharp 写 ZMS.MCP.Structured 时撞到的问题

> 背景：这一轮我用这套工具（`track_project` / `stage_draft` / `confirm_draft` / `edit_project_metadata` /
> `install_packages` / `add_project_to_solution` / `symbols`）从零写了一个新项目 —— 两个 MCP 工具 +
> 一个格式层 + 一个测试项目，约 1500 行。
>
> 结论先放前面：**新建类型、批量改造、删除成员这三件事最后都是我直接写文件完成的**，
> 工具在这三类上要么做不到、要么会改坏。下面每条给「现象 → 根因（定位到代码）→ 影响 → 建议」。

## 一、新建类型

### 1. 生成的壳写死 `public class`：没有 `[McpServerToolType]`，也不是 `static`

**现象**：`stage_draft(typePath="ZMS.MCP.Structured.Tools.StructuredTools", memberName="")` 落盘后文件是：

```csharp
namespace ZMS.MCP.Structured.Tools;

public class StructuredTools          // 不是 static，没有 [McpServerToolType]
{
    <我给的 content>
}
```

MCP 工具类必须是 `[McpServerToolType] public static class`，否则 `WithToolsFromAssembly()` 不注册它。只能手工补两行。

**根因**：`DraftService.BuildTypeSource`（`Draft/DraftService.cs:931-945`）里写死的：

```csharp
builder.AppendLine($"public class {typeName}");
```

**影响**：每个新建的工具类都要手工补 `[McpServerToolType]` + `static`。只要漏了，工具静默不注册（没有编译错误、没有警告）。

**建议**：`BuildTypeSource` 接受"类型声明的修饰符 + attribute"（或直接接受一段类型声明头），别写死。

### 2. `content` 是**类体**，不是类型声明

**现象**：给整个类声明 → 预检报 `CS0542 'StructuredTools': member names cannot be the same as their enclosing type`；带上 `using` → `Code does not parse as C# member declaration(s): A using clause must precede all other elements`。

**根因**：同上，`BuildTypeSource` 拼的是 `public class {name} { <content> }`；`DraftService.Stage` 里 `CodeEditor.EnsureMembersParse(content)` 的注释也写着"新内容必须是合法的 C# **成员声明**"。

**影响**：`stage_draft` 的 `[Description]` 只说了 "memberName = ... for methods"、"empty stages a brand new type"，**没说 content 是类体、不能带 using / 类型声明**。第一次用必然失败一次。

**建议**：把这条写进 `stage_draft` 的介绍里（一句话就能省掉一次失败）。

## 二、给已有的类加成员

### 3. `AddPartial` 把 `partial` 插在**访问修饰符之后**，任何 `static` / `sealed` 都会踩

**现象**：

| 原声明 | 它改成 | 编译结果 |
|---|---|---|
| `public class X` | `public partial class X` | ✅ 碰巧对 |
| `public static class X` | `public partial static class X` | ❌ `CS0267` |
| `internal sealed class X` | `internal partial sealed class X` | ❌ `CS0267` |

**根因**：`DraftService.AddPartial`（`Draft/DraftService.cs:1018-1031`）—— 它的注释写着"在访问修饰符之后插入 `partial`"，实现是：

```csharp
int index = 0;
while (index < modifiers.Count && IsAccessModifier(modifiers[index]))
{
    index++;
}
```

它只跳过**访问**修饰符。而 C# 要求 `partial` **紧邻类型关键字**（在 `static` / `sealed` / `abstract` 之后）。

**影响**：**每次给带 `static`/`sealed` 的类加成员都会踩**，因为 `EnsurePartial` 在每次加成员时都会跑。我当时只能先把类声明手工改成 `internal sealed partial class` 才不再触发。

**建议**：`index = modifiers.Count`（插到所有修饰符之后、类型关键字之前）。一行的事。

## 三、`memberName` 的写法（最花时间的一类）

### 4. 字段 / 嵌套类型必须用**裸名**，带参数表就"定位不到符号"

**现象**：删 `CookieEntry` 用 `CookieEntry(string,string)` → `⛔ 定位不到符号：...CookieEntry(string,string) —— 可能被改名/删除`；用裸名 `CookieEntry` → `删除（removed）`✅。

**根因**：`symbols` 列成员时，字段和嵌套类型显示的就是裸名（`...StructuredTools.Cookies`）；而 `stage_draft` 的匹配走 `SymbolLocator`，方法要求"含参数表"。两边对"路径怎么写"的约定**不完全一致**。

### 5. 方法参数表里的 `string?` 匹配不上

**现象**：`memberName = "Edit(string,string,string,string?,string,bool,bool,bool,bool)"` → 匹配失败（被判成"新增"）；而工具自己列的签名是 `Edit(string, string, string, string, ...)` —— 它把 `string?` 规范化成了 `string`。

**根因**：`SymbolLocator.TypeMatches` 只比 `ToDisplayString(QualifiedNameFormat)`、`Type.Name` 和关键字别名 —— 可空引用类型的 `Name` 是 `String`（`?` 只是注解，不是类型）。同一个根因还导致 **`argsList` 匹配不了数组**（`IArrayTypeSymbol.Name` 是空串）：`symbols(path='...RemovePackages(string,string[],bool)')` 报 `Member not found`。

### 6. 参数表里**带空格**的写法定位不到

**现象**：`Refuse(string, IReadOnlyList<JsonNode>, string)`（按 `symbols` 显示的写法照抄，带空格）→ 定位不到；改成不带空格的 `Refuse(string,IReadOnlyList<JsonNode>,string)` → 删除成功。

### 7. 匹配不上时**不报错，当"新增"处理**

**现象**：上面第 5 条那次匹配失败，`stage_draft` 照样回 `本次：写入（added）`，结果类里出现了**两份 `Edit`**（`CS0111`）。只有预检的诊断才看得出来。

**建议（4~7 一起）**：`symbols` 的路径解析和 `stage_draft` 的 `memberName` 解析用**同一套**，并且把"解析出来的实际写法"回显出来（我最后是靠**裸名**才稳定下来的）；匹配失败时报错，别当新增。

## 四、没有"替换整个类型"这个操作

### 8. `memberName=""` 对**已存在**的类型是"往类体里追加"

**现象**：我想整体替换 `StructuredTools` 的类体（一次改多处 + 删掉一批旧成员），传了完整类体 + `memberName=""` → `本次：写入（replaced）`。预检时发现是**两份完整类体**（`ReadStructured` / `Read` / `Edit` / `Load` / `Save` / `FormatOf` 全部 `CS0111`）。

**说明**：`memberName=""` 只在类型**不存在**时是"新建 = content 就是整个类体"；类型**已存在**时它变成"追加内容"。

**影响**：批量改造（删 9 个 + 改 5 个 + 加 15 个）只能一个一个 `stage_draft`；而"删 + 改"混在一起时又非常容易踩前面几条。我最终是**手写 `Document.cs` 和 `StructuredTools.cs` 两个文件**收场的。

**建议**：给一个明确的"整类替换"入口（比如 `memberName` 用 `null` / 特殊值表示"content 就是完整的类型声明"）。

## 五、其他

### 9. 删字段会留下声明壳

**现象**：删

```csharp
private static readonly ConcurrentDictionary<string, CookieEntry> Cookies = new(StringComparer.Ordinal);
```

之后文件里剩：

```csharp
private static readonly ConcurrentDictionary<string, CookieEntry>;      // CS1519 Invalid token ';'
```

**根因**：删除时取的区间只覆盖了"名字 + 初始化器"的一部分，没把整个字段声明（含 `;`）一起拿掉。

**影响**：手工清一行。

### 10. 新建项目、顶层语句、项目级 using 都在工具能力之外

这三件是**设计边界**，但叠起来就是"起步要三次手工"：

| 要做的事 | 为什么工具做不到 | 当时怎么绕的 |
|---|---|---|
| 建一个新项目 | `edit_project_metadata` 拒绝不存在的文件（设计如此） | `dotnet new console` |
| 写 `Program.cs` | 顶层语句不是符号，`typePath` 必须是类型 | 手写 3 行 |
| 给项目加 `using` | `content` 被拼进 `namespace` 之后，写不了 using | 改 csproj 加 `<Using Include="..." />` |

### 11. 好的一面也说一句

`track_project` → `stage_draft` → `confirm_draft`（预检）→ `confirm_draft`（落盘）这条链本身很好用：

- 预检的**诊断对比**（新增/消失若干条错误）帮我提前抓到了 4 次真问题（`CS0542` / `CS0267` / `CS1519` / `CS0111`）
- 落盘时的"定点替换 + 跑 `dotnet format`"干净，从不误伤别的成员
- 前提是**把 `memberName` 写对**（见第三节）

如果只做"给一个健康的类加/改一个成员"，这套工具是顺手且可靠的。

---

## 六、必须用 Roslyn 做、文本工具搓不出来的事

判据一句话：**要改的东西能不能用「名字」唯一确定。**

能 → 必须 Roslyn（它知道这个名字在**每一处**指的是哪个符号）；只知道位置或文本 → 文本工具（我们那个 Structured）。

### 只有语义层做得到的

| 能力 | 为什么文本工具搓不出来 |
|---|---|
| **重命名符号**（VS 里的 Rename） | 要改的是"所有引用**这个符号**的地方"。文本替换会连**同名但不同的符号**、注释里的名字、字符串里的字面量一起改掉。 |
| **查全部引用 / 调用者 / 实现者 / 派生类** | 文本搜 `Save(` 会把同名的另一个 `Save`、注释、字符串里的 `Save` 全算进来。 |
| **改签名（连带改所有调用点）** | 得按每个调用点的实参位置逐个改，还要管可选参数、`params`、命名实参、泛型推断。 |
| **安全删除一个成员** | "真的没人用"是语义判断 —— 成员可能在 `nameof`、反射用的字符串、或另一个 `#if` 分支里被引用。文本只能给出"这个名字没出现"。 |
| **这一处调用的是哪个重载 / `var` 背后是什么类型** | 需要重载解析与类型推断，纯文本没有。 |
| **一个成员在原文里从哪到哪** | 文本工具得自己推（前面的文档注释、attribute、行尾注释算不算这个成员）；Roslyn 的 `DeclaringSyntaxReferences` 直接给区间。 |
| **partial 类 / 多目标框架 / `#if` 下的同一符号** | 一个符号可能有几处声明、几套条件编译分支；按文件读只能得到**好几份残缺视图**。 |
| **诊断与 CodeFix** | 编译器知道"这里有错、该怎么修"；文本工具只能数括号。 |
| **source generator 产出的符号** | 那些符号在任何 `.cs` 里都不存在，只活在语义模型里。 |

### 反过来，文本工具该干的

- 改**值 / 字面量**（`$.logging.level`、`/config/logging/@level` 这种"位置 + 新值"）
- 改**非 C# 文件**（json / yaml / toml / ini / xml）—— 这些压根没有符号
- 以及"我知道它在哪、但没法用名字描述"的任何情况

**两者是互补的**：Roslyn 给得出"这个成员在原文的哪个区间"，这恰恰是文本工具做不到又最需要的；
而文本工具承诺的"只改这一处、其余原样"，也是 Roslyn 的语法树重写很难保证的（它会重排、会丢注释）。

## 七、别人家的 Roslyn MCP：他们的痛点

> 这一节是**搜来的**（不是我在本仓库实测的），来源附在最后。列出来是因为有几条和我们撞得一模一样。

### 都有谁（都叫 Roslyn MCP，可不是一个东西）

`BeinerChes/RoslynMcpServer`、`MarcelRoozekrans/roslyn-codelens-mcp`、`Atypical-Consulting/RoselineMCP`、
`brendankowitz/dotnet-roslyn-mcp`、`MadQ/RoslynMcp`、`alphaleonis/dotlens-mcp`、`sharplens-mcp`、
`skeletoken-mcp`、`sharp-mcp`，以及 npm 上包一层官方 **Roslyn Language Server（LSP）** 的 `roslyn-mcp-server`。

工具数从 7 个到 92 个都有 —— "该暴露多少个工具"业内也没共识。

### 1. 语义分析的边角：顶层语句

`BeinerChes/RoslynMcpServer` 的 Issue #139：`GetCallers` 对**顶层语句里调用的方法返回 0 个调用者** ——
图分析只遍历 `TypeDeclarationSyntax`，漏了 `GlobalStatementSyntax`。

（这条和本仓库直接相关：我们的 `Program.cs` 就是顶层语句。）

同一个 issue 还提到：只读工具在"没找到"时回 `isError = true`，会导致**同批的其它 MCP 调用被一起取消**；
以及它的 API 要 `file/line/column` 而不是符号名。

### 2. 死代码检测的误报

同一个项目：`roslyn_find_dead_code` 把**经 JSON 序列化反射访问的 DTO 属性**一律判成死代码 —— 静态分析看不到反射。
他们的补救是：自动排除入口点、带 attribute 的属性、外部/BCL 符号、测试文件；属性走 `Reads`/`Accesses` 边而不是 `Calls`。

### 3. 安全：加载 analyzer 就等于执行别人的代码

**CVE-2026-45555 / GHSA-552p-8f74-6x7q**（`roslyn-codelens-mcp` 0.0.9–1.17.0）：
`get_diagnostics` 会**加载并执行**目标解决方案引用的所有 `DiagnosticAnalyzer` 程序集 ——
没有白名单、没有签名校验、不提示用户，而且 `includeAnalyzers` **默认 true**。
谁能让目标解决方案引用一个恶意 `.csproj`，谁就能在 MCP 进程里执行任意代码（CVSS 7.8）。

### 4. MSBuildWorkspace 的代价（`MadQ/RoslynMcp` README 自己列的）

- 首次工具调用 ~10 秒（加载 workspace）
- **新增一个 `.cs` 文件会触发整个 workspace 重载**（~8-10 秒）—— MSBuildWorkspace 不支持给 SDK-style 项目原地加文档（`dotnet/roslyn#36781`）
- 大解决方案：Orleans（235 个编译项目）冷启动 **7 分钟以上**；换 adhoc workspace 能压到 ~22 秒，代价是丢掉完整 NuGet 解析
- 每个 subagent 各起一个 MCP 进程、各扛 100MB+ 的 workspace
- 只在 Windows 上测过，Linux/macOS 未验证

**哪条对我们适用**（本仓库是 `MsBuildEvaluator` 跑一次求值，不是 `MSBuildWorkspace` 打开方案）：

| 别人的痛点 | 我们 |
|---|---|
| 首次 ~10 秒 | **有**，但形态不同：我们跑 `dotnet msbuild` 求值拿属性/文件列表/引用，不是打开 workspace |
| **新增 `.cs` → 全量重载** | **没有** —— 拟定在内存里投影，编辑期间不动磁盘；落盘才写文件 |
| 大方案冷启 7 分钟 | 取决于方案规模（`MSBuildWorkspace` 才有那个量级） |
| 每 agent 一份 100MB+ workspace | 我们缓存的是求值结果（属性 + 文件 + 引用），不是 workspace |

**但有一笔省不掉**：`MsBuildEvaluator` 的缓存失效条件里有 `SourceFingerprint`（**文件数 + 最新写入时间**，
它的注释写着"新增 / 删除 / **改动** `.cs` 也要让缓存失效，否则刚落盘的新类型在本次会话里会一直看不到"）。
所以**每次落盘之后的下一次工具调用都要重新求值** —— 这是刻意要的，不是浪费。

可优化点：落盘分两种情形，现在一视同仁 ——
**新增文件**（文件列表变了）必须失效；**只改内容**（文件列表没变）理论上可以只刷新内容、保住求值结果。

### 5. 智能体**不用**这些工具（和我们撞得最狠的一条）

- `MadQ/RoslynMcp` README：agent **默认还是去 grep / 读文件**，除非明确要求（他们为此开了 issue #99 研究强制钩子）
- `philips-software/roslyn-analyzers` Issue #984：agent **无视** MCP 给的 `fix_formatting`，自己跑去调 `dotnet format`
- 同一个 issue 里更值得记的一条：`run_dogfood` 工具**超时**了（`MCP error -32001: Request timed out`）却返回 `{}`，
  agent 把它**当成成功**—— 这正是"工具失败了但看起来成功了"那类问题。
  （和我们前面说的"静默成功"是同一件事：**输出的形态必须让 agent 分得清"没做事"和"做完了"**。）

### 6. MCP 协议层的坑

- **stdout 纪律**：好几个 server 都强调 `Console.WriteLine` 会毁掉 JSON-RPC 会话，日志只能走 stderr（本仓库的 `Global.cs` 正是这么做的）
- **NativeAOT / 序列化**：MCP C# SDK 在 NativeAOT 下 `MissingMethodException`；工具传/返 `List<object>` 需要 `JsonTypeInfo` 元数据（改用 `JsonElement`）
- **DLL 被占用**：MCP 进程持着 workspace，`dotnet build` 想覆盖同一个 DLL 时 Windows 报"文件正被另一进程使用"；有人靠 `RoslynMcpWorkspace=true` 属性 + 打开 workspace 时禁用 analyzer 引用来绕
- **工具注解表达力不够**（`RoselineMCP` 点出的）：MCP SDK 的 `destructiveHint` 是**按工具静态**的，
  表达不了"只有 `previewOnly: false` 时才具破坏性" —— 于是带写能力的工具只能挂最坏情况的注解。
  （这正是我们 `edit_structured` 的处境：它的破坏性取决于 `insert` / `update` / `remove` 开关，注解表达不了。）

### 7. 两个有意思的设计动机

- **`sharp-mcp`**：直接读文件太慢、太烧上下文（**500 行的类 ≈ 2000 tokens**）；还有个叫得响的痛点 ——
  **NuGet 幻觉**（模型对着根本不存在的 API 写代码）。对策是用 `MetadataLoadContext` 加载**真实 IL** 来回答"这个包到底有什么"。
  （本仓库对应的是 `list_doc_symbols` / `get_package_metadata`：从本地缓存读真实文档与 nuspec。）
- **`skeletoken-mcp`** 自己承认：`find_symbol` 是**语法匹配不是语义**（只比名字，不解析引用）；
  `extract_member` 覆盖不到运算符、索引器、本地函数；骨架**按文件给**，所以 `partial` 类会得到好几份残缺视图。
  （最后这条和我们撞的 `EnsurePartial` 是同一类问题的两面：**工具在 partial 上都不太靠谱**。）

### 来源

- BeinerChes/RoslynMcpServer Issue #139、commit 78f7cac
- MarcelRoozekrans/roslyn-codelens-mcp 安全公告 GHSA-552p-8f74-6x7q（CVE-2026-45555）
- MadQ/RoslynMcp README
- philips-software/roslyn-analyzers Issue #984
- alphaleonis/dotlens-mcp、brendankowitz/dotnet-roslyn-mcp、Atypical-Consulting/RoselineMCP README
- skeletoken-mcp、sharplens-mcp、roslyn-mcp-server（npm）、sharp-mcp（DEV 文章）
