# ZMS.MCP.Structured 工具手册 v1

这份手册以**实机调用结果**写成：每个工具的参数取自运行时 schema，返回值示例是实际调用回来的原文（不是从代码推断的）。共 **2 个工具**。

写它的时候刻意做了一件 Csharp 手册没做的事：**把"写回会丢什么"实测出来单列成附录**。因为这个工具是**整份重新序列化**写回的，丢了什么必须让人一眼看到，而不是自己踩出来。

## 约定

- 参数类型用 C# 可空语法：`:string` = 必填；`:string?` = 可空（"没给"与"给了空串"等价）。
- **错误形态统一**：`Error: <消息>`。消息是中文；底层解析库（yaml / toml）自己抛的异常会带英文原文。
- **每次调用返回的都是 markdown 文本**，不是 JSON。

### 定位表达式（`point`）

| 格式 | 扩展名 | 写法 | 例子 |
|---|---|---|---|
| json | `.json` | JsonPath | `$.Logging.LogLevel.Default`、`$.items[0]`、`$..name`（递归） |
| xml | `.xml` | XPath | `/config/logging/@level`、`/r/item[1]`、`//item` |
| yaml | `.yaml` / `.yml` | 点路径 | `services.web.image`、`services.web.ports[0]` |
| toml | `.toml` | 点路径 | `tool.black.line-length` |
| ini | `.ini` | `节.键` | `database.port`（顶层键直接写 `key`） |

- `point` 为**空串**时指**整份文档**：读 = 整份内容，写 = 整份替换。
- 表达式**不自动创建**：没命中就是报错（要新建走 `insert` 开关）。

### 格式判定

显式 `format` 优先；否则按扩展名；都不认识就报错，要求显式给 `format`。

```
Error: 看不懂扩展名 .conf，请用 format 显式指定（json / xml / yaml / toml / ini）。
```

### 编码判定

`encoding` 为空时按这个顺序：

1. 开头有 BOM → 用它（UTF-8 BOM / UTF-16 LE / UTF-16 BE）
2. 没有 BOM → **严格**试 UTF-8
3. 解不出来 → **拒绝**（不静默替换成 `U+FFFD`）

```
Error: X:\temp\zms-doc\gbk.ini 不是 UTF-8（没有 BOM，严格 UTF-8 也解不出来）。用 encoding 显式指定，或先把它转成 UTF-8。
```

给了 `encoding`（`utf-8` / `utf-16` / `gb18030` / `gbk` …）就按它解；解不出来报错。**写回用的是读进来时的同一份编码**（BOM 状态也照原样）。

### 写回是整份重新序列化 —— 会丢东西

**这是用这个工具前必须知道的一件事**：写入不是"改那几个字节"，而是"把整棵树重新写成一份文件"。

- **会保**：键值、数组顺序、嵌套层级、键序（json）、大整数与科学计数法原文（json）、**toml 的日期与时间类型**、**xml 的声明 / 注释 / 处理指令**
- **会丢**：**注释**（yaml / toml / ini）、空行、缩进风格、引号风格（yaml）

具体每种格式丢了什么，见**附录 A**（实测）。如果你想的是"只改一行、其余一字不动"，这个工具**不是**那个工具。

### 数据安全上的三条承诺

这三种情况**宁可拒绝，也不动文件**：

1. 编码解不出来（见上）
2. yaml 带**锚点 / 别名 / merge**（见附录 B）
3. cookie 对不上（文件被改过，或 cookie 不是这个文件的）

---

## 一、读取

### 1. 读文档 `read_structured`

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `:string` | 文件路径 |
| `point` | `:string?` | 定位表达式；空 = 整个文档 |
| `depth` | `integer` | 展开深度；默认 `0` = 不限 |
| `length` | `integer` | 最多展示的字符数；默认 `500`，上限 `5000` |
| `format` | `:string?` | 显式格式；空 = 按扩展名推断 |
| `encoding` | `:string?` | 显式编码名；空 = 自动判定 |

**读整份**会给出 cookie（写的时候要用）；**读一处**不给 —— cookie 是"整份结构"的句柄，只读了局部就等于没看过全貌。

**实测输出**（整份，`path=X:\temp\zms-doc\appsettings.json`）：

```
# X:\temp\zms-doc\appsettings.json（json）
- 结构：object | 最大深度 4
- 编码：utf-8
- cookie：`69bc6f1eb2c9279b`

{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

**实测输出**（读一处 `$.Logging.LogLevel.Default`）：

```
"Information"
```

**实测输出**（`depth=1`，只展开一层）：

```
# X:\temp\zms-doc\appsettings.json（json）
- 结构：object | 最大深度 4
- 编码：utf-8
- cookie：`69bc6f1eb2c9279b`

{
  "Logging": …（此处还有 60 字符未展开）,
  "AllowedHosts": "*"
}
```

**实测输出**（`length=40`，超出上限的部分被砍）：

```
{
  "LogLevel": {
    "Default": "Info
（已省略 45 字符）
```

**实测输出**（`point` 命中多处 —— 读的时候全部列出，且**不带 cookie**）：

```
# X:\temp\zms-doc\deep.json（json）— `$..v`
- 命中 2 处（读只列出来，不带 cookie）
- 1. 1
- 2. 2
```

**实测输出**（空文件）：

```
# X:\temp\zms-doc\empty.json（json）
- 结构：空文档
- 编码：utf-8
- cookie：`74234e98afe7498f`

（空文件）
```

**实测输出**（没命中 / 扩展名不认识）：

```
Error: 没命中：$.nope（不自动创建）
Error: 看不懂扩展名 .conf，请用 format 显式指定（json / xml / yaml / toml / ini）。
```

- 之前：想知道"这个配置文件现在是什么样"，或者拿到一个 cookie 准备写。
- 之后：要改就用 `edit_structured`。
- 不能做：不写盘；不做格式转换（改完还是原来那个格式）；不认识多文档 yaml（见附录 B）。
- 注意：yaml / toml / ini **读出来统一显示成 json 树** —— 这不是格式转换，是让人（和 agent）用同一种眼光看所有配置：

```
# X:\temp\zms-doc\app.ini（ini）
- 结构：object | 最大深度 3
- 编码：utf-8
- cookie：`2474ba96ec86433a`

{
  "debug": "true",
  "database": {
    "host": "localhost",
    "port": "5432"
  }
}
```

---

## 二、写入

### 2. 局部改 `edit_structured`

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `:string` | 读该文件时拿到的 cookie |
| `path` | `:string` | 文件路径 |
| `point` | `:string?` | 定位表达式；空 = 整份文档 |
| `value` | `:string?` | 新值；`point` 为空时它是**整份新文档**；`null` = 删除。**xml 改元素要给完整片段**（`<a>2</a>`），改属性才是纯文本（`debug`） |
| `format` | `:string?` | 显式格式；空 = 按扩展名推断 |
| `encoding` | `:string?` | 显式编码名；空 = 自动判定 |
| `insert` | `boolean` | 允许"没命中时插入"；默认 `false` |
| `remove` | `boolean` | 允许"`value` 为 `null` 时删除"；默认 `false` |
| `update` | `boolean` | 允许"命中时改写"；默认 `false` |
| `multi` | `boolean` | 允许多目标（一次改多处）；默认 `false` |

**四种动作由"匹配结果 × `value`"决定，开关是它们的许可**：

| 匹配结果 | `value` | 需要的开关 | 动作 |
|---|---|---|---|
| 命中 | 有值 | `update` | 改写 |
| 命中 | `null` | `remove` | 删除（父容器留着，哪怕变空） |
| 没命中 | 有值 | `insert` | 插入 |
| 没命中 | `null` | —— | 报错（没有动作可做） |

**开关没开时不写盘，而是把命中的内容摆出来**，让你确认后再来一次：

```
# 没有写入
- 定位：`$.Logging.LogLevel.Default`
- 原因：命中了要改，但 update 没开

- 命中 1 处：
  - "Debug"
```

**实测输出**（改写）：

```
# 已落盘
- 文件：X:\temp\zms-doc\appsettings.json
- 动作：改写 $.Logging.LogLevel.Default（1 处）
- 结构：object | 最大深度 4
- 编码：utf-8
- 现在的 cookie：`bb65761a73a9fe5a`
```

**实测输出**（插入 / 删除 / 整份替换 / 多命中）：

```
- 动作：插入 $.Logging.LogLevel.Trace
- 动作：删除 $.AllowedHosts（1 处）
- 动作：整份替换
- 动作：改写 $..v（2 处）
```

**实测输出**（值没变 —— 不写盘）：

```
值没有变化，未写入。
```

| 情况 | 行为 |
|---|---|
| cookie 无效 / 文件被改过 | 报错，不写：`Error: 文件和你读的时候不一样了（或者 cookie 不对）：重新 read_structured 再改。` |
| `point` 多命中，没开 `multi` | 报错：`Error: 命中 2 处，multi 没开：要么收紧表达式，要么打开 multi。` |
| `point` 多命中，开了 `multi` | 每处都按同一个 `value` 处理，并报告改了几处 |
| 未命中要插入，但**父容器**不存在 | 报错：`Error: 要先定位父容器，但 $.nope 命中了 0 处。` |
| `point` 为空 + `value` 为 `null` + `remove` 开 | 报错（那等于清空整份文档） |
| 值没变 | `值没有变化，未写入。` |

**cookie 是什么**：它是**结构指纹** —— `SHA256(规范结构文本)` 的前 16 位十六进制。不存任何状态，所以同一个结构永远算出同一串。它的作用只有一个：**逼调用方动手前先看过全貌**。

写之前会重新读盘，比对的是**结构**（不是字节）：缩进、空白、换行、注释、**对象内的键序**都忽略；键名、值、数组顺序、嵌套层级都算。所以只改了排版不会误判成"文件被改过"。

- 之前：先 `read_structured` 拿 cookie。
- 之后：想要新的 cookie，重新读一次。
- 不能做：不保真（见附录 A）；不支持 yaml 多文档 / 锚点（见附录 B）；**不做并发保护**（同一个文件别被两个会话同时写）。
- 注意：`cookie` 不是安全机制（16 位十六进制，可以伪造），它防的是**手滑**，不是**恶意**。

---

## 附录 A：写回会把什么弄丢（实测）

一次改写之后，文件**原样重写**成这样（`⏎` 标出换行）：

### xml

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- 顶部注释 -->
<config>

  <!-- 里面的注释 -->
  <logging level="info" />
  <?keep this?>
  <hosts>*</hosts>
</config>
```

```xml
<?xml version="1.0" encoding="utf-8"?>⏎
<!-- 顶部注释 -->⏎
<config>⏎
  <!-- 里面的注释 -->⏎
  <logging level="debug" />⏎
  <?keep this?>⏎
  <hosts>*</hosts>⏎
</config>
```

- **保住了**：`<?xml ?>` 声明（连 `encoding` 一起）、注释（含 root 之前和元素内部的）、处理指令 `<?keep this?>`、元素顺序
- **丢了**：空行、原来的缩进（重排成 2 空格）

### yaml

```yaml
# 顶部注释
services:
  web:
    image: "nginx:latest"   # 行尾注释

    ports:
      - '80:80'
```

```yaml
services:⏎
  web:⏎
    image: nginx:latest⏎
    ports:⏎
    - 8080:80⏎
```

- **丢了**：注释（顶部和行尾）、空行、引号风格（`"nginx:latest"` → `nginx:latest`）、缩进风格（列表缩进从 6 空格变 4）

### toml

```toml
# 顶部注释
name = "demo"    # 行尾
released = 2024-01-31
clock = 09:30:00
deps = ["a", "b"]

[tool.black]
line-length = 88
```

```toml
name = "demo2"⏎
released = 2024-01-31⏎
clock = 09:30:00⏎
deps = ["a", "b"]⏎
⏎
[tool.black]⏎
line-length = 88⏎
```

- **保住了**：**日期 / 时间 / 日期时间的类型**（`2024-01-31` 还是日期，`09:30:00` 还是时间 —— 不会被写成字符串）、数组、`[tool.black]` 的层级（**不会**多出空的 `[tool]`）
- **丢了**：注释、部分空行

### ini

```ini
; 顶部注释
debug = true

# 另一种注释
[database]
host = localhost

port = 5432
```

```ini
debug=true⏎
⏎
[database]⏎
host=localhost⏎
port=6543⏎
```

- **丢了**：注释、等号两边的空格、空行

### json

```json
{
    "z": 1,
    "a": {
        "m": 2
    }
}
```

```json
{⏎
  "z": 9,⏎
  "a": {⏎
    "m": 2⏎
  }⏎
}
```

- **保住了**：键序、数组顺序、大整数（`12345678901234567890` 不被转成浮点）、科学计数法原文（`1e3` 还是 `1e3`）
- **丢了**：缩进风格（4 空格 → 2 空格）

---

## 附录 B：不支持的输入

| 输入 | 结果 |
|---|---|
| yaml **锚点 / 别名 / merge**（`&b` / `*b` / `<<:`） | **拒绝读**：`Error: 这份 yaml 用了锚点（&name），这个工具会把它们写坏：先手工展开再来改。`（别名和 merge 单独一条消息） |
| yaml **多文档**（`---` 分隔） | **拒绝读**：`Error: Expected 'StreamEnd', got 'DocumentStart' (at Line: 3, Col: 1, Idx: 9).` |
| 坏内容（语法错） | 报错，带底层库的原文：`Error: While parsing a flow sequence, did not find expected ',' or ']'.` |
| xlsx / csv / tsv / markdown | 从设计里删掉了，不支持 |
| 目录、不存在的文件 | 报错：`Error: 文件不存在：...` / `Error: ... 是目录，不是文件。` |

> 前两条是**拒绝**，不是"尽力而为"：这两种 yaml 的语义（合并、多文档）在这套"键值树"模型里表达不了，硬写回去会把别人的文件改坏。宁可让你先去手工展开。

---

## 附录 C：已知问题

1. **yaml / toml / ini 的注释在写回时丢失**（xml 的注释保得住）。这是模型层面的：这套工具把文档归一到"键值树"，树里没有注释这个概念。
2. **中文值在 json 树里显示成转义**：`\u4E2D\u6587\u503C`。这是显示层的选择（JSON 标准转义），值本身没问题，但对中文配置读起来不直观。
3. **结构行里 `object` 是英文**：同一行其它部分（`空文档`、`最大深度`）是中文，术语不统一。
4. **没有并发保护**：两个会话同时改一个文件会互相覆盖。cookie 只能在**读-写之间**发现"文件变了"，不能阻止**同时写**。

---

## 附：这个工具为什么长这样

它跟 `docs/ZMS.MCP.Csharp.md` 那套（Roslyn / 语法树）解决的不是同一类问题：

- Roslyn 那套管**符号**：改一个名字要连带所有引用，需要语义。
- 这套管**位置**：`$.a.b[0]` 指向文档里的一处值，不需要语义，但需要"我知道它在哪"。

所以它刻意不做的：不保真（不解析注释 AST）、不做格式转换、不在文档里加东西。如果哪天要"只改一行、其余一字不动"，那需要的是**区间替换**，不是整份重新序列化 —— 那是另一个设计，不在这份手册的范围内。
