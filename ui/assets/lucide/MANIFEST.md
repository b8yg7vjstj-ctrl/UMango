# MANIFEST — lucide 图标资源（UMango / PCL-CE 界面用）

## 1. 来源与许可

- **图标集来源**：lucide（官方仓库 `lucide-icons/lucide`，`icons/` 目录；`main` 分支）
- **主下载地址**（本目录绝大多数文件）：`http://gh-proxy.org/https://raw.githubusercontent.com/lucide-icons/lucide/main/icons/<name>.svg`
- **许可（License）**：lucide 采用 **ISC 许可**（宽松许可，与 MIT 等价/兼容）；`lucide-static` 包内文件头标注 `@license lucide-static - ISC`。任务书写作 “MIT”——两者同为宽松许可，可合规用于本项目，**保留本说明即满足署名要求**。
- **重要说明**：lucide 是一个**通用开源图标集（general-purpose icon set）**，**不是 PCL-CE 的资源**，与 PCL-CE / Minecraft 官方无隶属或授权关系；此处仅按名称引用其同名图标资产，PCL-CE 本身的图片/图标资源不在此目录内。
- **该目录只放图标资产**，不含任何 PCL-CE 专有素材。

## 2. 实际存在的图标（43 个，已排序）

- `anvil`
- `arrow-left`
- `box`
- `box-select`
- `boxes`
- `cat`
- `chevron-down`
- `chevron-right`
- `circle-check`
- `clock`
- `download`
- `egg`
- `external-link`
- `file-archive`
- `flask-conical`
- `folder-open`
- `gauge`
- `git-branch`
- `globe`
- `heart`
- `help-circle`
- `home`
- `info`
- `layers`
- `list-tree`
- `minus`
- `moon`
- `package`
- `puzzle`
- `refresh-cw`
- `rocket`
- `scroll`
- `search`
- `server`
- `settings`
- `sparkles`
- `star`
- `sun`
- `tag`
- `terminal`
- `triangle-alert`
- `wrench`
- `x`

## 3. 缺失（missing）清单

**无。** 任务清单中的 43 个名字全部落地为本地文件。

其中 3 个名字在 lucide 上游**已不是独立文件**（属于 lucide 官方重命名后的**弃用别名**，见第 4 节），
本目录通过**上游对应的规范名文件**补齐，未伪造任何几何数据（已与官方资产做几何级比对，见第 5 节）。

## 4. 别名（deprecated alias）解析记录

lucide 上游把这些旧名重命名了；`github.com/lucide-icons/lucide` 的 `icons/` 目录里**没有**同名独立文件，
但 lucide 仍通过包内别名机制（`packages/lucide/src/aliases`、`lucide-static` 发布的同名资产）支持这些旧名。
因此它们**不算“在 lucide 里不存在”**，故未列入 missing：

| 请求名（本目录文件名） | lucide 规范名 | 实际内容来源 URL |
| --- | --- | --- |
| `home` | `house` | `http://gh-proxy.org/https://raw.githubusercontent.com/lucide-icons/lucide/main/icons/house.svg` |
| `box-select` | `square-dashed` | `http://gh-proxy.org/https://raw.githubusercontent.com/lucide-icons/lucide/main/icons/square-dashed.svg` |
| `help-circle` | `circle-help` | `https://cdn.jsdelivr.net/npm/lucide-static@latest/icons/help-circle.svg` |

说明：`help-circle` 的规范名地址经 gh-proxy 连续 16+ 次尝试仍失败（连接重置/伪 404），
故改用 lucide 官方发布的 `lucide-static` npm 资产（同名文件，官方别名资产），
并去掉了 npm 包头的许可注释与 `class` 属性，以与其他 42 个文件风格一致（几何数据未改动，许可已在第 1 节注明）。

> 若后续要求“只用同名上游文件、不含别名解析”，需复核的即上述 3 个：`home` / `box-select` / `help-circle`。

## 5. 校验记录

- 每个 `.svg` 均通过：`python3 -c "import xml.dom.minidom,sys;xml.dom.minidom.parse(sys.argv[1])"`（43/43 通过）
- 每个文件均以 `<svg` 开头且包含 `</svg>`（24×24 viewBox、`stroke="currentColor"`，适配 flutter_svg）
- 全部 43 个文件与 lucide 官方发布资产做**几何级比对**（提取 path/circle/rect/line 等元素及其属性集合）：**43 个几何完全一致，0 个不一致**
- 下载策略：每个图标最多重试 4 次、间隔 2 秒（本轮实际对失败项做了更多轮次，因 gh-proxy 不稳定）
- 校验失败的文件会被删除并重下；未留下任何非 SVG 的错误页文件

## 6. 文件与字节数（43 个）

| 文件 | 字节数 |
| --- | --- |
| `anvil.svg` | 471 |
| `arrow-left.svg` | 262 |
| `box.svg` | 401 |
| `box-select.svg` | 532 |
| `boxes.svg` | 824 |
| `cat.svg` | 537 |
| `chevron-down.svg` | 236 |
| `chevron-right.svg` | 237 |
| `circle-check.svg` | 278 |
| `clock.svg` | 271 |
| `download.svg` | 318 |
| `egg.svg` | 271 |
| `external-link.svg` | 332 |
| `file-archive.svg` | 474 |
| `flask-conical.svg` | 383 |
| `folder-open.svg` | 401 |
| `gauge.svg` | 278 |
| `git-branch.svg` | 313 |
| `globe.svg` | 331 |
| `heart.svg` | 373 |
| `help-circle.svg` | 322 |
| `home.svg` | 383 |
| `info.svg` | 294 |
| `layers.svg` | 510 |
| `list-tree.svg` | 355 |
| `minus.svg` | 232 |
| `moon.svg` | 334 |
| `package.svg` | 446 |
| `puzzle.svg` | 685 |
| `refresh-cw.svg` | 392 |
| `rocket.svg` | 529 |
| `scroll.svg` | 370 |
| `search.svg` | 275 |
| `server.svg` | 413 |
| `settings.svg` | 586 |
| `sparkles.svg` | 567 |
| `star.svg` | 588 |
| `sun.svg` | 485 |
| `tag.svg` | 430 |
| `terminal.svg` | 261 |
| `triangle-alert.svg` | 345 |
| `wrench.svg` | 417 |
| `x.svg` | 260 |
| **合计** | **17002** |
