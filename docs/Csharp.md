# MCP.Csharp

## 扫描路径资源

### 扫描路径下项目

- path 
- depth


输出格式:
```
source\repos\Hello\Hello.slnx(2+1)
├src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
└src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj

source\repos\Hello\Hello.slnx(2+1)
├src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
└src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
─test/ZMS.MCP.Test/ZMS.MCP.Test.csproj

source\repos\Hello\src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
source\repos\Hello\src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
source\repos\Hello\test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
```
格式说明。
- slnx 内描述的引用项目完整照实描述（哪怕是绝对路径）
- 客观在slnx所在文件夹下，但不在slnx描述的，在树状制表符结束后以`-`开头描述额外关系。
- slnx 描述和客观所在，都要计数。已经在slnx描述过的项目，不再作为散装项目描述。
- 散装项目路径和slnx项目路径，都以工具参数的path的相对路径描述

## 查看项目

### 查看slnx

- Path
查看slnx，以树状图展示，包含虚拟文件夹信息，解离xml格式。此视图和扫描路径下项目展示的不同。
```
Hello.slnx
├─src
│  ├src/ZMS.MCP.Cli/ZMS.MCP.Cli.csproj
│  └src/ZMS.MCP.Csharp/ZMS.MCP.Csharp.csproj
└─test
    └test/ZMS.MCP.Test/ZMS.MCP.Test.csproj
```

### 查看csproj

- CsproPath 

从解决方案和项目名自动定位解决方案下唯一名的项目。
返回如同直接使用文件读取，原始返回xml内容。
然后附加所有可以向上找会参与项目声明的文件，并也列出内容（例如`prop`）。

在有多个项目名重名时，仍然需要完整的相对路径。

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

### 安装nuget包
- csprojPath
- (nugetName,ver?)[]
  - ver可空，若如此自动找本地缓存已有的最新版。没有的拉最新非预览版。
  - 如果要更新，则需要写`*`之类的通配符。

使用图库，对包依赖进行建图。
然后只对顶级包进行引入（除非依赖传递和参数要求的包版本不一致）

然后检查所有被依赖传递的包，对其中的任何`脆弱`，自动拉最新非脆弱非预览的包。

引入使用命令行。


返回结果为
```
#引入以下包

#以下包因为被引用而自动引入

#以下包因为脆弱被自动引入

#以下包被本次传递引入

#以下传递引入包原本就存在
```

### 移除nuget包
- csprojPath
- nugetName[]

移除前建图。移除后再建图。然后返回本次改动。
```
#本次移除包

#本次移除的依赖传递包
```

## nuget

