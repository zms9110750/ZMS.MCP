# ZMS.MCP.Workflow 工具手册 v1

这份手册以**实机调用结果**写成：每个工具的参数取自运行时 schema，返回值示例是实际调用回来的原文（不是从代码推断的）。共 **3 个工具**。

它干的事只有一件：**用一棵强类型树生成 GitHub Actions 工作流 yaml**，外加"查这棵树能怎么长"与"校验一份已有的 yaml"。

## 约定

- 参数类型用 C# 可空语法：`string` = 必填；`string?` = 可空（默认 `null`）。
- **错误形态统一**：`Error: <消息>`，且协议层的 `isError` 为 `true`。缺凭据、凭据对不上都**不算失败**——它们是两段式的正常路径。
- **每次返回的都是 markdown 文本**，不是 JSON。
- **树是工具自己的参数**：`write_workflow` 的 `tree` 是一个嵌套对象参数，
  它的 JSON 架构由 MCP 在 `tools/list` 里直接给出（字段名就是下面各处引用的那些）。
  **不再另写一份 schema** —— 协议自带的就是那份"强类型架构"。

## 树长什么样

顶层这几个键，与 yaml 一一对应：

| 树里的字段 | yaml 里 | 说明 |
|---|---|---|
| `name` | `name:` | 显示名 |
| `on` | `on:` | 触发器，**至少一个** |
| `env` | `env:` | 全局环境变量 |
| `permissions` | `permissions:` | 整句（`read-all` / `write-all`）**或**逐个权限，二者只能给一种 |
| `defaults` | `defaults:` | 全局默认（`run.shell` / `run.working-directory`） |
| `concurrency` | `concurrency:` | 并发组 |
| `jobs` | `jobs:` | **至少一个**作业，键就是作业名 |

模型里一共这 **7 个字段**；没给的那些不会出现在生成的 yaml 里。

字段名是**驼峰**（`runsOn`、`timeoutMinutes`、`branchesIgnore`），而 yaml 里是连字符
（`runs-on`、`timeout-minutes`、`branches-ignore`）——这层转换由生成器负责，调用方只管驼峰。

### 触发器：35 个，两种形状

- **形状特殊的 6 个**单独建模：`push` / `pullRequest` / `workflowDispatch` / `schedule` /
  `workflowCall` / `workflowRun`。
- **其余 29 个**形状一样（`types` / `branches` / `paths` / `tags` 几档过滤），走一个开放字典：
  写进 `on.other.<事件名>`。**官方以后加事件不用改模型**，能填哪些名字见 `list_workflow_schema`。

### `strategy.matrix` 两种形态

```jsonc
"matrix": { "rid": ["win-x64", "linux-x64"] }              // 把取值列出来
"matrixExpression": { "fw": "${{ fromJson(needs.build.outputs.tfm_json) }}" }  // 整维交给表达式
```

两者写进 yaml 的**同一个 `matrix` 下**；**同一维两边都给了会被校验拒掉**。

## 一、查可用结构 `list_workflow_schema`

`[McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]`

schema 说得了"这里是个对象"，说不了"这个位置能填哪些名字"。这个工具补的就是那半。

| 参数 | 类型 | 说明 |
|---|---|---|
| `kind` | `string` | 必填：`trigger` / `field` / `action` |
| `value` | `string?` | 定位到某个节点；空 = 列出整类 |

**实测输出**（`kind=trigger`）：

```
# 触发器（on）

## 单独建模的（形状不一样，各有各的写法）

- `push` —— 推到分支或打标签
- `pull_request` —— 开/更新 PR
- `workflow_dispatch` —— 手动点一下（可以带输入）
- `schedule` —— 按 cron 定时
- `workflow_call` —— 被别的流水线复用（自己的 inputs / secrets / outputs）
- `workflow_run` —— 盯别的流水线跑完

## 其余事件（形状一样：types / branches / paths / tags 几档过滤）

它们都写进 `on.other.<事件名>`，能用的名字是：

`branch_protection_rule` `check_run` `check_suite` `create` `delete` `deployment`
`deployment_status` `discussion` `discussion_comment` `fork` `gollum` `issue_comment`
`issues` `label` `merge_group` `milestone` `page_build` `project` `project_card`
`project_column` `public` `pull_request_review` `pull_request_review_comment`
`pull_request_target` `registry_package` `release` `repository_dispatch` `status` `watch`

用 value 看某一个接受什么，例如 value = `release`。
```

**实测输出**（`kind=field`，`value=job`）：

```
# job（一个作业）

- `name` —— 显示名
- `runs-on` —— **必填**
- `needs` —— 依赖哪些作业先跑完
- `if` —— 条件
- `env` —— 这个作业的环境变量
- `permissions` —— 同顶层
- `strategy.matrix` / `strategy.fail-fast` / `strategy.max-parallel`
- `container.image` … 或 `services.<名>`
- `outputs` —— 给下游作业用
- `timeout-minutes` / `continue-on-error`
- `steps` —— 至少一步
```

**实测输出**（`kind=action`，`value=checkout`）：

```
# actions/checkout

- `fetch-depth` —— 拉多少历史（`0` = 全部）
- `ref` / `repository` / `token`
- `path` —— 放到哪个子目录
- `submodules` / `lfs`
```

`kind` 只认那三个；`value` 认不出来也报错，不会静默给个空结果。

## 二、生成工作流 `write_workflow`

`[McpServerTool(ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]`

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `string` | 写到哪儿（`.yml` / `.yaml` 的绝对路径） |
| `tree` | 对象 | 那棵强类型树 |
| `cookie` | `string?` | 目标**已存在**时必填；不存在时不用给 |

流程是**先校验、再写**：树立不住（缺 `runs-on`、`uses` 与 `run` 同时给、依赖成环…）就
**什么都不写**，把问题列出来。

**落盘遵守两段式**（与 `Resource` 的改文件同一套纪律）：

| 目标 | 行为 |
|---|---|
| 不存在、没给凭据 | 直接写 |
| 不存在、**给了凭据**（对不上"不存在"那一档） | **不写**，报凭据对不上 |
| 已有内容、**没给凭据** | **不写**，把"会被整份替换、不可回滚"摆出来 |
| 已有内容、给了凭据且对得上 | 写 |
| 已有内容、凭据对不上 | 不写，报现状 |

**实测输出**（写进一个还不存在的路径）：

```
# 已生成
- 文件：C:\Users\16229\source\.wf-doc-out\demo.yml
- 作业：1 个（build）
- 它的凭据（要再改一次就带上）：`b86d405de8adf106`
- 提示：内容与这棵树一一对应；改内容请改树再生成，不要手改 yaml。
```

**它实际写出来的 yaml**（就是上面那次调用产生的原文）：

```yaml
name: CI
on:
  push:
    branches:
    - main
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
    - name: 构建
      run: dotnet build
```

缩进固定 2 格、行尾一律 LF（生成它的库在 Windows 上默认给 CRLF，这里归一过）。

## 三、校验 `validate_workflow`

`[McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]`

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `string?` | 读一份已有的 yaml 来校验 |
| `tree` | 对象? | 直接给一棵树来校验 |

**二者给一个，而且只给一个**；都给或都不给都会报错。

报的是两类问题：

**schema 层**（这棵树本身立不立得住）：

- 一个触发器都没给；一个作业都没有
- 作业缺 `runs-on`
- 作业一步都没有
- 步骤里 `uses` 与 `run` **只能给一个**（都给、都不给都算错）
- `uses` 指向 `actions/` 下的东西却没带版本（`actions/checkout` 少了 `@v4`）
- 作业名不合规（只能用字母、数字、下划线、连字符，且不能以数字开头）
- `permissions` 的整句写法与逐个权限**只能给一种**
- `container` 缺 `image`
- `strategy.matrix` 的某一维**既列了取值又给了表达式**

**语义提示**（它在 GitHub 上跑得起来吗）：`needs` 指向不存在的作业、依赖成环（报出环上的路径）。

**实测输出**（给一棵故意写坏的树）：

```
# 校验结果（树）
- 触发器：1 个
- 作业：1 个

发现 3 个问题：

- jobs.build：缺 runs-on（跑在什么机器上）。
- jobs.build：一步都没有。
- jobs.build.needs：依赖了一个不存在的作业 'nope'。
```

**实测输出**（校验上面刚生成的那份文件）：

```
# 校验结果（C:\Users\16229\source\.wf-doc-out\demo.yml）
- 触发器：1 个
- 作业：1 个

没有发现问题。
```

## 附录 A：什么叫"确定性"

"同一棵树两次生成**字节一致**"是硬要求，靠四条约定实现：

1. **键序固定** —— 不依赖字典 / 反射的枚举顺序（用有序字典，或代码里的固定顺序）；
2. **缩进固定 2 格**；
3. **行尾一律 LF**；
4. **多行内容用块标量**（`|`）。

第 4 条是实测踩出来的：折叠标量（`>-`）会把换行折成**空格**，
于是一段三行的 PowerShell 变成一行——**语义直接变了**。有回归测试盯着这件事。

## 附录 B：明确不做的

- **不读回**（不做 yaml → 树）。生成是单向的；改一份已有的工作流，要么在树里重建它，
  要么手工改那段 yaml 再来 `validate_workflow`。
- **不做官方 action 的完整目录**。只列最常用的四个（checkout / setup-dotnet / upload-artifact /
  download-artifact）；`with` 是开放字典，写什么都能进去，但工具给不了提示。
- **不解析表达式**（`${{ }}` 里能写什么）。表达式原样写进 yaml。
- **不做多个工作流之间的编排**，也不在本地跑工作流。
- **不做非 GitHub 的 CI**（GitLab CI / Jenkins）。
- **不保证"能跑"**。校验只覆盖 schema 与语义，跑不跑得起来是 CI 的事。

## 附录 C：与 `plan/` 的关系

这份手册是"做成了什么样"（事实）。它背后的**为什么**与**取舍**写在 `plan/Workflow/P-WF-01-工作流生成.md`：
那份提案里记着痛点（其中"没有直接对应的痛点"也如实写了）、备选方案（为什么没 fork 现成库、
为什么没直接用官方 schema）、缺点（不读回、覆盖是常用集）以及那些拍出来的数字。
