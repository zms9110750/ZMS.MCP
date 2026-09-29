# ZMS.MCP.Structured 工具手册 v1（草案）

`point` 的写法（按格式）：

| 格式 | 扩展名 | 定位表达式 |
|---|---|---|
| json | `.json` | JsonPath：`$.a.b[0]`、`$..name` |
| xml | `.xml` | XPath：`/root/item[1]`、`/r/x/@v` |
| yaml | `.yaml` / `.yml` | 点路径：`a.b.c`、`a.b[0]` |
| toml | `.toml` | 点路径：`a.b.c` |
| ini | `.ini` | `section.key`（顶层键直接写 `key`） |

`point` 为**空**时，指的是**整份文档**（读 = 整份内容，写 = 整份替换）。

格式判定：显式 `format` 优先 → 否则按扩展名 → 都不认识就报错（要求显式给 `format`）。

## 一、读取

### 1. 读文档 `read_structured`

| 参数 | 类型 | 说明 |
|---|---|---|
| `path` | `string` | 文件路径（必填） |
| `point` | `string?` | 定位表达式；空 = 整个文档 |
| `depth` | `int` | 展开深度，默认 `0` = 不限 |
| `length` | `int` | 最多展示的字符数，默认 `500`，上限 `5000` |
| `format` | `string?` | 显式格式；空 = 按扩展名推断 |

| 情况 | 行为 |
|---|---|
| 文件不存在 / 是目录 | 报错 |
| 扩展名不认识且 `format` 为空 | 报错，要求显式给 `format` |
| 解析失败 | 报错并带位置 |
| `point` 语法非法 / 无命中 | 报错 |
| `point` 多命中 | 全部列出（带规范化路径） |
| 被 `depth` / `length` 截断 | 明写"…此处还有 N 字符未展开" / "（已省略 N 字符）" |
| `point` 为空 | 返回整份内容，并附加修改用的 `cookie` |
| `point` 非空 | 只返回那一处，**不带 cookie**（cookie 是整份结构的句柄，只读了局部就没有它） |

**输出示例**（json）：

````
# X:\demo\appsettings.json（json，utf-8 无 BOM）
- 结构：object | 最大深度 4
- cookie：`sc-8f3a1d92`

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information" }
  },
  "AllowedHosts": "*"
}
```
````

---

## 二、写入

写之前会重新读盘，比对**解析后的结构**（不是字节）：缩进、空白、换行、注释、对象内的键顺序忽略；键名、值、数组顺序、嵌套层级都算。不一致就拒绝写，要求重新读。

### 2. 局部改 `edit_structured`

| 参数 | 类型 | 说明 |
|---|---|---|
| `cookie` | `string` | 读该文件时拿到的 cookie |
| `path` | `string` | 文件路径（必填） |
| `point` | `string?` | 定位表达式；空 = 整份文档 |
| `value` | `string?` | 新值；`point` 为空时它是**整份新文档**；`null` = 删除 |
| `format` | `string?` | 显式格式；空 = 按扩展名推断 |
| `insert` | `bool` | 允许"没命中时插入"，默认 `false` |
| `remove` | `bool` | 允许"`value` 为 `null` 时删除"，默认 `false` |
| `update` | `bool` | 允许"命中时改写"，默认 `false` |
| `multi` | `bool` | 允许多目标（一次改多处），默认 `false` |

`point` 的匹配结果与 `value` 组合出四种动作，开关是它们的许可：

| 匹配结果 | `value` | 需要的开关 | 动作 |
|---|---|---|---|
| 命中 | 有值 | `update` | 改写 |
| 命中 | `null` | `remove` | 删除（父容器留着，哪怕变空） |
| 没命中 | 有值 | `insert` | 插入 |
| 没命中 | `null` | — | 报错（没有动作可做） |

| 情况 | 行为 |
|---|---|
| cookie 无效 / 结构被改 | 报错，不写 |
| `value` 解析失败 | 报错，不写 |
| `point` 多命中，且没开 `multi` | 报错，列出全部命中路径（要么收紧表达式，要么打开 `multi`） |
| `point` 多命中，且开了 `multi` | 每个命中处都按同一个 `value` 处理，报告改了几处 |
| 该动作的开关**没开** | 不写；**回报匹配到的内容**，要求确认后再来一次 |
| `point` 为空 + `value` 为 `null` + 开了 `remove` | 报错（那等于清空整份文档；要清空就写新内容） |
| 新旧值相同 | 回"值没有变化，未写入。" |
