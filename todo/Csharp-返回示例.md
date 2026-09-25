# Csharp 工具返回示例（实测原文）

> 数据来源：本机实测（真实 MCP 服务走 stdio 协议逐条调用），返回**原样粘贴**。
> 用途：审阅"返回格式"本身——层次、缩进、树状图、代码块、提示语。
>
> 约定：
> - **调用** 行是这次实际传的参数；路径是本机路径。
> - 超过 60 行的返回只贴前 60 行，末尾标注"原文共 N 行 / M 字符，此处节选"。
> - 测试项目：
>   - `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo`（普通 classlib）
>   - `C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo`（50 个重载 + 200 行注释）
## 项目层

### scan_projects — 扫描仓库根

**调用**：`scan_projects(path="C:\Users\you\source\OpenSourceLibrary\ZMS.MCP")`

````text
ZMS.MCP.slnx(4+0)
├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
├─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
├─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
└─test/ZMS.MCP.Csharp.Test/ZMS.MCP.Csharp.Test.csproj
````

### list_solution_projects — 列解决方案内的项目

**调用**：`list_solution_projects(solutionPath="C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\ZMS.MCP.slnx")`

````text
# C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\ZMS.MCP.slnx

4 project(s):

- `ZMS.MCP.Cli` → `src\ZMS.MCP.Cli\ZMS.MCP.Cli.csproj`
- `ZMS.MCP.Csharp` → `src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj`
- `ZMS.MCP.Test` → `test\ZMS.MCP.Test\ZMS.MCP.Test.csproj`
- `ZMS.MCP.Csharp.Test` → `test\ZMS.MCP.Csharp.Test\ZMS.MCP.Csharp.Test.csproj`
````

### view_solution_tree — slnx 树（含虚拟文件夹）

**调用**：`view_solution_tree(path="C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\ZMS.MCP.slnx")`

````text
ZMS.MCP.slnx
├─src
│  ├─src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
│  └─src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
└─test
   ├─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
   └─test/ZMS.MCP.Csharp.Test/ZMS.MCP.Csharp.Test.csproj
````

### view_project — csproj 原文 + 参与声明的文件

**调用**：`view_project(csprojPath="C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj")`

````text
# C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj

## 项目文件
C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Description>ZMS.MCP.Csharp — Roslyn powered C# workspace tools over MCP</Description>
    <RootNamespace>ZMS.MCP.Csharp</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.9.0" />
    <!-- 拟定状态要落在 sqlite 里（放在 MCP 自己的目录，不进项目） -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.0" />
  </ItemGroup>

</Project>
```

## 目录级（MSBuild 自动导入，取最近一份） — Directory.Build.props
C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\Directory.Build.props
```xml
<Project>

  <!-- ===== 项目通用元数据 ===== -->
  <PropertyGroup>
    <Authors>zms9110750</Authors>
    <Version>0.1.0-a.0</Version>
    <RepositoryUrl>https://github.com/$(Authors)/ZMS.MCP</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
  </PropertyGroup>

  <!-- ===== 程序集名与命名空间根 ===== -->
  <PropertyGroup>
    <RootNamespace Condition="'$(Authors)' != ''">$(Authors).$(MSBuildProjectName)</RootNamespace>
    <RootNamespace Condition="'$(Authors)' == ''">$(MSBuildProjectName)</RootNamespace>
    <AssemblyName>$(RootNamespace)</AssemblyName>
  </PropertyGroup>

  <!-- ===== InternalsVisibleTo：向同名测试项目开放 internal ===== -->
  <ItemGroup>
    <InternalsVisibleTo Include="$(AssemblyName).Test" />
  </ItemGroup>

  <!-- ===== MCP 包（所有项目都需要） ===== -->
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.2.0" />
  </ItemGroup>

  <!-- ===== 全局 Global.cs（所有项目共享：MCP global using + 通用入口） ===== -->
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)Global.cs" />
  </ItemGroup>

</Project>
```

## 还原生成（属性） — obj 位置取自 MSBuild 的 MSBuildProjectExtensionsPath
C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\obj\ZMS.MCP.Csharp.csproj.nuget.g.props
```xml
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
    <RestoreSuccess Condition=" '$(RestoreSuccess)' == '' ">True</RestoreSuccess>
    <RestoreTool Condition=" '$(RestoreTool)' == '' ">NuGet</RestoreTool>
    <ProjectAssetsFile Condition=" '$(ProjectAssetsFile)' == '' ">$(MSBuildThisFileDirectory)project.assets.json</ProjectAssetsFile>
    <NuGetPackageRoot Condition=" '$(NuGetPackageRoot)' == '' ">C:\dotnet\nuget-packages</NuGetPackageRoot>
    <NuGetPackageFolders Condition=" '$(NuGetPackageFolders)' == '' ">C:\dotnet\nuget-packages;B:\Visual Studio\Shared\NuGetPackages</NuGetPackageFolders>
    <NuGetProjectStyle Condition=" '$(NuGetProjectStyle)' == '' ">PackageReference</NuGetProjectStyle>
    <NuGetToolVersion Condition=" '$(NuGetToolVersion)' == '' ">7.0.0</NuGetToolVersion>
  </PropertyGroup>
  <ItemGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
…（原文共 109 行 / 6961 字符，此处节选前 80 行）
````

### list_project_packages — 顶级/传递/项目引用带来的包

**调用**：`list_project_packages(csprojPath="C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj")`

````text
# 包引用
- 项目：C:\Users\you\source\OpenSourceLibrary\ZMS.MCP\src\ZMS.MCP.Csharp\ZMS.MCP.Csharp.csproj
- 依赖图来源：ReferencePath（还原产物缺失，可能不全）
- assets：
- ⚠ 依赖图可能已过期（csproj / props 比还原产物新）

## 顶级包（直接引用）
- Microsoft.CodeAnalysis.CSharp 5.9.0
- Microsoft.CodeAnalysis.CSharp.Workspaces 5.9.0
- Microsoft.Data.Sqlite 10.0.0
- Microsoft.Extensions.Hosting 10.0.12

## 依赖传递包
- microsoft.codeanalysis.common 5.9.0
- microsoft.codeanalysis.workspaces.common 5.9.0
- microsoft.data.sqlite.core 10.0.0
- microsoft.extensions.ai.abstractions 10.8.3
- microsoft.extensions.caching.abstractions 10.0.10
- microsoft.extensions.configuration 10.0.12
- microsoft.extensions.configuration.abstractions 10.0.12
- microsoft.extensions.configuration.binder 10.0.12
- microsoft.extensions.configuration.commandline 10.0.12
- microsoft.extensions.configuration.environmentvariables 10.0.12
- microsoft.extensions.configuration.fileextensions 10.0.12
- microsoft.extensions.configuration.json 10.0.12
- microsoft.extensions.configuration.usersecrets 10.0.12
- microsoft.extensions.dependencyinjection 10.0.12
- microsoft.extensions.dependencyinjection.abstractions 10.0.12
- microsoft.extensions.diagnostics 10.0.12
- microsoft.extensions.diagnostics.abstractions 10.0.12
- microsoft.extensions.fileproviders.abstractions 10.0.12
- microsoft.extensions.fileproviders.physical 10.0.12
- microsoft.extensions.filesystemglobbing 10.0.12
- microsoft.extensions.hosting.abstractions 10.0.12
- microsoft.extensions.logging 10.0.12
- microsoft.extensions.logging.abstractions 10.0.12
- microsoft.extensions.logging.configuration 10.0.12
- microsoft.extensions.logging.console 10.0.12
- microsoft.extensions.logging.debug 10.0.12
- microsoft.extensions.logging.eventlog 10.0.12
- microsoft.extensions.logging.eventsource 10.0.12
- microsoft.extensions.options 10.0.12
- microsoft.extensions.options.configurationextensions 10.0.12
- microsoft.extensions.primitives 10.0.12
- microsoft.netcore.app.ref 10.0.12
…（原文共 60 行 / 2452 字符，此处节选前 45 行）
````

## 项目层（写操作，在测试项目上）

### migrate_solution_to_slnx — .sln → .slnx（命令行改盘，不进事务）

**调用**：`migrate_solution_to_slnx(path="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221")`

````text
# 迁移完成（命令行改盘，不进事务）
- 源：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.sln
- 目标：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx

已生成 .slnx 文件 C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx。
````

### add_project_to_solution — 加入项目并放进虚拟文件夹

**调用**：`add_project_to_solution(slnxPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx", csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", folder="src/Core")`

````text
✅ Added C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj to C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx（/src/Core/）

```
已将项目“Demo\Demo.csproj”添加到解决方案中。
```
````

### view_solution_tree — 上一步写入后的 slnx

**调用**：`view_solution_tree(path="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx")`

````text
Demo.slnx
└─src
   └─Core
      └─Demo/Demo.csproj
````

### remove_project_from_solution — 从解决方案移除

**调用**：`remove_project_from_solution(slnxPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx", csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj")`

````text
✅ Removed C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj from C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo.slnx

```
已从解决方案中移除项目“Demo\Demo.csproj”。
```
````

### edit_project_metadata — dryRun 预演

**调用**：`edit_project_metadata(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", content=<加了 Description 的 csproj>, dryRun=true)`

````text
# 编辑元数据
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- 编码：utf-8（来自 BOM）
- **预演，未写入**。

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Description>demo project used by Csharp return-sample doc</Description>
  </PropertyGroup>

</Project>
```
````

### edit_project_metadata — 真写入

**调用**：`edit_project_metadata(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", content=<加了 Description 的 csproj>, dryRun=false)`

````text
# 编辑元数据
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- 编码：utf-8（来自 BOM）
- 已写入（XML 语法检查 + 根元素 Project 检查通过）。
````

### install_packages — dryRun（只决策）

**调用**：`install_packages(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", nugetPack=["Newtonsoft.Json@13.0.3"], dryRun=true)`

````text
# 以下直接引入包是漏洞的
Newtonsoft.Json 13.0.3（漏洞索引不可用，未经核对）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
Microsoft.CSharp
NETStandard.Library
System.ComponentModel.TypeConverter
System.Runtime.Serialization.Primitives
System.Runtime.Serialization.Formatters
System.Xml.XmlDocument
System.Dynamic.Runtime
System.Reflection.TypeExtensions
Microsoft.NETCore.Platforms
System.Runtime.InteropServices.RuntimeInformation
Microsoft.Win32.Primitives
System.AppContext
System.Console
System.Globalization.Calendars
System.IO.Compression
System.IO.Compression.ZipFile
System.IO.FileSystem
System.IO.FileSystem.Primitives
System.Net.Http
System.Net.Sockets
System.Security.Cryptography.Algorithms
System.Security.Cryptography.Encoding
System.Security.Cryptography.Primitives
System.Security.Cryptography.X509Certificates
System.Xml.ReaderWriter
System.Collections
System.Diagnostics.Debug
System.Diagnostics.Tools
System.Globalization
System.IO
System.Linq
System.Linq.Expressions
System.Net.Primitives
System.ObjectModel
System.Reflection
System.Reflection.Extensions
System.Reflection.Primitives
System.Resources.ResourceManager
System.Runtime
System.Runtime.Extensions
System.Text.Encoding
System.Text.Encoding.Extensions
System.Text.RegularExpressions
System.Threading
System.Threading.Tasks
System.Xml.XDocument
System.Collections.Concurrent
System.Diagnostics.Tracing
System.Runtime.InteropServices
System.Runtime.Numerics
System.Threading.Timer
System.Runtime.Handles
System.Diagnostics.Contracts
…（原文共 158 行 / 4154 字符，此处节选前 60 行）
````

### install_packages — 真安装（dotnet add package）

**调用**：`install_packages(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", nugetPack=["Newtonsoft.Json@13.0.3"], dryRun=false)`

````text
# 以下直接引入包是漏洞的
Newtonsoft.Json 13.0.3（漏洞索引不可用，未经核对）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
Microsoft.CSharp
NETStandard.Library
System.ComponentModel.TypeConverter
System.Runtime.Serialization.Primitives
System.Runtime.Serialization.Formatters
System.Xml.XmlDocument
System.Dynamic.Runtime
System.Reflection.TypeExtensions
Microsoft.NETCore.Platforms
System.Runtime.InteropServices.RuntimeInformation
Microsoft.Win32.Primitives
System.AppContext
System.Console
System.Globalization.Calendars
System.IO.Compression
System.IO.Compression.ZipFile
System.IO.FileSystem
System.IO.FileSystem.Primitives
System.Net.Http
System.Net.Sockets
System.Security.Cryptography.Algorithms
System.Security.Cryptography.Encoding
System.Security.Cryptography.Primitives
System.Security.Cryptography.X509Certificates
System.Xml.ReaderWriter
System.Collections
System.Diagnostics.Debug
System.Diagnostics.Tools
System.Globalization
System.IO
System.Linq
System.Linq.Expressions
System.Net.Primitives
System.ObjectModel
System.Reflection
System.Reflection.Extensions
System.Reflection.Primitives
System.Resources.ResourceManager
System.Runtime
System.Runtime.Extensions
System.Text.Encoding
System.Text.Encoding.Extensions
System.Text.RegularExpressions
System.Threading
System.Threading.Tasks
System.Xml.XDocument
System.Collections.Concurrent
System.Diagnostics.Tracing
System.Runtime.InteropServices
System.Runtime.Numerics
System.Threading.Timer
System.Runtime.Handles
System.Diagnostics.Contracts
runtime.native.System
System.Diagnostics.DiagnosticSource
System.Runtime.WindowsRuntime
runtime.native.System.Net.Http
runtime.native.System.Security.Cryptography.OpenSsl
System.Globalization.Extensions
System.Security.Cryptography.OpenSsl
runtime.native.System.Security.Cryptography.Apple
System.Security.Cryptography.Cng
System.Security.Cryptography.Csp
…（原文共 156 行 / 4139 字符，此处节选前 70 行）
````

### remove_packages — dryRun

**调用**：`remove_packages(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", nugetName=["Newtonsoft.Json"], dryRun=true)`

````text
# 本次移除包
Newtonsoft.Json

# 本次移除的依赖传递包
（无）

（预演，未真正执行；上面「依赖传递包」一栏按实际移除后的图算，预演时为 0。）
````

### remove_packages — 真移除

**调用**：`remove_packages(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", nugetName=["Newtonsoft.Json"], dryRun=false)`

````text
# 本次移除包
Newtonsoft.Json

# 本次移除的依赖传递包
Newtonsoft.Json
````

## NuGet 层

### search_packages — 本地 + 线上，[] 标记本地也有

**调用**：`search_packages(packName="Serilog.Sinks.File", page=0, local=true, web=true)`

````text
本页查询到 15 个，[] 标记为本地也存在

[Serilog.Sinks.File]
Serilog.Sinks.RollingFile
Serilog.Sinks.FileEx
Serilog.Sinks.File.Archive
Serilog.Sinks.File.Header
Serilog.Sinks.File.GZip
Serilog.Sinks.AsyncFile
Serilog.Sinks.RollingFileAlternate
Serilog.Sinks.File.Extensions
Serilog.Sinks.ZipFile
Serilog.Sinks.AmazonS3
Serilog.Sinks.RollingFile.Extension
Serilog.Sinks.ContextRollingFile
Serilog.Sinks.PersistentFile
Serilog.Sinks.AsyncRollingFile
````

### search_packages — 只要本地（空关键字 = 列出本地全部）

**调用**：`search_packages(packName="", local=true, web=false)`

````text
# 本地查询到 519 个

acornima
alder
anglesharp
anglesharp.css
anglesharp.diffing
argon
assemblypublicizer
autofac
autofac.extensions.dependencyinjection
autofac.pooling
axion.extensions.caching.hybrid.serialization.http
axion.extensions.http.resilience.caching.hybrid
axion.extensions.polly.caching.hybrid
aze.publicise.msbuild.task
azure.ai.openai
azure.core
azure.identity
bemit
benchmarkdotnet
benchmarkdotnet.annotations
blake3
blazor.localstorage.webassembly
blazor.serialization
…（原文共 522 行 / 14932 字符，此处节选前 25 行）
````

### list_package_versions — 本地缓存的版本

**调用**：`list_package_versions(packName="Newtonsoft.Json", verRange="", local=true, web=false)`

````text
# Newtonsoft.Json（本地缓存）
> 共筛选到 2 个版本，还有 0 个预览版未列出

13.0.3
13.0.2
````

### list_package_versions — 线上按版本范围筛

**调用**：`list_package_versions(packName="Serilog", verRange="[4.0,5.0)", local=false, web=true)`

````text
# Serilog（nuget.org）
> 共筛选到 8 个版本，还有 0 个预览版未列出

4.4.0
4.3.1
4.3.0
4.2.0
4.1.0
4.0.2
4.0.1
4.0.0
````

### get_package_metadata — nuspec + readme（本地缓存）

**调用**：`get_package_metadata(packName="Newtonsoft.Json")`

````text
# Newtonsoft.Json 13.0.3（本地缓存）

## nuspec
```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Newtonsoft.Json</id>
    <version>13.0.3</version>
    <title>Json.NET</title>
    <authors>James Newton-King</authors>
    <license type="expression">MIT</license>
    <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
    <icon>packageIcon.png</icon>
    <readme>README.md</readme>
    <projectUrl>https://www.newtonsoft.com/json</projectUrl>
    <iconUrl>https://www.newtonsoft.com/content/images/nugeticon.png</iconUrl>
    <description>Json.NET is a popular high-performance JSON framework for .NET</description>
    <copyright>Copyright © James Newton-King 2008</copyright>
    <tags>json</tags>
    <repository type="git" url="https://github.com/JamesNK/Newtonsoft.Json" commit="0a2e291c0d9c0c7675d445703e51750363a549ef" />
    <dependencies>
      <group targetFramework=".NETFramework2.0" />
      <group targetFramework=".NETFramework3.5" />
      <group targetFramework=".NETFramework4.0" />
      <group targetFramework=".NETFramework4.5" />
      <group targetFramework=".NETStandard1.0">
        <dependency id="Microsoft.CSharp" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="NETStandard.Library" version="1.6.1" exclude="Build,Analyzers" />
        <dependency id="System.ComponentModel.TypeConverter" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="System.Runtime.Serialization.Primitives" version="4.3.0" exclude="Build,Analyzers" />
      </group>
      <group targetFramework=".NETStandard1.3">
        <dependency id="Microsoft.CSharp" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="NETStandard.Library" version="1.6.1" exclude="Build,Analyzers" />
        <dependency id="System.ComponentModel.TypeConverter" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="System.Runtime.Serialization.Formatters" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="System.Runtime.Serialization.Primitives" version="4.3.0" exclude="Build,Analyzers" />
        <dependency id="System.Xml.XmlDocument" version="4.3.0" exclude="Build,Analyzers" />
      </group>
      <group targetFramework="net6.0" />
      <group targetFramework=".NETStandard2.0" />
    </dependencies>
  </metadata>
</package>
…（原文共 120 行 / 4445 字符，此处节选前 45 行）
````

### get_package_metadata — 版本不存在

**调用**：`get_package_metadata(packName="Newtonsoft.Json", ver="9.9.9")`

````text
# Newtonsoft.Json 9.9.9
未找到（本地缓存与 nuget.org 都没有）。
````

### list_doc_symbols — 显式 type=PF（属性+字段）

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.JsonConvert", type="PF")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 8 | 生效 type: `PF`（显式指定）

- `P:Newtonsoft.Json.JsonConvert.DefaultSettings` — Gets or sets a function that creates default . Default settings are automatically used by serialization methods on , and  and  on . To serialize without using any default settings create a  with .
- `F:Newtonsoft.Json.JsonConvert.True` — Represents JavaScript's boolean value true as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.False` — Represents JavaScript's boolean value false as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.Null` — Represents JavaScript's null as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.Undefined` — Represents JavaScript's undefined as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.PositiveInfinity` — Represents JavaScript's positive infinity as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.NegativeInfinity` — Represents JavaScript's negative infinity as a string. This field is read-only.
- `F:Newtonsoft.Json.JsonConvert.NaN` — Represents JavaScript's NaN as a string. This field is read-only.
````

### list_doc_symbols — 唯一成员 → D（原始 XML 片段）

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.JsonConvert.SerializeObject", argsList="object")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 1 | 生效 type: `D`（按精度推断）

```xml
<member name="M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)">
  <summary>
            Serializes the specified object to a JSON string.
            </summary>
  <param name="value">The object to serialize.</param>
  <returns>A JSON string representation of the object.</returns>
</member>
```
````

### list_doc_symbols — 显式 type=M（全部重载）

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.JsonConvert.SerializeObject", type="M")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 8 | 生效 type: `M`（显式指定）

- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)` — Serializes the specified object to a JSON string.
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting)` — Serializes the specified object to a JSON string using formatting.
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonConverter[])` — Serializes the specified object to a JSON string using a collection of .
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonConverter[])` — Serializes the specified object to a JSON string using formatting and a collection of .
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonSerializerSettings)` — Serializes the specified object to a JSON string using .
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.JsonSerializerSettings)` — Serializes the specified object to a JSON string using a type, formatting and .
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)` — Serializes the specified object to a JSON string using formatting and .
- `M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)` — Serializes the specified object to a JSON string using a type, formatting and .
````

### list_doc_symbols — 命名空间（前缀反推）

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.Linq", type="N")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 25 | 生效 type: `N`（显式指定）
- 提示: XML 里没有 N: 条目，命名空间只能靠调用方手传前缀 + 类型全名反推（没有文档注释的类型会漏）。

- `T:Newtonsoft.Json.Linq.CommentHandling` — Specifies how JSON comments are handled when loading JSON.
- `T:Newtonsoft.Json.Linq.DuplicatePropertyNameHandling` — Specifies how duplicate property names are handled when loading JSON.
- `T:Newtonsoft.Json.Linq.Extensions` — Contains the LINQ to JSON extension methods.
- `T:Newtonsoft.Json.Linq.IJEnumerable`1` — Represents a collection of  objects.
- `T:Newtonsoft.Json.Linq.JArray` — Represents a JSON array.
- `T:Newtonsoft.Json.Linq.JConstructor` — Represents a JSON constructor.
- `T:Newtonsoft.Json.Linq.JContainer` — Represents a token that can contain other tokens.
- `T:Newtonsoft.Json.Linq.JEnumerable`1` — Represents a collection of  objects.
- `T:Newtonsoft.Json.Linq.JObject` — Represents a JSON object.
- `T:Newtonsoft.Json.Linq.JProperty` — Represents a JSON property.
- `T:Newtonsoft.Json.Linq.JPropertyDescriptor` — Represents a view of a .
- `T:Newtonsoft.Json.Linq.JRaw` — Represents a raw JSON string.
- `T:Newtonsoft.Json.Linq.JsonCloneSettings` — Specifies the settings used when cloning JSON.
- `T:Newtonsoft.Json.Linq.JsonLoadSettings` — Specifies the settings used when loading JSON.
- `T:Newtonsoft.Json.Linq.JsonMergeSettings` — Specifies the settings used when merging JSON.
…（原文共 31 行 / 2519 字符，此处节选前 20 行）
````

## 符号层（在测试项目 demo 上）

### list_types — 项目内所有类型

**调用**：`list_types(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj")`

````text
# C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- TFM: `net11.0` | source files: 1 | types: 1

- `Demo.Class1` (class) — Class1.cs:3
````

### list_members — 某类型的成员

**调用**：`list_members(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", typePath="Demo.Class1")`

````text
# Demo.Class1  (class)
- TFM: `net11.0`

- `Demo.Class1.Add(int, int)` — Class1.cs:5
- `Demo.Class1.Sub(int, int)` — Class1.cs:7
````

### get_member — 给类型名（整个类型：签名 + 行号 + 源码）

**调用**：`get_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1")`

````text
## Demo.Class1

- Kind: `NamedType`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs`
- Lines: 3-8

```csharp
public partial class Class1
{
    public static int Add(int a, int b) { return a + b; }

    public static int Sub(int a, int b) { return a - b; }
}
```
````

### get_member — 给成员（带参数消歧）

**调用**：`get_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1.Add(int,int)")`

````text
## Demo.Class1.Add(int, int)

- Kind: `Method`
- Declaring type: `Demo.Class1`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs`
- Lines: 5-5

```csharp
    public static int Add(int a, int b) { return a + b; }
```
````

### list_symbols — 全部符号（NCSIPFEMD）

**调用**：`list_symbols(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", type="NCSIPFEMD")`

````text
# C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 0 | 过滤: type='NCSIPFEMD' modifier='' args=''

_(无匹配符号)_
````

### list_symbols — 方法 + 修饰符 + 参数过滤

**调用**：`list_symbols(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", type="M", modifier="public,static", argsList="int,int")`

````text
# C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 2 | 过滤: type='M' modifier='public,static' args='int,int'

## Demo
  - `public static int Add(int a, int b)` (method) [Class1] — Class1.cs:5
  - `public static int Sub(int a, int b)` (method) [Class1] — Class1.cs:7
````

### list_symbols — 带无法识别的字母（应有提醒）

**调用**：`list_symbols(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", type="Cx")`

````text
# C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- TFM: `net11.0` | 符号: 1 | 过滤: type='Cx' modifier='' args=''
⚠ 无法识别的 type 字母已忽略：X（可用：N C S I T P F E M D）

## Demo
- `class Class1` (class) — Class1.cs:3
````

### add_member — 插到指定成员之前（即时落盘）

**调用**：`add_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", typePath="Demo.Class1", code="public int Mul(int a, int b) { return a * b; }", before="Add")`

````text
✅ added `new member in Demo.Class1`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs` (Class1.cs)
- Lines: 5-5

Re-read with GetMember to verify the result.
````

### update_member — 整段替换成员

**调用**：`update_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1.Add(int,int)", code=<新方法体>)`

````text
✅ replaced `Demo.Class1.Add(int, int)`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs` (Class1.cs)
- Lines: 6-9

Re-read with GetMember to verify the result.
````

### get_member — 替换后的同一个成员

**调用**：`get_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1.Add(int,int)")`

````text
## Demo.Class1.Add(int, int)

- Kind: `Method`
- Declaring type: `Demo.Class1`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs`
- Lines: 6-9

```csharp
    public static int Add(int a, int b)
    {
        return a + b + 1;
    }
```
````

### remove_member — 删除成员

**调用**：`remove_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1.Sub(int,int)")`

````text
✅ removed `Demo.Class1.Sub(int, int)`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs` (Class1.cs)
- Lines: 11-11

Re-read with GetMember to verify the result.
````

### get_member — 增删改之后的类型

**调用**：`get_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1")`

````text
## Demo.Class1

- Kind: `NamedType`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs`
- Lines: 3-11

```csharp
public partial class Class1
{
    public int Mul(int a, int b) { return a * b; }
    public static int Add(int a, int b)
    {
        return a + b + 1;
    }

}
```
````

## 拟定层（stage → list → confirm，测试项目 demo）

### stage_draft — 拟定新增成员

**调用**：`stage_draft(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", typePath="Demo.Class1", memberName="Div(int,int)", content="public static int Div(int a, int b) { return a / b; }")`

````text
# 拟定已累加
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- cookit：`32bb2b33-64f4-49a7-b10e-7f43a8aea52d`
- 本次：在 Demo.Class1 新增成员 Div(int,int)

- 本次涉及的文件：
  - C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs（added）
````

### list_draft — 列出拟定与 cookit

**调用**：`list_draft(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj")`

````text
# 拟定
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- cookit：`32bb2b33-64f4-49a7-b10e-7f43a8aea52d`
- 起始时间：2026-09-23T20:58:21.2053276+00:00
- 条数：1

- [1] 增加 Demo.Class1.Div(int,int) → C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs
````

### confirm_draft — 不带 cookit = 预演

**调用**：`confirm_draft(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", cookit="", apply=true)`

````text
# 拟定确认
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- cookit：`32bb2b33-64f4-49a7-b10e-7f43a8aea52d`

## 变更分类
### 增加（1）
- Demo.Class1.Div(int,int) → C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：无
- 消失：无

（预演，未落盘。带准确的 cookit 再调用一次即落盘。）
````

### confirm_draft — 带 cookit = 真落盘（含 dotnet format）

**调用**：`confirm_draft(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", cookit="32bb2b33-64f4-49a7-b10e-7f43a8aea52d", apply=true)`

````text
# 拟定确认
- 项目：C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
- cookit：`32bb2b33-64f4-49a7-b10e-7f43a8aea52d`

## 变更分类
### 增加（1）
- Demo.Class1.Div(int,int) → C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs

## 诊断对比（按 错误码 + 消息 + 文件 配对，行号只用于展示）
- 新增：无
- 消失：无

## 已落盘
- C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs
- 编码：按各文件原编码写回（新建文件 UTF-8 无 BOM）
- 格式化：已对本次改动的文件跑 dotnet format
- 未做任何 git 操作（提交/分支/贮藏都不动）
````

### confirm_draft — 落盘后再调一次（拟定已清）

**调用**：`confirm_draft(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", cookit="32bb2b33-64f4-49a7-b10e-7f43a8aea52d", apply=true)`

````text
# 拟定确认
C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj
没有未完成的拟定。
````

### get_member — 落盘后的类型

**调用**：`get_member(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", memberPath="Demo.Class1")`

````text
## Demo.Class1

- Kind: `NamedType`
- File: `C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Class1.cs`
- Lines: 3-12

```csharp
public partial class Class1
{
    public int Mul(int a, int b) { return a * b; }
    public static int Add(int a, int b)
    {
        return a + b + 1;
    }

    public static int Div(int a, int b) { return a / b; }
}
```
````

## 边界返回（测试项目 edge：50 个重载 + 200 行 XML 注释）

### list_symbols type=M — 51 个方法（重载）

**调用**：`list_symbols(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", type="M")`

````text
# C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj
- TFM: `net10.0` | 符号: 51 | 过滤: type='M' modifier='' args=''

## Demo
  - `public void Documented()` (method) [Bloat] — Bloat.cs:207
  - `public void Many(int p1)` (method) [Bloat] — Bloat.cs:209
  - `public void Many(int p1, int p2)` (method) [Bloat] — Bloat.cs:210
  - `public void Many(int p1, int p2, int p3)` (method) [Bloat] — Bloat.cs:211
  - `public void Many(int p1, int p2, int p3, int p4)` (method) [Bloat] — Bloat.cs:212
  - `public void Many(int p1, int p2, int p3, int p4, int p5)` (method) [Bloat] — Bloat.cs:213
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6)` (method) [Bloat] — Bloat.cs:214
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7)` (method) [Bloat] — Bloat.cs:215
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)` (method) [Bloat] — Bloat.cs:216
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9)` (method) [Bloat] — Bloat.cs:217
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10)` (method) [Bloat] — Bloat.cs:218
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11)` (method) [Bloat] — Bloat.cs:219
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12)` (method) [Bloat] — Bloat.cs:220
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13)` (method) [Bloat] — Bloat.cs:221
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14)` (method) [Bloat] — Bloat.cs:222
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15)` (method) [Bloat] — Bloat.cs:223
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15, int p16)` (method) [Bloat] — Bloat.cs:224
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15, int p16, int p17)` (method) [Bloat] — Bloat.cs:225
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15, int p16, int p17, int p18)` (method) [Bloat] — Bloat.cs:226
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15, int p16, int p17, int p18, int p19)` (method) [Bloat] — Bloat.cs:227
  - `public void Many(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, int p11, int p12, int p13, int p14, int p15, int p16, int p17, int p18, int p19, int p20)` (method) [Bloat] — Bloat.cs:228
…（原文共 56 行 / 14112 字符，此处节选前 25 行）
````

### list_members — 51 个成员

**调用**：`list_members(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", typePath="Demo.Bloat")`

````text
# Demo.Bloat  (class)
- TFM: `net10.0`

- `Demo.Bloat.Documented()` — Bloat.cs:207
- `Demo.Bloat.Many(int)` — Bloat.cs:209
- `Demo.Bloat.Many(int, int)` — Bloat.cs:210
- `Demo.Bloat.Many(int, int, int)` — Bloat.cs:211
- `Demo.Bloat.Many(int, int, int, int)` — Bloat.cs:212
- `Demo.Bloat.Many(int, int, int, int, int)` — Bloat.cs:213
- `Demo.Bloat.Many(int, int, int, int, int, int)` — Bloat.cs:214
- `Demo.Bloat.Many(int, int, int, int, int, int, int)` — Bloat.cs:215
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int)` — Bloat.cs:216
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int)` — Bloat.cs:217
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:218
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:219
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:220
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:221
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:222
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:223
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:224
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:225
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:226
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:227
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:228
- `Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:229
…（原文共 55 行 / 8262 字符，此处节选前 25 行）
````

### get_member — 带 200 行 XML 注释的成员

**调用**：`get_member(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", memberPath="Demo.Bloat.Documented")`

````text
## Demo.Bloat.Documented()

- Kind: `Method`
- Declaring type: `Demo.Bloat`

### Documentation
```xml
<member name="M:Demo.Bloat.Documented">
    <summary>
    Documentation line 1 : this comment block exists only to make the member huge.
    Documentation line 2 : this comment block exists only to make the member huge.
    Documentation line 3 : this comment block exists only to make the member huge.
    Documentation line 4 : this comment block exists only to make the member huge.
    Documentation line 5 : this comment block exists only to make the member huge.
    Documentation line 6 : this comment block exists only to make the member huge.
    Documentation line 7 : this comment block exists only to make the member huge.
    Documentation line 8 : this comment block exists only to make the member huge.
    Documentation line 9 : this comment block exists only to make the member huge.
    Documentation line 10 : this comment block exists only to make the member huge.
    Documentation line 11 : this comment block exists only to make the member huge.
    Documentation line 12 : this comment block exists only to make the member huge.
    Documentation line 13 : this comment block exists only to make the member huge.
    Documentation line 14 : this comment block exists only to make the member huge.
    Documentation line 15 : this comment block exists only to make the member huge.
    Documentation line 16 : this comment block exists only to make the member huge.
    Documentation line 17 : this comment block exists only to make the member huge.
    Documentation line 18 : this comment block exists only to make the member huge.
    Documentation line 19 : this comment block exists only to make the member huge.
    Documentation line 20 : this comment block exists only to make the member huge.
    Documentation line 21 : this comment block exists only to make the member huge.
    Documentation line 22 : this comment block exists only to make the member huge.
    Documentation line 23 : this comment block exists only to make the member huge.
    Documentation line 24 : this comment block exists only to make the member huge.
    Documentation line 25 : this comment block exists only to make the member huge.
    Documentation line 26 : this comment block exists only to make the member huge.
    Documentation line 27 : this comment block exists only to make the member huge.
    Documentation line 28 : this comment block exists only to make the member huge.
    Documentation line 29 : this comment block exists only to make the member huge.
    Documentation line 30 : this comment block exists only to make the member huge.
    Documentation line 31 : this comment block exists only to make the member huge.
    Documentation line 32 : this comment block exists only to make the member huge.
    Documentation line 33 : this comment block exists only to make the member huge.
    Documentation line 34 : this comment block exists only to make the member huge.
    Documentation line 35 : this comment block exists only to make the member huge.
    Documentation line 36 : this comment block exists only to make the member huge.
…（原文共 219 行 / 17478 字符，此处节选前 45 行）
````

### get_member — 整个类型（259 行的类）

**调用**：`get_member(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", memberPath="Demo.Bloat")`

````text
## Demo.Bloat

- Kind: `NamedType`
- File: `C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Bloat.cs`
- Lines: 3-259

```csharp
public class Bloat
{
    /// <summary>
    /// Documentation line 1 : this comment block exists only to make the member huge.
    /// Documentation line 2 : this comment block exists only to make the member huge.
    /// Documentation line 3 : this comment block exists only to make the member huge.
    /// Documentation line 4 : this comment block exists only to make the member huge.
    /// Documentation line 5 : this comment block exists only to make the member huge.
    /// Documentation line 6 : this comment block exists only to make the member huge.
    /// Documentation line 7 : this comment block exists only to make the member huge.
    /// Documentation line 8 : this comment block exists only to make the member huge.
    /// Documentation line 9 : this comment block exists only to make the member huge.
    /// Documentation line 10 : this comment block exists only to make the member huge.
    /// Documentation line 11 : this comment block exists only to make the member huge.
    /// Documentation line 12 : this comment block exists only to make the member huge.
    /// Documentation line 13 : this comment block exists only to make the member huge.
    /// Documentation line 14 : this comment block exists only to make the member huge.
    /// Documentation line 15 : this comment block exists only to make the member huge.
    /// Documentation line 16 : this comment block exists only to make the member huge.
    /// Documentation line 17 : this comment block exists only to make the member huge.
    /// Documentation line 18 : this comment block exists only to make the member huge.
    /// Documentation line 19 : this comment block exists only to make the member huge.
    /// Documentation line 20 : this comment block exists only to make the member huge.
    /// Documentation line 21 : this comment block exists only to make the member huge.
    /// Documentation line 22 : this comment block exists only to make the member huge.
    /// Documentation line 23 : this comment block exists only to make the member huge.
    /// Documentation line 24 : this comment block exists only to make the member huge.
    /// Documentation line 25 : this comment block exists only to make the member huge.
    /// Documentation line 26 : this comment block exists only to make the member huge.
    /// Documentation line 27 : this comment block exists only to make the member huge.
    /// Documentation line 28 : this comment block exists only to make the member huge.
    /// Documentation line 29 : this comment block exists only to make the member huge.
    /// Documentation line 30 : this comment block exists only to make the member huge.
…（原文共 266 行 / 30545 字符，此处节选前 40 行）
````

### get_member — 重载不消歧（报错列候选）

**调用**：`get_member(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", memberPath="Demo.Bloat.Many")`

````text
Error: 'Many' matches 50 overloads, add a parameter list to disambiguate:
  - Demo.Bloat.Many(int)
  - Demo.Bloat.Many(int, int)
  - Demo.Bloat.Many(int, int, int)
  - Demo.Bloat.Many(int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
…（原文共 51 行 / 7448 字符，此处节选前 30 行）
````

### get_member — 消歧后的正常返回

**调用**：`get_member(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", memberPath="Demo.Bloat.Many(int)")`

````text
## Demo.Bloat.Many(int)

- Kind: `Method`
- Declaring type: `Demo.Bloat`
- File: `C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Bloat.cs`
- Lines: 209-209

```csharp
    public void Many(int p1) { }
```
````

### stage_draft — 重载不消歧（报错列候选）

**调用**：`stage_draft(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", typePath="Demo.Bloat", memberName="Many", content="public void Many(int q) { }")`

````text
Error: 'Many' 匹配到 50 个重载，请带上参数列表消歧：
  - Demo.Bloat.Many(int)
  - Demo.Bloat.Many(int, int)
  - Demo.Bloat.Many(int, int, int)
  - Demo.Bloat.Many(int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
  - Demo.Bloat.Many(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)
…（原文共 51 行 / 7410 字符，此处节选前 30 行）
````

### list_symbols type=D — 只看带 XML 注释的符号

**调用**：`list_symbols(csprojPath="C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj", type="D")`

````text
# C:\demo\zms-edge-54e630f0e8aa44deb254c7e61fbdb44f\Demo\Demo.csproj
- TFM: `net10.0` | 符号: 1 | 过滤: type='D' modifier='' args=''

## Demo
  - `public void Documented()` (method) [Bloat] — Bloat.cs:207
````

## 修复对照：install_packages 的漏洞索引（修 index.json 解析之后）

### install_packages — dryRun（漏洞索引应当已可用）

**调用**：`install_packages(csprojPath="C:\demo\zms-draft-demo-d14430258ca047f0b9a242ed7e173221\Demo\Demo.csproj", nugetPack=["Newtonsoft.Json@13.0.3"], dryRun=true)`

````text
# 以下直接引入包是漏洞的
（无）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
Microsoft.CSharp
NETStandard.Library
System.ComponentModel.TypeConverter
System.Runtime.Serialization.Primitives
System.Runtime.Serialization.Formatters
System.Xml.XmlDocument
System.Dynamic.Runtime
System.Reflection.TypeExtensions
Microsoft.NETCore.Platforms
System.Runtime.InteropServices.RuntimeInformation
Microsoft.Win32.Primitives
System.AppContext
System.Console
…（原文共 158 行 / 4133 字符，此处节选前 20 行）
````

## 规则改动后的复测（2026-09-24）

### 3.5 分部类：类型行列出全部声明位置

**调用**：`list_types(csprojPath="C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Demo.csproj")`

````text
# C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Demo.csproj
- TFM: `net11.0` | source files: 2 | types: 1

- `Demo.Class1` (class) — Class1.cs:3, Class1.Part2.cs:3
````

### 3.4 不含 D：只列成员，不给文档注释

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.JsonConvert", type="PF")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 8 | 生效 type: `PF`（显式指定）

- `P:Newtonsoft.Json.JsonConvert.DefaultSettings`
- `F:Newtonsoft.Json.JsonConvert.True`
- `F:Newtonsoft.Json.JsonConvert.False`
- `F:Newtonsoft.Json.JsonConvert.Null`
- `F:Newtonsoft.Json.JsonConvert.Undefined`
- `F:Newtonsoft.Json.JsonConvert.PositiveInfinity`
- `F:Newtonsoft.Json.JsonConvert.NegativeInfinity`
- `F:Newtonsoft.Json.JsonConvert.NaN`
````

### 3.4 带 D：给文档注释（原始片段）

**调用**：`list_doc_symbols(packName="Newtonsoft.Json", path="Newtonsoft.Json.JsonConvert.DefaultSettings", type="D")`

````text
# Newtonsoft.Json 13.0.3 (net6.0)
- 文档文件: C:\dotnet\nuget-packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.xml
- 条目总数: 1613 | 命中: 1 | 生效 type: `D`（显式指定）

```xml
<member name="P:Newtonsoft.Json.JsonConvert.DefaultSettings">
  <summary>
            Gets or sets a function that creates default <see cref="T:Newtonsoft.Json.JsonSerializerSettings" />.
            Default settings are automatically used by serialization methods on <see cref="T:Newtonsoft.Json.JsonConvert" />,
            and <see cref="M:Newtonsoft.Json.Linq.JToken.ToObject``1" /> and <see cref="M:Newtonsoft.Json.Linq.JToken.FromObject(System.Object)" /> on <see cref="T:Newtonsoft.Json.Linq.JToken" />.
            To serialize without using any default settings create a <see cref="T:Newtonsoft.Json.JsonSerializer" /> with
            <see cref="M:Newtonsoft.Json.JsonSerializer.Create" />.
            </summary>
</member>
```
````

### 3.2 同时要 A 与 A 的依赖 → 后者归入"因为被引用而未直接引入"（且不直接引入）

**调用**：`install_packages(csprojPath="C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Demo.csproj", nugetPack=["Microsoft.Extensions.Hosting", "Microsoft.Extensions.Hosting.Abstractions"], dryRun=true)`

````text
# 以下直接引入包是漏洞的
（无）

# 引入以下包
Microsoft.Extensions.Hosting 10.0.12

# 以下包因为被引用而未直接引入
Microsoft.Extensions.Hosting.Abstractions

# 以下包因为漏洞被自动升级引入
（无）

# 以下包被本次传递引入
Microsoft.Bcl.AsyncInterfaces
Microsoft.Extensions.Configuration.Abstractions
Microsoft.Extensions.Configuration.Binder
Microsoft.Extensions.Configuration.CommandLine
Microsoft.Extensions.Configuration.EnvironmentVariables
Microsoft.Extensions.Configuration.FileExtensions
Microsoft.Extensions.Configuration.Json
Microsoft.Extensions.Configuration.UserSecrets
Microsoft.Extensions.Configuration
Microsoft.Extensions.DependencyInjection.Abstractions
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Diagnostics
Microsoft.Extensions.FileProviders.Abstractions
Microsoft.Extensions.FileProviders.Physical
Microsoft.Extensions.Logging.Abstractions
Microsoft.Extensions.Logging.Configuration
Microsoft.Extensions.Logging.Console
…（原文共 120 行 / 3412 字符，此处节选前 30 行）
````

## install_packages 真落盘 + 落盘后的 NU1901–NU1904 核对（2026-09-24）

### install_packages — 真安装，末尾应有落盘后核对

**调用**：`install_packages(csprojPath="C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Demo.csproj", nugetPack=["Newtonsoft.Json@13.0.3"], dryRun=false)`

````text
# 以下直接引入包是漏洞的
（无）

# 引入以下包
Newtonsoft.Json 13.0.3

# 以下包因为被引用而未直接引入
（无）

# 以下包因为漏洞被自动升级引入
（无）

…（原文共 92 行 / 2279 字符，此处节选前 12 行）
````

## 3.6 / 3.7 / 3.8 复测（2026-09-24）

### 3.6+3.7 get_member 给类型：只给结构 + 重载分组显示

**调用**：`get_member(csprojPath="C:\Users\you\zms-mcp-test\zms-edge\Demo\Demo.csproj", memberPath="Demo.Bloat")`

````text
## public class Bloat

- Kind: `Class`
- Members: 51
- File: `C:\Users\you\zms-mcp-test\zms-edge\Demo\Bloat.cs`（3-259）

### Members
- public void Documented()
  ```xml
  <member name="M:Demo.Bloat.Documented">
      <summary>
      Documentation line 1 : this comment block exists only to make the member huge.
      Documentation line 2 : this comment block exists only to make the member huge.
      Documentation line 3 : this comment block exists only to make the member huge.
      Documentation line 4 : this comment block exists only to make the member huge.
      Documentation line 5 : this comment block exists only to make the member huge.
      Documentation line 6 : this comment block exists only to make the member huge.
      Documentation line 7 : this comment block exists only to make the member huge.
      Documentation line 8 : this comment block exists only to make the member huge.
      Documentation line 9 : this comment block exists only to make the member huge.
      Documentation line 10 : this comment block exists only to make the member huge.
      Documentation line 11 : this comment block exists only to make the member huge.
      Documentation line 12 : this comment block exists only to make the member huge.
      Documentation line 13 : this comment block exists only to make the member huge.
      Documentation line 14 : this comment block exists only to make the member huge.
      Documentation line 15 : this comment block exists only to make the member huge.
      Documentation line 16 : this comment block exists only to make the member huge.
      Documentation line 17 : this comment block exists only to make the member huge.
      Documentation line 18 : this comment block exists only to make the member huge.
      Documentation line 19 : this comment block exists only to make the member huge.
      Documentation line 20 : this comment block exists only to make the member huge.
      Documentation line 21 : this comment block exists only to make the member huge.
      Documentation line 22 : this comment block exists only to make the member huge.
      Documentation line 23 : this comment block exists only to make the member huge.
      Documentation line 24 : this comment block exists only to make the member huge.
      Documentation line 25 : this comment block exists only to make the member huge.
      Documentation line 26 : this comment block exists only to make the member huge.
      Documentation line 27 : this comment block exists only to make the member huge.
      Documentation line 28 : this comment block exists only to make the member huge.
      Documentation line 29 : this comment block exists only to make the member huge.
…（原文共 113 行 / 11241 字符，此处节选前 40 行）
````

### 3.8 单个成员：注释 200 行也完全显示（对比下面的类型结构）

**调用**：`get_member(csprojPath="C:\Users\you\zms-mcp-test\zms-edge\Demo\Demo.csproj", memberPath="Demo.Bloat.Documented")`

````text
## Demo.Bloat.Documented()

- Kind: `Method`
- Declaring type: `Demo.Bloat`

### Documentation
```xml
<member name="M:Demo.Bloat.Documented">
    <summary>
    Documentation line 1 : this comment block exists only to make the member huge.
    Documentation line 2 : this comment block exists only to make the member huge.
    Documentation line 3 : this comment block exists only to make the member huge.
    Documentation line 4 : this comment block exists only to make the member huge.
    Documentation line 5 : this comment block exists only to make the member huge.
    Documentation line 6 : this comment block exists only to make the member huge.
    Documentation line 7 : this comment block exists only to make the member huge.
    Documentation line 8 : this comment block exists only to make the member huge.
    Documentation line 9 : this comment block exists only to make the member huge.
    Documentation line 10 : this comment block exists only to make the member huge.
    Documentation line 11 : this comment block exists only to make the member huge.
    Documentation line 12 : this comment block exists only to make the member huge.
    Documentation line 13 : this comment block exists only to make the member huge.
    Documentation line 14 : this comment block exists only to make the member huge.
    Documentation line 15 : this comment block exists only to make the member huge.
    Documentation line 16 : this comment block exists only to make the member huge.
…（原文共 219 行 / 17421 字符，此处节选前 25 行）
````

### 3.6 普通类型：方法只签名 + 初始化器/访问器记号

**调用**：`get_member(csprojPath="C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Demo.csproj", memberPath="Demo.Class1")`

````text
## public partial class Class1

- Kind: `Class`
- Members: 3
- File: `C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Class1.cs`（3-8）
- File: `C:\Users\you\zms-mcp-test\zms-draft-demo\Demo\Class1.Part2.cs`（3-6）

### Members
- public static int Add(int a, int b)
- public static int Div(int a, int b)
- public static int Mod(int a, int b)
````

### 3.7 list_members：50 个重载改分组显示

**调用**：`list_members(csprojPath="C:\Users\you\zms-mcp-test\zms-edge\Demo\Demo.csproj", typePath="Demo.Bloat")`

````text
# Demo.Bloat  (class)
- TFM: `net11.0`

- `Demo.Bloat.Documented()` — Bloat.cs:207
- `Demo.Bloat.Many` 有 50 个重载：
  - `(int)` — Bloat.cs:209
  - `(int, int)` — Bloat.cs:210
  - `(int, int, int)` — Bloat.cs:211
  - `(int, int, int, int)` — Bloat.cs:212
  - `(int, int, int, int, int)` — Bloat.cs:213
  - `(int, int, int, int, int, int)` — Bloat.cs:214
  - `(int, int, int, int, int, int, int)` — Bloat.cs:215
  - `(int, int, int, int, int, int, int, int)` — Bloat.cs:216
  - `(int, int, int, int, int, int, int, int, int)` — Bloat.cs:217
  - `(int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:218
  - `(int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:219
  - `(int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:220
  - `(int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:221
  - `(int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:222
  - `(int, int, int, int, int, int, int, int, int, int, int, int, int, int, int)` — Bloat.cs:223
…（原文共 56 行 / 7643 字符，此处节选前 20 行）
````


## 交付前全量回归（2026-09-24）

24 个工具全部真实调用（stdio 协议，`zms-mcp-test` 下的 demo / edge 项目 + 本仓库）。只记规模与状态，返回原文见前面各节。

| 工具 | 场景 | 字符 / 行 | 状态 |
|---|---|---|---|
| scan_projects | 扫仓库根 | 197 / 6 | OK |
| view_solution_tree | slnx 树 | 219 / 8 | OK |
| view_project | csproj + 参与声明文件 | 6961 / 109 | OK |
| list_project_packages | 真实还原结果 | 2435 / 58 | OK |
| migrate_solution_to_slnx | sln → slnx | 209 / 6 | OK |
| add_project_to_solution | 带 Folder | 183 / 6 | OK |
| remove_project_from_solution | 移除 | 174 / 6 | OK |
| edit_project_metadata | dryRun | 256 / 9 | OK |
| install_packages | dryRun（含落盘后核对逻辑） | 2196 / 91 | OK |
| remove_packages | dryRun | 90 / 8 | OK |
| search_packages | 本地 + 线上 | 398 / 18 | OK |
| search_packages | 空关键字 + 线上 | 544 / 18 | OK |
| list_package_versions | 本地 + 线上 | 462 / 59 | OK |
| list_package_versions | `*`（Serilog 全部版本） | **8519 / 605** | ⚠ 无上限 |
| get_package_metadata | nuspec + readme | 4445 / 120 | OK |
| get_package_metadata | 版本不存在 | 49 / 2 | OK（"未找到"）|
| list_doc_symbols | `T` 全类型 | **7284 / 152** | ⚠ 无上限 |
| list_doc_symbols | `PF` | 529 / 13 | OK |
| list_doc_symbols | `M`（重载） | 1048 / 13 | OK |
| list_doc_symbols | `D`（原始片段） | 493 / 14 | OK |
| list_doc_symbols | `N`（命名空间） | 1293 / 31 | OK |
| list_types | 分部类一行多位置 | 170 / 5 | OK |
| list_members | 普通类型 | 185 / 7 | OK |
| list_members | 50 个重载 → 分组显示 | 7643 / 56 | OK |
| get_member | 类型（只给结构） | 345 / 12 | OK |
| get_member | 类型（259 行 + 200 行注释） | 11241 / 113 | OK（重载分组 + 注释截 50 行）|
| get_member | 精确成员（给实现） | 266 / 14 | OK |
| get_member | 重载不消歧 | 1572 / 22 | 预期报错（只列前 20 + 统计；此前 7448 / 51）|
| list_symbols | 全部种类 | 475 / 10 | OK |
| list_symbols | 修饰符 + 参数过滤 | 306 / 7 | OK |
| list_symbols | 拼错字母 | 241 / 7 | OK（有提醒）|
| list_symbols | `D`（只看有注释的） | 191 / 6 | OK |
| add_member / update_member / remove_member | 即时落盘 | 180 / 183 / 194 | OK |
| stage_draft / list_draft | 拟定累积 | 246 / 268 | OK |
| confirm_draft | 预演 / 落盘 | 322 / 453 | OK |

**交付门**：`dotnet publish -c Release` 成功（`zms9110750.ZMS.MCP.Csharp.exe` + 47 个 dll）；`dotnet build` 0 错误 0 警告；`dotnet test` 211 通过；`tools/list` 24 个工具；仓库无临时文件。

**仍开放**：列表**条目数**没有上限规则（只有 3.7 的重载分组与 3.8 的注释截断）——
`list_package_versions(*)` 605 行、`search_packages(空关键字)` 522 行、`list_doc_symbols(path="")` 152 行仍会一次吐完。