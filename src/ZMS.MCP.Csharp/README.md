# ZMS.MCP.Csharp

基于 **Roslyn** 的 C# 工作区 MCP Server：用符号模型访问和修改代码，而不是用文件工具读写整份源码。

## 为什么

用 `file` 类工具改 C#，AI 必须先读整个文件（大量无关内容），再按文本匹配替换，容易错位。
这里把 **解决方案 / 项目 / 类型 / 成员** 作为一等对象暴露给 AI：

- 给文件夹 → 扫出 `.sln` / `.slnx` / `.csproj`
- 给解决方案 → 列项目，可添加 / 创建 / 移除项目
- 给项目 + 完全限定名 → 列类型、列成员
- 给项目 + 完全限定名 + 成员路径 → 查看成员源码，或整成员替换 / 新增 / 删除

所有定位都走 Roslyn 语法树与语义模型，命中即改，不需要猜文件路径。

## 工具

> MCP 客户端看到的工具名是 snake_case（C# SDK 自动转换），例如 `ScanProjects` → `scan_projects`。

### 项目层

| 工具 | 说明 |
|------|------|
| `scan_projects` | 扫描文件夹，列出 `.sln` / `.slnx` / `.csproj`（默认排除 `bin`、`obj`） |
| `list_solution_projects` | 列出解决方案内的项目（支持 `.sln` 与 `.slnx`） |
| `add_project_to_solution` | 把已有项目加入解决方案（`dotnet sln add`） |
| `remove_project_from_solution` | 从解决方案移除项目（`dotnet sln remove`） |
| `create_project` | 用 `dotnet new` 创建项目 |

### 符号层

`memberPath` 语法：`命名空间.类型[.成员[(参数类型,...)]]`
参数类型支持 C# 关键字别名与简名，例如 `Probe.Class1.Add(int,int)`、`Foo.Bar.Log(string)`。

| 工具 | 说明 |
|------|------|
| `list_types` | 列出项目内所有类型（完全限定名 + 文件 + 行号，可按关键字过滤） |
| `list_members` | 列出某个类型的成员（签名 + 文件 + 行号；不重复列 get/set 访问器） |
| `get_member` | 查看成员（或整个类型）的签名、行范围与源码，不需要知道文件路径 |
| `update_member` | 用新 C# 代码整段替换成员声明 |
| `add_member` | 在类型中新增成员（可一次多个，可指定插到某个成员之前） |
| `remove_member` | 删除成员 |

写操作统一返回改动文件与行号范围，便于后续核对。

### 例子

```
list_types            projectPath=...\Probe.csproj  filter=Class
get_member            projectPath=...\Probe.csproj  memberPath=Probe.Class1.Add(int,int)
update_member         projectPath=...\Probe.csproj  memberPath=Probe.Class1.Add
                      code="public static int Add(int a, int b) { return a + b; }"
```

## 构建

```bash
# 在 ZMS.MCP 仓库根
dotnet build ZMS.MCP.slnx
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

- 入口一行：`await McpStdioServer.RunAsync(args);`（见仓库根 `Global.cs`），stdio 传输与工具扫描由它统一处理。
- 项目加载是**轻量静态解析**：直接读 `.csproj`（`TargetFramework`、`Compile` 项、`ProjectReference`、`PackageReference`），
  收集源文件后用 Roslyn 构建 `CSharpCompilation`，**不启动 MSBuild**、不产生中间文件。
- 引用来自宿主运行时程序集（`TRUSTED_PLATFORM_ASSEMBLIES`）。
- 包引用目前只做记录，尚未参与编译引用解析（等 NuGet 相关能力一起做）。
- 编辑通过 `SyntaxNode` 替换/插入/删除后回写文件，默认用 Roslyn `Formatter` 统一格式（`format=false` 可关）。
- 新增/替换的代码会先包进一个临时类型解析：多成员、XML 注释、语法错误都能得到确定结果（语法错误直接报错，不落盘）。

## 项目结构

```
ZMS.MCP/
└── src/ZMS.MCP.Csharp/         # 本项目：ZMS.MCP 仓库内的一个 MCP 服务
    ├── README.md
    ├── Program.cs              # 入口：McpStdioServer.RunAsync
    ├── Roslyn/
    │   ├── ProjectLoader.cs    # .csproj 解析 + CSharpCompilation 构建
    │   ├── SolutionExplorer.cs # 扫描 / sln、slnx 读写 / dotnet CLI 调用
    │   ├── SymbolLocator.cs    # 完全限定名 → 类型 / 成员符号
    │   └── CodeEditor.cs       # 成员查看 / 替换 / 新增 / 删除
    └── Tools/
        ├── ToolGuard.cs        # 异常 → 工具文本结果
        ├── ProjectTools.cs     # 项目层 MCP 工具
        └── SymbolTools.cs      # 符号层 MCP 工具
```
