# ZMS.MCP.Csharp 工具手册 v3

> ⚠ **已被 [`ZMS.MCP.Csharp.v4.md`](ZMS.MCP.Csharp.v4.md) 取代** —— 工具签名有几处变了（`symbols` 多了四个"要不要列出"的信息开关、
> 去掉了 `read`；`edit_project_metadata` 改成两段式；三个 `dryRun` 被去掉；`confirm_draft` 去掉了 `apply`），
> 而且 v4 起所有调用都受**工作空间边界**约束。这份留作历史对照。

这份手册以**实机调用结果**写成：每个工具的参数取自运行时 schema，返回值示例是实际调用回来的原文（不是从代码推断的）。共 **19 个工具**。

> 与 v1 / v2 的主要差别：`view_project` 与 `view_solution_tree` 已合并为 `view_project_or_solution`；`list_types` / `list_members` / `get_member` 已合并为单个 `symbols`；`scan_projects` 去掉了 `kinds` 参数。

## 约定

- 参数类型用 C# 可空语法：`:string` = 必填；`:string?` = 可空（"没给"与"给了空串"等价）。
- `integer` / `boolean` 不可为 null，但通常有默认值。
- **所有工具的错误都是同一种文本形态**：`Error: <异常消息>`。需要外部命令的工具（`dotnet add/remove/sln ...`）在命令非 0 退出时把命令行输出整段带回。
- `csprojPath` 统一解析：绝对路径（存在即用）；否则当成**项目名**，从当前目录向上找解决方案、在其中找唯一同名项目，重名时要求给完整相对路径。
- 需要外部命令的写入工具（解决方案编辑、装包/删包、迁移）**不进任何事务**：立刻改盘、不可回滚。

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

```
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
```

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
| `content` | `:string` | **完整** csproj 内容；先过两道校验：XML 语法合法；根元素是 `<Project>`。不通过一个字节都不写 |
| `dryRun` | `boolean` | 默认 `false`；`true` 只校验并展示，不写盘 |

整文件替换 csproj 内容，写回用**文件原编码**。

**实测输出**（`dryRun=true`）：

```
# 编辑元数据
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj
- 编码：utf-8（来自 无 BOM 且是合法 UTF-8）
- **预演，未写入**。

```xml
<Project Sdk="Microsoft.NET.Sdk">
...
</Project>
```
```

- 不做 XML 片段合并 / 补丁；不校验 MSBuild 语义；不进事务。
- 内容与现状完全一致时不算失败，会明确说"内容没有变化，未写入。"
- 编码来源可能是 `.editorconfig 的 charset` / `BOM` / `无 BOM 且是合法 UTF-8` 三种。

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

```
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
```

- 一旦本地命中就**不再**去线上补 readme。
- readme 缺失时给 `projectUrl` / `repositoryUrl` 让你自己去看。
- **线上取失败时不会说成"包不存在"**：会区分成 `本地缓存没有，线上取失败：<原因>`（网络故障 / 超时都算）。

### 11. 安装包 `install_packages`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `nugetPack` | `string[]` | 至少一项；每项 `名字` 或 `名字@版本`（按**最后一个** `@` 切分）；空项忽略 |
| `dryRun` | `boolean` | 默认 `false` |
| `allowPrerelease` | `boolean` | 默认 `false` |

跑 `dotnet add package`（**不进事务**）。

**实测输出**（`dryRun=true`，节选）：

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
| `dryRun` | `boolean` | 默认 `false` |

跑 `dotnet remove package`，并在移除前后各建一次包图，所以能说明"顺带消失的传递包"。

**实测输出**（`dryRun=true`）：

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

**一个工具、两种模式**：列符号 / 读符号。

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件（**必填**） |
| `path` | `string?` | 空 = 列出本项目源码声明的符号（不含引用程序集）；非空 = 读该符号 |
| `type` | `string?` | 字母取自 `NCSITPFEMD`：`N` 命名空间、`C` class、`S` struct（**含 enum**）、`I` 接口、`T` = `C`\|`S`\|`I`、`P` 属性、`F` 字段、`E` 事件、`M` 方法、`D` 只列带 XML 文档注释的 |
| `modifier` | `string?` | 逗号分隔修饰符过滤 |
| `argsList` | `string?` | 逗号分隔参数类型，消歧重载 |
| `nameFilter` | `string?` | 对完全限定名做大小写不敏感的子串过滤 |

**实测输出 A**（列命名空间，`type=N`）：

```
# X:\temp\zms-tool-drill\Demo\Demo.csproj
- TFM: `net10.0` | 符号: 8 | 过滤: type='N' modifier='' args='' nameFilter=''

## ZMS.MCP
- `MCP` (namespace) — ../../Global.cs:10, Draft/ConflictService.cs:4, ...

## ZMS.MCP.Csharp
- `Csharp` (namespace) — Draft/ConflictService.cs:4, ...
```

> **纯层级外壳不列**：只有子命名空间、自己没有任何类型定义的命名空间不会各出一条（上面就没有 `ZMS`），否则它们的位置串会等于整棵子树、看起来跟最深的那条完全重复。

**实测输出 B**（列方法，`type=M`）：

```
# X:\temp\zms-tool-drill\Demo\Demo.csproj
- TFM: `net10.0` | 符号: 2 | 过滤: type='M' modifier='' args='' nameFilter=''

## Demo
  - `public int Add(int a, int b)` (method) [Class1] — Class1.cs:5
  - `public int Sub(int a, int b)` (method) [Class1] — Class1.cs:10
```

- **分部类**会把全部声明位置列在同一行。
- 返回值里的类型名就是其它工具的 `typePath`。

---

## 六、拟定流程（cookie）

一条完整的写代码流程：`track_project` 建立追踪 → `stage_draft` 累积改动 → `confirm_draft` 预检（**会编译并汇报诊断**）→ 带落盘 cookie 再 `confirm_draft` 落盘。

### 15. 追踪项目 `track_project`

**一个工具、两种用途**，也是 tracking cookie 的唯一来源。

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `csprojPath` | `:string` | 要能解析到项目文件 |
| `cookie` | `string?` | 不带 = 开始追踪；带（传回之前拿到的）= **解除追踪**（清掉追踪记录与全部拟定） |

**实测输出**（不带 cookie）：

```
# 追踪已开始
- 项目：C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
- 追踪 cookie：`d0d82aa5-d70b-473c-b0c2-2ae40fb0b534`
  - 用途一：传给 stage_draft / confirm_draft 做拟定编写与落盘（这两个工具只认 cookie，不要 csprojPath）
  - 用途二：传回 track_project 的 cookie 参数，即可解除追踪（清掉追踪记录与全部拟定）
- 已保存 819 个符号的 hash（只存 hash，不存文件快照；追踪只回答「哪些符号变了」，不会把文件还原回去）

## 现在已经有这些拟定写
（无）

## 有这些未追踪更改
（无 —— 已保存的 hash 就是当前现状）
```

**实测输出**（带 cookie = 解除）：

```
# 取消追踪
- 项目：...
- 已删除拟定：0 条
- 追踪快照已删除；要再写这个项目，请重新 track_project。
```

### 16. 拟定改动 `stage_draft`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | `track_project` 返回的 cookie（**不要给 csprojPath**） |
| `typePath` | `:string` | 目标类型，如 `Demo.Class1` |
| `memberName` | `:string` | 成员，含参数表以消歧，如 `Mul(int,int)` |
| `content` | `:string` | 成员的完整代码 |
| `before` | `string?` | 插入位置参照（加成员时用） |

拟定的改动会**累积**（重复调用叠在同一个拟定上）。

**实测输出**：

```
# 拟定已更新
- 项目：C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
- 符号：ZMS.MCP.Csharp.Roslyn.SymbolQuery.MatchesTypeKind(Microsoft.CodeAnalysis.INamedTypeSymbol, ZMS.MCP.Csharp.Roslyn.SymbolKinds)
- 本次：写入（replaced）
- 拟定条数：1
- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```

- `本次` 可能是 `added` / `replaced` / 删除等。
- **改动拟定会作废已有的落盘许可** —— 必须重新 `confirm_draft` 预检。

### 17. 查看拟定 `list_draft`

参数：`csprojPath`:`:string`。

**实测输出**：

```
# 拟定
- 项目：X:\temp\zms-tool-drill\Demo\Demo.csproj
- 起始时间：2026-09-25T20:05:36.6521501+00:00
- 条数：1

- [1] 增加 Demo.Class1.Mul(int,int)
```

这是 agent 重启后**捡回拟定**的方式。

### 18. 解决冲突 `select_draft`

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

### 19. 确认拟定 `confirm_draft`

参数：

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | `track_project` 返回的 cookie（**不要给 csprojPath**） |
| `applyCookie` | `:string` | 空串 = **只做预检**；填预检返回的落盘 cookie = **落盘** |

**实测输出 A**（预检，`applyCookie` 为空）：

```
# 拟定确认
- 项目：C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj

## 变更分类
### 修改（1）
- ZMS.MCP.Csharp.Roslyn.SymbolQuery.MatchesTypeKind(INamedTypeSymbol,SymbolKinds)

## 本次改动涉及
- C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\Roslyn\SymbolQuery.cs（ZMS.MCP.Csharp.Roslyn.SymbolQuery.MatchesTypeKind(...)）

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：0 条（错误 0 / 警告 0）
- 消失：0 条（错误 0 / 警告 0）

## 预检查
- ✅ 无占用、无冲突

- 落盘 cookie：`6b0f9a29-452e-41a6-a9a5-5b3aa185e470`（传回来才落盘；每次预检换新，旧 cookie 立刻失效）
```

> **`诊断对比` 就是它自带的编译验证** —— 落盘前就知道这次改动会不会引入错误/警告，不用自己再跑一遍构建。

**实测输出 B**（落盘，带上一步的落盘 cookie）：

```
（同上的分类 / 涉及 / 诊断对比）

## 已落盘
- C:\Users\16229\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\Roslyn\SymbolQuery.cs
- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）
- 格式化：已对本次改动的文件跑 dotnet format
- format 额外改动：无
- 追踪继续，基线已按落盘后的现状整体重算。
```

落盘时它自己会做的事：**按原编码写回**、**对改动的文件跑 `dotnet format`**、**把追踪基线按落盘后的现状整体重算**。

- 没有未完成的拟定时会直接回 `没有未完成的拟定。`
- 预检查不通过（文件被占用 / 有未解决冲突）会拦下来，不落盘。
