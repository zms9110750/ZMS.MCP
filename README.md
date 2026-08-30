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
    "zms-mcp": {
      "command": "path\\to\\ZMS.MCP.Cli.exe",
      "args": []
    }
  }
}
```

## 工具

> 在此列出你提供的 MCP 工具

| 工具 | 描述 |
|------|------|
| Echo | 回显消息（示例工具） |

## 项目结构

```
ZMS.MCP/
├── src/ZMS.MCP.Cli/        # MCP Server 主程序
│   ├── Program.cs          # 入口：Host + stdio 传输
│   └── Tools/              # MCP 工具定义
├── test/ZMS.MCP.Test/      # 单元测试
└── ...
```
