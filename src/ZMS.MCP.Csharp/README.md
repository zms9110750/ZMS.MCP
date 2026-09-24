# ZMS.MCP.Csharp

基于 **Roslyn** 的 C# 工作区 MCP Server：把 **解决方案 / 项目 / 类型 / 成员** 当作一等对象来访问和修改，
而不是用文件工具读写整份源码。

需求基线见仓库 `docs/Csharp.md`。

## 为什么

用 `file` 类工具改 C#，AI 必须先读整个文件（大量无关内容），再按文本匹配替换，容易错位。
这里用 Roslyn 的语法树与语义模型定位：

- 给文件夹 → 扫出 `.sln` / `.slnx` / `.csproj`（按解决方案分组，标出"客观存在但没被解决方案描述"的项目）
- 给解决方案 → 看 slnx 的虚拟文件夹树，添加 / 移除项目
- 给项目 → 看原文 + 所有参与项目声明的文件（props / 中央包管理 / 还原生成物）
- 给项目 + 完全限定名 → 列类型、列成员、看成员源码
- 改成员 / 新增 / 删除 → 走**拟定**（累积 → 确认 → 落盘），可预演、可核对诊断
- NuGet → 查询包与版本、读写包引用、安装 / 移除（走命令行，不进事务）

## 工具

> 工具名由 C# 方法名自动转成 snake_case（例如 `scan_projects`）；参数名与 `docs/Csharp.md` 一致
> （`path` / `csprojPath` / `slnxPath` / `type` / `modifier` / `argsList` / `nugetPack` / `nugetName` / `cookit`）。

### 项目层

| 工具 | 说明 |
|------|------|
| `scan_projects` | 扫描文件夹，按解决方案分组列出 `.sln` / `.slnx` / `.csproj`（跳过 `bin`、`obj`、`.git`、`.vs`、`node_modules`） |
| `view_solution_tree` | 以树状图展示 **slnx**，含虚拟文件夹（`Folder Name="/src/"`）层级 |
| `view_project` | 展示 csproj 原文 + 参与项目声明的文件：最近的 `Directory.Build.props`（含它自己 `Import` 的上层）、`Directory.Packages.props`、`Directory.Build.targets`、`global.json`、`NuGet.config`、还原生成的 `*.nuget.g.props\|targets` |
| `migrate_solution_to_slnx` | `dotnet sln migrate`：sln → slnx（命令行改盘，不进事务） |
| `add_project_to_solution` | 把**已有**项目加入 slnx，可指定虚拟文件夹（`dotnet sln add --solution-folder`） |
| `remove_project_from_solution` | 从 slnx 移除项目（`dotnet sln remove`） |
| `edit_project_metadata` | 用新内容替换 csproj：先过 XML 语法检查 + 根元素 `<Project>` 检查，合法才写 |
| `list_project_packages` | 列出包引用：顶级包 / 依赖传递包 / 项目引用带来的顶级包。图取自真实还原结果，比输入旧时标注"可能已过期" |
| `install_packages` | 安装 NuGet 包（`dotnet add package`）：自建图 + 漏洞索引选版本，只直接引入顶级包，返回分类结果 |
| `remove_packages` | 移除 NuGet 包（`dotnet remove package`）：移除前 / 后各建一次图，报告消失的传递包 |

编辑解决方案只支持 **slnx**：`.sln` 会先被拒绝，要求先迁移。

### NuGet 查询层

| 工具 | 说明 |
|------|------|
| `search_packages` | 按关键字搜包（本地缓存 / nuget.org；两者同时打开时 `[]` 标记本地也存在） |
| `list_package_versions` | 列某个精确包名的版本（版本范围过滤、`*` 含预览版；头部报告还有多少未列出） |
| `get_package_metadata` | 取包的 readme 与 nuspec（先本地缓存，再 nuget.org；都没有时给出 projectUrl / repositoryUrl） |
| `list_doc_symbols` | 列出某个 **NuGet 包**的 XML 文档注释符号（只读本地缓存；`NTPFMED`、按精度推断 type、重载消歧） |

### 符号层

| 工具 | 说明 |
|------|------|
| `list_symbols` | 列项目**自身源码**里的符号（`NCSIPFEMD` 种类过滤、修饰符过滤、方法参数过滤、`D` = 只看有 XML 文档注释的） |
| `list_types` | 列项目内所有类型（完全限定名 + 文件 + 行号） |
| `list_members` | 列某个类型的成员（不重复列 get/set 访问器） |
| `get_member` | 看成员（或整个类型）的签名、行范围与源码 —— 不需要知道文件路径 |
| `update_member` | 用新 C# 代码整段替换成员声明 |
| `add_member` | 在类型中新增成员（可插到某个成员之前） |
| `remove_member` | 删除成员 |

`memberPath` 语法：`命名空间.类型[.成员[(参数类型,...)]]`，参数类型支持 C# 关键字别名与简名。

### 拟定层（事务）

| 工具 | 说明 |
|------|------|
| `stage_draft` | 拟定新增 / 修改 / 删除成员（可累积；语法不过则不记录） |
| `list_draft` | 列出当前拟定与 cookit（agent 重启后靠它接上） |
| `confirm_draft` | 确认拟定：给变更分类（增加 / 删除 / 修改）与诊断增减，带 cookit 才真正落盘 |

拟定状态存 sqlite（`%LOCALAPPDATA%\ZMS.MCP.Csharp\drafts.db`，不在被操作的项目里）；MCP 重启不丢。
落盘时：校验基线 hash（拟定期间被外部改过的文件 → 拒绝覆盖）、写前日志 + 前滚、按原编码写回、
对改动文件跑 `dotnet format --include`、**不做任何 git 操作**。

## 构建

```bash
# 在 ZMS.MCP 仓库根
dotnet build ZMS.MCP.slnx
dotnet test ZMS.MCP.slnx
```

## 使用

MCP Server 走 stdio 协议，由支持 MCP 的客户端启动（程序集名带 `zms9110750.` 前缀，由 `Directory.Build.props` 统一生成）：

```json
{
  "mcpServers": {
    "zms-mcp-csharp": {
      "command": "path\\to\\zms9110750.ZMS.MCP.Csharp.exe",
      "args": []
    }
  }
}
```

## 实现说明

- 入口：`await McpStdioServer.RunAsync(args);`（仓库根 `Global.cs` 提供通用入口），启动时先按写前日志**前滚**补齐上次未写完的落盘。
- 项目加载：先问 **MSBuild**（`dotnet msbuild -getProperty/-getItem`）要"项目的事实"——编译项、引用集、`DefineConstants`、`LangVersion`、`Nullable`；拿不到才降级成轻量静态解析，并在输出里标注"简化模式"。
- `obj` 的位置一律问 MSBuild（`MSBuildProjectExtensionsPath` / `ProjectAssetsFile`），不硬编码 `<项目目录>/obj/`。
- 包依赖图取自真实还原结果（`ReferencePath`，退回 `project.assets.json`），所以"显示什么"和"实际编译什么"一致。
- 漏洞选版本：直接取 NuGet 漏洞索引就地做版本范围匹配，**落盘前**就选出"最新且不漏洞"的版本；索引拿不到时明确标注"未经核对"，不假装安全。
- 写文件通则：按 `.editorconfig` 的 `charset` → BOM → UTF-8 严格校验定编码，都不成立就**拒绝写**；单文件写临时文件后原子替换，多文件用写前日志 + 前滚。
- 新增 / 替换的代码先包进一个临时类型解析：多成员、XML 注释、语法错误都能得到确定结果（语法错误直接报错，不落盘）。
- 修改/新增成员时会把类型的所有 `partial` 声明补齐；新建类型按命名空间与 `RootNamespace` 推导文件路径，内部类用 `Outer.Inner.cs`。
- 参数封闭的命令行操作做成工具（加包、迁移解决方案等），代码里统一标注"命令行改盘，不进事务"。

## 项目结构

```
src/ZMS.MCP.Csharp/
├── Program.cs                    # 入口：前滚 + McpStdioServer.RunAsync
├── Roslyn/
│   ├── ProjectLoader.cs          # MSBuild 评估装配 CSharpCompilation（失败降级并标注）
│   ├── MsBuildEvaluator.cs       # dotnet msbuild -getProperty/-getItem 评估 + 缓存
│   ├── SolutionExplorer.cs       # 扫盘 / sln、slnx 读写 / dotnet CLI 调用
│   ├── SolutionViewer.cs         # 查看 slnx 树 / 迁移 sln→slnx
│   ├── SymbolLocator.cs          # 完全限定名 → 类型 / 成员符号
│   ├── SymbolQuery.cs            # 列出符号（种类 / 修饰符 / 参数 / 文档注释过滤）
│   ├── NuGetXmlDocumentation.cs  # 本地 NuGet 缓存的 XML 文档注释查询
│   └── CodeEditor.cs             # 成员查看 / 替换 / 新增 / 删除
├── NuGet/
│   ├── NuGetCache.cs             # 本地包缓存（版本、nuspec、readme）
│   ├── NuGetOnline.cs            # nuget.org（搜索、版本、nuspec、readme）
│   ├── PackageGraph.cs           # 真实还原结果 → 包依赖图
│   ├── PackageManager.cs         # 安装 / 移除包（建图 + 漏洞索引 + 命令行）
│   ├── VersionRange.cs           # 版本号与版本区间
│   └── VulnerabilityIndex.cs     # NuGet 漏洞索引
├── Project/
│   ├── ProjectViewer.cs          # 查看 csproj + 参与声明的文件
│   ├── ProjectEditor.cs          # 编辑元数据
│   ├── FileWriter.cs             # 写文件通则（编码判定 / 原子替换 / 基线 hash）
│   └── CommandRunner.cs          # 命令行通道
├── Draft/
│   ├── DraftService.cs           # 拟定：累积 / 确认 / 诊断对比 / 落盘 / 前滚
│   └── DraftStore.cs             # 拟定状态与写前日志（sqlite）
├── Storage/McpPaths.cs           # MCP 自己的数据目录
└── Tools/                        # MCP 工具（项目层 / NuGet 层 / 符号层 / 拟定层）
```
