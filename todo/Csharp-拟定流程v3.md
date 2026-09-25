# Csharp 拟定流程 v3.3（计划定稿，待实现）

> v3.3 = v3.2 经二轮只读子代理复审后的定稿。本轮三条决定：
> ① **落盘/选择 cookie 全部放内存**（要立刻用、不持久化）；② 两轨**不是矛盾而是分工 + 时间窗**；
> ③ **不做**轻量"清拟定"（只有 `untrack`）。
> 只写计划，未动代码。
>
> **文案纪律**：本文引号里的输出文案只是**语义示意**；唯一的逐字契约是 `docs/Csharp.md` + 工具 `[Description]`
> + 测试断言。实现文案微调时不必回改本文引文，只要语义一致（§十一 是"实现对照"，天然滞后于实现）。

## 一、状态机

```
S0 未追踪
 │  track_project（编译 → 记符号基线 → 发追踪 cookie，**持久**）
 ▼
S1 已追踪·拟定空 ──confirm("")──▶ S1（"没有未完成的拟定"）
 │  stage_draft（未追踪时拒绝）
 ▼
S2 已追踪·拟定非空·无许可 ──stage_draft──▶ S2（累积）
 │  confirm("") 预检通过 → **内存**发 applyCookie + 内存记许可快照（符号 → 路径 + 字节）
 ▼
S3 已追踪·拟定非空·许可有效
 ├─ confirm(applyCookie) 校验通过 ──▶ S1（写 → format → 清拟定/许可 → 重算基线）
 ├─ confirm(applyCookie) 校验不过 ──▶ S2（许可作废：路径/字节/符号与**发 cookie 时**不一致）
 ├─ stage_draft ───────────────────▶ S2（许可作废 + 明确提示）
 ├─ get_member 见冲突 ──▶ 提示用选择器（内存发 selectCookie，按 (项目,符号) 键）
 └─ select_draft ──────────────────▶ S2（许可作废）
S* ── track_project(csprojPath, cookie) ──▶ S0（一个事务：删 追踪/拟定/许可**内存表**；见 journal 规则）
```

## 二、状态与数据

| 状态 | 存哪 | 内容 | 生命周期 |
|---|---|---|---|
| **追踪记录** | `trackings`（**持久**） | `project_path` · `tracking_cookie` · `symbol_baseline`（符号 FQN → 声明 hash） · `created_at` | `track_project` 建；`untrack` 删；落盘后**整体重算** |
| **拟定** | `draft_edits`（**持久**） | 序号 · 符号定位 · 意图 · 首次编辑时该符号的快照文本 | `stage`/`select` 追加；落盘 / `untrack` 清 |
| **落盘许可** | **内存** `applyPermits` | `project_path` → （`applyCookie` · 许可快照：符号 → 文件路径 + 文件字节 hash） | 预检时建/轮换；落盘、`untrack`、**进程退出**即失效 |
| **选择许可** | **内存** `selectPermits` | `(project_path, symbol)` → （`selectCookie` · 三方内容 · 当时的现状 hash） | `get_member` 遇冲突时发；选择后、`untrack`、**进程退出**即失效 |
| **写前日志** | `write_journal`（**持久**） | `cookit` · `seq` · `file_path` · `previous_hash` · `content` · **编码** | 落盘前写、成功后清；**不得被 untrack 静默删除** |

> 内存 cookie 的含义：**MCP 重启即全部失效**（要重新预检 / 重新查看）；这是有意的——它们本就是"立刻用"的一次性令牌。

## 三、工具与动作

### 1) `track_project(csprojPath, cookie?)` —— 一个工具两用（原 §6 的取消追踪已并入这里）
- S0：编译 → 记符号基线 → 发**追踪 cookie**（持久）→ 汇报（拟定写 / 未追踪更改均为空）；
- 已追踪：不重记基线，回同一 cookie + 汇报两份清单（只列**符号**）。

### 2) `stage_draft(csprojPath, typePath, memberName, content)`
- **S0 → 拒绝**（"请先 track_project"）；
- 语法检查 → 追加一条（符号 + 意图 + 首次快照）；
- **S3 时作废许可**并在返回里明确提示"落盘许可已作废，请重新预检"。

### 3) `get_member(csprojPath, memberPath)`（保持**只读**）
- 有快照 → **当前 / 拟定 / 快照**；无快照 → 只有当前；
- 该符号**有冲突**（当前 ≠ 快照）→ 附 **selectCookie**（内存，键 `(project, symbol)`）+ "用选择器解决"；
- 不写数据库（cookie 在内存），因此 `ReadOnly` 标注保留。

### 4) `select_draft(csprojPath, memberPath, selectCookie, choice)`
| `choice` | 含义 | 效果 |
|---|---|---|
| `draft` | 用**拟定**内容 | 坚持拟定写法 |
| `snapshot` | 用**快照**内容 | 回到编辑前 |
| `disk` | 用**现状**内容 | 跟随磁盘（该符号等于不改） |
| `drop` | **取消拟定** | 删掉该符号的拟定条目 |

- 前三种 = "选一种内容**代替现在的拟定**"：**替换**该符号在拟定里的条目（内容 = 选中内容，快照 = 当前磁盘文本）；
- `drop` = 删掉该符号的条目；
- 之后 **许可作废** → 必须重新预检；
- `selectCookie` 不匹配 / 符号不匹配 → 拒绝并提示"重新 `get_member` 拿新的"。

### 5) `confirm_draft(csprojPath, applyCookie)`

**不带 cookie = 预检 + 发许可**
1. 重建符号树；逐条拟定**现场定位文件**；新建类型按命名空间 + `RootNamespace` 推导；
2. **预检五项**：① 占用（`FileWriter.Probe`）；② 符号冲突（当前文本 ≠ 拟定快照）；③ **新建类型目标已存在**（实现另有"新建目标文件已存在"一条，防的是整文件覆盖）；④ 目标文件**目录不可写** → 进 `problems`；**目录不存在** → 只提示（`WriteProbe.DirectoryWillBeCreated` → 输出"⚠ 会新建目录：…"，**不进 `problems`、不影响发 cookie**）；⑤ 符号找不到 / 文件消失 → 单独成条（`⛔ 定位不到符号：…`），说明可能被改名/删除并给下一步（`get_member` 看三方 / 重新 `stage`）；
3. 另有两条**预检就拒绝**（不等落盘才发现）：**编码无法判定**（`DetectEncoding` 会抛）；**部分类**目标存在多声明且有异议时列出；
4. 通过 → **内存**发 `applyCookie` + 记许可快照（符号 → 路径 + 字节 hash）→ 汇报"本次改动涉及" + 预检查 + cookie；
5. 不过 → 列问题，**不发 cookie**（原许可清掉 → S2）。

**带 cookie = 落盘**（判定顺序写死）
| 序 | 条件 | 结果 |
|---|---|---|
| ① | 内存里没有该项目的许可 | `没有有效的落盘许可，请先不带 cookie 调用做预检` |
| ② | 有许可，但 cookie ≠ 当前 | `cookie 已过期或无效（可能被新的预检替换或进程重启），请重新预检` |
| ③ | cookie 相符 | **再重建** → 与许可快照逐项比对（**路径、字节、符号存在性**） |
| ③a | 全同 | **落盘事务**（见第九节）：journal → 原子写 → format → **成功后才**清拟定/许可 → 整体重算基线 → 汇报 |
| ③b | 有不符 | **作废许可** → 拒绝：`落盘目标状态与发许可时不一致：… 请重新预检` |

### 6) 取消追踪 —— **已并入 §三.1 的 `track_project(csprojPath, cookie)`**（带 cookie 就是清除）
- cookie 不匹配 → 拒绝；S0 → "该项目没有在追踪"；
- 匹配 → 一个事务删 `trackings` + `draft_edits` + **`drafts`**（拟定身份记录）+ **内存两张许可表**；
- **写前日志不静默删**：若该项目还有未完成的 journal → **先前滚**（或报告"有未完成落盘，已前滚/请先处理"），再清。

## 四、cookie（三种）

| cookie | 存哪 | 键 | 用途 | 轮换 | 失效 |
|---|---|---|---|---|---|
| **追踪 cookie** | `trackings`（持久） | `project_path` | `track_project(csprojPath, cookie)` 带 cookie 时清除（一个工具两用） | 不轮换 | 清除后 |
| **落盘 cookie** | **内存** | `project_path` | `confirm_draft` 落盘 | 每次预检换新 | 新预检替换 / 校验不过 / 落盘成功 / untrack / **进程退出** |
| **选择 cookie** | **内存** | `(project_path, symbol)` | `select_draft` | 每次查看该符号换新 | 选择生效 / 符号状态不符 / untrack / **进程退出** |

## 五、两轨分工与时间窗（写死，不是矛盾）

| 轨 | 粒度 | 时间窗 | 用途 | 结论举例 |
|---|---|---|---|---|
| **追踪轨**（宽松） | 符号语义（FQN → 声明 hash，**不含路径**） | `track` 之后、到本次预检之前 | 算"未追踪更改"（报告） | 文件被移动：**不算**未追踪更改 |
| **许可轨**（严格） | 符号 → **路径 + 文件字节 hash** | **只在"发 cookie → 传 cookie"这段窗口内** | 落盘前的最后校验 | 该窗口内文件被移动/改名 → **判冲突、许可失效** |

- 两条轨各管一段，**没有优先级冲突**：预检时用追踪轨算"未追踪更改"并生成许可；落盘时只用许可轨校验窗口内的变化。

## 六、并发与重启

- **同一项目同一时刻只允许一个有效许可**（内存储存天然单键）；后到的预检替换前者，被替换者落盘得 ② 的提示；
- `draft_edits` 按项目共享 → **按单会话使用约定**，不实现多会话合并；
- **MCP 重启**：追踪记录与拟定仍在（持久）；**两种许可 cookie 全部失效** → 重新预检 / 重新查看即可（不需要重新追踪）；
- **启动顺序**：① 前滚（`journal`，三分支：磁盘 == 新内容 → 跳过；== `previous_hash` → 覆盖；第三种 → 只报告）→ ② 前滚后**同步刷新追踪基线** → ③ 孤儿清理（S0 却有 `draft_edits` 的项目：报告并清理；**排除 journal 仍在的项目**）→ ④ 启动工具。

## 七、符号身份与定位

- 符号身份**一律用 `SymbolDisplayFormat` 生成**（含泛型元数、参数类型；格式串在实现里锁定成常量）；
- **泛型元数**：`Ns.Type` 而实际是 `` Ns.Type`1 `` → 枚举同基名类型，命中一个就用、多个则报"请指定元数"；
- **参数类型解析**：按**顶层括号/尖括号配平**切分（修 `FindMembers` 现有 `inner.Split(',')` 的缺陷）；
- 参数类型允许 `int` / `System.Int32` / 别名（沿用 `KeywordAliases`）；
- 命中多个（同名类型两文件、部分类）→ 报"匹配到多个声明"，不猜；
- **部分类**：类型级 hash = 各分部声明文本 hash 的**有序集合**；许可快照里**逐文件**登记。

## 八、同文件 / 同符号的合成规则

- **整文件新文本 = 该文件当前磁盘内容 + 逐条符号意图**（非拟定区域一律取**当前磁盘**，绝不用旧快照覆盖）；
- 条目按**符号在文件内的声明位置**升序（位置相同按拟定序号）逐条应用；`EnsurePartial` 与新增成员在同一文件内**合并**；
- **同一符号只保留一条生效条目**：`stage_draft` / `select_draft` 对同一符号是**替换**该符号的条目（不追加重复条目）；
- `disk` 选择**不隔离**：它只让该符号"不改"，但同文件里其它区域若有外部改动，许可轨照样会挡（文档明确写出，避免误解）。

## 九、`dotnet format` 与落盘事务（统一口径）

```
写前日志（含 previous_hash + 编码）
   → 原子写（临时文件 + 替换 + 原编码）
   → dotnet format --include <本次文件>  →  对比前后 hash，把"format 额外改动"写进返回
   → 成功后才：清拟定 + 清许可
   → 整体重算追踪基线（范围 = 本项目 Compilation 的源文件集合；format 之后）
```
- format 失败 → **保留拟定**（可重试），journal 保留 → 下次启动前滚/重试；
- 编码无法判定 → 预检阶段就拒绝（不进事务）；
- 共享文件（同一 `.cs` 被两个项目 Compile）→ 基线按"文件 + 项目"隔离，另一方视为外部更改。

## 十、已确认的决定（累计 10 条）

| # | 决定 |
|---|---|
| 1 | 两种持久 cookie 语境：追踪 cookie 持久；**落盘 / 选择 cookie 内存、不持久化、立刻用** |
| 2 | 追踪快照 = 符号级语义（FQN → 声明 hash，不含路径）→ 移动文件不算"未追踪更改" |
| 3 | 落盘后继续追踪，并**整体重算**基线（范围 = 本项目源文件集合） |
| 4 | 落盘粒度 = 双轨：整文件写 + 许可轨的字节校验 |
| 5 | 拟定只存符号 + 意图；路径现场找、整文件内容现场算 |
| 6 | 落盘校验**只覆盖"发 cookie → 传 cookie"窗口**：路径与字节必须一致，否则许可失效 |
| 7 | 新建类型被外部抢先创建 → 当冲突，必须重新编辑 |
| 8 | 不做轻量"清拟定"：要重来就 `untrack` + 重新 `track`（AI 自己负责后果） |
| 9 | 同符号只保留一条生效条目；`select` 是替换，不是追加 |
| 10 | 逃生/解决入口：`untrack`（清一切但保留持久日志规则）+ `select_draft`（保留外部更改） |

## 十一、实现对照（对齐版）

### 11.1 转移
```
S0 ─track─▶ S1 ─stage─▶ S2 ─confirm("") 通过─▶ S3
S1 ─confirm("") 自环▶ S1（没有未完成的拟定）
S2/S3 ─stage─▶ S2（S3 时许可作废 + 提示）
S3 ─confirm(applyCookie) 通过─▶ S1（落盘事务）
S3 ─confirm(applyCookie) 不过─▶ S2（许可作废）
任意 ─get_member（冲突）─▶ 内存发 selectCookie（状态不变）
任意 ─select_draft─▶ S2（许可作废；drop 则删该符号条目）
S* ─untrack(追踪 cookie)─▶ S0
```

### 11.2 流程表（agent ／ MCP）
| # | agent 调用 | 前置 | MCP 做的 | agent 看到 |
|---|---|---|---|---|
| 1 | `track_project` | S0 | 编译 → 记符号基线 → 持久发追踪 cookie | 追踪已开始 + cookie + 两清单（空） |
| 2 | `track_project` | S1–S3 | 不重记；比对基线 | 已在追踪 + 同一 cookie + 拟定写 / 未追踪更改 |
| 3 | `stage_draft` | S0 | 拒绝 | 请先 track_project |
| 4 | `stage_draft` | S1/S2 | 语法检查 → 替换/新增该符号条目 | 拟定已更新 + 累计条数 |
| 5 | `stage_draft` | S3 | 同上 + **作废许可** | 拟定已更新 + **许可已作废，请重新预检** |
| 6 | `get_member` | 任意 | 编译 → 三方；冲突则**内存**发 selectCookie | 当前/拟定/快照（+ selectCookie + 选择器提示） |
| 7 | `select_draft` | 该符号有冲突 | 校验 cookie → 按 choice 替换/删除该符号条目 → 作废许可 | 已处理 + 许可已作废，请重新预检 |
| 8 | `confirm_draft("")` | S1 | — | 没有未完成的拟定 |
| 9 | `confirm_draft("")` | S2/S3 | 重建 → 定位 → 五项预检 → 内存发 applyCookie + 许可快照 | 改动涉及 + 预检查 + cookie |
| 10 | `confirm_draft(applyCookie)` | S2/无许可 | 判定① | 没有有效的落盘许可 |
| 11 | `confirm_draft(applyCookie)` | S3、cookie 不符 | 判定② | cookie 已过期或无效 |
| 12 | `confirm_draft(applyCookie)` | S3、相符且一致 | 判定③a：落盘事务 | 落盘汇报（文件/编码/format 额外改动/未做 git） |
| 13 | `confirm_draft(applyCookie)` | S3、相符但状态变 | 判定③b：作废许可 → 拒绝 | 状态与发许可时不一致，请重新预检 |
| 14 | `track_project(csprojPath, cookie)` | S1–S3 匹配 | 事务删追踪+拟定+**内存两张许可表**（`PermitStore.Invalidate`）；journal 有未完成则先前滚/报告 | `# 取消追踪` + 项目路径 + "已删除拟定：N 条" + 重新 track 的指引 |
| 15 | `track_project(csprojPath, cookie)` | 不匹配 / S0 | 拒绝 | cookie 不匹配 / 没有在追踪 |

### 11.3 行为矩阵
| 工具 | S0 | S1 | S2 | S3（许可有效） |
|---|---|---|---|---|
| `track_project` | 建会话 | 汇报 | 汇报 | 汇报 |
| `stage_draft` | 拒绝 | 更新拟定 → S2 | 更新拟定 | 更新 + 作废 → S2 |
| `get_member` | 当前 | 当前 | 三方（冲突则给 cookie） | 三方（冲突则给 cookie） |
| `select_draft` | 拒绝 | 无冲突可选 | 解决 → S2 | 解决 → S2 |
| `confirm_draft("")` | 拒绝 | 没有未完成的拟定 | 预检 → S3 | 重新预检（轮换） |
| `confirm_draft(applyCookie)` | 拒绝 | 判定① | 判定① | 判定②/③ |
| `track_project`（带 cookie） | 没在追踪 | 删会话 | 删会话 | 删会话 |

## 十二、实现顺序

| 步 | 内容 |
|---|---|
| 1 | 数据层：`trackings` 新表；`draft_edits` 加 `symbol_snapshot`（**去掉** file_path / result_content / baseline_hash 的旧列）；`write_journal` 加 `previous_hash` + 编码；`PRAGMA`+`ALTER` 幂等；内存许可表（两个字典） |
| 2 | 追踪：`track_project`（无 cookie 开始/汇报；带 cookie 清除）（整体重算基线；journal 规则） |
| 3 | 拟定编辑：`stage_draft`（替换同符号条目；S3 作废许可并提示） |
| 4 | 查看：`get_member` 三方 + 冲突时内存发 selectCookie |
| 5 | 选择器：`select_draft`（四种 choice） |
| 6 | 预检与落盘：`confirm_draft` 两段式 + 判定顺序 + 五项预检 + 同文件合成 + 落盘事务（journal → 写 → format → 清 → 重算基线） |
| 7 | 启动：前滚三分支 → 刷新基线 → 孤儿清理 |
| 8 | 收尾：`FileWriter` 去重试与"内容未变不写"；同步 `docs/Csharp-边缘情况.md` 与 README；全量测试；在 `C:\temp\zms-mcp-demo` 上把四步动作返回原文贴出 |
