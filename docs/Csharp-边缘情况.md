# Csharp 边缘情况与返回格式规则

> 本文档记录 `ZMS.MCP.Csharp` 的**返回格式与降级规则**（`docs/Csharp.md` 之外的部分）。
> 第一节是实测数据，第二节是实测出的问题，**第三节是已确认的规则**（据此实现），第四节是待确认点。
>
> 实测环境：本机 NuGet 缓存 519 个包、`Newtonsoft.Json 13.0.3`、`dotnet 10.0.303`；
> 调用方式：真实 MCP 服务 + stdio 协议。返回原文见 `docs/Csharp-返回示例.md`。

## 一、实测数据（输出规模）

| 工具 / 输入 | 输出规模 | 备注 |
|---|---|---|
| `get_member` 整类型（259 行的类） | **30 545 字符 / 266 行** | 把整个类型源码吐回来 |
| `get_member` 单成员（带 200 行 XML 注释） | **17 478 字符 / 219 行** | 文档注释整段输出 |
| `list_doc_symbols` 包内全部类型（`path=""`） | **17 197 字符 / 152 行** | 147 个类型，每条带摘要 |
| `search_packages` 本地全部（`packName=""`） | **14 932 字符 / 522 行** | 519 个包 |
| `list_symbols` `type=M`（50 个重载 + 1） | 14 112 字符 / 56 行 | 其中 2 行超长（50 参签名约 700 字符/行） |
| `list_doc_symbols` 一个类（`JsonConvert`） | 10 057 字符 / 72 行 | 67 个成员 |
| `list_members`（50 个重载的类） | 8 262 字符 / 55 行 | |
| `get_member` / `stage_draft` 重载歧义报错 | 7 448 / 7 410 字符 / 各 51 行 | 把 50 个重载全列出来 |
| `get_package_metadata`（nuspec + readme） | 4 445 字符 / 120 行 | 无上限 |
| `list_project_packages` | 2 452 字符 / 60 行 | 无上限 |
| `install_packages` dryRun | 4 154 字符 / **158 行** | 同一批传递包列了三遍 |
| `list_package_versions`（Serilog 在线） | 1 459 字符 / **189 行** | 185 个版本 |
| `list_solution_projects` / `view_solution_tree` | 数百字符 | 正常 |
| `get_member` 单个普通成员 | 266 字符 | 正常 |
| `list_symbols` `type=D`（只看有注释的） | 250 字符 | 正常 |

## 二、问题清单（实测）

### A. 列表型输出没有上限
`list_types` / `list_members` / `list_symbols` / `list_doc_symbols` / `search_packages` /
`list_package_versions` / `list_project_packages` 都会把命中的全部条目吐出来。

### B. 单条内容可能极大
`get_member` 给类型时输出整个类型源码；成员带长 XML 注释时注释整段输出；
`list_doc_symbols` 的 `D` 片段、`get_package_metadata` 的 readme/nuspec 都没有上限。

### C. 报错信息把全部候选吐出来
`get_member` / `update_member` / `remove_member` / `stage_draft` 在重载不消歧时报错并列出**所有**重载。

### D. 内容质量
| 现象 | 例 |
|---|---|
| 摘要丢 `<see cref>` 内容 | `Converts a  to and from JSON.`（原文 `<see cref="T:System.String"/>`）|
| 路径分隔符不一致 | `scan_projects` 用 `/`，`list_solution_projects` 用 `\` |
| 两条提示语义重复 | `list_project_packages` 在 assets 缺失时同时打"还原产物缺失"与"⚠ 依赖图可能已过期" |
| 无意义文案 | `list_package_versions`：`还有 0 个预览版未列出` |
| 超长单行 | 50 个参数的签名单行约 700 字符 |

### E. 状态残留
`stage_draft` 失败时（重载不消歧、语法检查不过）`drafts` 行已创建，留下一条空拟定。

## 三、已确认的返回格式规则（据此实现）

### 3.1 工具集：不要重复的工具
- 「查看 slnx」只要有**一个**带虚拟文件夹的树状视图工具。
- `list_solution_projects` 与之重复（`docs/Csharp.md` 里也没有这一项）→ **删除**。

### 3.2 `install_packages`：「因为被引用而未直接引入」的定义
这一类 = **参数里要求了、但被其他参数包的依赖传递满足**的包，因此不直接引入。
- 例：参数同时要 `Microsoft.Extensions.Caching` 与 `Microsoft.Extensions.Caching.Abstractions`
  → 后者归入这一类（不直接 `dotnet add`）。
- 判据：该包的版本已由**其他参数包**的依赖图满足（图里解析出的版本 ≥ 参数要求）。
- **只有出现在参数里的包才可能进这一类**；纯传递包不许写进这里。

### 3.3 `remove_packages`：「本次移除的依赖传递包」的定义
- 只列**因本次移除而消失的传递包**。
- 被直接移除的顶级包已经在「本次移除包」里，**不得**再出现在「依赖传递包」里（间接移除就是间接移除）。

### 3.4 `list_doc_symbols`：文档注释只在 `D` 时输出
- `type` 不含 `D` 时：**只列成员**，不输出文档注释/摘要（英文说明文字不出现）。
- 只有 `D` 才输出文档注释内容；命中唯一成员 → 原始 XML `<member>` 片段。

### 3.5 列出成员 / 类型的文件与行号
- 成员行给出**该成员自己**所在文件与行号（分部类的成员各归各的文件）。
- 待确认：**类型**（分部类）怎么显示全部声明位置（见第四节）。

### 3.6 `get_member` 给类型时：只要结构，不要实现
- **方法**：只给签名（不给方法体）。
- **字段 / 属性 / 事件**：带初始化器的要带上初始化器表达式。
- **属性 / 索引器**：只给签名；访问器有实现时用记号表示（不展开实现）——记号形式见第四节。
- **类型头**：要包含**接口实现/基类列表**与**主构造器参数**（分析器把这两者都放在类定义那一行）。
- **到了成员级别（`memberPath` 精确到某个成员）才给实现**。

### 3.7 重载的显示与截断
- 同名方法**只显示前 10 个**的「文档 + 实现」；超过 10 个的一律不给（即使参数要求给文档），必须用参数定位到具体重载。
- 列表里若有**至少两个不同名字**的方法，则**每个方法名只显示前 5 个**的「文档 + 实现」。
- **重载超过 10 个时改为分组显示**（不重复方法名），形如：
  ```
  Demo.Bloat.Many 有20个重载
  (int)` — Bloat.cs:209
  (int, int)` — Bloat.cs:210
  (int, int, int)` — Bloat.cs:211
  ```
- 「项目和 nuget 都这样」：源码侧（`list_symbols` / `list_members` / `get_member`）与 NuGet 侧（`list_doc_symbols`）同一套规则。

### 3.8 注释长度的降级
| 场景 | 规则 |
|---|---|
| 结果只有**一个**成员 | 注释超过 200 行也**完全显示** |
| 结果**不止一个**成员 | 每个成员的注释**截断到 200 行** |
| 结果**超过 5 个**成员 | 每个成员显示**截断到 50 行** |
| 结果**超过 10 个**成员 | 只显示**前 10 个**，每个截断到 50 行 |

### 3.9 进度（已修 / 待做）
| 项 | 状态 |
|---|---|
| 3.1 删掉重复的 `list_solution_projects`（README 同步） | **已完成** |
| 3.2 `install_packages`「因为被引用而未直接引入」只收"参数里要了、被别的参数包满足"的包；传递包不再重复列 | **已完成**（`BuildIntroductions` + `VersionSatisfied`）|
| 3.3 `remove_packages` 的「依赖传递包」不再包含被直接移除的顶级包 | **已完成** |
| 3.4 `list_doc_symbols` 不含 `D` 时只列成员、不给文档注释 | **已完成** |
| 3.5 分部类：类型行列出**全部**声明位置（`A.cs:3, B.cs:7`） | **已完成**（`ListTypes` 按符号合并多声明 + `Location` 全部位置）|
| 3.9 漏洞索引 `index.json` 真实形状（数组 + `@id`） | **已完成**（`ReadShardUrls` + 3 个单测；实测"有漏洞的包"变为 `（无）`）|
| `Csharp.md:146` 落盘后核对 NU1901–NU1904（`NuGetAuditMode=all`） | **已完成**（`AppendAudit` + `ExtractAuditWarnings` + 单测；实测真装后输出"未发现 NU1901–NU1904"）|
| 3.6 `get_member` 给类型时只给结构（方法只签名、初始化器、访问器 `{ get { … } }`、类型头含 base list + 主构造器） | **已完成**（`CodeEditor.DescribeType` + `MemberSignature`；实测输出 `## public class Bloat` + 成员只给签名）|
| 3.7 重载规则（前 10 / 多方法名时前 5 带文档与实现；>10 分组显示） | **已完成**（`MemberListRendering`；实测 `Bloat.Many 有 50 个重载` + 每行只给参数列表；`list_members` / `list_symbols` / `get_member` 类型结构 / `list_doc_symbols` 都生效）|
| 3.8 注释长度降级（1 个全显示 / >1 截 200 行 / >5 截 50 行 / >10 只前 10） | **已完成**（`DocumentationBudget` + `TruncateLines`；实测类型结构里 204 行注释截断到 50 行并标注"注释共 204 行"）|
| 摘要丢 `<see cref>` 内容 | **已消失**（3.4 之后非 `D` 不输出摘要、`D` 给原始 XML，cref 不再被啃）|
| `list_project_packages` 重复提示、`list_package_versions` 的"还有 0 个"、`list_symbols` 空结果提示、报错候选截断 | **已完成** |
| `search_packages(packName="", web=true)`（空关键字打给 nuget.org） | 待测 |
| 新增单测 | `RenderingTests`（7 个：文档预算/前 5/截断/结构签名/类型结构/重载分组/候选截断）|

## 四、待确认的点

1. **分部类（`partial`）在列表里怎么显示声明位置**：
   - 类型行是列出**所有**分部文件（如 `A.cs:3, B.cs:7`），还是只给第一个？
   - 成员行保持"成员自己的文件 + 行号"（3.5）是否就是你要的？
2. **属性访问器"脏记号"的具体写法**（3.6）：例如
   - 自动属性 `public string Name { get; set; }`
   - 访问器有实现时写成什么？`{ get { … } }`、`{ get; set; }  ⟵带体`、还是别的符号？
3. **3.7 的"前 10 / 前 5"作用范围**：是只适用于"带文档/带实现"的显示，还是也决定**是否出现在列表里**（即超过 10 个的重载是否只列出分组头 + 前若干个裸签名）？

## 五、验证方式
沿用现有手段：`%TEMP%` 下的两个测试项目（`zms-draft-demo-…` 普通类库、`zms-edge-…` 50 重载 + 200 行注释）
与本地 NuGet 缓存，用 stdio 协议逐个调用，把输出规模与新规则对照；同时补单元测试锁住规则。
