# MCP.Workflow

生成 GitHub Actions 工作流文件：**参数是一棵有类型的树**，由树生成稳定的 yaml。

## 强类型模型从哪来

先查证的结果（和最初的印象不一样，写清楚免得走弯路）：

- 官方的 Actions 工作流解析器是 **TypeScript**（npm 包 `@actions/workflow-parser`，仓库 `actions/languageservices`），早期还有过 Go 版；**没有官方 C# 实现**
- 但官方有一份机器可读的语法规范：**`workflow-v1.0.json`**（就在 `actions/languageservices` 里，是官方解析器的驱动 schema）—— 最权威的真相来源
- C# 侧最对口的是 **`samsmithnz/GitHubActionsDotNet`**：提供 GitHub Actions 的 **C# 强类型模型**和 YAML 序列化（`GitHubActionsSerialization.Serialize`），MIT 许可。它正好是"模型 → yaml"的方向，可以直接反过来用
- 其他候选：`Sharpliner`（用 C# 写流水线 DSL，偏"写代码"）、`sator-imaging/GitHubWorkflow`（YamlDotNet 解析，偏"读"）

## 路线取舍

| 路线 | 优点 | 代价 |
|------|------|------|
| **fork `GitHubActionsDotNet`** | 现成的强类型模型 + 序列化，直接能"树 → yaml" | 模型覆盖度取决于作者跟进；要跟着官方语法演进 |
| 照官方 `workflow-v1.0.json` 自己建模 | 最权威、最可控，新语法能自己补 | 要自己写模型 + 序列化 + 校验 |
| 用 `Sharpliner` | 成熟、自带校验 | 它是"C# 代码"而不是"参数树"，与"参数有类型的树"这个目标不一致 |

推荐：**fork `GitHubActionsDotNet` 起步，把官方 `workflow-v1.0.json` 当权威参照** —— 缺什么补什么，生成前后都过一遍 schema。

## 反向用（核心流程）

1. 工具参数就是那棵强类型的树（字段与模型一一对应）
2. 树 → 反序列化成模型对象
3. 模型 → 序列化成 yaml
4. 生成后过一遍 schema 校验，再把 yaml 交给落盘

## 稳定输出

"稳定"是指同一棵树生成的结果**永远一样**，可回归测试：

- 键顺序固定（不能依赖字典/反射的枚举顺序）
- 缩进、引号、多行字符串的折行规则固定
- 同一棵树生成两次，字节一致

## 列出可用结构

让调用方知道树能怎么长。

- kind：要查的结构（必填）—— 触发器（`on`）/ 字段 / 动作
- value?：定位到某个具体节点，返回它的可选值（如某触发器的类型、某动作的输入）

## 生成工作流

- path：目标 yaml 路径（必填）
- tree：强类型参数树（必填）

生成 yaml → schema 校验 → 落盘（遵守"写文件通则"）。

## 校验

- path?：读一个已有 yaml 来校验
- tree?：直接给树来校验

二者给一个；返回 schema 错误 + 语义提示（缺 `runs-on`、job 依赖成环之类）。
