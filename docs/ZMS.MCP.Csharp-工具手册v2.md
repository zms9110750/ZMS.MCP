# ZMS.MCP.Csharp（工具手册 v2）

本手册按**当前实现**重写：工具数 **18**。每个工具给出：参数（含执行前的合法性校验）、工具描述与配套用法、以及按实现代码推断的返回值示例。

**本版格式约定**：

- 一个工具若**因参数取值不同而有多种情况**，先在「工具描述」里列出**情况分类**，再用四级标题 `####` **逐情况**描述行为、示例与注意点（例：`view`、`symbols`、`list_package_versions`、`install_packages`、`select_draft`、`confirm_draft`、`track_project`）。
- 单情况工具不写 `####`，只给参数、描述、示例。
- 参数类型按 C# 可空语法：`:string` = 必须传且必须有效；`:string?` = 可空、默认 null（"没给"与"给了空串"等价）；`integer` / `boolean` 不带 `?`，有默认值时写在参数下面。
- 通用异常形态：任何工具抛出的异常都被统一转成 `Error: <异常消息>`（`找不到可执行文件` / `操作超时` 会附一句环境提示）。**例外**：`add_project_to_solution`、`remove_project_from_solution` 是 async 且没走这层包装，参数/环境错误会变成协议级错误 `An error occurred invoking '<工具名>'`；另外**缺少必需参数**也是协议级错误（参数绑定阶段就失败）。

## 工具总览（18）

| 组 | 工具 |
|---|---|
| 查看与扫描 | `view` |
| 解决方案编辑 | `migrate_solution_to_slnx`、`add_project_to_solution`、`remove_project_from_solution` |
| 项目文件编辑 | `edit_project_metadata` |
| NuGet | `list_project_packages`、`search_packages`、`list_package_versions`、`get_package_metadata`、`install_packages`、`remove_packages`、`list_doc_symbols` |
| 符号查询 | `symbols` |
| 拟定流程 | `track_project`、`stage_draft`、`list_draft`、`select_draft`、`confirm_draft` |

设计取向：**编辑一律走拟定**（先 `track_project` 拿 cookie → `stage_draft` → `confirm_draft`）；已经没有「直接改盘」的符号编辑工具了。落盘的语义是「符号 + 意图 + 首次 hash」，追踪只保存**符号 hash**（不保存文件快照，也不会把文件还原回去）。

## 查看与扫描

### 查看 `view`

- `path`:string
  - 参数限制：必须能解析成"存在的东西"，或能被当成**项目名**解析（见情况四）。
- `depth`:integer
  - 默认 4。**只在情况一（文件夹）时**有意义。
- `kinds`:string?
  - 默认空串 = `sln` / `slnx` / `csproj` 三种都要；只能给这三种，逗号分隔。**只在情况一时**有意义。

**一个工具三种目标，由 `path` 指向什么决定**（原来是三个工具 `scan_projects` / `view_project` / `view_solution_tree`）：

1. **文件夹** → 扫描它的解决方案与项目（`depth` / `kinds` 生效）；
2. **csproj 文件，或唯一项目名** → 打 csproj 原文 + 所有参与声明的文件；
3. **.slnx 文件** → 打解决方案树（含虚拟文件夹）；
4. **`.sln` 文件，或解析不了的东西** → 拒绝，并提示下一步。

- 之前：多数流程的第一步；想知道"这堆目录里有什么 / 这个项目怎么声明的 / 解决方案挂了什么"。
- 之后：拿到项目路径后喂 `list_project_packages`（看包）、`symbols`（看符号）、`track_project`（开始拟定）。
- 不能做：不写盘、不做 MSBuild 求值（情况二给的是"参与声明的文件原文"，不是展开后的属性值）；**一次只做一件事**（不会既扫盘又展开项目）。
- 注意：`depth` / `kinds` 在情况二/三下被忽略，给它们不会报错但不起作用。

#### 情况一：`path` 是文件夹 → 扫描

输出：每个解决方案一块 `<解决方案路径>(被描述数+额外数)`，接着是它描述的项目树（`├─` / `└─`），再接着是"在该解决方案文件夹下、但没被它描述"的项目（`-` 前缀）；不被任何解决方案覆盖的项目在最后平铺。`bin` / `obj` / `.git` 跳过。一个都没扫到时报 `No solution/project files under <路径> (depth N).`。

````
X:\temp\zms-mcp-demo\Demo.slnx(0+1)
-Demo/Demo.csproj
````
> 实测：这个 slnx 里没挂项目（项目被移除了），但同目录下发现了 1 个没被它描述的项目，所以是 `(0+1)` 加一行 `-` 前缀。

````
No solution/project files under X:\temp\empty (depth 4).
````
> 一个都没扫到（`kinds` 把命中全过滤掉时也是这个形态，看起来一样）。

#### 情况二：`path` 是 csproj 或唯一项目名 → 看项目

打 csproj 原文，然后按文档顺序附上所有"参与声明这个项目"的文件：项目文件本身、最近的 `Directory.Build.props`（以及它自己 `Import` 的更上层文件）、`Directory.Packages.props`、`Directory.Build.targets`、`global.json`、`NuGet.config`，最后是还原生成物 `obj/<项目>.csproj.nuget.g.props|targets`。`obj` 的位置是问 MSBuild 要的，问不到时那一份列成"未列出"并说明原因（不猜 `<项目目录>/obj/`）。

````
# X:\temp\zms-mcp-demo\Demo\Demo.csproj

## 项目文件
X:\temp\zms-mcp-demo\Demo\Demo.csproj
    <Project Sdk="Microsoft.NET.Sdk">
      ...
    </Project>
````
> 实测：每份文件都是一段 `## <角色>` + 路径 + ```xml 原文；角色文字说明"这份为什么算参与声明"。

````
Error: 项目文件不存在：X:\temp\zms-mcp-demo\Nope.csproj
````
> 给了 `.csproj` 结尾的路径但文件不在。

#### 情况三：`path` 是 .slnx → 看解决方案树

````
Demo.slnx
└─src
   └─Core
      └─Demo/Demo.csproj
````
> 虚拟文件夹会被拆成一层层目录（`Folder Name="/src/Core/"`）；`Project` / `File` 节点的路径**照 slnx 里写的原样**显示，缺 `Path` 时显示 `(缺 Path)`。

````
Demo.slnx
````
> 空解决方案（只有根节点）时只有文件名一行。

#### 情况四：`.sln` 或其他不可解析的路径 → 拒绝

````
Error: 只能查看 .slnx：Demo.sln。请先用「迁移解决方案为 slnx」把它迁过来。
````
> `.sln` 会被明确拒绝（编辑与查看都只支持 `.slnx`）。

````
Error: 解决方案 Demo.slnx 里没有名为 'X:\temp\nope' 的项目。
````
> 实测：路径既不是文件夹、也不是 `.csproj` / `.sln` / `.slnx` 时，会被当成**项目名**去最近的解决方案里找 —— 所以这种"看起来像路径但不存在"的输入会得到这句（信息不够直白，但指明了它把它当项目名了）。想避免就走完整的 `.csproj` 路径。

## 解决方案编辑

### 迁移解决方案为 slnx `migrate_solution_to_slnx`

- `path`:string
  - 参数限制：必须存在；给文件夹时里面要有解决方案文件；命中的必须是 `.sln`（已经是 `.slnx` 会被拒）；目标 `.slnx` 已存在时必须带 `force=true`。
- `force`:boolean
  - 默认 `false`；`true` 时给命令行加 `--force`。

跑 `dotnet sln <文件> migrate` 把 `.sln` 变成 `.slnx`。

- 之前：确认目标还是 `.sln`（已经是 `.slnx` 会直接报错，白跑一趟）。
- 之后：`view` 看结果；此后才能用另外两个解决方案写入工具。
- 不能做：**不进任何事务** —— 立刻改盘、不可回滚；不校验迁移结果是否等价。
- 注意：幂等性没做（已是 slnx 就是错误）；命令行非 0 退出时会把完整输出带回来。

````
# 迁移完成（命令行改盘，不进事务）
- 源：X:\temp\demo\Demo.sln
- 目标：X:\temp\demo\Demo.slnx

已成功迁移解决方案文件。
````
> 成功形态：明确写"命令行改盘、不进事务"，并把命令行输出原样跟上。

````
Error: Demo.slnx 已经是 slnx 了。
Error: 目标已存在：X:\temp\demo\Demo.slnx。确认要覆盖请带 force=true（dotnet sln migrate 需要 --force）。
````

### 添加项目到解决方案 `add_project_to_solution`

- `slnxPath`:string
  - 参数限制：必须是 `.slnx`（给 `.sln` 会被拒）；必须存在。
- `csprojPath`:string
  - 参数限制：项目文件**必须已经存在**（这个工具不建项目）。
- `folder`:string?
  - 默认空串 = 放到解决方案根；非空 = 虚拟文件夹，如 `src/Core`。

跑 `dotnet sln add` 把项目挂进解决方案。

- 之前：解决方案必须是 `.slnx`（否则先 `migrate_solution_to_slnx`）。
- 之后：`view` 确认落在哪个虚拟文件夹。
- 不能做：不创建项目、不改项目文件、不进事务。
- 注意：**错误形态与其它工具不同** —— 参数/环境问题不走统一包装，是协议级错误；成功且带 `folder` 时目标会额外标成 `（/src/Core/）`。

````
✅ Added X:\temp\demo\Demo\Demo.csproj to X:\temp\demo\Demo.slnx（/src/Core/）

````
已将项目“Demo\Demo.csproj”添加到解决方案中。

````
An error occurred invoking 'add_project_to_solution'.
````
> 参数/环境错误（如项目文件不存在）时是协议级错误，看不到文本原因。

### 从解决方案移除项目 `remove_project_from_solution`

- `slnxPath`:string
  - 参数限制：必须是 `.slnx`；必须存在。
- `csprojPath`:string
  - 参数限制：要是该解决方案里被描述的项目。

跑 `dotnet sln remove` 把项目从解决方案摘掉（不删项目文件本身，也不清理空掉的虚拟文件夹）。

- 之前：`view` 确认它确实挂在里面。
- 之后：`view` 复核。
- 不能做：不删项目文件；不进事务。
- 注意：错误形态同 `add_project_to_solution`（协议级错误）。

````
✅ Removed X:\temp\demo\Demo\Demo.csproj from X:\temp\demo\Demo.slnx

````
已从解决方案中移除项目“Demo\Demo.csproj”。

## 项目文件编辑

### 编辑项目元数据 `edit_project_metadata`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件（绝对路径或唯一项目名）。
- `content`:string
  - 参数限制：必须是**完整** csproj 内容，且先过两道校验：XML 语法合法、根元素是 `<Project>`。不通过就报错，一个字节都不写。
- `dryRun`:boolean
  - 默认 `false`；`true` 时只校验并展示内容，不写盘。

整文件替换 csproj，写回用**文件原编码**。

- 之前：`view` 把现状全文拿走（这个工具不吃"片段合并"，你给什么就写什么）。
- 之后：`view` 复核；改的是包引用就用 `list_project_packages` 看实际结果。
- 不能做：不做 XML 片段合并/补丁；不校验 MSBuild 语义；不进事务。
- 注意：内容与现状**完全一致**时不算失败（会明说"内容没有变化，未写入。"）。

````
# 编辑元数据
- 项目：X:\temp\demo\Demo\Demo.csproj
- 编码：utf-8（来自 无 BOM 且是合法 UTF-8）
- 已写入（XML 语法检查 + 根元素 Project 检查通过）。
````
> 真写形态；`dryRun=true` 时第三行换成 `- **预演，未写入**。` 并把内容原样贴出。编码来源可能是 `.editorconfig 的 charset` / `BOM` / `无 BOM 且是合法 UTF-8` 三种。

````
Error: XML 语法不通过：Data at the root level is invalid. Line 1, position 1.
Error: 最低 csproj 语法检查不通过：根元素必须是 <Project>，实际是 <Foo>
````

## NuGet 包

### 查看项目包引用 `list_project_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。

列出包引用：直接引用的顶级包、依赖传递进来的包、通过 `ProjectReference` 传递进来的顶级包。依赖图来自真实还原（MSBuild 的 `ReferencePath`，退化时读 `project.assets.json`）。

- 之前：想拿到最准的图，先自己还原过。
- 之后：装/删包用 `install_packages` / `remove_packages`。
- 不能做：不还原、不修改项目、不解析版本冲突。
- 注意：输出会写依赖图来源；`⚠ 依赖图可能已过期` 表示 csproj / props 比还原产物新。

````
# 包引用
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj
- 依赖图来源：真实还原结果
- assets：X:\temp\zms-mcp-demo\Demo\obj\project.assets.json

## 顶级包（直接引用）
- Microsoft.CodeAnalysis.CSharp 5.9.0

## 依赖传递包
- microsoft.codeanalysis.common 5.9.0

## 项目引用而传递的顶级包
（无）
````
> 实测形态（某一栏为空写 `（无）`）。没有还原产物时来源那行会变成 `ReferencePath（还原产物缺失，可能不全）（…）`。

### 搜索包 `search_packages`

- `packName`:string
  - 可以为空串 —— 那时本地分支会列出本地全部包名。
- `page`:integer
  - 默认 0；只对线上有效。
- `local`:boolean
  - 默认 `true`（查本地 NuGet 缓存）。
- `web`:boolean
  - 默认 `false`（查 nuget.org）。

按关键词搜包 id。**两种开关的组合会改变输出形状**：只 `local` → 本地清单（计数 + 名字）；只 `web` → `# 线上查询到 N 个（第 X 页，每页 N 条）` + 每行 `id 版本`；两个都开 → **只给线上那一页**，本地也有的用 `[]` 标出。

````
# 本地查询到 1 个

newtonsoft.json
````
> 实测（默认只查本地）。

### 列出包版本 `list_package_versions`

- `packName`:string
  - 参数限制：必须是**精确**包 id（不是关键词）。
- `verRange`:string?
  - 默认空串 = 只列正式版；写法见下面四种情况。
  - 参数限制：语法错会被拒（用 NuGet 官方 `VersionRange` 解析）。
- `local`:boolean
  - 默认 `true`。
- `web`:boolean
  - 默认 `false`。

**`verRange` 永远是「范围」，符合的版本全部列出来，绝不挑一个**（原来实现是"挑一个"，已改）：

1. **空串** → 只列正式版（等价 `[0.0.0,)` 且排除预览版）；
2. **`*`** → 所有正式版，等价于范围 `[0.0.0,9999.9999.9999]`；
3. **`*-*`** → 连同预览版；
4. **其他写法**（如 `[13.0,14.0)`、`1.2.*`、`13.0`）→ 一律交给 NuGet 解析；显式把预览版写进范围两端时也算"包含预览版"。

- 之前：`search_packages` 确认 id 拼写。
- 之后：选好版本传给 `install_packages`（`名字@版本`）。
- 不能做：不安装；不解析依赖树。
- 注意：表头会写明本次生效的范围与是否含预览版。

#### 情况一：`verRange` 空串

````
# Newtonsoft.Json（本地缓存）
````
> 范围 release versions only (>= 0.0.0)：共列出 2 个版本

13.0.3
13.0.2
> 只列正式版。

#### 情况二：`verRange` = `*`

````
# Newtonsoft.Json（本地缓存）
````
> 范围 all release versions ([0.0.0,9999.9999.9999])：共列出 2 个版本

13.0.3
13.0.2
> 实测：与你给的等价范围一字不差地写在表头。

#### 情况三：`verRange` = `*-*`

````
# Newtonsoft.Json（本地缓存）
````
> 范围 *-* (prerelease included)：共列出 5 个版本

13.0.4-beta1
13.0.3
> 连预览版一起列。

#### 情况四：显式范围

````
# Newtonsoft.Json（本地缓存）
````
> 范围 range [13.0,14.0) (release versions only)：共列出 2 个版本

13.0.3
13.0.2
> 实测：`[13.0,14.0)` 走的这条；浮点写法 `1.2.*` 会额外用前缀段收紧（NuGet 的 `Satisfies` 边界会把 `1.3.0` 也算进 `1.2.*`，实现自己拦掉了）。

````
Error: '[' is not a valid NuGet version range (…)
````
> 范围写坏的形态。

### 查询包元数据 `get_package_metadata`

- `packName`:string
  - 参数限制：必须是精确包 id。
- `ver`:string?
  - 默认空串 = 本地缓存最高版本（本地没有时走线上最新）。

给出包的 **nuspec + readme**：先看本地缓存，本地没有再问 nuget.org。

- 之前：想查特定版本先 `list_package_versions`。
- 之后：学 API 时配 `list_doc_symbols`。
- 不能做：不做在线搜索；readme 缺失时不给替代内容（只给 `projectUrl` / `repositoryUrl`）。
- 注意：一旦本地命中就**不再**去线上补 readme。

````
# Newtonsoft.Json 13.0.3（本地缓存）

## nuspec
    <?xml version="1.0" encoding="utf-8"?>
    <package …>…</package>

## readme
# Json.NET is a popular high-performance JSON framework for .NET

````
# Newtonsoft.Json 99.0.0
未找到（本地缓存与 nuget.org 都没有）。
````

````
### 安装包 `install_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `nugetPack`:string[]
  - 参数限制：至少一项；每项是 `名字` 或 `名字@版本`（按**最后一个** `@` 切分）；空项被忽略。
- `dryRun`:boolean
  - 默认 `false`。
- `allowPrerelease`:boolean
  - 默认 `false`。

跑 `dotnet add package` 装包。**四种情况**：

1. **`dryRun=true`**：只决定并展示要跑的命令，一条都不跑；
2. **`dryRun=false`**：真跑，并在末尾附**落盘后漏洞核对**（`dotnet restore -p:NuGetAuditMode=all`，摘 `NU1901–NU1904`）；
3. **`allowPrerelease=true`**：选版本时允许预览版；
4. **没给版本**：先本地缓存最新、再线上；**选中的版本若有已知漏洞会换成最新安全版**；漏洞索引拿不到时明确标"未经核对"，不默认当安全。

- 之前：`search_packages` + `list_package_versions` 定名字与版本。
- 之后：`list_project_packages` 看真实结果。
- 不能做：**不进事务**（立刻改 csproj 并触发还原，不可回滚）；不单独引入传递包（除非被引用包的版本与参数冲突）。
- 注意：输出会分六栏（哪些请求版本有漏洞 / 要引入哪些包 / 哪些因为被引用而未直接引入 / 哪些因漏洞被自动升级 / 哪些被本次传递引入 / 哪些传递包原本就有），`---` 之后是命令清单。

#### 情况一：`dryRun=true`（预演）

````
# 以下直接引入包是漏洞的
（无）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
（无）

# 以下包因为漏洞被自动升级引入
（无）

# 以下包被本次传递引入
System.Text.Json

# 以下传递引入包原本就存在
（无）

---
以下用命令行改盘，**不进事务**（立即生效、不可回滚）：
- dotnet add "Demo.csproj" package Newtonsoft.Json --version 13.0.3

（预演，未真正执行。）
````
> 每栏为空写 `（无）`；末尾明说预演。

#### 情况二：`dryRun=false`（真跑）

````
# 版本冲突被钉住的包
Polly：参数要 8.4.2，依赖图要 8.5.0 → 采用 8.5.0

---
…
## 落盘后核对（NU1901–NU1904，NuGetAuditMode=all）
- 未发现 NU1901–NU1904（已带 NuGetAuditMode=all，覆盖传递依赖）。
````
> 真跑比预演多两段：依赖图要的版本比参数高时的"钉住"说明，以及落盘后漏洞核对。

#### 情况三：漏洞索引不可用

````
# 以下直接引入包是漏洞的
Foo 1.0.0（漏洞索引不可用，未经核对）
````
> 索引拿不到时不会默默当"安全"，而是明确标"未经核对"。

#### 情况四：参数为空 / 包找不到 / 命令行失败

````
Error: 至少要给一个包。
Error: 本地缓存没有 Foo，也无法从 nuget.org 取版本：…
Error: dotnet add package Polly 失败（退出码 1）：
<命令行输出>
````

### 移除包 `remove_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `nugetName`:string[]
  - 参数限制：至少一项。
- `dryRun`:boolean
  - 默认 `false`。

跑 `dotnet remove package`。**两种情况**：

1. **`dryRun=true`**：不执行任何命令，但**照样算出一并消失的传递包** —— 用本地依赖图"建图 → 切掉要移除的直接包及其不再被需要的传递包 → 与移除前的图比对"；
2. **`dryRun=false`**：真删，并在移除前后各建一次图，用真实结果复核那一栏。

- 之前：`list_project_packages` 看当前图。
- 之后：`list_project_packages` 复核。
- 不能做：**不进事务**（立刻改盘）；不能移除"只是被传递引入"的包（要先处理它的上游）。
- 注意：这个工具**没有**"不可移除"的参数；预演与真跑的报告形状一致。

#### 情况一：预演（`dryRun=true`）—— 传递包也算得出

````
# 本次移除包
Microsoft.CodeAnalysis.CSharp

# 本次移除的依赖传递包
microsoft.codeanalysis.common

（预演：未执行任何命令；上面的「依赖传递包」由本地依赖图切图推出。）
````
> 实测：连"一并消失的传递包"都列出来了，不再写"预演时为 0"。

#### 情况二：真跑（`dryRun=false`）

````
# 本次移除包
Microsoft.Extensions.Logging

# 本次移除的依赖传递包
Microsoft.Extensions.Logging.Abstractions
````
> 被直接移除的包不会再在第二栏重复出现（它只属于第一栏）。

````
Error: 至少要给一个包名。
Error: dotnet remove package Foo 失败（退出码 1）：
<命令行输出>
````

### 列出文档注释符号 `list_doc_symbols`

- `packName`:string
  - 参数限制：必须是精确包 id。
- `ver`:string?
  - 默认空串 = 本地缓存最高版本。
- `tar`:string?
  - 默认空串 = 实现自己挑一个最合适的 TFM；也可以给 `lib/` 下的目录名，如 `net8.0`。
- `path`:string?
  - 默认空串 = 列出（有文档注释的）所有类型；非空 = 点分隔的对象路径。
- `argsList`:string?
  - 默认空串；逗号分隔参数类型，用来消歧重载；两种写法都吃（`string,int` 与 `(System.Int32,System.String)`）。
- `type`:string?
  - 默认空串 = 按 `path` 精度推断；也可以显式给字母，取自 `NTPFMED`（N 命名空间、T 类型、P 属性、F 字段、M 方法、E 事件、D 原始 XML 片段）。

从**本地 NuGet 缓存**的 XML 文档注释里查符号（不做在线查找）。**四种情况**（由 `path` 的精度决定生效的 kind）：

1. **`path` 空** → 列全部类型（按 `type`，默认 `T`）；
2. **`path` 不是类型**（如命名空间前缀）→ `T` 前缀匹配；
3. **`path` 正好是类型** → `PFME`（属性/字段/方法/事件）；
4. **`path` 是单个成员** → `D`（直接给原始 XML 片段）；重载则给 `M`。

- 之前：包必须在本地缓存里（用 `list_package_versions` 的本地分支确认）。
- 之后：配 `get_package_metadata`（readme）一起学 API。
- 不能做：不查网页文档、不读"野生" docs 目录；不做反射（给的是文档条目，不是真实签名）。
- 注意：表头永远写明本次生效的 kind 以及它是"按精度推断"还是"显式指定"；`type` 里带 `D` 表示**输出原始 XML 片段**（此时 `D` 不参与种类过滤）；文档里 `ref` / `out` 参数带 `@` 后缀，匹配时**不带** `@` 写。

#### 情况一：`path` 空 → 列全部类型

````
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\Users\me\.nuget\packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 147 | 生效 type: `T`（按精度推断）

- `T:Newtonsoft.Json.Bson.BsonObjectId`
- `T:Newtonsoft.Json.Bson.BsonReader`
````
> 实测形态；同名重载超过阈值会分组显示参数表。

#### 情况二：`path` 正好是类型 → `PFME`

````
# Newtonsoft.Json 13.0.3 (net6.0)
- 条目总数: 1613 | 命中: 12 | 生效 type: `PFME`（按精度推断）

- `P:Newtonsoft.Json.Linq.JObject.Item(System.String)`
- `M:Newtonsoft.Json.Linq.JObject.Add(System.String,System.Object)`
````

#### 情况三：`path` 是单个成员 → `D` 原始片段

````
# Newtonsoft.Json 13.0.3 (net6.0)
- 条目总数: 1613 | 命中: 1 | 生效 type: `D`（按精度推断）

    <member name="M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)">
      <summary>Serializes the specified object to a JSON string.</summary>
    </member>
````

#### 情况四：路径没命中 / 本地没有这个包

````
- 提示: 路径 'Newtonsoft.Json.Nope' 没有命中任何文档条目

_(没有命中的文档条目)_
````
> 表头之后会多一行"提示"，末尾是空结果占位行。

````
Error: 本地缓存里没有 Newtonsoft.Json 13.0.3 的 XML 文档注释（package 未下载或该 TFM 没有文档）。
````

## 符号查询

### 查符号 `symbols`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `path`:string?
  - 默认空串 = 列整个项目的符号；也可是类型（`Ns.Type`）或成员（`Ns.Type.Method(int,int)`）。
- `read`:boolean
  - 默认 `false`：`path` 给类型时"列成员"；`true` 时改成"读这个类型的结构"。
- `type`:string?
  - 默认空串 = 全部；字母取自 `NCSIPFEMD`（N 命名空间、C 类、S 结构、I 接口、P 属性、F 字段、E 事件、M 方法），另有 `T` = 类/结构/接口三种合起来、`D` = 只要带 XML 文档注释的符号。
- `modifier`:string?
  - 默认空串；逗号分隔 `public` / `internal` / `protected` / `private` / `static` / `const` / `abstract` / `readonly` / `virtual` / `override`。
- `argsList`:string?
  - 默认空串；逗号分隔参数类型，只列参数类型**完全一致**的方法。
- `nameFilter`:string?
  - 默认空串；对成员名做子串过滤。

一个工具覆盖原来四个（`list_types` / `list_members` / `list_symbols` / `get_member`）。**四种情况**，由 `path` 与 `read` 决定：

1. **`path` 空** → 列整个项目的符号（`type` / `modifier` / `argsList` / `nameFilter` 都生效）；
2. **`path` = 类型，`read=false`** → 列该类型的成员；
3. **`path` = 成员** → 读该成员（签名 + 文件 + 行范围 + 源码）；
4. **`path` = 类型，`read=true`** → 读该类型的结构（只给成员签名，不展开实现；此时 `type` 不再起过滤作用）。

- 之前：`view` 拿到项目路径。
- 之后：要看包用 `list_project_packages`；要改成员走拟定（`track_project` → `stage_draft`）。
- 不能做：不写盘（**没有**直接编辑符号的工具了）；不查外部程序集。
- 注意：签名渲染上，属性只隐藏**访问器实现**（用 `{ get { … } }` 记号），属性本身的 `ref` / `ref readonly` / 访问权限、访问器的访问权限（如 `private set`）、`override`、`required` + `init`、struct 上的 `readonly` 都会显示出来。

#### 情况一：`path` 空 → 列整个项目

````
# X:\temp\zms-mcp-demo\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 3 | 过滤: type='' modifier='' args='' nameFilter=''

## Demo
- `Demo` (namespace) — Class1.cs:1
- `class Class1` (class) — Class1.cs:3
  - `public static int Add(int a, int b)` (method) [Class1] — Class1.cs:5
````
> 实测：表头回显过滤条件；按命名空间分段（根命名空间写 `(global)`）、按层级缩进、成员带 `[所属类型]`。

````
- TFM: `net11.0` | 符号: 0 | 过滤: type='X' modifier='' args='' nameFilter=''
⚠ 无法识别的 type 字母已忽略：X（可用：N C S I P F E M D）

_(无匹配符号)_
````
> `type` 写错字母会**明确警告**（不静默吞掉）。

````
# X:\temp\zms-mcp-demo\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 1 | 过滤: type='' modifier='' args='' nameFilter='Add'

## Demo
  - `public static int Add(int a, int b)` (method) [Class1] — Class1.cs:5
````
> 实测：`nameFilter="Add"` 只剩 1 个符号。

#### 情况二：`path` = 类型，`read=false` → 列成员

````
# Demo.Class1  (class)
- TFM: `net11.0`

- `Demo.Class1.Add(int, int)` — Class1.cs:5
````
> 实测形态：第一行是 `# <类型显示名>  (<种类小写>)`；没有成员时给 `_(no members)_`。

#### 情况三：`path` = 成员 → 读源码

````
## Demo.Class1.Add(int, int)

- Kind: `Method`
- Declaring type: `Demo.Class1`
- File: `X:\temp\zms-mcp-demo\Demo\Class1.cs`
- Lines: 5-5

        public static int Add(int a, int b) { return a + b + 5; }
````
> 实测形态：要成员就给源码与行范围。

#### 情况四：`path` = 类型，`read=true` → 读类型结构

````
## public class Class1

- Kind: `Class`
- Members: 1
- File: `X:\temp\zms-mcp-demo\Demo\Class1.cs`（3-6）

````
### Members
- public static int Add(int a, int b)
> 实测形态：给类型结构时**只给签名**（方法体、属性实现都不展开），并给类型声明所在的行范围。

## 拟定流程

链路：**追踪（拿 cookie）→ 拟定编辑 → 查看符号 / 解决冲突 → 确认（预检 / 落盘）**。拟定状态存在 MCP 自己的 sqlite 里（重启不丢）；**编辑一律先追踪**，`stage_draft` / `confirm_draft` 只认 `track_project` 发出来的 cookie。

### 追踪 / 清除追踪 `track_project`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件；带 cookie 时该 cookie 必须与这个项目的追踪记录一致，否则报错且不动任何数据。
- `cookie`:string?
  - 默认空串 = 开始或继续追踪。

**两种情况**：

1. **`cookie` 空**：首次调用保存**每个符号的 hash**并返回追踪 cookie，之后调用保留基线、只报告（列出待编写的拟定 + 符号 hash 对不上的"未追踪更改"）；
2. **`cookie` 非空**：校验通过后**删除该项目的追踪记录与全部拟定**（丢弃拟定的唯一途径）；有未完成的写前日志会**先前滚**，绝不静默丢。

- 之前：`view` 拿到项目路径。
- 之后：把 cookie 传给 `stage_draft` / `confirm_draft`；或 `list_draft` 复看拟定。
- 不能做：不做"只清拟定、保留追踪"；**不保存文件快照、也不会把文件还原回去** —— 追踪只回答"哪些符号变了"。
- 注意：返回里会写明这个 cookie 的**两个用途**（拟定编写、解除追踪）。

#### 情况一：`cookie` 空 → 开始/汇报追踪

````
# 追踪已开始
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj
- 追踪 cookie：`fa8f9165-…`
  - 用途一：传给 stage_draft / confirm_draft 做拟定编写与落盘（这两个工具只认 cookie，不要 csprojPath）
  - 用途二：传回 track_project 的 cookie 参数，即可解除追踪（清掉追踪记录与全部拟定）
- 已保存 2 个符号的 hash（只存 hash，不存文件快照；追踪只回答「哪些符号变了」，不会把文件还原回去）

## 现在已经有这些拟定写
（无）

## 有这些未追踪更改
（无 —— 已保存的 hash 就是当前现状）
````
> 实测原文。

#### 情况二：`cookie` 非空 → 清除追踪与拟定

````
# 取消追踪
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj
- 已删除拟定：1 条
- 追踪快照已删除；要再写这个项目，请重新 track_project。
````
> 若前滚时因"文件被人手改过"而刻意保留了日志，这里会多一行"同时丢弃 N 条未处理的写前日志"。

````
Error: 追踪 cookie 不匹配（或该项目没有在追踪）。
````

### 拟定编辑 `stage_draft`

- `cookie`:string
  - 参数限制：必须是 `track_project` 发的**有效**追踪 cookie（缺这个参数是协议级错误；cookie 无效则报 `Error:`）。
- `typePath`:string
  - 参数限制：要能**精确**定位到一个类型（到顶层类型的完全限定名，嵌套类型可多几个点）。
- `memberName`:string?
  - 默认空串 = **拟定一个全新类型**；非空 = 成员名（方法带参数列表，如 `Add(int,int)`）。
- `content`:string?
  - 默认 null = 删除该成员；非 null = 新源码。

拟定新增 / 修改 / 删除一个成员。

- 之前：先 `track_project`（无 cookie）拿 cookie；`symbols` 看现状。
- 之后：`list_draft` 看全量；`symbols`（`path` 给该符号）看拟定视图；`confirm_draft` 预检。
- 不能做：不落盘（只记"符号 + 意图 + 首次 hash"）；**不合并**同符号的多次调用 —— 同一符号是替换，只保留最后一条。
- 注意：内容先过 **C# 语法检查**，不过就整条拒绝；任何一次调用都会**作废现有的落盘许可**；给一个**有未解决冲突的符号**拟定，同时会**解决掉那个冲突**（把它的 hash 更新为现状）。

````
# 拟定已更新
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj
- 符号：Demo.Class1.Add(int, int)
- 本次：写入（replaced）
- 拟定条数：1
- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
````
> 实测原文（`replaced` = 该符号原有拟定被替换）。`content=null` 时"本次"那行是"删除"。

````
Error: Code does not parse as C# member declaration(s): 应输入 )
An error occurred invoking 'stage_draft'.
````
> 第一行是语法不过；第二行是**没给 `cookie`**（必需参数缺失，协议级错误）。

### 列出拟定 `list_draft`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。

列出某个项目的待落盘拟定（含 cookit）—— 重启后的 agent 靠它把拟定接回来。只读，不改状态。

- 之前：无（这是"接回旧拟定"的正规入口）。
- 之后：`confirm_draft` 预检。
- 不能做：不显示"未追踪更改"（那在 `track_project` 的汇报里）。
- 注意：它仍用 `csprojPath`（不认 cookie）。

````
# 拟定
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj
- 起始时间：2026-09-25T08:28:54.…+00:00
- 条数：1

- [1] 修改 Demo.Class1.Add(int,int)
````
> 实测形态；无拟定时只有三行元信息（`- 条数：0`）。

### 解决冲突 `select_draft`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件；该项目要有待处理的拟定。
- `memberPath`:string
  - 参数限制：要能定位到冲突对应的符号（通常直接复制 `symbols` 给的 `memberPath`）。
- `selectCookie`:string
  - 参数限制：必须是 `symbols` 在冲突时给出的那个，且还得是最新的（`symbols` 每次调用都会换）。
- `choice`:string
  - 参数限制：只能是 `keep` / `drop`。
  - **取消**了原来的 `snapshot`（还原为快照）。

**两种情况**：

1. **`keep`** → 保留这个符号的拟定；
2. **`drop`** → 把这个符号从拟定里去掉。

两种选择都会**把这个符号的 hash 更新为现在的 hash**（于是冲突消失）。

- 之前：`symbols`（`path` 给该符号）拿到 `selectCookie`。
- 之后：任何落盘许可都已作废 → 重新 `confirm_draft` 预检。
- 不能做：不落盘；不一次解决多个符号。
- 注意：这是"接受外部改动"的语义 —— 冲突消失是因为基线被对齐到现状，不是把文件改回去。

````
# 选择已应用
- 符号：Demo.Class1.Add(int, int)
- 选择：用拟定内容（坚持本次写法）
- 落盘许可已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
````

````
Error: 没有未完成的拟定可以处理。
Error: 无法识别选择：'snapshot'。可用：keep / drop
````

### 确认拟定 `confirm_draft`

- `cookie`:string
  - 参数限制：必须是 `track_project` 发的**有效**追踪 cookie。
- `applyCookie`:string?
  - 默认空串 = 只做预检（不落盘）。
- `apply`:boolean
  - 默认 `true`。

**三种情况**：

1. **`applyCookie` 空** → 预检：重建符号树、逐符号定位文件、跑预检（文件占用 / **未解决冲突** / 文件字节），**只有不存在未解决冲突时**才返回内存 applyCookie 与会碰的文件清单；
2. **`applyCookie` 非空** → 复查每个符号是否还在同一文件、文件字节是否未变（只覆盖"发 cookie → 传 cookie"这段窗口），然后落盘；
3. **`apply=false`** → 即使 cookie 匹配也只预览，不写盘。

落盘顺序固定：**写前日志 → 原子写 → 清日志/拟定/许可 → `dotnet format` → 重算追踪基线**。**不做任何 git 操作**（也不再在汇报里提这件事）。

- 之前：`stage_draft` 至少一条；要落盘先跑一次"无 applyCookie"的预检拿许可。
- 之后：`symbols` 复核结果；`track_project`（无 cookie）看新基线与未追踪更改。
- 不能做：不提交 git；不部分落盘（一次把该项目全部拟定落完）；不接受过期许可。
- 注意：`dotnet format` 失败时实现会**保留**拟定与写前日志（可直接重跑），并把已写入的文件列出来。

#### 情况一：`applyCookie` 空 → 预检（无冲突才发 cookie）

````
# 拟定确认
- 项目：X:\temp\zms-mcp-demo\Demo\Demo.csproj

## 变更分类
````
### 修改（1）
- Demo.Class1.Add(int,int)

## 本次改动涉及
- X:\temp\zms-mcp-demo\Demo\Class1.cs（Demo.Class1.Add(int, int)）

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：无
- 消失：无

## 预检查
- ✅ 无占用、无冲突

- 落盘 cookie：`451d9f5a-…`（传回来才落盘；每次预检换新，旧 cookie 立刻失效）
> 实测原文。

#### 情况二：存在未解决冲突 → 不发 cookie

````
## 预检查
- ⛔ 还有 2 个未解决的冲突（没有未解决冲突才发落盘 cookie）：
-   - Demo.Class1 —— 追踪之后被外部改动
-   - Demo.Class1.Add(int, int) —— 追踪之后被外部改动（拟定里有它）
-   解决路径：查看符号（拿 selectCookie）→ select_draft（keep 保持拟定 / drop 移除拟定）→ 或重新 stage_draft 重新拟定。

（预检查未通过，不发放 applyCookie：解决上面列出的问题后重新预检。）
````
> 实测原文：手工改盘（模拟外部改动）后预检就是这个形态 —— **新增的、没有拟定的符号也算冲突**；类型级与成员级会分别列出。

#### 情况三：`applyCookie` 非空 → 落盘

````
## 已落盘
- X:\temp\zms-mcp-demo\Demo\Class1.cs
- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）
- 格式化：已对本次改动的文件跑 dotnet format
- format 额外改动：无
- 追踪继续，基线已按落盘后的现状整体重算。
````
> 实测原文（已经**没有**那句"未做任何 git 操作"）；内容本来就没变的文件会标"（内容未变，跳过）"。

````
Error: 落盘目标状态与发许可时不一致，已作废许可：
  - X:\temp\…\Class1.cs 在发许可之后被外部改过
````

````
# 落盘未完成（文件已写入，但格式化失败）
- 已写入 1 个文件：X:\temp\…\Class1.cs
- dotnet format 未成功（退出码 1）：…
- 拟定与写前日志**已保留**：处理完之后重新 confirm_draft（不带 cookie）预检即可重试。
````

## 关于示例

- 示例按实现里拼字符串的代码与**已实测**的返回推出，不是"为写文档去跑各种情况"。
- 措辞无法断言的地方只写到能确定的那一层，没有编造。
