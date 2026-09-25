# ZMS.MCP.Csharp

本手册只看 `src/ZMS.MCP.Csharp` 的**实现代码**写成。每个工具给出：参数（含执行前的合法性限制）、工具描述与配套用法、以及**从代码推断出来的返回值示例**（不同情况各一份）。示例不是实机抄回来的，是按实现里拼字符串的代码推的。

约定：

- 参数类型用 **C# 的可空语法**：`:string` = 必须传、且必须是个有效值，否则这个工具没法工作；`:string?` = 可空、默认 null（这类参数在实现里"没给"和"给了空串"是一个意思）。
- `integer` / `boolean` 参数不带 `?`（C# 里它们不可为 null），但通常有默认值，默认值写在参数下面。
- 「参数限制」写的是**执行前的校验**：不满足就直接报错、什么都还没发生。
- 参数取值不同导致**进入执行阶段后**行为不同，写在工具描述里。
- 所有工具的异常都由一层统一的 try/catch 转成文本：`Error: <异常消息>`。**例外**：`add_project_to_solution`、`remove_project_from_solution` 是 async 且没走这层包装，参数/环境错误会变成协议级错误 `An error occurred invoking '<工具名>'`。
- `csprojPath` 这类参数统一走同一个解析：绝对路径（存在即用）；否则当成**项目名**，从当前目录向上找解决方案、在其中找唯一同名项目。重名时要求给完整路径。

## 扫描与查看

### 扫描项目 `scan_projects`

- `path`:string
  - 参数限制：必须是**存在的文件夹**，否则报错。
- `depth`:integer
  - 默认 4。
- `kinds`:string?
  - 默认空串 = 三种都要（`sln` / `slnx` / `csproj`）；只能给这三种，逗号分隔。

扫描一个文件夹里的解决方案与项目文件。每个解决方案一块：先是 `<解决方案路径>(被描述数+额外数)`，接着是它描述的项目树（`├─` / `└─`），再接着是"在该解决方案文件夹下、但没被它描述"的项目（`-` 前缀）；不被任何解决方案覆盖的项目在最后平铺。`bin` / `obj` / `.git` 跳过。

- 之前：想知道"这堆目录里有什么"时用它，通常是流程的第一步。
- 之后：拿到的路径喂 `view_project`（看单个项目）或 `view_solution_tree`（照解决方案文件看树）。
- 不能做：不写盘、不改解决方案；不跟随 `ProjectReference` 展开；不判断项目能不能编译。
- 注意：`depth` 默认只有 4，仓库很深时给大值；`kinds` 给了值就把别的种类**整体过滤掉**，看起来会像"这里没有项目"。

```
C:\src\ZMS.MCP\ZMS.MCP.slnx(3+1)
├─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
└─test/ZMS.MCP.Csharp.Test/ZMS.MCP.Csharp.Test.csproj
-samples/Demo/Demo.csproj

C:\tools\foo\bar.csproj
```
> 有一个解决方案：`(3+1)` = 描述了 3 个项目、另外发现 1 个没被它描述（`-` 前缀）；末行是完全不受任何解决方案覆盖的散项目。

```
No solution/project files under C:\temp\empty (depth 4).
```
> 一个都没扫到时只有这一行 —— `kinds` 把命中全过滤掉时也是这个形态，看起来一样。

### 查看项目 `view_project`

- `csprojPath`:string
  - 参数限制：要么是存在的 csproj 路径；要么是项目名，且能在最近的解决方案里唯一匹配到（重名要求给完整相对路径）。

把 csproj 原文打出来，然后按文档顺序附上所有"参与声明这个项目"的文件：项目文件本身、最近的 `Directory.Build.props`（以及它自己 `Import` 的更上层文件）、最近的 `Directory.Packages.props`、`Directory.Build.targets`、`global.json`、`NuGet.config`，最后是还原生成物 `obj/<项目>.csproj.nuget.g.props|targets`。

- 之前：通常先用 `scan_projects` 拿路径。
- 之后：要改内容用 `edit_project_metadata`（它要求给全文，所以先在这里把现状拿走）；要看包用 `list_project_packages`。
- 不能做：不修改、不格式化；不做 MSBuild 求值 —— 给的是"参与声明的文件原文"，不是展开后的属性值。
- 注意：`obj` 的位置是问 MSBuild 要的；问不到时那份列成"未列出"并说明原因，而不是猜一个 `<项目目录>/obj/`。

```
# C:\temp\zms-mcp-demo\Demo\Demo.csproj

## 项目文件
C:\temp\zms-mcp-demo\Demo\Demo.csproj
    <Project Sdk="Microsoft.NET.Sdk">

      <PropertyGroup>
        <TargetFramework>net11.0</TargetFramework>
      </PropertyGroup>

    </Project>

## 目录级（MSBuild 自动导入，取最近一份） — Directory.Build.props
C:\temp\zms-mcp-demo\Directory.Build.props
    <Project>
      <PropertyGroup>
        <LangVersion>latest</LangVersion>
      </PropertyGroup>
    </Project>

## 还原生成（属性） — obj 位置取自 MSBuild 的 MSBuildProjectExtensionsPath
C:\temp\zms-mcp-demo\Demo\obj\Demo.csproj.nuget.g.props
    ...
```
> 正常形态：每一份都是 `## <角色>` + 路径 + ```` ```xml ```` 原文；角色文字说明"这份文件为什么算参与声明"。

```
Error: 项目文件不存在：C:\temp\zms-mcp-demo\Nope.csproj
```
> 给了 `.csproj` 结尾的路径但文件不在。

```
Error: 解决方案 ZMS.MCP.slnx 里有 2 个叫 'Demo' 的项目，请给完整相对路径：...; ...
```
> 用项目名定位且重名（0 个匹配时换成"解决方案里没有名为 'X' 的项目"；向上找不到解决方案时提示"请给完整路径"）。

```
## 还原生成（属性/目标） — 未列出
(obj 的位置未知)
    问不到 MSBuild（MSB1009: ...），因此不按默认值猜 obj 的位置。
```
> 拿不到 obj 位置时的降级形态：明确说"没列"以及为什么，而不是给一个可能错的路径。

### 查看解决方案树 `view_solution_tree`

- `path`:string
  - 参数限制：路径必须存在；给文件夹时里面要能找到 `.slnx`（找不到 `.slnx` 才退而找 `.sln`）；命中的文件不是 `.slnx` 会被拒。

把 `.slnx` 按**解决方案文件本身**展开成树，包含虚拟文件夹（`Folder Name="/src/"` 会被拆成一层层目录）。

- 之前：想知道"解决方案里现在挂了什么、挂在哪个虚拟文件夹"时用它（`scan_projects` 的树是照磁盘扫的，不是一回事）。
- 之后：要加 / 删项目用 `add_project_to_solution` / `remove_project_from_solution`；要把 `.sln` 换过来用 `migrate_solution_to_slnx`。
- 不能做：不读 csproj 内部结构；不支持凭 `.sln` 看树（先迁移）。
- 注意：`Project` / `File` 节点的路径是**照 slnx 里写的原样**显示（哪怕是绝对路径）；缺 `Path` 属性时显示 `(缺 Path)`。

```
Demo.slnx
└─src
   └─Core
      └─Demo/Demo.csproj
```
> 一层虚拟文件夹 `src/Core` 里挂着一个项目；树形用 `├─` / `└─`，缩进用 `│  ` / `   `。

```
Demo.slnx
```
> 空解决方案（或里面只有根节点）时只有文件名这一行。

```
Error: 路径不存在：C:\temp\nope
Error: 这个文件夹里没有解决方案文件：C:\temp\empty
Error: 只能查看 .slnx：Demo.sln。请先用「迁移解决方案为 slnx」把它迁过来。
```
> 三种拒绝：路径不存在 / 文件夹里没有解决方案 / 命中的是 `.sln`。

## 解决方案编辑

### 迁移解决方案为 slnx `migrate_solution_to_slnx`

- `path`:string
  - 参数限制：路径必须存在；给文件夹时里面要有解决方案文件；命中的必须是 `.sln`（已经是 `.slnx` 会被拒）；目标 `.slnx` 已存在时必须带 `force=true`。
- `force`:boolean
  - 默认 `false`；`true` 时给命令行加 `--force`。

跑 `dotnet sln <文件> migrate` 把 `.sln` 变成 `.slnx`。

- 之前：确认目标还是 `.sln`（已经是 `.slnx` 会直接报错，白跑一趟）。
- 之后：`view_solution_tree` 看结果；此后才能用那两个解决方案写入工具。
- 不能做：**不进任何事务** —— 立刻改盘、不可回滚；不校验迁移结果是否等价。
- 注意：命令行非 0 退出时报错里带完整输出；`force=true` 会覆盖已存在的 `.slnx`。

```
# 迁移完成（命令行改盘，不进事务）
- 源：C:\temp\demo\Demo.sln
- 目标：C:\temp\demo\Demo.slnx

已成功迁移解决方案文件。
```
> 成功形态：明确写"命令行改盘、不进事务"，命令行的输出原样跟上。

```
Error: Demo.slnx 已经是 slnx 了。
```
> 幂等性**没有**做：目标已经是 slnx 就是错误，不是"已完成"。

```
Error: 目标已存在：C:\temp\demo\Demo.slnx。确认要覆盖请带 force=true（dotnet sln migrate 需要 --force）。

Error: dotnet sln migrate 失败（退出码 1）：
<命令行的完整输出>
```
> 命令行失败时不会只给一句"失败"，而是把输出整段带回来。

### 添加项目到解决方案 `add_project_to_solution`

- `slnxPath`:string
  - 参数限制：必须是 `.slnx`（给 `.sln` 会被拒）；路径必须存在。
- `csprojPath`:string
  - 参数限制：项目文件**必须已经存在**（这个工具不建项目）。
- `folder`:string?
  - 默认空串 = 放到解决方案根；非空表示虚拟文件夹，如 `src/Core`。

跑 `dotnet sln add` 把项目挂进解决方案。

- 之前：解决方案必须是 `.slnx`（否则先 `migrate_solution_to_slnx`）；项目文件必须先存在。
- 之后：`view_solution_tree` 确认落在哪个虚拟文件夹。
- 不能做：不创建项目、不改项目文件、不进事务。
- 注意：**错误形态与其它工具不同** —— 参数/环境问题不会变成 `Error:` 文本，而是协议级错误 `An error occurred invoking 'add_project_to_solution'`（这个工具没走统一的异常包装）；成功且带 `folder` 时目标会额外标成 `（/src/Core/）`。

```
✅ Added C:\temp\demo\Demo\Demo.csproj to C:\temp\demo\Demo.slnx（/src/Core/）

    已将项目“Demo\Demo.csproj”添加到解决方案中。
```
> 成功形态：第一行结论（带虚拟文件夹标注），后面附命令行原样输出。

```
❌ exit code 1

项目 ... 已经存在于解决方案中。
```
> 命令行非 0 退出但不算异常时的形态。

```
An error occurred invoking 'add_project_to_solution'.
```
> 参数/环境错误（比如项目文件不存在）—— 协议级错误，不是文本结果。

### 从解决方案移除项目 `remove_project_from_solution`

- `slnxPath`:string
  - 参数限制：必须是 `.slnx`；路径必须存在。
- `csprojPath`:string
  - 参数限制：要是该解决方案里被描述的项目路径。

跑 `dotnet sln remove` 把项目从解决方案摘掉。

- 之前：`view_solution_tree` 确认它确实挂在里面（顺带看清挂在哪个虚拟文件夹）。
- 之后：`view_solution_tree` 复核。
- 不能做：不删项目文件本身；不清理空掉的虚拟文件夹；不进事务。
- 注意：错误形态与 `add_project_to_solution` 一样是协议级错误。

```
✅ Removed C:\temp\demo\Demo\Demo.csproj from C:\temp\demo\Demo.slnx

    已从解决方案中移除项目“Demo\Demo.csproj”。

An error occurred invoking 'remove_project_from_solution'.
```
> 项目不在解决方案里 / 参数不合法时的形态。

## 项目文件编辑

### 编辑项目元数据 `edit_project_metadata`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件（绝对路径或唯一项目名）。
- `content`:string
  - 参数限制：必须是**完整** csproj 内容，且先过两道校验：XML 语法合法；根元素是 `<Project>`。不通过就报错，一个字节都不写。
- `dryRun`:boolean
  - 默认 `false`；`true` 时只校验并展示内容，不写盘。

整文件替换 csproj 内容，写回用**文件原编码**。

- 之前：`view_project` 把现状全文拿走（这个工具不吃"片段合并"，你给什么就写什么）。
- 之后：`view_project` 复核；改的是包引用的话用 `list_project_packages` 看实际结果。
- 不能做：不做 XML 片段合并 / 补丁；不校验 MSBuild 语义（属性名对不对、导入能不能解析，都不管）；不进事务。
- 注意：内容与现状**完全一致**时不算失败，会明确说"内容没有变化，未写入。"；`dryRun` 会把整份内容回显出来，长内容注意输出体积。

```
# 编辑元数据
- 项目：C:\temp\demo\Demo\Demo.csproj
- 编码：utf-8（来自 无 BOM 且是合法 UTF-8）
- **预演，未写入**。

    <Project Sdk="Microsoft.NET.Sdk">
      ...
    </Project>
```
> 预演形态：先说编码（写回就用它），再把要写的内容原样贴出。

```
# 编辑元数据
- 项目：C:\temp\demo\Demo\Demo.csproj
- 编码：utf-8（来自 BOM）
- 已写入（XML 语法检查 + 根元素 Project 检查通过）。
```
> 真写形态；编码来源可能是 `.editorconfig 的 charset` / `BOM` / `无 BOM 且是合法 UTF-8` 三种。

```
# 编辑元数据
- 项目：C:\temp\demo\Demo\Demo.csproj
- 编码：utf-8（来自 无 BOM 且是合法 UTF-8）
- 内容没有变化，未写入。
```
> 内容一致就早退，不写盘也不算错。

```
Error: XML 语法不通过：Data at the root level is invalid. Line 1, position 1.
Error: 最低 csproj 语法检查不通过：根元素必须是 <Project>，实际是 <Foo>
```
> 两道校验各自的拒绝形态。

## NuGet 包

### 查看项目包引用 `list_project_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。

列出包引用：直接引用的顶级包、依赖传递进来的包、通过 `ProjectReference` 传递进来的顶级包。依赖图来自**真实还原**（MSBuild 的 `ReferencePath`；退化时读 `project.assets.json`，它的位置也是问 MSBuild 要的）。

- 之前：想拿到准确的图，最好先自己还原过。
- 之后：要装 / 删包用 `install_packages` / `remove_packages`。
- 不能做：不还原、不修改项目、不解析版本冲突。
- 注意：输出里会写依赖图来源；`⚠ 依赖图可能已过期` 表示 csproj / props 比还原产物新，这时结论可能不准。

```
# 包引用
- 项目：C:\temp\demo\Demo\Demo.csproj
- 依赖图来源：真实还原结果
- assets：C:\temp\demo\Demo\obj\project.assets.json

## 顶级包（直接引用）
- Newtonsoft.Json 13.0.3

## 依赖传递包
- System.Text.Json 8.0.0

## 项目引用而传递的顶级包
（无）
```
> 正常（有还原产物）形态；某一栏为空就写 `（无）`。

```
# 包引用
- 项目：C:\temp\demo\Demo\Demo.csproj
- 依赖图来源：ReferencePath（还原产物缺失，可能不全）（MSBuild 没给出 ProjectAssetsFile）

## 顶级包（直接引用）
（无）
```
> 没还原产物时降级成 `ReferencePath`，并且**同一件事只说一遍**（不再追加"可能已过期"）。

```
- ⚠ 依赖图可能已过期（csproj / props 比还原产物新）
```
> 有还原产物、但项目文件比它新时，跟在"依赖图来源：真实还原结果"那段后面。

### 搜索包 `search_packages`

- `packName`:string
  - 可以为空串 —— 那时"本地"分支会列出本地全部包名。
- `page`:integer
  - 默认 0；只对线上有效（每页条数是实现里的常量）。
- `local`:boolean
  - 默认 `true`（查本地 NuGet 缓存）。
- `web`:boolean
  - 默认 `false`（查 nuget.org）。

按关键词搜包 id。

- 之前：只有一个模糊的包名时用它确认准确 id。
- 之后：`list_package_versions` 挑版本 → `get_package_metadata` 看 readme → `install_packages`。
- 不能做：不安装、不改项目、不解析版本（只给名字，`web` 下才额外带一个版本）。
- 注意：两个开关的组合会改变输出形状 ——
  - 只 `local`：本地清单（计数 + 名字，按名排序）；
  - 只 `web`：`# 线上查询到 N 个（第 X 页，每页 N 条）` + 每行 `id 版本`；
  - 两个都开：**只给线上那一页**，本地也有的用 `[]` 标出。

```
# 本地查询到 1 个

newtonsoft.json
```
> 只查本地（默认）。

```
本页查询到 20 个，[] 标记为本地也存在

[Newtonsoft.Json]
Polly
```
> 本地 + 线上同时开：只给线上这一页，命中本地的加方括号。

```
# 线上查询到 20 个（第 2 页，每页 20 条）

Newtonsoft.Json 13.0.3
Polly 8.4.2
```
> 只查线上：带页码表头。

### 列出包版本 `list_package_versions`

- `packName`:string
  - 参数限制：必须是**精确**包 id（不是关键词）。
- `verRange`:string?
  - 默认空串 = 只列正式版；`*` 表示要最新（连预览版一起）；其他取值走 NuGet 版本范围语法，如 `[13.0,14.0)`。
  - 参数限制：格式不对会直接报错。
- `local`:boolean
  - 默认 `true`。
- `web`:boolean
  - 默认 `false`。

列出某个包的版本（从高到低）。

- 之前：`search_packages` 确认 id 拼写。
- 之后：选好版本传给 `install_packages`（`名字@版本`）。
- 不能做：不安装；不解析依赖树；不判断"这个版本能不能用在我的 TFM 上"。
- 注意：进入执行阶段后按参数不同行为 —— 默认**不列预览版**（表头说明漏掉多少）；`verRange="*"` 时预览版一起列、表头改口径；`web=true` 以线上集合为准，线上取失败会降级成"本地缓存（线上取失败：…）"而不是报错；`local` + `web` 都开时，本地也有的版本加 `[]`，并在末尾附注释行。

```
# Newtonsoft.Json（本地缓存）
    > 共筛选到 2 个版本，还有 5 个预览版未列出

13.0.3
13.0.2
```
> 默认（`verRange` 空）：只列正式版，并告诉你漏掉多少预览版。

```
# Newtonsoft.Json（本地缓存 + nuget.org）
    > 共查询到 128 个版本，其中预览版 96 个

[13.0.3]
13.0.3-beta1

（[] = 本地缓存里也存在）
```
> `verRange="*"` + 本地与线上都开。

```
Error: 版本范围格式不对：'[' is not a valid version string.
```
> `verRange` 写坏的形态。

### 查询包元数据 `get_package_metadata`

- `packName`:string
  - 参数限制：必须是精确包 id。
- `ver`:string?
  - 默认空串 = 本地缓存的最高版本（本地一个都没有时，走线上最新）。

给出包的 **nuspec + readme**：先看本地缓存，本地没有再问 nuget.org。

- 之前：想查特定版本先 `list_package_versions`（`ver` 写错不会有"版本不存在"的提示，只是查不到）。
- 之后：学 API 时配 `list_doc_symbols`。
- 不能做：不做在线搜索；readme 缺失时不给替代内容。
- 注意：一旦本地命中就**不再**去线上补 readme；readme 缺失时给 `projectUrl` / `repositoryUrl` 让你自己去看。

```
# Newtonsoft.Json 13.0.3（本地缓存）

## nuspec
    <?xml version="1.0" encoding="utf-8"?>
    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
      <metadata>...</metadata>
    </package>

## readme
# Json.NET is a popular high-performance JSON framework for .NET
...
```
> 本地命中且有 readme。

```
## readme
未找到 readme。可以自己去这里看：
- projectUrl: https://www.newtonsoft.com/json
- repositoryUrl: https://github.com/JamesNK/Newtonsoft.Json
```
> readme 缺失（nuspec 那段照旧给）；两个 URL 在 nuspec 里没有时写 `(nuspec 里没有)`。

```
# Newtonsoft.Json 99.0.0
未找到（本地缓存与 nuget.org 都没有）。
```
> 两边都没有（`ver` 照原样回显）。

### 安装包 `install_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `nugetPack`:string[]
  - 参数限制：至少一项；每项是 `名字` 或 `名字@版本`（按**最后一个** `@` 切分）；空项被忽略。
- `dryRun`:boolean
  - 默认 `false`。
- `allowPrerelease`:boolean
  - 默认 `false`。

跑 `dotnet add package` 装包。

- 之前：`search_packages` + `list_package_versions` 定名字与版本。
- 之后：`list_project_packages` 看真实结果。
- 不能做：**不进事务**（立刻改 csproj 并触发还原，不可回滚）；不单独引入传递包（除非被引用的传递包版本与参数冲突）。
- 注意：进入执行阶段后按参数不同行为 ——
  - `dryRun=true`：只决定并展示要跑的命令，一条都不跑，末尾写"（预演，未真正执行。）"；
  - `dryRun=false`：真跑，并在末尾附**落盘后漏洞核对**（`dotnet restore -p:NuGetAuditMode=all`，摘 `NU1901–NU1904`）；
  - `allowPrerelease=true`：选版本时允许预览版；
  - 版本没给时：先本地缓存最新、再线上；**选中的版本若有已知漏洞，会换成最新安全版**；漏洞索引拿不到时明确标"未经核对"，不会默认当安全。

```
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
```
> `dryRun=true`：六个分类栏 + `---` + 命令清单 + 预演声明；每栏为空写 `（无）`。

```
# 以下直接引入包是漏洞的
Polly 7.2.3

# 引入以下包
Polly 8.4.2

# 版本冲突被钉住的包
Polly：参数要 8.4.2，依赖图要 8.5.0 → 采用 8.5.0

---
...
## 落盘后核对（NU1901–NU1904，NuGetAuditMode=all）
- 未发现 NU1901–NU1904（已带 NuGetAuditMode=all，覆盖传递依赖）。
```
> 真跑：额外出现"被钉住的包"（依赖图要的版本比参数高时）和"落盘后核对"。索引不可用时的漏洞栏写成 `X 1.0.0（漏洞索引不可用，未经核对）`；核对跑不起来时写 `- 核对未执行：<原因>`。

```
Error: 至少要给一个包。
Error: 本地缓存没有 Foo，也无法从 nuget.org 取版本：...
Error: dotnet add package Polly 失败（退出码 1）：
<命令行输出>
```
> 参数为空 / 包找不到 / 命令行失败。

### 移除包 `remove_packages`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `nugetName`:string[]
  - 参数限制：至少一项。
- `dryRun`:boolean
  - 默认 `false`。

跑 `dotnet remove package`，并在**移除前后各建一次包图**，所以报告能说明"顺带消失的传递包"。

- 之前：`list_project_packages` 看当前图。
- 之后：`list_project_packages` 复核。
- 不能做：**不进事务**（立刻改盘）；不能移除"只是被传递引入"的包（得先处理它的上游）。
- 注意：`dryRun=true` 因为没真删，"移除的依赖传递包"一栏必然是 `（无）` —— 末尾会明说这一栏按真实移除后的图算。

```
# 本次移除包
Newtonsoft.Json

# 本次移除的依赖传递包
（无）

（预演，未真正执行；上面「依赖传递包」一栏按实际移除后的图算，预演时为 0。）
```
> `dryRun=true`。

```
# 本次移除包
Microsoft.Extensions.Logging

# 本次移除的依赖传递包
Microsoft.Extensions.Logging.Abstractions
```
> 真删：被直接移除的包不会再在第二栏重复出现（它只属于第一栏）。

```
Error: 至少要给一个包名。
Error: dotnet remove package Foo 失败（退出码 1）：
<命令行输出>
```
> 参数为空 / 命令行失败两种拒绝形态。

### 列出文档注释符号 `list_doc_symbols`

- `packName`:string
  - 参数限制：必须是精确包 id。
- `ver`:string?
  - 默认空串 = 本地缓存里的最高版本。
- `tar`:string?
  - 默认空串 = 实现自己挑一个最合适的 TFM；也可以给 `lib/` 下的目录名，如 `net8.0`。
- `path`:string?
  - 默认空串 = 列出（有文档注释的）所有类型；非空表示点分隔的对象路径，如 `Newtonsoft.Json.Linq.JObject`。
- `argsList`:string?
  - 默认空串；逗号分隔的参数类型，用来消歧重载；两种写法都吃：`string,int` 与 `(System.Int32,System.String)`。
- `type`:string?
  - 默认空串 = 按 `path` 精度推断 kind；也可以显式给字母，取自 `NTPFMED`（N 命名空间、T 类型、P 属性、F 字段、M 方法、E 事件、D 原始 XML 片段）。

从**本地 NuGet 缓存**的 XML 文档注释里查符号（不做在线查找）。

- 之前：包必须在本地缓存里（先用 `list_package_versions` 的本地分支确认）。
- 之后：配合 `get_package_metadata`（readme）一起学这个包的 API。
- 不能做：不查网页文档、不读"野生" docs 目录；不做反射（给的是文档条目，不是真实签名）。
- 注意：进入执行阶段后按参数不同行为 ——
  - `path` 空：按 `type`（默认 `T`）过滤类型；
  - `path` 非空：精度决定默认 kind（不是类型 → `T` 前缀匹配、正好是类型 → `PFME`、单个成员 → `D` 原始片段、重载 → `M`）；
  - `type` 里带 `D`：**输出原始 XML 片段**（此时 `D` 不参与种类过滤）；
  - 表头永远写明本次生效的 kind 以及它是"按精度推断"还是"显式指定"；
  - 文档里 `ref` / `out` 参数带 `@` 后缀，匹配时**不带** `@` 写。

```
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\Users\me\.nuget\packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 147 | 生效 type: `T`（按精度推断）

- `T:Newtonsoft.Json.Bson.BsonObjectId`
- `T:Newtonsoft.Json.Bson.BsonReader`
```
> 没给 `path`：列全部类型（表头写明"按精度推断"）。

```
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: ...
- 条目总数: 1613 | 命中: 1 | 生效 type: `D`（按精度推断）

    <member name="M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)">
      <summary>Serializes the specified object to a JSON string.</summary>
    </member>
```
> `path` 精确命中单个成员：默认给 `D`（原始 XML 片段）。多个重载时给 `M`，超过阈值会分组显示参数表。

```
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: ...
- 条目总数: 1613 | 命中: 0 | 生效 type: `T`（显式指定）
- 提示: 路径 'Newtonsoft.Json.Nope' 没有命中任何文档条目

_(没有命中的文档条目)_
```
> 路径写错：表头之后会有一行"提示"，末尾是空结果占位行。

```
Error: 本地缓存里没有 Newtonsoft.Json 13.0.3 的 XML 文档注释（package 未下载或该 TFM 没有文档）。
```
> 本地没有这个包的文档。

## 符号查询

### 列出类型 `list_types`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `filter`:string?
  - 默认空串；给了就对**类型名**做大小写不敏感的子串过滤。

列出项目里声明的每个类型：完全限定名、种类、文件与行号。**分部类**会把全部声明位置列在同一行。

- 之前：想做"符号级"操作时先在这里拿名字，返回值就是其它工具的 `typePath`。
- 之后：`list_members` 看成员；`get_member` 读源码。
- 不能做：只列**本项目源码**里的类型（引用程序集不算）；不列成员；不显示继承关系。
- 注意：表头会回显 TFM、源文件数、类型数；`filter` 匹配的是类型名本身（不含命名空间）。

```
# C:\temp\zms-mcp-demo\Demo\Demo.csproj
- TFM: `net11.0` | source files: 1 | types: 2

- `Demo.Class1` (class) — Class1.cs:3
- `Demo.Partial` (class) — A.cs:1, B.cs:5
```
> 正常形态；分部类（`Demo.Partial`）把两处声明位置写在同一行。

```
# C:\temp\zms-mcp-demo\Demo\Demo.csproj
- TFM: `net11.0` | source files: 1 | types: 0

```
> 过滤后没命中：只有表头（**没有**"无结果"之类的提示行，别误以为出错）。

### 列出成员 `list_members`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `typePath`:string
  - 参数限制：必须是完全限定类型名（如 `My.Namespace.MyType`）。

列出某个类型的成员：签名 + 文件与行号。

- 之前：`list_types` 拿准确 `typePath`。
- 之后：改 / 删成员用 `update_member` / `remove_member`（`memberPath` 就用这里给出的签名）；加成员用 `add_member`。
- 不能做：不给方法体（要体用 `get_member`）；不列编译器隐式生成的成员，也不列属性 / 事件的访问器。
- 注意：排序按"成员种类名 + 名字"；同名重载超过实现里的阈值会折叠成"某名字有 N 个重载"，再逐条列参数表。

```
# Demo.Class1  (class)
- TFM: `net11.0`

- `Demo.Class1.Add(int, int)` — Class1.cs:5
- `Demo.Class1.Name` — Class1.cs:3
```
> 正常形态：第一行是 `# <类型显示名>  (<种类小写>)`。

```
# Demo.Empty  (class)
- TFM: `net11.0`

_(no members)_
```
> 一个成员都没有时的占位行。

### 列出符号 `list_symbols`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `type`:string?
  - 默认空串 = 全部；字母取自 `NCSIPFEMD`（N 命名空间、C 类、S 结构、I 接口、P 属性、F 字段、E 事件、M 方法）。另有两位特殊含义：`T` = 类 / 结构 / 接口三种合起来；`D` = 只保留带 XML 文档注释的符号。不认识的字母会被忽略（但会警告）。
- `modifier`:string?
  - 默认空串 = 不过滤；逗号（也吃分号 / 空格）分隔 `public` / `internal` / `protected` / `private` / `static` / `const` / `abstract` / `readonly` / `virtual` / `override`。
- `argsList`:string?
  - 默认空串；逗号分隔的参数类型，只列参数类型**完全一致**的方法。

把项目**自己源码**里的符号全列出来，按命名空间分段、按所属层级缩进，带文件与行号。

- 之前：想"全项目扫一遍结构"时用它；只想看某个类型用 `list_members`。
- 之后：定位到具体成员后用 `get_member` 读源码。
- 不能做：不查外部程序集；不做模糊名匹配（`argsList` 是精确匹配）；不判断可访问性是否真的可达。
- 注意：进入执行阶段后按参数不同行为 ——
  - `type` 里不认识的字母会**明确警告**（不静默吞掉，否则"筛出来是空的"看起来像"项目里没符号"）；
  - 筛出来为空且用了 `D` 时补一句说明 `D` 的语义；
  - `modifier` 是**与**关系（每个条件都要满足；访问性那几个互斥，同时给两个基本筛不出东西）。

```
# C:\temp\zms-mcp-demo\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 3 | 过滤: type='' modifier='' args=''

## Demo
- `Demo` (namespace) — Class1.cs:1
- `class Class1` (class) — Class1.cs:3
  - `public static int Add(int a, int b)` (method) [Class1] — Class1.cs:5
```
> 正常形态：表头回显过滤条件；命名空间一段（`## <命名空间>`，根命名空间写 `(global)`）；成员带缩进与 `[所属类型]`。

```
- TFM: `net11.0` | 符号: 0 | 过滤: type='X' modifier='' args=''
⚠ 无法识别的 type 字母已忽略：X（可用：N C S I T P F E M D）

_(无匹配符号)_
```
> `type` 写错字母：先警告、再给空结果。用了 `D` 且为空时还会多一行"D 表示只看带 XML 文档注释的符号"的提示。

```
- `Demo.Class1.Add` 有 3 个重载：
  - `(int, int)` — Class1.cs:5
  - `(int, int, int)` — Class1.cs:9
```
> 同名重载超过阈值：折叠成一条标题 + 参数表（不重复写方法名）。

### 读取成员 `get_member`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `memberPath`:string
  - 参数限制：`Ns.Type` 表示要整个类型；`Ns.Type.Member` 或 `Ns.Type.Method(int,string)` 表示要单个成员。

读一个成员（或整个类型）的签名、文件、行范围与源码 —— **不需要知道文件路径**。若该符号有拟定，末尾追加一段「拟定视图」。

- 之前：从 `list_types` / `list_members` / `list_symbols` 拿到准确路径。
- 之后：要改就用拟定（`stage_draft`）或直接改（`update_member`）；有冲突时用它给的 `selectCookie` 去 `select_draft`。
- 不能做：**不写盘** —— 即使它给了 `selectCookie`，那也只是内存里的许可；不给"整个文件"的内容。
- 注意：进入执行阶段后按参数不同行为 ——
  - 给类型：**只给结构**（方法只给签名、访问器给记号、文档注释按实现里的预算截断）；
  - 给成员：给该成员源码与行范围；
  - 有拟定：追加三方视图（某份不存在写 `（不存在）`，删除类拟定在"拟定"那段写 `（删除）`）；
  - 有冲突：追加冲突提示**并当场发一个 `selectCookie`**，**每次调用都会换新的**（旧的立刻失效）。

```
## Demo.Class1.Add(int, int)

- Kind: `Method`
- Declaring type: `Demo.Class1`
- File: `C:\temp\zms-mcp-demo\Demo\Class1.cs`
- Lines: 5-5

        public static int Add(int a, int b) { return a + b; }
```
> 要单个成员：头信息 + ```` ```csharp ```` 源码。

```
## Demo.Class1

- Kind: `NamedType`
- File: `C:\temp\zms-mcp-demo\Demo\Class1.cs`

    public class Class1
    {
        public static int Add(int a, int b) { get { … } }
        public static string Name { get { … } }
    }
```
> 要整个类型：只给**结构**（方法体、属性实现都不展开）。

```
## 拟定视图
- 符号：Demo.Class1.Add(int, int)
- 本次拟定：写入

### 快照（拟定开始时）
public static int Add(int a, int b) { return a + b; }

### 现状（磁盘上，忽略拟定）
public static int Add(int a, int b) { return a + b; }

### 拟定（要写进磁盘的）
public static int Add(int a, int b) { return a + b + 9; }
```
> 有拟定：在正常内容后追加三方视图。

```
- ⚠ 冲突：这个符号在拟定期间被非工具改动过
- 解决：select_draft(csprojPath, memberPath="Demo.Class1.Add(int, int)", selectCookie="d7b3cb0d-...", choice=...)
  - `draft` 用拟定内容 / `snapshot` 用快照内容 / `disk` 用现状内容 / `drop` 取消该符号的拟定
```
> 有冲突：附在拟定视图里，并把 cookie 写成可直接调用的形式。

## 符号直接编辑

这三个工具**不走拟定**：改完立刻落盘（落盘前做语法检查，`format=true` 时跑 Roslyn 格式化）。要"先看后写、可回滚"就用拟定那组。

### 新增成员 `add_member`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `typePath`:string
  - 参数限制：必须是完全限定类型名。
- `code`:string
  - 参数限制：成员声明源码（要能解析成成员）。
- `before`:string?
  - 默认空串 = 追加到类型末尾；非空表示插到该名字的成员之前。
  - 参数限制：给的名字必须能找到。
- `format`:boolean
  - 默认 `true`。

往一个类型里加成员。

- 之前：`list_members` 看现有成员（尤其要用 `before` 定位时）。
- 之后：`get_member` 复核（返回里也直接提示去复核）。
- 不能做：不建新类型；不改别的成员；不是事务（已改的部分不会自动回滚）。
- 注意：进入执行阶段后按参数不同行为 —— `before` 用**成员名**（不带参数表），同名重载时定位未必符合预期；`format=false` 就完全不动格式。

```
✅ added `new member in Demo.Class1`
- File: `C:\temp\zms-mcp-demo\Demo\Class1.cs` (Class1.cs)
- Lines: 7-7

Re-read with GetMember to verify the result.
```
> 三个直接编辑工具共用这套形态：`✅ <动作>` + 文件（带项目内相对路径）+ 行范围 + 一句复核提示。

```
Error: 找不到成员 'Nope'，无法插到它前面。
```
> `before` 给了不存在的名字时拒绝。

### 替换成员 `update_member`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `memberPath`:string
  - 参数限制：`Ns.Type.Member` 或 `Ns.Type.Method(int,string)`。
- `code`:string
  - 参数限制：新的**完整**成员声明（不是片段）。
- `format`:boolean
  - 默认 `true`。

用新代码替换一个已存在的成员声明。

- 之前：`get_member` 看现状。
- 之后：`get_member` 复核。
- 不能做：不新增（成员不存在就报错）；不改类型级声明；不是事务。
- 注意：返回的是**新的**文件与行范围 —— 行号和原来不一样是正常的。

```
✅ replaced `Demo.Class1.Add(int, int)`
- File: `C:\temp\zms-mcp-demo\Demo\Class1.cs` (Class1.cs)
- Lines: 5-8

Re-read with GetMember to verify the result.
```
> 替换后行数变多，返回范围跟着变。

### 删除成员 `remove_member`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。
- `memberPath`:string
  - 参数限制：`Ns.Type.Member` 或 `Ns.Type.Method(int,string)`。
- `format`:boolean
  - 默认 `true`。

从它的类型里删掉一个成员。

- 之前：`get_member` 确认删的就是这个（不可回滚）。
- 之后：`list_members` 复核。
- 不能做：不删类型本身；不处理"删了之后别处编译不过"。
- 注意：同名重载必须用参数表把目标指明确，否则会命中多个而报错。

```
✅ removed `Demo.Class1.Welcome()`
- File: `C:\temp\zms-mcp-demo\Demo\Class1.cs` (Class1.cs)
- Lines: 7-7

Re-read with GetMember to verify the result.

```
## 拟定流程

整条链是：**追踪 → 拟定编辑 → 查看（三方视图）→ 解决冲突 → 确认（预检 / 落盘）**。拟定状态存在 MCP 自己的 sqlite 里（重启不丢）；`track_project` 既是入口，也是唯一能"丢弃拟定"的出口。

### 追踪 / 清除追踪 `track_project`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件；带 cookie 时，该 cookie 必须与这个项目的追踪记录一致，否则报错且不动任何数据。
- `cookie`:string?
  - 默认空串 = 开始或继续追踪。

一个工具两个用途：开始 / 汇报追踪，或清除追踪与拟定。

- 之前：先 `scan_projects` / `view_project` 定好路径。**要拟定任何东西都必须先来这一趟**（未追踪的项目会被 `stage_draft` 拒绝）。
- 之后：`stage_draft` 拟定编辑；`list_draft` 复看；`get_member` 看某个符号的三方视图。
- 不能做：不做"只清拟定、保留追踪"（要重来就带 cookie 走一趟清除）；**不刷新未追踪更改的判定基准**（首次快照之后，别人改的东西会一直被报为未追踪更改）。
- 注意：进入执行阶段后按参数不同行为 ——
  - `cookie` 空：首次调用建立**符号级快照**并返回追踪 cookie；之后的调用只报告，不动快照；
  - `cookie` 非空：先做一遍写前日志前滚（有未完成的落盘就先补齐），再删除该项目的追踪快照与**全部拟定**；前滚若因为"文件被人手改过"而刻意保留了日志，这一步会把它们一并丢弃并报出条数；
  - 带 cookie 的调用**会连带删除拟定**，别拿它当"刷新一下"。
```

# 追踪已开始
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 追踪 cookie：`a054a503-...`（要清除追踪与拟定，把它传回 track_project 的 cookie 参数）
- 快照：2 个符号

## 现在已经有这些拟定写
（无）

## 有这些未追踪更改
（无 —— 快照就是当前现状）
```
> 首次调用（`cookie` 空）。

```
# 追踪已开始
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 追踪 cookie：`a054a503-...`（要清除追踪与拟定，把它传回 track_project 的 cookie 参数）
- 快照：2 个符号

## 现在已经有这些拟定写
- Demo.Class1.Add(int, int)：修改

## 有这些未追踪更改
- Demo.Class1.Name：在磁盘上被改过
```
> 再次调用（仍不带 cookie）：快照不变，只报现状 —— 拟定里有什么、磁盘上有什么被改了。

```
# 取消追踪
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 已删除拟定：1 条
- 追踪快照已删除；要再写这个项目，请重新 track_project。
```
> 带 cookie：清追踪 + 清拟定（若有被刻意保留的写前日志，会多一行"同时丢弃 N 条未处理的写前日志"）。

```
Error: 追踪 cookie 不匹配（或该项目没有在追踪）。
```
> cookie 对不上：拒绝，什么都不动。

### 拟定编辑 `stage_draft`

- `csprojPath`:string
  - 参数限制：项目必须**处于追踪中**（未追踪直接拒绝）；要能解析到项目文件。
- `typePath`:string
  - 参数限制：要能**精确**定位到一个类型（到顶层类型的完全限定名，嵌套类型可多几个点）。被宽容解析到外层类型的写法会被挡下。
- `memberName`:string?
  - 默认空串 = **拟定一个全新类型**；非空 = 成员名（方法要带参数列表，如 `Add(int,int)`）。
- `content`:string?
  - 默认 null = 删除该成员；非 null = 新源码。

拟定新增 / 修改 / 删除一个成员。

- 之前：**先 `track_project`（不带 cookie）**；用 `get_member` 看现状。
- 之后：`list_draft` 看全量；`get_member` 看三方视图；`confirm_draft`（不带 cookie）预检。
- 不能做：不落盘（只记"符号 + 意图 + 首次快照"）；**不合并**同符号的多次调用 —— 同一符号是替换，只保留最后一条。
- 注意：进入执行阶段后按参数不同行为 ——
  - `content` 非 null：走"写入"分支（同符号已有拟定就是替换）；
  - `content=null`：走"删除"分支；
  - `memberName` 空：走"新建类型"分支（会连带要求外层类型补 `partial`，并按命名空间 + 根命名空间推导新文件路径）；
  - 内容必须先过 **C# 语法检查**，不过就整条拒绝、什么都不记（语义错才轮到 `confirm_draft` 的诊断对比）；
  - 任何一次调用都会**作废现有的落盘许可**。

```
# 拟定已更新
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 符号：Demo.Class1.Add(int, int)
- 本次：写入（replaced）
- 拟定条数：1
- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```
> 正常写入（`replaced` = 这个符号原来已有拟定，被替换掉了）。

```
# 拟定已更新
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 符号：Demo.Class1.Welcome()
- 本次：删除
- 拟定条数：2
- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```
> `content=null` 的删除分支。

```
# 拟定已更新
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 符号：Demo.NewThing
- 本次：新建类型
- 拟定条数：1
- 落盘许可（若有）已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```
> `memberName` 空：拟定一个新类型。

```
Error: Code does not parse as C# member declaration(s): 应输入 )
```
> 语法检查不过：整条拒绝，什么都没记。

```
Error: 这个项目还没有开始追踪，请先调用 track_project。
```
> 未追踪就拟定时的拒绝。

### 列出拟定 `list_draft`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件。

列出某个项目的待落盘拟定，**包含 cookit** —— 重启后的 agent 靠它把拟定接回来。

- 之前：无（这是"接回旧拟定"的正规入口）。
- 之后：`confirm_draft` 预检；或 `get_member` 看具体符号。
- 不能做：只读，不改任何状态；不显示未追踪更改（那在 `track_project` 的汇报里）。
- 注意：条数少的时候直接看 `stage_draft` 的返回更快。

```
# 拟定
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 起始时间：2026-09-24T20:12:23.1174505+00:00
- 条数：2

- [1] 修改 Demo.Class1.Add(int,int)
- [2] 删除 Demo.Class1.Welcome()
```
> 每行是 `[序号] <动作> <符号>`；序号就是拟定内部顺序，落盘时按它应用。

```
# 拟定
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj
- 起始时间：2026-09-24T20:12:23.1174505+00:00
- 条数：0
```
> 没有任何拟定（三行元信息还在）。

### 解决冲突 `select_draft`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件；该项目要有待处理的拟定。
- `memberPath`:string
  - 参数限制：要能定位到拟定条目对应的符号（通常**直接复制 `get_member` 给的 `memberPath`**）。
- `selectCookie`:string
  - 参数限制：必须是 `get_member` 在冲突时给出的那个，且**还得是最新的**（`get_member` 每次调用都会换）。
- `choice`:string
  - 参数限制：只能是 `draft` / `snapshot` / `disk` / `drop` 之一。

用一个符号的冲突解决方式收尾。

- 之前：必须先从 `get_member` 拿到冲突时的 `selectCookie`。
- 之后：任何落盘许可都已作废 → 重新 `confirm_draft`（不带 cookie）预检再落盘。
- 不能做：不替你决定选哪个；不一次解决多个符号；不落盘。
- 注意：进入执行阶段后按 `choice` 不同行为 ——
  - `draft` = 用拟定内容（坚持本次写法）；
  - `snapshot` = 用拟定开始时的快照（撤掉外部改动的影响）；
  - `disk` = 用现在磁盘上的内容（接受别人的改动）；
  - `drop` = 把这个符号从拟定里去掉。
  `disk` 与 `snapshot` 方向相反，别选错。

```
# 选择已应用
- 符号：Demo.Class1.Add(int, int)
- 选择：用拟定内容（坚持本次写法）
- 落盘许可已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。

# 选择已应用
- 符号：Demo.Class1.Add(int, int)
- 选择：用现状内容（接受磁盘上的改动）
- 落盘许可已作废：要落盘请重新 confirm_draft（不带 cookie）做预检。
```
> 换成 `disk` 时的措辞变化（`snapshot` / `drop` 各有对应说明）。

```
Error: 没有未完成的拟定可以处理。
```
> cookie 过期 / 没有待办拟定。

```
Error: 无法识别选择：'keep'。可用：draft / snapshot / disk / drop
```
> `choice` 给了枚举外的值。

### 确认拟定 `confirm_draft`

- `csprojPath`:string
  - 参数限制：要能解析到项目文件；该项目要有拟定。
- `applyCookie`:string?
  - 默认空串 = 只做预检（不落盘）。
- `apply`:boolean
  - 默认 `true`。

拟定确认：先预检、再落盘。

- 之前：`stage_draft` 至少一条；要落盘就先跑一次"不带 cookie"的预检拿许可。
- 之后：`get_member` 复核结果；`track_project`（不带 cookie）看新的基线 / 未追踪更改。
- 不能做：**不做任何 git 操作**（提交 / 分支 / 贮藏都不动）；不"部分落盘"（一次把该项目全部拟定落完）；不接受过期许可。
- 注意：进入执行阶段后按参数不同行为 ——
  - `applyCookie` 空：重建符号树、定位每个符号所在文件、跑预检（文件被占用 / 符号冲突 / 文件字节 / 编码可判定 / 会不会新建目录），干净时返回**内存** applyCookie 与会碰的文件清单；
  - `applyCookie` 非空：复查每个符号是否还在同一文件、文件字节是否未变 —— **只覆盖"发 cookie → 传 cookie"这段窗口**，不符就作废许可并拒绝落盘；
  - `apply=false`：即使 cookie 匹配也只预览，不写盘；
  - 落盘顺序是固定的：**写前日志 → 原子写 → 清日志 / 拟定 / 许可 → `dotnet format` → 重算追踪基线**；
  - `dotnet format` 失败时**保留**拟定与写前日志（可直接重跑），并列出已经写入的文件。

```
# 拟定确认
- 项目：C:\temp\zms-mcp-demo\Demo\Demo.csproj

## 变更分类
### 修改（1）
- Demo.Class1.Add(int,int)

## 本次改动涉及
- C:\temp\zms-mcp-demo\Demo\Class1.cs（Demo.Class1.Add(int, int)）

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：无
- 消失：无

## 预检查
- ✅ 无占用、无冲突

- 落盘 cookie：`1373fb43-...`（传回来才落盘；每次预检换新，旧 cookie 立刻失效）
```
> 预检通过：先给变更分类（新增 / 修改 / 删除分组），再给会碰的文件，再给诊断对比，最后是预检结论与 cookie。

```
## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：
  - CS0029 error 无法将类型“string”隐式转换为“int” @ Class1.cs:5
- 消失：无

## 预检查
- ⚠ 会新建目录：C:\temp\zms-mcp-demo\Demo\New

- 落盘 cookie：`...`
```
> 诊断对比列出拟定带来的**新错误 / 消失的错误**（配对只看错误码 + 消息 + 文件，行号仅展示）；"会新建目录"是**提示级**（进提示，不进问题），不会被拦死。

```
## 预检查
- ❌ Demo.Class1.Add(int, int)：文件 C:\temp\...\Class1.cs 在拟定期间被非工具改动过

（没有发落盘 cookie：先把上面这些问题处理掉，再来一次预检。）
```
> 预检发现问题：**不发** cookie，并说明下一步。

```
## 已落盘
- C:\temp\zms-mcp-demo\Demo\Class1.cs
- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）
- 格式化：已对本次改动的文件跑 dotnet format
- format 额外改动：无
- 追踪继续，基线已按落盘后的现状整体重算。
- 未做任何 git 操作（提交/分支/贮藏都不动）
```
> 落盘成功：文件逐个列出（内容本来就没变的会标"（内容未变，跳过）"）；`format 额外改动` 非空时会列出被 `dotnet format` 顺手改过的文件。

```
Error: 落盘目标状态与发许可时不一致，已作废许可：
  - C:\temp\...\Class1.cs 在发许可之后被外部改过
  - Demo.Class1.Add(int, int)：C:\temp\...\Class1.cs 在发许可之后被外部改过
```
> 带 cookie 落盘但窗口期内文件被外部改了：作废许可并**拒绝写入**（文件与符号两个层级都报出来）。

```
# 落盘未完成（文件已写入，但格式化失败）
- 已写入 1 个文件：C:\temp\...\Class1.cs
- dotnet format 未成功（退出码 1）：...
- 拟定与写前日志**已保留**：处理完之后重新 confirm_draft（不带 cookie）预检即可重试。
```
> `dotnet format` 失败的形态：改动已在盘上，但拟定与写前日志不清理，方便原样重试。

```
Error: 没有待落盘的拟定。
```
> 该项目没有拟定就想落盘时的拒绝。

## 关于这些示例

- 示例是按实现里拼字符串的代码推出来的（`Tools/*.cs` 与对应的 `Project` / `Roslyn` / `NuGet` / `Draft` 服务），**不是**为了写文档去实机跑各种情况。
- 断言不了措辞的地方（比如某个列表为空时的具体占位文字）只写到能确定的那一层，没有编造。
- 通用异常形态：任何工具抛出的异常都会被转成 `Error: <异常消息>`；只有 `add_project_to_solution` / `remove_project_from_solution` 例外（协议级错误）。

