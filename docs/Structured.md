# MCP.Structured

结构化文档的路径定位、查询、读取、编写。写操作统一走 cookie。

## 支持格式与定位表达式

| 格式 | 定位表达式 |
|------|-----------|
| json | JsonPath（`$.store.book[0].title`、`$..author`） |
| xml | XPath（`/root/item[1]`、`/r/x/@v`） |
| yaml | 点路径（`a.b.c`） |
| toml | 点路径（`a.b.c`） |
| ini | `section.key` |

## 读

- path（必填）
- point?：定位表达式；给了返回定位结果，不给返回整个文档
- depth?：展开深度；过深的不展开，只说明"此处还有 N 字符未展开"
- length?：最多展示字数；超出只说明剩余

## 覆写

需要 cookie。

- path（必填）
- content：完整的新文档（必填）

## 局部改

需要 cookie。

- path（必填）
- point：定位表达式（必填）
- value：新值（必填）

## 插入

需要 cookie。

- path（必填）
- point：定位表达式（必填）
- value：新节点（必填）

## 删除

需要 cookie。

- path（必填）
- point：定位表达式（必填，防止误删整个文档）

## 格式判定

- 显式参数优先
- 否则按扩展名推断：`.json` / `.xml` / `.yaml` / `.yml` / `.toml` / `.ini`
- 两者都不确定 → 报错，要求显式指定格式

## 其他要求

- 局部改 / 插入 / 删除必须给定位表达式，且定位不存在时报错，不自动创建
- 编码：写回用原编码（判定顺序：`.editorconfig` 的 `charset` → BOM 检测 → UTF-8 严格校验；都不成立则拒绝写）；新建文件用 UTF-8 无 BOM
- 只访问本地文件；远程结构化文档要先下载到本地再读
- 落盘遵守"写文件通则"（原子替换 / 落盘前校验）
