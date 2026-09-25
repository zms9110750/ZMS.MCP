# MCP.Csharp

## 扫描路径资源

### 扫描路径下项目

- path 
- depth


输出格式:
```
source\repos\Hello\Hello.slnx(2+1)
├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
├─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
-test/ZMS.MCP.Test/ZMS.MCP.Test.csproj

source\repos\Hello\src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
source\repos\Hello\src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
source\repos\Hello\test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
```
格式说明。
- slnx 内描述的引用项目完整照实描述（哪怕是绝对路径）
- 客观在slnx所在文件夹下，但不在slnx描述的，在树状制表符结束后以`-`开头描述额外关系。
- slnx 描述和客观所在，都要计数。已经在slnx描述过的项目，不再作为散装项目描述。
- 散装项目路径和slnx项目路径，都以工具参数的path的相对路径描述
- `.sln` 能读就读，`.slnx` 优先

## 查看项目

### 查看slnx

- Path
查看slnx，以树状图展示，包含虚拟文件夹信息，解离xml格式。此视图和扫描路径下项目展示的不同。
```
Hello.slnx
├─src
│  ├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
│  └─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
└─test
    └─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
```

### 查看csproj

- CsproPath 

从解决方案和项目名自动定位解决方案下唯一名的项目。
返回如同直接使用文件读取，原始返回xml内容。
然后附加所有可以向上找会参与项目声明的文件，并也列出内容（例如`prop`）。

在有多个项目名重名时，仍然需要完整的相对路径。

- MSBuild 向上查找时**只取最近一个** `Directory.Build.props`（找到即停；除非该文件自己 Import 了更上层的）
- 除 props 外，参与项目声明的还有：`Directory.Packages.props`（中央包管理的版本）、`Directory.Build.targets`、`obj/<项目>.csproj.nuget.g.props|targets`、`global.json`、`NuGet.config`。逐个列出并标明路径

## 编辑解决方案

### 迁移解决方案为slnx

参数 
- path

编辑解决方案只能对slnx格式进行。
使用命令行将sln迁移为slnx

### 添加项目
参数
- slnxPath
- csprojPath
- Folder

slnx里可以为项目设置所在文件夹参数。
一个有文件夹的slnx长这样
```
<Solution>
  <Folder Name="/src/">
    <Project Path="src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj" />
    <Project Path="src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj" />
  </Folder>
  <Folder Name="/test/">
    <Project Path="test/ZMS.MCP.Test/ZMS.MCP.Test.csproj" />
  </Folder>
</Solution>
```

添加项目必须是已经存在的。如果不存在，仍然需要命令行先创建项目。

### 移除项目
参数
- slnxPath
- csprojPath

## 编辑项目

### 编辑元数据
- csprojPath
- content

简单将内容经过xml语法检查和最低csproj语法检查。
合法后才写进去

### 查看nuget包引用
- csprojPath

展示项目里的
- 顶级包
- 依赖传递包
- 项目引用而传递的顶级包

- 依赖图取自真实还原结果，不自建：`obj/project.assets.json` 的 compile 资产，或 MSBuild 评估出的 `ReferencePath` 项
- `obj` 的位置可能被 `BaseIntermediateOutputPath` 重定向，路径要问 MSBuild 的 `ProjectAssetsFile` 属性，不能硬编码 `<项目目录>/obj/`
- 数据比 `csproj`/`Directory.Build.props`/`Directory.Packages.props` 的修改时间旧时，标注"依赖图可能已过期"

### 安装nuget包
- csprojPath
- nugetPack:(nugetName,ver?)[]
  - ver可空，若如此自动找本地缓存已有的最新版。没有的拉最新非预览版。
  - 如果要更新，则需要写`*`之类的通配符。
  - 更新的新依赖比本地更新的，使用更高的自动引入版本

使用图库，对包依赖进行建图。
然后只对顶级包进行引入（除非依赖传递和参数要求的包版本不一致）

然后检查所有被依赖传递的包，对其中的任何`漏洞`，自动拉最新非漏洞非预览的包。

引入使用命令行。


返回结果为
```
#以下直接引入包是漏洞的

#引入以下包

#以下包因为被引用而未直接引入

#以下包因为漏洞被自动升级引入

#以下包被本次传递引入

#以下传递引入包原本就存在
```

- 漏洞数据不依赖 restore 产生的警告：直接取 NuGet 的漏洞索引（包名 → 受影响版本范围），拉一次本地缓存后就地做版本范围匹配，**落盘前**即可选出"最新且不漏洞"的版本
- 落盘后顺手核对 NU1901–NU1904 警告（要覆盖传递依赖需 `NuGetAuditMode=all`）；索引有滞后，这一步不能省
- "不漏洞"只代表没有**已被收录**的漏洞（GitHub Advisory / OSV），不等于安全
- 自建图只用于决策与展示，最终版本以真实还原结果为准
- 把传递包提升为直接引用 = 用直接依赖钉住版本（有意为之），落盘后要重新还原核对

### 移除nuget包
- csprojPath
- nugetName:string[]

移除前建图。移除后再建图。然后返回本次改动。
```
#本次移除包

#本次移除的依赖传递包
```

## nuget

### 查询包
- packName
- page
- local:bool
- web:bool

当local和web同时打开时,还需要描述同时存在。
```
本页查询到4个，[]标记为本地也存在

[godot.net.sdk]
[godot.sourcegenerators]
godotsharp
godotsharpeditor
```

### 查询版本

- packName
- verRange
- local:bool
- web:bool

`packName`需要是精确包名。返回这个包的版本列表。
以及不在这个列表的版本数量。
如果版本参数是`*`,则还需要返回预览版的版本数量。
当local和web同时打开时，还需要描述本地同时存在。

输出类似于这样。
```
>共查询到13个版本，还有42个预览版未列出

13.0.8
13.0.7
13.0.6
13.0.2
12.1.8
12.1.4
```
```
>共筛选到7个版本，还有6个版本未列出

12.1.8
12.1.4
```

### 查询元数据

- packName
- ver

返回readme和nuspec。先检查本地。没有再在线获取这两项。

- 本地和在线都没有时，明确返回"未找到"，并给出 nuspec 里的 `projectUrl` / `repositoryUrl`，让人自己去看

## 列出符号

### 列出文档注释符号

- packName
- ver?
- tar?
- path
- argsList?
- type?:string
  - type的范围：无序的字符串。
  - NTPFMED 
  - 缺省时基于精度
    - 不是具体类型时：T（类型）
    - 命中具体类型时：PFME（属性，字段，方法，事件）
    - 命中方法但是有重载时：M
    - 命中唯一成员时：D（文档）
    - N（命名空间）必须手动入参。并且基于文档成员完全限定名猜测。

只能从本地nuget缓存进行查找。不能搜索野生的文档注释。

实现时注意引用参数导致的重载

XML 文档条目是**平铺**的，没有层级，归属全靠名字前缀：

- `T:{类型全名}` 必须**完全相等**才算该类型本身。`T:Ns.Outer.Inner` 这类嵌套类型要单列，不能混进成员列表
- `P:{类型全名}.` 属性（含索引器，形如 `P:Ns.Type.Item(System.Int32)`）
- `F:{类型全名}.` 字段（含 `const`、**枚举成员** —— 枚举值就是 F 条目）
- `M:{类型全名}.` 方法（含 `#ctor` 构造、`#cctor` 静态构造、`op_*` 运算符、终结器）。**属性访问器不单独成条目**（XML 里没有 get/set）
- `E:{类型全名}.` 事件（`event EventHandler E;` 这种字段式事件也有 `E:` 条目）
- `!:xxx` 编译器无法解析的成员 → 跳过
- 引用参数在名字里带 `@` 后缀，如 `M:Ns.Type.Foo(System.Int32@)` —— 重载消歧要按这个匹配
- 泛型：类型形如 ``T:Ns.Type`1``，泛型方法形如 ``M:Ns.Type.Foo``1(...)``。反引号在类型名之后、成员名之前，"类型全名 + `.`" 这个成员前缀规则仍然成立
- `N:` 条目在 csc/Roslyn 生成的 XML 里**不存在**，所以命名空间只能靠手传入参 + 用类型全名反推（C# 不允许同一层里类型与子命名空间同名，所以两者互斥、不歧义；但完全没有文档注释的类型会漏）

### 列出符号

- csprojPath
- modifier?:string[]
  -  缺省时不限制。
  - 选项包括公开，程序集，保护，私有，静态，常量，抽象，只读，虚，覆写
  - 异步，密封等不影响签名的不在此列。
- type?:string
  - type的范围：无序的字符串。
  - NCSIPFEMD（N命名空间，C类型，S结构，I接口，P属性，F字段，E事件，M方法，D文档注释）
    - M 是补的：原来写的是 NCSIPFED，但方法显然要能列
    - T 也接受，等价于 C|S|I
    - D 不是"一种种类"，而是过滤开关：只列**带 XML 文档注释**的符号；只给 D 时按"所有种类"处理
    - 无法识别的字母会被忽略，并在输出里提醒（不静默吞掉）
- argsList?
  - 逗号分隔的参数类型（如 `int,string`）；给了它就只看参数完全匹配的方法

仅限于项目源码内的声明。不包含也不能反射查询其他程序集的符号。

- 与"列出文档注释符号"的 type 枚举不同是故意的：XML 里只有 `T:`，**分不出 class/struct/interface**，所以那边只有 T；源码符号能分辨，所以拆成 C/S/I

### 拟定新增或修改或删除符号

- csprojPath
- 完全限定名
  - 描述到顶级类
- 成员名字
  - 内部类也要越过多个点进行描述 
  - 方法参数也在成员名字里面
-  content
 - 为null时为删除成员

这个过程进行语法检查。语法没有通过则拟定失败。

- 拟定可以**累积**：多次调用依次叠加在同一个拟定上
- 拟定状态存 **sqlite，放在 MCP 自己的目录**（不在项目里）
- MCP 重启：从 sqlite 恢复同一个拟定
- agent 重启：靠"列出拟定"重新拿到 cookit，继续

### 拟定修改确认

- csprojPath
- cookit?

效果
- 列出拟定过程中的变更，包括增加，删除，修改的分类
- 列出因此增加或减少的编译错误和警告
- 列出执行本次更改所用的随机cookit（一个GUID）

用有cookit的调用来落盘
- 根据添加的类型命名空间和项目设置的根命名空间来决定文件路径。
- 内部类使用`Outer.Inner.cs`作为文件名，外部类使用`partial`来定义，并找到所有涉及此操作的外部类，加上`partial`.
- 对**本次改动的文件**执行 format（见"写文件通则"）
- 不做任何 git 操作（提交、分支、贮藏都不做）。要提交由调用方自己跑命令行

- 诊断对比按"错误码 + 消息 + 文件"配对，行号只用于展示（改动会挪行号，只按位置比会刷出一片假新增）
- 落盘前校验基线 hash：拟定期间被外部改动过的文件 → 报冲突、拒绝落盘，不覆盖别人的改动
- 多文件落盘不是真原子：写前日志 + 前滚（中途崩溃，下次继续补齐）

## 写文件通则

所有写操作适用。

编码
- 写回用文件**原编码**，不要自己指定
- 判定顺序：`.editorconfig` 的 `charset` → BOM 检测 → UTF-8 严格校验（能解出来）
- 三者都不成立（无 BOM 且不是合法 UTF-8 字节序列）→ **拒绝写这个文件并报告**，不猜
- 新建文件用 UTF-8 无 BOM

格式化
- `dotnet format --include <本次改动的文件列表> --no-restore`
- 不带子命令（whitespace + style + analyzers 都跑），有代码风格就按代码风格做
- `--include` 收数组，所以只跑一次、只碰改动的文件

落盘
- 单文件：写同目录临时文件 + 原子替换
- 多文件：写前日志 + 前滚
- 落盘前校验基线 hash

## 命令行类操作

- 参数封闭的 → 做成工具，但内部实现可以是调命令行（例如加包引用必须走 `dotnet add package`：项目文件该怎么改只有 MSBuild 知道，中央包管理、条件项都是它的规则）
- 参数无限的（模板参数由外部定义，如 `dotnet new`）→ 不进工具，走通用命令行通道
- 命令行改盘的操作**不进事务**（立即生效、不可回滚），返回里必须标注
