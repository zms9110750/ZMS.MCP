# ZMS.MCP

Model Context Protocol (MCP) stdio server —— 一组可组合的工具，通过标准输入/输出与 AI 客户端通信。

## 构建与测试

```bash
dotnet build ZMS.MCP.slnx
dotnet test  ZMS.MCP.slnx
```

## 发布

```bash
dotnet publish src/ZMS.MCP/ZMS.MCP.csproj -c Release -o publish
```

## 使用

MCP Server 走 stdio，通常由支持 MCP 的客户端（Claude Desktop、VS Code 等）自动启动。在客户端配置里加：

```json
{
  "mcpServers": {
    "zms-mcp": {
      "command": "path\\to\\zms9110750.ZMS.MCP.exe",
      "args": []
    }
  }
}
```

`zms9110750.ZMS.MCP.exe` 是**聚合入口**：一个进程提供下列四组工具（共 40 个）。四个项目也各自能独立启动，
把上面那行换成 `zms9110750.ZMS.MCP.Csharp.exe` 之类即可，只提供那一组。

## 项目

| 项目 | 说明 | 工具 |
|------|------|------|
| `src/ZMS.MCP` | **聚合入口**：引用下面四个，一个进程同时提供它们的全部工具 | 40 个（不新增） |
| `src/ZMS.MCP.Core` | 共享类库：凭据（cookie）、路径规范化、指纹 —— 四个项目都引用它 | — |
| `src/ZMS.MCP.Csharp` | 基于 **Roslyn** 的 C# 工作区工具：解决方案 / 项目 / 符号 / NuGet / 拟定事务 | 20 |
| `src/ZMS.MCP.Resource` | 资源访问：本地文件与目录、FTP、压缩包、http、浏览器、缓存、区间抽取 | 15 |
| `src/ZMS.MCP.Structured` | 结构化文档（json / xml / yaml / toml / ini） | 2 |
| `src/ZMS.MCP.Workflow` | 由强类型树生成 GitHub Actions 工作流 | 3 |

测试项目与之一一对应，放在 `test/`。

## 文档

需求文档在 `docs/`，实现以文档为基线。四份都是**工具手册**——每个工具的准确行为，
以实机调用结果写成：

- **`docs/ZMS.MCP.Csharp.md`** — C# 工作区工具（当前是 v4，20 个工具）
- `docs/ZMS.MCP.Resource.md` — 资源访问（15 个工具）
- `docs/ZMS.MCP.Structured.md` — 结构化文档（2 个工具）
- `docs/ZMS.MCP.Workflow.md` — GitHub Actions 工作流生成（3 个工具）

`plan/` 是另一层：那里放**痛点**（为什么要做）与**提案**（打算怎么做，含备选、缺点、
以及那些还没定下来的数字）。`docs/` 只写已经做成的样子。

## 工程约定

- `Directory.Build.props` — 全仓库通用：项目元数据、程序集名（`zms9110750.<项目名>`）、`InternalsVisibleTo`，
  以及所有 MCP 项目都需要的 **MCP 包**（`ModelContextProtocol`）。没有模板开关，没有条件包引用，也没有编译前格式化。
- `Global.cs` — **MCP 项目**共享：MCP 的 `global using`、通用入口 `McpStdioServer`、工作空间边界。
  每个 MCP 项目的 `Program.cs` 只需要：

  ```csharp
  using ZMS.MCP;

  await McpStdioServer.RunAsync(args);
  ```

  `McpStdioServer` 负责 stdio 传输、注册 MCP 工具、**统一校验每次调用的路径参数是否落在工作区内**，
  并把日志固定到 stderr（stdout 留给协议）。项目自己的启动准备（如 Csharp 的写前日志前滚）写在各自的 `Program.cs`。
  聚合入口额外传一个 `toolAssemblies` 参数，把工具分散在四个程序集这件事报给它。

  工作区 = 环境变量 `ZMS_MCP_WORKSPACE`（多个目录用 `;` 分隔）→ 进程当前目录。路径越界会在**工具执行前**被拒绝，
  拒绝的话里说清"你给的是哪、边界在哪、怎么改"。这**不是**安全边界（服务是独立进程、用启动者的权限跑），
  它要解决的是"让调用方知道自己碰的是哪儿"。

  不注入 `Global.cs` 的有两类：**测试项目**（引用被测程序集，`internal` 经 `InternalsVisibleTo` 可见，
  注进去会撞 `CS0436`）和 **`ZMS.MCP.Core`**（纯工具库，不该背 MCP 入口与 Hosting 依赖）。
- 工具失败一律由 `Global.cs` 里那道调用过滤器收口：异常转成 `Error: ` 开头的文本 + `isError=true`，
  所以工具方法里不再自己 `try/catch` 包一层。

## 项目结构

```
ZMS.MCP/
├── Directory.Build.props   # 全仓库通用：元数据 + MCP 包
├── Global.cs               # MCP 项目共享：global using + 通用入口 + 工作空间边界
├── docs/                   # 需求文档与工具手册
├── src/
│   ├── ZMS.MCP/            # 聚合入口（引用下面四个）
│   ├── ZMS.MCP.Core/       # 共享类库（凭据等）
│   ├── ZMS.MCP.Csharp/     # Roslyn 驱动的 C# 工作区工具
│   ├── ZMS.MCP.Resource/   # 本地 / FTP / 压缩包 / http / 浏览器
│   ├── ZMS.MCP.Structured/ # 结构化文档
│   └── ZMS.MCP.Workflow/   # GitHub Actions 工作流生成
└── test/                   # 与上面五个一对应的测试项目
```
