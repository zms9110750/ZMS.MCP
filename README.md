# ZMS.MCP

Model Context Protocol (MCP) stdio server — 提供可组合的工具集，通过标准输入/输出与 AI 客户端通信。

## 构建

```bash
dotnet build
```

## 发布

```bash
dotnet publish -c Release -o publish
```

## 使用

MCP Server 通过 stdio 协议工作，通常由支持 MCP 的 AI 客户端（如 Claude Desktop、VS Code 等）自动启动。

在客户端配置中添加：

```json
{
  "mcpServers": {
    "zms-mcp-cli": {
      "command": "path\\to\\zms9110750.ZMS.MCP.Cli.exe",
      "args": []
    }
  }
}
```

## 项目

| 项目 | 说明 | 工具清单 |
|------|------|----------|
| `src/ZMS.MCP.Csharp` | 基于 **Roslyn** 的 C# 工作区工具：解决方案 / 项目 / 符号 / NuGet / 拟定事务 | 见 [`src/ZMS.MCP.Csharp/README.md`](src/ZMS.MCP.Csharp/README.md) |
| `src/ZMS.MCP.Cli` | 通用工具集（URI 协议层建设中，见 `docs/`） | 见 `src/ZMS.MCP.Cli` |
| `test/ZMS.MCP.Test`、`test/ZMS.MCP.Csharp.Test` | 单元测试 | — |

## 文档

需求文档在 `docs/`，实现以文档为基线：

- **`docs/ZMS.MCP.Csharp.v4.md`** — C# 工作区工具的**工具手册**（当前版本，以实机调用结果写成）
- `docs/Csharp.md` — C# 工作区工具的需求基线；签名在 v4 有变动，见 v4 手册的差异清单
- `docs/Resource.md` — 网络与文件资源（http / ftp / 本地文件与目录 / 压缩包）
- `docs/Structured.md` — 结构化文档（json / xml / yaml / toml / ini）
- `docs/Workflow.md` — GitHub Actions 工作流生成

## 工程约定

- `Directory.Build.props` — 全仓库通用：项目元数据、程序集名（`zms9110750.<项目名>`）、`InternalsVisibleTo`，以及所有项目都需要的 **MCP 包**（`ModelContextProtocol`）。
  没有模板开关，没有条件包引用，也没有编译前格式化。
- `Global.cs` — 全仓库共享：MCP 相关的 `global using`、通用入口 `McpStdioServer`、以及**工作空间边界**。
  每个 MCP 项目的 `Program.cs` 只需要：

  ```csharp
  using ZMS.MCP;

  await McpStdioServer.RunAsync(args);
  ```

  `McpStdioServer` 负责注册 stdio 传输、扫描本程序集注册全部工具、**统一校验每次调用的路径参数是否落在工作区内**，
  并把日志固定到 stderr（stdout 留给协议）。
  项目自己的启动准备（如 ZMS.MCP.Csharp 的写前日志前滚）写在各自的 `Program.cs` 里。

  工作区 = 环境变量 `ZMS_MCP_WORKSPACE`（多个目录用 `;` 分隔）→ 进程当前目录；路径越界会在**工具执行前**被拒绝，
  拒绝的话里说清"你给的是哪、边界在哪、怎么改"。这**不是**安全边界（服务是独立进程、用启动者的权限跑），
  它要解决的是"让调用方知道自己碰的是哪儿"。细节见 v4 手册的"工作空间边界"一节。

  测试项目**不注入** `Global.cs`（它们引用被测程序集，`internal` 经 `InternalsVisibleTo` 可见）——
  注进去会让"源里的类型"和"引用程序集里的同名类型"撞出 `CS0436`。

## 项目结构

```
ZMS.MCP/
├── Directory.Build.props   # 全仓库通用：元数据 + MCP 包
├── Global.cs               # 全仓库共享：MCP global using + 通用入口
├── docs/                   # 需求基线（Csharp / Resource / Structured / Workflow）
├── src/
│   ├── ZMS.MCP.Cli/        # 通用工具集（协议层尚未实现，见 docs）
│   └── ZMS.MCP.Csharp/     # Roslyn 驱动的 C# 工作区工具
└── test/
    ├── ZMS.MCP.Test/       # 单元测试
    └── ZMS.MCP.Csharp.Test/# C# 工作区工具的单元测试
```
