# MCP.Resource

网络与文件资源访问：http/https、本地文件、目录、FTP、压缩包。

**各协议操作方式不同，参数各按自己的语义**：只有 http 用 HTTP 方法；FTP 用它自己的操作；本地文件和目录直接用"读 / 写 / 删"这类参数，不套 HTTP 动词。

破坏性操作统一走 cookie 两阶段确认。

## cookie 两阶段确认

破坏性操作时，如果没有 cookie，转为执行"查看"并返回结果：

```
本次操作被拒绝，如果仍然需要操作，携带cookie:xxx
如果操作成功，将干涉以下内容：
<查看结果>
```

带 cookie 执行成功后，返回**下一个 cookie**，用于链式修改。

- cookie 是一个 16 位 GUID
- 影响清单与 cookie 对应不上（清单已变）时拒绝执行，重新返回影响清单

破坏性操作是指
- 删除
- 移动目的有文件，需要覆盖
- 移动文件夹，目标已经有文件夹，进行合并（无论有没有覆盖文件）
- 替换文件部分

## 缓存

- 未缓存的 http/https 操作，执行后自动缓存
- 返回消息开头追加：操作已经缓存，用返回的缓存 key 访问
- key 为 12 位 hash

## http / https

### 请求

- url：目标地址（必填）
- method：GET / POST / PUT / PATCH / DELETE / HEAD / OPTIONS（必填）
- head?：JSON 对象字符串，请求头
- body?：请求体文本

其他参数原样转发；返回大于 5k 截断，说明还有多少被截断。

### 下载

- url：目标地址（必填）
- targetPath：必须是本地绝对路径，且该处没有文件、文件夹（必填）
- method?：缺省 GET
- head? / body?：同上

限流 1MB/分钟，缓存最大储存 10 分钟。

## 本地文件

### 读文本内容

- path（必填）
- skipline?：跳过多少行
- takeline?：至多取多少行
- offset?：跳过多少字符
- length?：至多展示这么多字符
- encoding?：缺省 UTF-8（可改，如 gbk）
- regex?：在内容中搜索（正则表达式），按匹配数量分级返回
  - 大于 20 处 → 只返回所在行 + 行号
  - 大于 5 处 → 各返回上下 1 行 + 行号
  - 5 处以内 → 各返回上下 3 行 + 行号

### 元数据

- path（必填）

### 新建 / 覆写

- path（必填）
- content：文件内容

### 替换部分

- path（必填）
- old_string：替换源
- new_string：替换为；为空串即删除该段

### 批量替换

- path（必填）
- edits：数组，每项 `{old_string, new_string}`，按序应用

### 追加文本行

- path（必填）
- content：新行内容

### 移动

- source（必填）
- destination（必填）
- cookie?：目的有文件、或文件夹需要合并时需要（见"破坏性操作"）

### 复制

- source（必填）
- destination（必填）
- cookie?：同移动

### 删除文件

- path（必填）
- cookie?：需要被删对象的 cookie；删除走回收站

## 目录

### 列目录

- path（必填）
- depth?：递归深度（缺省 3；1 = 只当前层）
- limit?：每层最多展示的条目数（缺省 20，文件 + 文件夹合计）
- type?：`file` / `dir`（缺省两者都有）
- meta?：逗号分隔属性名（缺省无），如 `created,modified,size`
- regex?：搜索结果过滤

### 元数据

- path（必填）

### 创建目录

- path（必填）：目标处没有资源时创建，无破坏，不需要 cookie

### 删除目录

- path（必填）
- cookie?：需要被删对象的 cookie；删除走回收站

## FTP

以下每个操作都带这些连接参数：

- host（必填）
- port?：缺省 21
- user? / password?：缺省匿名
- path：服务器上的路径

### 列目录

- path
- depth? / limit? / type? / meta? / regex?：同本地"列目录"

### 读文本内容

- path
- skipline? / takeline? / offset? / length? / encoding? / regex?：同本地"读文本内容"

### 下载

- path：服务器上的文件
- targetPath：必须是本地绝对路径，且目标不存在

限流 1MB/分钟，缓存最大储存 10 分钟；返回大于 5k 截断。

### 上传 / 创建文件夹

- path
- localPath?：上传时的本地源文件

### 移动

- source
- destination
- cookie?：目的已存在时需要

### 删除

- path
- cookie?

## 压缩包

支持 zip / nupkg / tar / tgz。

### 列包内 / 读条目

- path：包路径（必填）
- entry?：留空 = 列全部条目；给了 = 读该条目的内容

### 解压

- path：包路径（必填）
- entry?：留空 = 整包；给了 = 只解该路径
- targetDir：目标目录（必填）

### 创建

- path：要创建的包路径（必填）
- entries：内容清单（必填）
- format：zip / tar / tgz（必填）
- cookie?：源大小合计大于 10M 时需要所有源的 cookie（源 = 被压缩的各个对象，先逐个授权）；包路径不存在时创建无破坏（免 cookie），覆写已存在的包需要该包的 cookie

### 包内写

- path（必填）
- entry（必填）
- content（必填）
- cookie?：覆写已存在的包内项时需要

### 包内删

- path（必填）
- entry（必填）
- cookie?
