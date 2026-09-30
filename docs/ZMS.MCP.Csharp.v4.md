# ZMS.MCP.Csharp 工具手册 v4

这份手册以**实机调用结果**写成：每个工具的参数取自运行时 schema，返回值示例是实际调用回来的原文（不是从代码推断的）。共 **20 个工具**。

> 与 v3 的主要差别：
> 新增 `rename_symbol`（改名走拟定，不自己写盘）；
> `symbols` 新增**四个"要不要列出"的信息开关**（文档注释 / 特性 / 被引用 / 实现），原来的 `read` 被它们收编，
> `type` 里的 `D` 从"文档注释"换成"委托"；
> `stage_draft` 的路径语义改为"`typePath` 指向什么就加什么"，并且每加一条改动**当场编译、当场报诊断变化**；
> 拟定全程在内存里做，追踪基线换成**语义指纹**（语义不变则 hash 不变）。
>
> 工具数从 v3 的 19 变成 20：`find_references` **没有**做成独立工具 —— 它的能力并进了 `symbols` 的 `references` 开关。

## 约定

- 参数类型用 C# 可空语法：`:string` = 必填；`:string?` = 可空（"没给"与"给了空串"等价）。
- `integer` / `boolean` 不可为 null，但通常有默认值。
- **所有工具的错误都是同一种文本形态**：`Error: <异常消息>`。需要外部命令的工具（`dotnet add/remove/sln ...`）在命令非 0 退出时把命令行输出整段带回。
- `csprojPath` 统一解析：绝对路径（存在即用）；否则当成**项目名**，从当前目录向上找解决方案、在其中找唯一同名项目，重名时要求给完整相对路径。
- 需要外部命令的写入工具（解决方案编辑、装包/删包、迁移）**不进任何事务**：立刻改盘、不可回滚。
- **符号寻址只有一套语法**：`命名空间.类型.成员`，方法带**无空格**参数表。`symbols`、`stage_draft`、`rename_symbol` 全都走同一个解析器，`symbols` 列出来的写法可以直接抄给其它工具。
- **编译器项目进度的三种状态**要分清：`按需加载`（首次求值，秒级）/ `命中缓存`（磁盘没变，毫秒级）/ `重新求值`（磁盘变了）。输出里会标明这次是哪一种。
- **只有"读现状 → 决策 → 写回"的操作才需要两段式确认**（cookie）。全文替换的项目文件也算 —— 因为那份"全文"是基于读到的现状构造的；**现读现改的命令行**（`dotnet add/remove package`）不算，它不存在"基于过期快照覆盖"；**只写自己库的**（追踪 / 拟定记录）也不算。
- **失败会被明确标出来，不用靠读输出猜。** 工具失败时返回的第一段文本以 `Error: ` 开头，同时协议层的 `isError` 置为 `true` —— 越界拒绝、`dotnet` 命令退出码非 0、文件不存在、写盘失败都算。成功时 `isError` 为 `false`。

## 工作空间边界

**每次调用带的所有路径参数，都必须落在本次会话的工作区里。** 越界会在**工具执行之前**被拒绝，拒绝的话里写清三件事：**你给的是哪个路径**、**工作区在哪**、**要越界该怎么办**。

工作区从哪来（按优先级）：

1. 环境变量 `ZMS_MCP_WORKSPACE`（多个目录用 `;` 分隔）
2. 进程的当前目录（宿主启动服务时给的）—— 通常就是"agent 给你的工作区"

两者都取不到时视为**不限制**（模型的 server instructions 里会写明边界是什么）。

**它怎么认路径**：先按参数名（`path` / `file` / `dir` / `folder` / `slnx` / `csproj`），再按值（含分隔符或盘符）—— 所以 `symbols` 的 `path='Ns.Type.Member'` 那种**符号路径**不会被误当成文件路径。相对路径按工作区解析。

**实测输出**（边界设为 `X:\temp\zms-guard`，去读工作区之外的项目）：

```
Error: 路径不在本次会话的工作区里：C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
  工作区：X:\temp\zms-guard
  要动它的话：让宿主把这个目录加进去（环境变量 ZMS_MCP_WORKSPACE，多个目录用 ';' 分隔），或者明确告诉我你确认要越界 —— 我不替你猜。
```

**它不是什么**（重要）：

- **不是安全边界**：服务是独立进程，用启动者的权限跑；宿主要真想越狱，自己起个命令行就行。MCP 的 `roots` 协议同样只是"协作约定"（规范用 SHOULD，且已被 SEP-2577 废弃），这套工具**没有**依赖它。
- **它管的是"你被要求做什么"，不是"你能做什么"**：工具代码内部写死的路径（临时目录之类）、以及工具调起来的外部命令（`dotnet build` / `dotnet format` / `dotnet add package`），都不在它的射程内。
- 它要解决的是**手滑和误会** —— 让调用方知道"这件事我碰的是哪儿"，而不是静默地按一个没人核对过的路径去读写。

**和"两段式确认"的分工**：

| 机制 | 管什么 | 什么时候生效 |
|---|---|---|
| **工作空间边界** | 路径**落在哪** | 每次调用（读也一样 —— 读也可能触发 MSBuild 求值） |
| **两段式 cookie** | 写出去的**是不是你读过的那一版** | 只有"读现状 → 决策 → 写回"的操作 |

两条判据互不替代：边界保证"不越出工作区"，cookie 保证"不覆盖别人的改动"。

---

## 一、扫描与查看

### 1. 扫描项目 `scan_projects`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `:string` | 必须是**存在的文件夹**，否则报错 |
| `depth` | `integer` | 默认 4 |

扫描一个文件夹里的解决方案与项目文件。每个解决方案一块：`<解决方案路径>(被描述数+额外数)`，接着是它描述的项目树（`├─` / `└─`），再接着"在该解决方案文件夹下、但没被它描述"的项目（`-` 前缀）；不被任何解决方案覆盖的项目在最后平铺。`bin` / `obj` / `.git` 跳过。

**实测输出**（`path=C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP`，`depth=2`）：

```
ZMS.MCP.slnx(4+0)
├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
├─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
├─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
└─test/ZMS.MCP.Csharp.Test/ZMS.MCP.Csharp.Test.csproj
```

一个都没扫到时只有一行：

```
No solution/project files under C:\temp\empty (depth 4).
```

- 之前：想知道"这堆目录里有什么"时用它，通常是流程第一步。
- 之后：拿到的路径喂 `view_project_or_solution`。
- 不能做：不写盘、不改解决方案；不跟随 `ProjectReference` 展开；不判断项目能不能编译。
- 注意：`depth` 默认 4，仓库很深时给大值。

### 2. 查看项目或解决方案 `view_project_or_solution`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `:string` | csproj（或解决方案内唯一的项目名）→ 看项目；`.slnx` → 看解决方案树 |

**给 csproj**：打出 csproj 原文，然后按顺序附上所有"参与声明这个项目"的文件：项目文件本身、最近的 `Directory.Build.props`（以及它自己 `Import` 的更上层文件）、`Directory.Packages.props`、`Directory.Build.targets`、`global.json`、`NuGet.config`，最后是还原生成物 `obj/<项目>.csproj.nuget.g.props|targets`。

**实测输出**（项目，节选）：

````
# X:\temp\zms-tool-drill\Demo\Demo.csproj

## 项目文件
X:\temp\zms-tool-drill\Demo\Demo.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

</Project>
```

## 还原生成（属性） — obj 位置取自 MSBuild 的 MSBuildProjectExtensionsPath
X:\temp\zms-tool-drill\Demo\obj\Demo.csproj.nuget.g.props
```xml
...
```
````

**给 `.slnx`**：按**解决方案文件本身**展开成树，包含虚拟文件夹（`Folder Name="/src/"` 会被拆成一层层目录）。

- 之前：通常先用 `scan_projects` 拿路径。
- 之后：要改 csproj 内容用 `edit_project_metadata`；要加/删项目用 `add_project_to_solution` / `remove_project_from_solution`。
- 不能做：不修改、不格式化；**不做 MSBuild 求值**（给的是"参与声明的文件原文"，不是展开后的属性值）；不支持凭 `.sln` 看树（先迁移）。
- 注意：`obj` 的位置是**问 MSBuild 要的**；问不到时那份列成"未列出"并说明原因，而不是猜一个 `<项目目录>/obj/`。给 `.sln` 会被拒：`Error: 只能查看 .slnx：Demo.sln。请先用「迁移解决方案为 slnx」把它迁过来。`

---

## 二、解决方案编辑

### 3. 迁移解决方案为 slnx `migrate_solution_to_slnx`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `:string` | 路径必须存在；命中的必须是 `.sln`（已是 `.slnx` 会被拒） |
| `force` | `boolean` | 默认 `false`；`true` 时加 `--force`（覆盖已存在的 `.slnx`） |

**实测输出**：

```
# 迁移完成（命令行改盘，不进事务）
- 源：X:\temp\zms-tool-drill\Demo.sln
- 目标：X:\temp\zms-tool-drill\Demo.slnx

已生成 .slnx 文件 X:\temp\zms-tool-drill\Demo.slnx。
```

- **不进任何事务** —— 立刻改盘、不可回滚。
- 迁移后会**核对产物是否真的生成**：退出码为 0 但没生成 `.slnx` 时报错并带上命令行输出。
- 已经是 `.slnx` 是**错误**（幂等性没做）。
- 命令行非 0 退出时报错里带完整输出；`force=true` 会覆盖已存在的 `.slnx`。

### 4. 添加项目到解决方案 `add_project_to_solution`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `slnxPath` | `:string` | 必须是 `.slnx`（给 `.sln` 会被拒）；路径必须存在 |
| `csprojPath` | `:string` | 项目文件**必须已经存在**（这个工具不建项目） |
| `folder` | `string?` | 默认空串 = 解决方案根；非空=虚拟文件夹，如 `src/Core` |

跑 `dotnet sln add`。

**实测输出**（项目已在解决方案里时是幂等的）：

```
✅ Added X:\temp\zms-tool-drill\Demo\Demo.csproj to X:\temp\zms-tool-drill\Demo.slnx

```
解决方案 X:\temp\zms-tool-drill\Demo.slnx 已包含项目 Demo\Demo.csproj。
```
```

- 错误形态与其它工具**一致**：`Error: <消息>`。
- 不创建项目、不改项目文件、不进事务。
- 成功且带 `folder` 时目标会额外标成 `（/src/Core/）`。

### 5. 从解决方案移除项目 `remove_project_from_solution`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `slnxPath` | `:string` | 必须是 `.slnx`；路径必须存在 |
| `csprojPath` | `:string` | 要是该解决方案里被描述的项目路径 |

**实测输出**：

```
✅ Removed X:\temp\zms-tool-drill\Demo\Demo.csproj from X:\temp\zms-tool-drill\Demo.slnx

```
已从解决方案中移除项目“Demo\Demo.csproj”。
```
```

不删项目文件本身；不清理空掉的虚拟文件夹；不进事务。

---

## 三、项目文件编辑

### 6. 编辑项目元数据 `edit_project_metadata`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `content` | `:string?` | **完整** csproj 新内容；**空串 = 只读**（第一段） |
| `cookie` | `:string?` | 第一段读到的 cookie；写的时候必填 |

**两段式**（这是它唯一的使用方式）：

| 调用 | 做什么 |
|---|---|
| `content` 与 `cookie` 都空 | **读**：给 csproj 原文 + 文件编码 + 一个 cookie。**不动文件** |
| `content` 非空 + `cookie` 非空 | **写**：先重读文件算指纹、和 cookie 比对，**一致才**过校验（XML 语法合法 + 根元素 `<Project>`）并写回（用**文件原编码**） |
| 其它组合 | 报错（不猜） |

**为什么要两段**：写出去的内容是"基于读到的现状"构造的 —— 这期间文件被别人改过，我们就会拿一份**自己没见过的现状**去覆盖，事后也不知道该还原成什么。cookie 就是"我读的是这一版"的凭据，写之前再确认一次。

**实测输出**（第一段，只读）：

````
# 编辑元数据
- 项目：C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
- 编码：utf-8（来自 .editorconfig 的 charset）
- cookie：`fd65a0506a7e66d3`

<Project Sdk="Microsoft.NET.Sdk">
...
````

**实测输出**（第二段，带 cookie 写入）：

```
# 编辑元数据
- 项目：...\Demo\Demo.csproj
- 编码：utf-8（来自 无 BOM 且是合法 UTF-8）
- 已写入（XML 语法检查 + 根元素 Project 检查通过）。
```

**实测输出**（期间被人改过 → 拒绝，一个字节都不动）：

```
Error: cookie 对不上了：你读的是 `fd65a0506a7e66d3`，现在文件是 `a1b2c3d4e5f60718` —— 这期间它被改过。重新读一次、基于最新内容再改。
```

- cookie 是**文件内容指纹**（换行归一后的 SHA256 前 16 位）：幂等、不存状态 —— 同一份内容永远算出同一串，所以它既能当"我读过这一版"的凭据，也能在写之前确认"还是那一版"。
- 不做 XML 片段合并 / 补丁；不校验 MSBuild 语义；不进事务。
- 内容与现状完全一致时不算失败，会明确说"内容没有变化，未写入。"
- 编码来源可能是 `.editorconfig 的 charset` / `BOM` / `无 BOM 且是合法 UTF-8` 三种。
- **它不创建新文件**：要一个全新的项目，先跑 `dotnet new`。

---

## 四、NuGet 包

### 7. 查看项目包引用 `list_project_packages`

参数：`csprojPath`:`:string`。

列出直接引用的顶级包、依赖传递进来的包、通过 `ProjectReference` 传递进来的顶级包。依赖图来自**真实还原**（MSBuild 的 `ReferencePath`；退化时读 `project.assets.json`）。

**实测输出**（未还原过的空项目）：

```
# 包引用
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj
- 依赖图来源：ReferencePath（还原产物缺失，可能不全）（MSBuild 没给出 ProjectAssetsFile）

## 顶级包（直接引用）
（无）

## 依赖传递包
（无）

## 项目引用而传递的顶级包
（无）
```

- 输出里会写依赖图来源；`⚠ 依赖图可能已过期` 表示 csproj / props 比还原产物新。
- 不还原、不修改项目、不解析版本冲突。

### 8. 搜索包 `search_packages`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `packName` | `:string` | 可为空串（那时本地分支列出本地全部包名） |
| `page` | `integer` | 默认 0；只对线上有效 |
| `local` | `boolean` | 默认 `true` |
| `web` | `boolean` | 默认 `false` |

**实测输出**（`local=true, web=false, packName=Newtonsoft`）：

```
# 本地查询到 1 个

newtonsoft.json
```

- 两个开关都关 → 报错（没有可查的来源）。
- **关掉 `local` 时不再读本地缓存**（哪怕只查线上）。
- 三个形态：只 `local` = 本地清单；只 `web` = `# 线上查询到 N 个（第 X 页，每页 N 条）` + 每行 `id 版本`；两个都开 = 只给线上那一页，本地也有的用 `[]` 标出。

### 9. 列出包版本 `list_package_versions`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `packName` | `:string` | 必须是**精确**包 id |
| `verRange` | `string?` | 默认空串 = 只列正式版；`*` = 所有正式版；`*-*` = 连预览版；其余走 NuGet 版本范围语法 |
| `local` | `boolean` | 默认 `true` |
| `web` | `boolean` | 默认 `false` |

**实测输出**（`verRange="[13.0.0, 14.0.0)"`，`local=true`）：

```
# Newtonsoft.Json（本地缓存）
> 范围 range [13.0.0, 14.0.0) (release versions only)：共列出 2 个版本

13.0.3
13.0.2
```

- **范围内符合条件的版本全部列出**，从不只给一个。
- 表头会说明漏掉多少预览版。
- 关掉 `local` 时不再读本地缓存；两个来源都关 → 报错。

### 10. 查询包元数据 `get_package_metadata`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `packName` | `:string` | 必须是精确包 id |
| `ver` | `string?` | 默认空串 = 本地缓存最高版本（本地空则走线上最新） |

**实测输出**（节选）：

````
# Newtonsoft.Json 13.0.3（本地缓存）

## nuspec
```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Newtonsoft.Json</id>
    <version>13.0.3</version>
    ...
  </metadata>
</package>
```

## readme
# Json.NET is a popular high-performance JSON framework for .NET
...
````

- 一旦本地命中就**不再**去线上补 readme。
- readme 缺失时给 `projectUrl` / `repositoryUrl` 让你自己去看。
- **线上取失败时不会说成"包不存在"**：会区分成 `本地缓存没有，线上取失败：<原因>`（网络故障 / 超时都算）。

### 11. 安装包 `install_packages`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `nugetPack` | `string[]` | 至少一项；每项 `名字` 或 `名字@版本`（按**最后一个** `@` 切分）；空项忽略 |
| `allowPrerelease` | `boolean` | 默认 `false` |

跑 `dotnet add package`（**不进事务**）。

**没有 `dryRun`**：`dotnet add package` 自己**现读** csproj 再改，不存在"基于过期快照覆盖"的问题 —— 所以它不需要两段式确认。它把"会做什么"和"做了什么"放在**一次调用**里。

**实测输出**（节选）：

```
# 以下直接引入包是漏洞的
（无）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
（无）

# 以下包因为漏洞被换成了别的版本
（无）

# 以下包被本次传递引入
Microsoft.CSharp
NETStandard.Library
（共 75 个）

---

以下用命令行改盘，**不进事务**（立即生效、不可回滚）：
- dotnet add "Demo.csproj" package Newtonsoft.Json --version 13.0.3

（预演，未真正执行。）
```

**选版本规则**：候选以「本地/线上版本里**最高版的大版本**」为下界收窄，**不含预览版**（除非 `allowPrerelease`）；在该范围内从最高往下挑**无漏洞的正式版**。**如果该范围内挑不出无漏洞的正式版（或漏洞索引不可用）→ 直接报错**并提示"要装就显式指定版本"，**不会**静默退回一个有漏洞的版本。

- `dryRun=true` 只决定并展示要跑的命令，末尾写"（预演，未真正执行。）"。
- `dryRun=false` 真跑，并在末尾附**落盘后漏洞核对**（`dotnet restore -p:NuGetAuditMode=all`，摘 `NU1901–NU1904`）。

### 12. 移除包 `remove_packages`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `nugetName` | `string[]` | 至少一项 |

跑 `dotnet remove package`，并在移除前后各建一次包图，所以能说明"顺带消失的传递包"。

**没有 `dryRun`**：同 `install_packages` —— 命令行现读现改，不存在"基于过期快照覆盖"。

**实测输出**：

```
# 本次移除包
Newtonsoft.Json

# 本次移除的依赖传递包
（无）

（预演：未执行任何命令；上面的「依赖传递包」由本地依赖图切图推出。）
```

不能移除"只是被传递引入"的包（得先处理它的上游）。

### 13. 列出文档注释符号 `list_doc_symbols`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `packName` | `:string` | 必须是精确包 id |
| `ver` | `string?` | 默认空串 = 本地缓存最高版本 |
| `tar` | `string?` | 默认空串 = 实现自选最合适 TFM；也可给 `lib/` 下目录名，如 `net8.0` |
| `path` | `string?` | 默认空串 = 列出（有文档注释的）所有类型；非空 = 点分隔对象路径 |
| `argsList` | `string?` | 逗号分隔参数类型，消歧重载；`string,int` 与 `(System.Int32,System.String)` 都吃 |
| `type` | `string?` | 默认空串 = 按 `path` 精度推断；也可显式给 `NTPFMED` |

从**本地 NuGet 缓存**的 XML 文档注释查符号（不做在线查找）。

**实测输出**（`path=Newtonsoft.Json.JsonConvert`，节选）：

```
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: X:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 67 | 生效 type: `PFME`（按精度推断）

- `P:Newtonsoft.Json.JsonConvert.DefaultSettings`
- `F:Newtonsoft.Json.JsonConvert.True`
- `Newtonsoft.Json.JsonConvert.ToString` 有 25 个重载：
  - `(System.DateTime)`
  ...
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)`
```

- 粒度**最细到成员，不出现行号**。
- `type` 里带 `D` 时输出**原始 XML 片段**。
- 表头永远写明本次生效的 kind 以及它是"按精度推断"还是"显式指定"。

---

## 五、符号查询

### 14. 符号 `symbols`

**一个工具、两层开关**：先选**列哪些符号**（筛选），再选**每个符号列出什么**（信息开关）。

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件（**必填**） |
| `path` | `string?` | 空 = 列出符号；`Ns.Type` = 只列这个类型的成员；`Ns.Type.Member(...)` = 直接读这一个成员（给签名 + 文件 + 行号 + 源码） |
| `type` | `string?` | 字母取自 `NCSITPFEMD`：`N` 命名空间、`C` class、`S` struct（含 enum）、`I` 接口、`T` = `C`\|`S`\|`I`、`P` 属性、`F` 字段、`E` 事件、`M` 方法、`D` 委托；空 = 全部 |
| `modifier` | `string?` | 修饰符过滤，见下 |
| `argsList` | `string?` | 参数类型过滤（消歧重载），见下 |
| `nameFilter` | `string?` | 完全限定名的子串过滤，见下 |
| `documentation` | `boolean` | 默认 `false`；`true` = **把每个符号的 XML 文档注释也列出来** |
| `attributes` | `boolean` | 默认 `false`；`true` = **把每个符号的特性（attribute）也列出来** |
| `references` | `boolean` | 默认 `false`；`true` = **把"谁引用了这个符号"也列出来**（给的是符号，不是位置 —— 见下） |
| `implementation` | `boolean` | 默认 `false`；`true` = **把每个符号的实现体也列出来** |

**前四个是筛选（列哪些），后四个是信息开关（每条列出什么）。** 两层正交：先圈范围，再决定每条带多少内容。四个开关可任意组合 —— `path='Ns.Type'&implementation=true&references=true&attributes=true` 就是"这个类型的成员，连同特性、实现体、谁在用它，一起给我"。

**三个筛选参数各自怎么工作**：

- **`modifier`** —— 逗号分隔，**每一个都要满足**（是 AND，不是 OR）。写 `'public,static'` 就是"既 public 又 static"；想表达"public 或 static"得查两次。认的写法就是源码里那些词：`public` / `internal` / `protected` / `private` / `static` / `const` / `abstract` / `readonly` / `virtual` / `override`。
- **`argsList`** —— 逗号分隔的**参数类型**，只对**方法**有意义，而且是**精确匹配**：给了 `'string,int'`，只有参数表**恰好**是 `(string, int)` 的方法留下；`(string)`、`(string, int, bool)` 都不留。它用来**消歧重载**，不是"包含这些参数"。类型写法两边都会规范化，`string[]`（数组）、`List<int>`（泛型，简名/全名都认）、`string?`（可空标注不算签名的一部分）、`int` / `System.Int32` 都对得上。
- **`nameFilter`** —— 对**完全限定名**（`命名空间.类型.成员`）做**大小写不敏感**的**子串**匹配。所以 `'Log'` 会同时命中 `Logging`、`Logger`、`MyLog` —— 它是"缩小范围"，不是"精确定位"；要精确点到某一个用 `path`。

**实测输出 A**（默认：只有签名与位置，`type=M&nameFilter=MatchesTypeKind`）：

```
# C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
- TFM: `net10.0` | 符号: 1 | 过滤: type='M' modifier='' args='' nameFilter='MatchesTypeKind' | 列出: （只有签名与位置）

## ZMS.MCP.Csharp.Roslyn
  - `private static bool MatchesTypeKind(INamedTypeSymbol type, SymbolKinds kinds)` (method) [SymbolQuery] — Roslyn/SymbolQuery.cs:354
```

> 表头那行 `列出: （只有签名与位置）` 会随开关变化，写成 `文档 + 特性`、`被引用`、`实现` 这样的组合 —— 一眼能看出这次列出的是哪些信息。

**实测输出 B**（`path=类型&documentation=true&attributes=true`，节选）：

```
# ZMS.MCP.Csharp.Draft.SymbolBaseline  (class)
- TFM: `net10.0` | 列出: 文档 + 特性

- `ZMS.MCP.Csharp.Draft.SymbolBaseline.Key(Microsoft.CodeAnalysis.ISymbol)` — Draft/SymbolBaseline.cs:81
  文档：
    <member name="M:ZMS.MCP.Csharp.Draft.SymbolBaseline.Key(Microsoft.CodeAnalysis.ISymbol)">
    <summary>符号身份（完全限定名，含参数类型与泛型元数）。</summary>
    </member>
  特性：（无）
- `ZMS.MCP.Csharp.Draft.SymbolBaseline.CollectNamespace(...)` — Draft/SymbolBaseline.cs:132
  文档：（无）
  特性：（无）
```

- 文档注释是**原文**（含 `<member>` 外壳）；没有就打 `（无）`。特性同理，多个特性用空格分隔。

**实测输出 C**（`path=类型&references=true`，节选）：

```
# ZMS.MCP.Csharp.Draft.SymbolBaseline  (class)
- TFM: `net10.0` | 列出: 被引用

- `ZMS.MCP.Csharp.Draft.SymbolBaseline.Capture(Microsoft.CodeAnalysis.Compilation)` — Draft/SymbolBaseline.cs:42
  被这些引用：
    - `ZMS.MCP.Csharp.Draft.ConflictService.AcceptCurrent(string, string, IReadOnlyDictionary<string, string>?, string?)` — 1 次
    - `ZMS.MCP.Csharp.Draft.DraftService.Stage(string, string, string, string?)` — 1 次
    - `ZMS.MCP.Csharp.Draft.DraftService.Precheck(DraftStore, string, LoadedProject, DraftRecord, Plan, DiagnosticSnapshot, DiagnosticSnapshot)` — 1 次
- `ZMS.MCP.Csharp.Draft.SymbolBaseline.HashText(string)` — Draft/SymbolBaseline.cs:114
  被这些引用：（无）
```

- **引用给的是"哪个类的哪个成员"，不给文件位置。** 要位置就**再查一次那个符号**（`path='Ns.Type.Member(...)'` 会一并给出文件与行号）—— 位置是另一个问题，不塞进引用列表里。
- 声明点**不算引用**；泛型 / 扩展方法 / 同名重载用 `OriginalDefinition` 判等，不会混。
- 顶层语句里的调用也**算**。
- **一次最多给 20 个符号做引用扫描**（每查一个都要遍历全部语法树）：超了会报错并让你收窄范围。

**实测输出 D**（`path=类型&implementation=true`，节选）：

```
- `ZMS.MCP.Csharp.Draft.DraftEdit.IsDelete` — Draft/DraftStore.cs:20
  实现：
        public bool IsDelete => RequestedContent == null;
```

- 给的是**整个成员声明**的源码（去掉行号前缀）。没有源码（引用程序集 / 编译器生成）会明确说 `（没有源码声明：来自引用程序集或编译器生成）`。
- 位置参数型 record 属性这类节点，声明会**上溯到整个类型声明** —— 所以它们会各自打印一遍同一份类型源码（信息不丢，只是啰嗦）。

**实测输出 E**（`path` 指到**成员**时，四个开关无效）：

````
## ZMS.MCP.Csharp.Draft.SymbolBaseline.Key(Microsoft.CodeAnalysis.ISymbol)

- Kind: `Method`
- Declaring type: `ZMS.MCP.Csharp.Draft.SymbolBaseline`

### Documentation
```xml
<member name="M:ZMS.MCP.Csharp.Draft.SymbolBaseline.Key(Microsoft.CodeAnalysis.ISymbol)">
    <summary>符号身份（完全限定名，含参数类型与泛型元数）。</summary>
</member>
```
- File: `C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\Draft\SymbolBaseline.cs`
- Lines: 81-84

```csharp
    public static string Key(ISymbol symbol)
    {
        return symbol.ToDisplayString(IdentityFormat);
    }
```
````

- `path` 指到成员时走的是"读这一个成员"，**本来就把源码和文档给你了**，所以四个开关在这里没有意义。
- 末尾追加 `.get` / `.set` / `.add` / `.remove` 可以精确匹配一个访问器。

**平时要注意的**：

- **列出的就是可以抄的 `memberName`**：无空格参数表、参数类型用**源码里那个写法**（`string` 不写成 `System.String`）。
- **分部类**会把全部声明位置列在同一行。
- 返回值里的类型名就是其它工具的 `typePath`。
- **纯层级外壳不列**：只有子命名空间、自己没有任何类型定义的命名空间不会各出一条，否则它们的位置串会等于整棵子树、看起来跟最深的那条完全重复。

---

## 六、改名（走拟定）

### 15. 重命名符号 `rename_symbol`

**只有语义层做得到的那件事。** 改名一个符号，把**引用它的每一处**一起改。

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | `track_project` 返回的追踪 cookie（**不要给 csprojPath**） |
| `memberPath` | `:string` | 要改名的符号，写法同 `symbols` |
| `newName` | `:string` | 新名字（不含类型前缀，就是个标识符） |

**它不自己写盘 —— 它往拟定里加一条改名。** 落盘统一走 `confirm_draft`，和 `stage_draft` 是同一套流程：同一次拟定里可以既有"改成员实现"又有"改名"，预检时一起编译、一起报诊断，落盘时一起写。

**拟定阶段只记"改哪个符号、改成什么"，一个字节都不碰。** 真正"会动哪几处"是在**预检**（`confirm_draft` 不带 `applyCookie`）时算的 —— 那时候的编译才是最新的，而拟定本身按设计不存文件路径。

**实测输出**（预检里"改名的改动点"那一节）：

```
## 改名的改动点（声明 + 引用；不是文本替换）
- Demo.Calculator.AddUp → Sum
  - X:\temp\zms-rename\src\Client.cs：8:27 引用、8:58 引用
  - X:\temp\zms-rename\src\Calculator.cs：5:5 声明
```

**这一节是它存在的理由**：文本替换会把同名但**不是它**的东西一起改掉 —— 另一个类型里的同名成员、字符串里和注释里的同名文字；语义改名只动这个符号的**声明与引用**。坐标按**改名之前**的原文算（改完行号就没意义了）。

**它管不到的地方**（得自己核对）：字符串里拼出来的名字、注释、非 C# 引用（XAML / JSON / 配置）、**别的项目**对它的引用、以及 `#if` 未启用的分支 —— 那些地方只算了**当前编译**里看得见的部分。

它和 `stage_draft` 的差别只在"改什么"：一个改实现体、一个改名字，但**记进拟定、预检、落盘、作废许可**这套行为完全一样。所以：

- 一条改名也要**先 `track_project`** 拿 cookie。
- 拟定里同时有改名和多处成员改动时，**取消/放弃也只有一个口子** —— `confirm_draft` 落盘，或者重新 `track_project` 清掉。

**范围**（这条很重要，实机踩过）：拟定与落盘都是**单项目**装配的 —— 追踪哪个 csproj，编译里就只有那个项目的源文件。所以改名**只改得到本项目里看得见的引用**：

- **引用本项目的其它项目，它看不见。** `rename_symbol` 会扫同层与上层的兄弟 csproj，把引用了本项目的那几个**列出来**警告你去补 —— 但它自己改不到那边。
- `#if` 未启用的分支、字符串里拼出来的名字、注释、非 C# 引用（XAML / JSON / 配置）同样够不着。

预检里的"诊断对比"也**只覆盖本项目**：别的项目因为这个改名编不过，它看不出来。

---

## 七、拟定流程

一条完整的写代码流程：`track_project` 建立追踪 → `stage_draft` / `rename_symbol` 累积改动 → `confirm_draft` 预检（编译并汇报诊断）→ 带落盘 cookie 再 `confirm_draft` 落盘。

这一节的几条规则是整条链的地基：

1. **拟定全程在内存里做，不碰磁盘。** `track_project` 之后项目加载**一次**（拿到一份编译），之后每一次 `stage_draft` / `rename_symbol` 都只是在这份编译上做**内存替换** —— Roslyn 的编译对象是不可变的，改一次得到新的一份，没动过的语法树共享。磁盘上的 `.cs` 从头到尾没被读过第二遍，更没被写过。
2. **每加一条改动就编译一次，当场回报"这条改动引入了什么诊断"。** 一条一条来，错在哪一条立刻就知道，不用攒一堆再回头找。
3. **报的是"新增 / 消失的诊断"，不是"有没有诊断"。** 因为拟定是**串行**的，中间态本来就可能不完整（先加接口成员、再加实现，第一步天然有错）—— 报绝对的对错会一直吵，报"你这一步让什么变多了 / 变少了"才有信息量。
4. **落盘那一刻才重新看磁盘。** `confirm_draft` 落盘时重新求值、重新读 `.cs`、重新编译。所以它能发现"拟定期间磁盘上有人动过手"，动过就拒绝落盘 —— 不会把别人改的东西盖掉。
5. **落盘之后，基线按落盘后的现状整体重算。**
6. **拟定一变，落盘许可立刻作废**，必须重新预检。

### 追踪基线算的是语义，不是文本

**语义没变，hash 就必须一样。** 这是硬要求 —— 否则"只把 `List<int>` 写成 `System.Collections.Generic.List<int>`"会被当成"别人改过"，白白拦你一次。

所以基线不是对源码文本取哈希，而是先把声明里**每个名字都解析成符号**，再哈希：

| 这几种写法 | 算出来的 hash |
|---|---|
| `List<int>` / `System.Collections.Generic.List<int>` / `using L = …; L<int>` | **一样** |
| `int` / `System.Int32` | **一样** |
| 注释、空白、换行 | **不参与** |
| `List<int>` / `List<string>` | **不一样** |
| `Dictionary<string,int>` / `Dictionary<int,string>` | **不一样** |
| `a + b` / `a - b` | **不一样** |

**归不到一起的那一类**（只能保守地误报，绝不会漏报）：`a + 0` vs `a`、`for` vs `while`、`x += 1` vs `x = x + 1`、无依赖的两行交换 —— 判定"两段代码是不是同一个程序"没有有限规则（一般不可判定）。

### 16. 追踪项目 `track_project`

**一个工具、两种用途**，也是 tracking cookie 的唯一来源。

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `cookie` | `string?` | 不带 = 开始追踪；带（传回之前拿到的）= **解除追踪**（清掉追踪记录与全部拟定） |

**实测输出**（不带 cookie，节选）：

```
# 追踪已开始

- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj

- 追踪 cookie：`60a48496-8397-4c5c-ac54-615c202e720c`

  - 用途一：传给 stage_draft / confirm_draft 做拟定编写与落盘（这两个工具只认 cookie，不要 csprojPath）

  - 用途二：传回 track_project 的 cookie 参数，即可解除追踪（清掉追踪记录与全部拟定）

- 已保存 2 个符号的 hash（只存 hash，不存文件快照；追踪只回答「哪些符号变了」，不会把文件还原回去）
```

**实测输出**（带 cookie = 解除）：

```
# 取消追踪
- 项目：...
- 已删除拟定：0 条
- 追踪快照已删除；要再写这个项目，请重新 track_project。
```

### 17. 拟定改动 `stage_draft`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | `track_project` 返回的 cookie（**不要给 csprojPath**） |
| `typePath` | `:string` | 指向**类** → 改它的成员；指向**命名空间** → 在那个命名空间下**加一个类**（见下） |
| `memberName` | `:string` | 成员名；跟已有重载撞名时带参数表消歧。**`typePath` 指向命名空间时忽略它** |
| `content` | `:string` | 要写进去的代码 —— 是**成员**还是**类声明**，由 `typePath` 指向什么决定 |
| `before` | `string?` | 插入位置参照（加成员时用） |

**`typePath` 指向什么，就加什么**：

| `typePath` 指向 | `memberName` | `content` 是什么 | 含义 |
|---|---|---|---|
| **一个类**（已存在） | 给名字 | 该成员的完整代码 | 加/改**这个类的一个成员** |
| **一个命名空间** | 忽略 | **该类的完整声明**（含修饰符、特性、基类、`partial`） | **在那个命名空间下加一个类** |

**加成员时 `memberName` 是"新成员的名字"，不是一条已有路径** —— 成员还不存在，所以工具不要求它"先能被定位到"，也不要求你先把参数表写全（只有跟已有重载撞名时才需要带参数表消歧）。

**`content` 就是完整的类型声明**（新建类时），工具**不会**再代你套一层 `public class` —— 所以 `[McpServerToolType] public static partial class Tools { … }` 这种声明能原样写进去。

**整类替换**走另一条路：`typePath` 指向已存在的类、`memberName` 给**空串**、`content` 给替换后的**完整类体**。

**实测输出**（加一个成员 —— 注意这次的 `memberName` 用的是**裸名**）：

```
# 拟定已更新
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj
- 符号：Demo.Class1.Sub
- 本次：写入（added）
- 拟定条数：1

## 这条改动带来的诊断变化
- 新增：0 条（错误 0 / 警告 0）
- 消失：0 条（错误 0 / 警告 0）

- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```

**实测输出**（第二条引用了不存在的成员 → **当场**报出编译错误，不用等预检）：

```
- 符号：Demo.Class1.Bad
- 本次：写入（added）
- 拟定条数：2

## 这条改动带来的诊断变化
- 新增：1 条（错误 1 / 警告 0）
  - CS0103 当前上下文中不存在名称“NotExist”（...\Class1.cs:9）
- 消失：0 条（错误 0 / 警告 0）
```

**实测输出**（带参数表却匹配不上 → 报错，并列出名字对得上的那些）：

```
Error: 定位不到符号：Demo.Class1.Add(int,long)；Class1 里叫 Add 的有这些：
  - public static int Add(int a, int b) —— 参数表写错了？要是本来就想新增，就别带参数表。
```

> 带了参数表的 `memberName` 是一条**精确路径**（"我要的就是这个签名"），匹配不上就报错 —— 当成新增会写出**第二份同名成员**（`CS0111`），而那要等预检诊断才看得出来。**裸名字**才是"新成员的名字"，匹配不上就是新增。

**实测输出**（`typePath` 指向命名空间 → 加一个类）：

```
- 符号：Demo.Widget
- 本次：写入（新建类型）
- 拟定条数：3
```

- 拟定改动会**累积**（重复调用叠在同一个拟定上），并**作废已有的落盘许可**。
- 给**已存在的类**加成员时，如果那个类不是 `partial`，会自动补上；补的位置是**所有修饰符之后**（`public sealed partial class` 合法，不会拼出 `public partial sealed class`）。
- 如果项目没还原过（MSBuild 求值失败），输出里会有 `⚠ 简化模式` 提示：引用集与宏可能不全，**诊断可能夹带假错误**。

### 18. 查看拟定 `list_draft`

参数：`csprojPath`:`:string`。

**实测输出**：

```
# 拟定
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj
- 起始时间：2026-09-29T23:41:02.1360284+00:00
- 条数：3

- [1] 增加 Demo.Class1.Sub
- [2] 增加 Demo.Class1.Bad
- [3] 增加 Demo
```

这是 agent 重启后**捡回拟定**的方式。

（`[3]` 那条写的是 `Demo` 而不是 `Demo.Widget`：它就是 `typePath` 原样 —— 类是新建的，当时还没有符号全名。）

### 19. 解决冲突 `select_draft`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `memberPath` | `:string` | 目标符号 |
| `selectCookie` | `:string` | 符号工具在该符号有未解决冲突时返回的 selectCookie |
| `choice` | `:string` | `keep`（保留这个符号的拟定）或 `drop`（丢掉这个符号的拟定） |

**实测输出**（没有对应拟定时的拒绝形态）：

```
Error: 拟定里没有这个符号：Demo.Class1（keep 需要该符号有拟定；没有拟定就用 drop 接受现状）。
```

### 20. 确认拟定 `confirm_draft`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | `track_project` 返回的 cookie（**不要给 csprojPath**） |
| `applyCookie` | `:string` | 空串 = **只做预检**；填预检返回的落盘 cookie = **落盘** |

**实测输出 A**（预检，`applyCookie` 为空）：

```
# 拟定确认
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj

## 变更分类
### 增加（3）
- Demo.Class1.Sub
- Demo.Class1.Bad
- Demo

## 本次改动涉及
- ...\Class1.cs（Demo.Class1.Sub、Demo.Class1.Bad）
- ...\Widget.cs（Demo.Widget）

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：1 条（错误 1 / 警告 0）
  - CS0103 当前上下文中不存在名称“NotExist”（...\Class1.cs:9）
- 消失：0 条（错误 0 / 警告 0）

## 预检查
- ✅ 无占用、无冲突

- 落盘 cookie：`e0486ab2-326f-4e21-ac78-c092b62906c2`（传回来才落盘；每次预检换新，旧 cookie 立刻失效）
```

> **`诊断对比` 就是它自带的编译验证** —— 落盘前就知道这次改动会不会引入错误/警告，不用自己再跑一遍构建。

**实测输出 B**（落盘，带上一步的落盘 cookie）：

```
（同上的分类 / 涉及 / 诊断对比）

## 已落盘
- ...\Class1.cs
- ...\Widget.cs
- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）
- 格式化：已对本次改动的文件跑 dotnet format
- format 额外改动：无
- 追踪继续，基线已按落盘后的现状整体重算。
```

落盘时它自己会做的事：**按原编码写回**、**对改动的文件跑 `dotnet format`**、**把追踪基线按落盘后的现状整体重算**。

- 没有未完成的拟定时会直接回 `没有未完成的拟定。`
- 预检查不通过（文件被占用 / 有未解决冲突）会拦下来，不落盘。

---

## 附：与 v3 的差异清单

| 位置 | v3 | v4 |
|---|---|---|
| `symbols` 的 `type` | 字母 `D` = "只列带 XML 文档注释的"（一个**筛选**） | `D` = **委托**；"文档注释"变成信息开关 |
| `symbols` 的 `read` | `false` 列成员 / `true` 看类型结构（成员体隐藏） | **去掉**了：统一成"列成员 + 四个开关"（旧 `read=true` ≈ 四个开关全关） |
| `symbols` 的成员寻址 | 与 `stage_draft` 是两套约定（字段要裸名、参数写作 `System.String`…） | 收敛成**一套解析器**，列表里印出的写法可直接抄 |
| `symbols` 的 `argsList` | 数组 / 泛型 / 可空标注匹配不上 | 两边都规范化，`string[]`、`List<int>`、`string?` 都认 |
| `symbols` 的类型匹配 | 委托不参与类型过滤 | `D` 参与（`TypeKind.Delegate`） |
| 引用查询 | **没有** | `symbols` 的 `references` 开关（只给符号，不给位置） |
| 改名 | **没有** | `rename_symbol`，**走拟定**、不自己写盘 |
| 新建类型的壳 | 写死 `public class {名}` | `content` 就是完整声明，工具不代套壳 |
| 补 `partial` | 插在**访问修饰符**之后（`public static class` → `public partial static class`，`CS0267`） | 插在**所有修饰符之后** |
| `stage_draft` 的路径 | `memberName` 要"含参数表以消歧" | `typePath` 指向什么就加什么；加成员用**裸名** |
| 匹配不上时 | 当"新增"照写（写出第二份同名成员，`CS0119`/`CS0111` 只在预检才看得见） | **报错**，并列出名字对得上的候选 |
| 删成员 | 删的是字段的**声明符**，留下 `private static readonly T;`（`CS1519`） | 上溯到**整个成员声明**，清干净 |
| 每条改动的反馈 | 只有**语法**检查，编译要等 `confirm_draft` | 每加一条就**在内存编译**，报**新增/消失的诊断** |
| 项目加载 | 每次 `stage_draft` 都重新读盘 + 装配编译 | **会话级编译缓存**（源码时间戳变了才重装配） |
| 追踪基线 | 声明**文本** hash（写法不同就误报"被改过"） | **语义** hash（`List<int>` 与全名与别名同一个值） |
| MSBuild 求值缓存 | 指纹含"最新写入时间"，改任何一行都作废整个求值 | 指纹只认**源文件路径集合**，改内容不重跑求值 |
| `edit_project_metadata` | 文档没说替代路径；有 `dryRun` 布尔 | 明确写"**不创建新文件**，先 `dotnet new`"；改成**两段式**：先读拿内容指纹，写前重读比对再写 |
| `install_packages` / `remove_packages` | 有 `dryRun` 布尔（两阶段靠重传参数） | **去掉 `dryRun`**：命令行现读现改，不存在"基于过期快照覆盖"，不需要确认步骤 |
| `confirm_draft` | `apply` + `applyCookie` 两个维度表达同一件事 | 只留 `applyCookie`：**有就是落盘、没有就是预检**，没有第三种状态 |
| **工作空间边界** | 没有（任何路径都照收） | **每次调用**校验所有路径参数，越界在**工具执行前**拒绝，并说清"你给的是哪、边界在哪、怎么改" |

## 附：性能与代价

| 场景 | 代价 |
|---|---|
| `track_project` / 第一次 `stage_draft` | 首次要跑 MSBuild 求值（秒级），之后命中缓存 |
| 每次 `stage_draft` | 在内存里**重算全部拟定 + 跑两遍编译诊断**（几百毫秒级）；换来"错在哪一条当场知道" |
| `symbols` 开着 `references` | 每查**一个**符号都要遍历全部语法树 —— 所以一次最多 20 个符号，超了报错 |
| `symbols` 开着 `implementation` | 只是读源码，很便宜 |
| `rename_symbol` | 要遍历语法树找引用（和 `references` 同量级） |
| `confirm_draft` 落盘 | 重新求值 + 重读文件 + 编译 + 跑 `dotnet format`（数秒） |
