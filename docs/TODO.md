{action,uri,head,body}
get读，head元数据，post创建，追加，put，覆写，patch替换部分，delete删除，move，copy，移动，复制。

## 破坏

ftp和webdva操作，进行破坏性操作时，如果没有cookie，转为进行get操作，并返回结果：
```
本次操作被拒绝，如果仍然需要操作，携带cookie:xxx
如果操作成功，将干涉以下内容：
展示get结果。
```
如果操作成功，则返回下一个cookie，用于链式修改。

cookie是一个16位GUID。
破坏性操作是指
- 删除
- 移动目的有文件，需要覆盖。
- 移动文件夹，目标已经有文件夹，进行合并（无论有没有覆盖文件）
- 替换文件部分


## 缓存
未下载的http，https，cmd，执行操作后都会自动缓存。
在返回消息开头追加：操作已经缓存，访问webdva://cache/{key}访问缓存。
此三者的key为12位hash。


## http/https 协议

- 片段部分
  - 若有，必须是本地绝对路径，且没有文件，文件夹。下载到该路径。
  限流，1m/分钟，最大储存10分钟。
  - 若无，自动缓存结果，额外在开头返回缓存key。
- 如果返回大于5k，截断，说明还有xx被截断。
- 其他参数原样转发 

## ftp 协议

- get
  - 片段部分
    - 若有，必须是本地绝对路径，且没有文件，文件夹。下载到该路径。
    限流，1m/分钟，最大储存10分钟。
  - 如果返回大于5k，截断，说明还有xx被截断。
- post
  - 上传/创建文件夹。
- move
  - 移动
- deldete
  - 删除 

## webdav 协议（本地一切资源）


- host环境变量。nuget，cache，user，temp。表示这些文件夹的根。
- path。绝对路径，或者有host时的相对路径。
  - host为cache时，路径为key。
- userinfo 断言，file,dir,archive,json,xml,yaml,toml,ini,csharp，有这些时，要求地址必须为这种格式，否则报错。并且基于断言，有不一样的查询字符串和片段
  - 没有断言时，除了get和head都报错。根据目标自动视作file或dir。

### file@（文件）

- **读文本内容**：GET，path = 文件绝对路径
  - query：`skipline`/`takeline`/`offset`/`length`/`encoding`
    - **skipline** = 跳过多少行；**takeline** = 至多取多少行
    - **offset** = 跳过多少字符；**length** = **至多展示这么多字符**
    - encoding 缺省 UTF-8（可改，如 `?encoding=gbk`）
  - fragment：**正则表达式**（在文件内容中搜索）
    - 返回匹配结果，按数量分级：
      - **> 20 处** → 返回所在行 + 行号
      - **> 5 处** → 返回各自**上下 1 行** + 行号
      - **≤ 5 处** → 返回各自**上下 3 行** + 行号
- **文件元数据**：HEAD → 属性/元数据
- **新建/覆写**：POST/PUT，身体=内容
- **替换部分**：PATCH，身体 = **替换参数**（对齐 Reasonix 的 edit_file / multi_edit）
  - body 结构：`old_string`（替换源）+ `new_string`（替换为）——同 edit_file
  - **多选替换**：body 为 `edits` 数组，每项 `{old_string, new_string}`——同 multi_edit（按序应用）
  - **删除 = 替换为空**：`new_string` 为空串即删除该段
- **追加文本行**：POST，身体 = 新行内容
- **移动/复制**：MOVE/COPY，头 Destination/cookie
- **删文件**：DELETE → 删→回收站；需被删对象 cookie

### dir@（目录）

- **列目录**：GET
  - 查询字符串
    - `depth` = 递归深度（缺省 **3**；1=只当前层）
    - `limit` = 每层最多展示的条目数（缺省 **20**，文件+文件夹合计）
    - `type` = （缺省 **file&dir**）：`file`=文件、`dir`=目录；多值 `&type=file&type=dir` 或逗号 `type=file,dir`
    - `meta` = 逗号分隔属性名（缺省无）：FileInfo/DirectoryInfo 的属性（如 `created,modified,size`），随条目输出
  - fragment：搜索结果过滤
- **目录元数据**：HEAD → 属性/元数据
- **建目录**：POST + **dir@ 断言**（目标处没有资源）→ 创建无破坏，不需要 cookie
- **删目录**：DELETE
  - 删→回收站；需被删对象 cookie（GET+authorize 列目录拿 cookie）

### archive@（压缩包）

- **读/列包内**：GET，fragment = 容器内路径
- **创建压缩包**：POST，path = 要创建的包路径，身体 = 内容清单 + 压缩包格式
  - body 填：所有要加入该包的内容、压缩包格式（zip/tar/tgz 等）
  - **源大小综合 > 10M 时，需要所有源的 cookie**（源 = 被压缩的各个对象，先对每个源授权）
  - 创建无破坏（包路径不存在时），免 cookie；覆写已存在的包 = 破坏，需该包 cookie
- **包内写/删**：PUT/DELETE，fragment = 容器内路径，头 cookie，身体内容
  - PUT 覆写包内项 = 破坏需 cookie；删除需 cookie（GET+authorize 读包内项拿 cookie）

### json@ / xml@ / yaml@ / toml@ / ini@（结构化文档）

- **读**：GET，把对象**当作该格式解析**
  - query：
    - `depth` = 展开深度：过深的不展开，只说明"此处还有 N 字符未展开"
    - `length` = **最多展示字数**，超出只说明剩余
  - fragment：该语言的查询表达式。JsonPath，XPath，其他语言的Path
- **结构化写**：PUT/PATCH/POST/DELETE，fragment = 定位路径（必带），头 cookie，身体 = 新内容/新节点
  - 结构化写都需要cookie。

### csharp@（C# 代码）

- path = **项目文件地址**（必须是项目，而不是具体的 .cs 文件）
- **代码切片**：GET，fragment = 完全限定符号名
  - 读成员/列符号树；Roslyn 项目解析
- 任何写操作都要cookie。此cookie仅针对Roslyn解析出来的对象（类型）字符串。
- 任何写操作执行后，都要返回改动的文件地址和行号。防止语法错误时无法解析。
 
## nuget:// 协议

定位：`nuget://{包名}/{对象路径}?version=&tfm=&type=[TPFMED][#参数签名]`

- **host = 包名**
- **path = 对象路径**（点分名）
- **version/tfm 走查询字符串定位 .xml 文件**（`lib/{tfm}/` 由实现补全；tfm/version 缺省自动找可用项）
- **type**缺省时由 path 的精确度决定
  - 指向**精确类型**（能找到 `T:{path}` 节点）→ `TPFME`（类型+全部成员）
  - 指向**精确成员**（能找到 `M:/P:/F:/E:` 节点）→ `D`（该成员的原始 XML `<member>` 片段）
  - 非精确（根/命名空间前缀/找不到）→ `T`（只列类型）
- **显式 type** 覆盖 auto：`T`=类型、`P`=属性、`F`=字段、`M`=方法、`E`=事件、`D`=原始 XML 片段；可以则个，如 `TPFME`
- **找命名空间下所有类型**：`nuget://{包名}/{命名空间前缀}?type=T`
- **重载消歧用 `#`**：`#` 后即 XML member name 的参数段，完全匹配（如 `#(System.Data.IDbConnection,System.String)`）；不带 `#` 返回全部同名重载
- **包级资源**（?readme / ?nupkg，与 API 文档正交）：`nuget://{包名}?readme[&version=]` / `nuget://{包名}?nupkg[&version=]`
  - **path 段位留空**（包级资源，包唯一）
  - 查询字符串只允许该关键字 + 可选 `version`（版本，缺省最新）
  - `?readme` = 返回包内 readme 内容；`?nupkg` = 返回 .nupkg 包文件本身
- **在线资源**（?online，nuget.org）：`nuget://{包名}?online[&version=][&readme|nupkg][&page=]`
  - **path 段位留空**（线上资源按包名定位）
  - `?online` 固定表示走 nuget.org（映射现有 `SearchNuGet`，online 固定 true）
  - **包名不精确** → 模糊搜索（映射 SearchNuGet(packageName, version="", online=true)）
    - `page` 翻页（现有搜索每页 15 条，page 控制翻页偏移）
    - `version`/`readme`/`nupkg` 被忽略并**返回警告**
  - **包名精确** → 可用 `version`（版本范围，同 SearchNuGet 语义：空=列全部版本、有=列匹配范围）和 `?readme`/`?nupkg`（取该包线上资源）

## cmd 协议（执行命令）

- host = 程序名，path = 命令树，action = POST（执行），身体 = 所有参数，头 path = 绝对路径
- **授权方式 = MCP 工具描述符 + 客户端权限处理**（AI 知道命令是干什么的）
  - **不锁命令**：所有使用过的命令持久化到描述符文件
  - 可配置工具描述符（是否外部/是否写/是否破坏），留接口未来由 AI 判断返回描述符，基于权限通过

## help:// 协议

- 空 path = 列出可用协议和管理范围。
- 协议名为path = 列出这个协议下的文档
