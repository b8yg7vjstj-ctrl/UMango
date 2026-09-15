# PCL-CE 界面复刻规格（Design Spec）

> 本文件是**多 Agent 并行实现的唯一契约**。所有参与实现的 agent 都必须先读它，
> 并严格按其中的命名与数值实现 —— 否则并行产出的代码无法编译到一起。
>
> 参照物来源：`PCL-Community/PCL-CE` 仓库（dev 分支）的 XAML 资源定义
> 与官网截图。**只提取设计数值，不复制任何源码、XAML 文本或图片资源。**

---

## 0. 硬性约束（必须遵守）

1. **所有 Flutter 代码手写**，不得粘贴/改写 PCL-CE 的 C#、XAML 文本。
2. **不得使用 PCL-CE 的图片资源**（logo、图标、背景图一律不用）。
   图标统一使用 **lucide**（MIT 许可），放在 `ui/assets/lucide/`。
3. 数值（颜色、尺寸、字号）按本规格照抄 —— 那是"1:1"的依据。
4. 命名严格按第 4、5 节的 API 契约，不得自行改名。

---

## 1. 颜色 token（来自 PCL-CE `Application.xaml` 资源字典）

### 主色阶（蓝色系）
| token | 值 | 用途 |
|-------|-----|------|
| `PclColors.c1` | `#343d4a` | 主文字色 / 按钮描边 |
| `PclColors.c2` | `#0b5bcb` | 深蓝（强调文字） |
| `PclColors.c3` | `#1370f3` | **主强调色**（选中、主按钮） |
| `PclColors.c4` | `#4890f5` | 中蓝 |
| `PclColors.c5` | `#96c0f9` | 浅蓝 |
| `PclColors.c6` | `#d5e6fd` | 极浅蓝（标签底） |
| `PclColors.c7` | `#e0eafd` | 极浅蓝 |
| `PclColors.c8` | `#eaf2fe` | 最浅蓝（面板底） |
| `PclColors.bg0` | `#96c0f9` | 标题栏渐变端色 |
| `PclColors.bg1` | `#bee0eafd` | 半透明面板 |

### 灰阶
| token | 值 | 用途 |
|-------|-----|------|
| `PclColors.gray1` | `#404040` | 主要文字 |
| `PclColors.gray2` | `#737373` | 次级文字 |
| `PclColors.gray3` | `#8c8c8c` | 说明文字 / 元信息 |
| `PclColors.gray4` | `#a6a6a6` | 更淡的元信息 |
| `PclColors.gray5` | `#cccccc` | 分隔线 / 描边 |
| `PclColors.gray6` | `#ebebeb` | 浅分隔 |
| `PclColors.gray7` | `#f0f0f0` | 浅底 |
| `PclColors.gray8` | `#f5f5f5` | 更浅底 |

### 语义色
| token | 值 |
|-------|-----|
| `PclColors.redLight` | `#ff4c4c` |
| `PclColors.redDark` | `#ce2111` |
| `PclColors.redBack` | `#fbdddd` |
| `PclColors.fatal` | `#c23616` |
| `PclColors.error` | `#e74c3c` |

### 背景与透明
| token | 值 | 用途 |
|-------|-----|------|
| `PclColors.background` | `#fbfbfb` | 窗口背景 |
| `PclColors.halfWhite` | `#55ffffff` | 按钮底（半透明白） |
| `PclColors.semiWhite` | `#bbffffff` | 卡片底 |
| `PclColors.tooltip` | `#e5ffffff` | 提示框底 |
| `PclColors.transparentBackground` | `#d2fbfbfb` | 半透明窗口底 |

---

## 2. 尺寸与度量（来自 `FormMain.xaml` / `MyCompItem.xaml` / `MyButton.xaml`）

### 窗口与外壳
| 项 | 值 |
|----|-----|
| 窗口默认尺寸 | 850 × 500 |
| 窗口最小尺寸 | 810 × 470 |
| 外层边距（四周留白） | **10** |
| 外层圆角 | **6** |
| 顶部标题栏高度 | **48** |
| 标题栏右侧窗口按钮 | **28 × 28**，右边距 12，间距 32（12 / 44 / 76） |
| 标题标签按钮（RadioButton）高度 | **27**，水平外边距 5，内边距 2,0 |
| 二级页标题栏内文字字号 | 15，白色 |
| 二级页返回按钮 | 28 × 28，左边距 12 |
| 左栏右侧阴影宽度 | **4** |

### 左栏列表项（`MyListItem`）
| 项 | 值 |
|----|-----|
| 项高度 | **36** |
| 分组标题外边距 | 13,10,5,4 |
| 分组标题字号 / 透明度 | 12 / 0.6 |

### 包卡片（`MyCompItem`）
| 项 | 值 |
|----|-----|
| 卡片高度 | **64** |
| 列布局 | `7 / 50 / 8 / Auto / 1* / Auto` |
| 行布局 | `1* / 20 / 18 / 16 / 1.1*` |
| 图标尺寸 | **50 × 50**，圆角 **6** |
| 标题字号 | **14**，色 `c1` |
| 原始名（副标题）字号 | 12，色 `gray1`，透明度 0.4 |
| 简介字号 | 12，色 `gray3` |
| 元信息字号 | 12，色 `gray3`（版本）/ `gray4`（下载量、时间、来源） |
| 元信息图标 | 12 × 12，与文字间距 4–5 |
| 右侧操作按钮 | **25 × 25**，间距 4，默认 Opacity 0（hover 显示） |

### 按钮（`MyButton`）
| 项 | 值 |
|----|-----|
| 圆角 | **3** |
| 描边 | **1px** `c1` |
| 底色 | `halfWhite` |
| 字号 | **13**，文字色 = 描边色 |
| 图标按钮尺寸 | 25 × 25 或 28 × 28 |

---

## 3. 字体

PCL-CE 自带 `PCL English` 字体 + 微软雅黑。**我们不能使用它的字体文件**。
Flutter 侧使用系统字体回退链：

```dart
fontFamilyFallback: ['PingFang SC', 'Microsoft YaHei UI', 'Segoe UI', 'Helvetica Neue']
```

即 `PclTypography.fontFallback`。

字号层级：11（标签）/ 12（说明与元信息）/ 13（按钮与正文）/ 14（卡片标题）/ 15（二级页标题）/ 20+（大标题）。

---

## 4. `lib/theme/pcl_theme.dart` 必须提供的 API

```dart
/// 颜色 token（全部为 const Color）
class PclColors {
  static const c1, c2, c3, c4, c5, c6, c7, c8, bg0, bg1,
               gray1, gray2, gray3, gray4, gray5, gray6, gray7, gray8,
               redLight, redDark, redBack, fatal, error,
               background, halfWhite, semiWhite, tooltip, transparentBackground;
  /// 标题栏渐变（水平）：c3 -> c4 -> c5 类似观感
  static const LinearGradient titleBarGradient;
}

/// 尺寸常量
class PclMetrics {
  static const windowMargin = 10.0;
  static const windowRadius = 6.0;
  static const titleBarHeight = 48.0;
  static const windowButtonSize = 28.0;
  static const tabHeight = 27.0;
  static const listItemHeight = 36.0;
  static const cardHeight = 64.0;
  static const cardIconSize = 50.0;
  static const cardIconRadius = 6.0;
  static const iconButtonSize = 25.0;
  static const buttonRadius = 3.0;
  static const buttonBorderWidth = 1.0;
  static const leftShadowWidth = 4.0;
  static const metaIconSize = 12.0;
}

/// 字号
class PclTypography {
  static const label = 11.0;
  static const caption = 12.0;
  static const body = 13.0;
  static const cardTitle = 14.0;
  static const pageTitle = 15.0;
  static const List<String> fontFallback;
}

/// ThemeData：整体 Material 主题向 PCL 观感靠拢
ThemeData buildPclTheme();
```

---

## 5. `lib/widgets/pcl_widgets.dart` 必须提供的 API

```dart
/// 面板：白色/半透明白底 + 6 圆角，可选描边
class PclPanel extends StatelessWidget {
  const PclPanel({super.key, required this.child, this.padding, this.radius, this.color, this.border});
  final Widget child; final EdgeInsetsGeometry? padding; final double? radius;
  final Color? color; final BoxBorder? border;
}

/// PCL 风格按钮：3 圆角 + 1px c1 描边 + halfWhite 底 + 13 字号
class PclButton extends StatelessWidget {
  const PclButton({super.key, required this.label, this.onPressed, this.icon, this.primary = false, this.dense = false});
  final String label; final VoidCallback? onPressed; final String? icon;
  final bool primary; final bool dense;
}

/// 图标按钮（25 或 28）：hover 背景 c6
class PclIconButton extends StatefulWidget {
  const PclIconButton({super.key, required this.icon, this.onPressed, this.size = 25, this.tooltip});
  final String icon; final VoidCallback? onPressed; final double size; final String? tooltip;
}

/// lucide SVG 图标：按 name 从 assets/lucide/<name>.svg 加载，用 colorFilter 着色
class PclIcon extends StatelessWidget {
  const PclIcon({super.key, required this.name, this.size = 16, this.color, this.strokeWidth});
  final String name; final double size; final Color? color; final double? strokeWidth;
}

/// 左栏列表项：36 高，RadioBox 选中态（c6 底 + c2/c3 文字 + 左侧 3px c3 竖条）
class PclListItem extends StatelessWidget {
  const PclListItem({super.key, required this.label, this.icon, this.selected = false, this.onTap, this.trailing, this.badge});
  final String label; final String? icon; final bool selected;
  final VoidCallback? onTap; final Widget? trailing; final String? badge;
}

/// 分组标题（左栏用）：12 字号 + 0.6 透明度 + 边距 13,10,5,4
class PclGroupLabel extends StatelessWidget {
  const PclGroupLabel({super.key, required this.text});
  final String text;
}

/// 顶部标签按钮（27 高，选中态白色文字 + 下划线/底块）
class PclTabButton extends StatelessWidget {
  const PclTabButton({super.key, required this.label, required this.selected, this.onTap});
  final String label; final bool selected; final VoidCallback? onTap;
}

/// 小标签（c6 底 + c2 文字 + 3 圆角 + 11 字号）
class PclTag extends StatelessWidget {
  const PclTag({super.key, required this.text, this.color, this.background});
  final String text; final Color? color; final Color? background;
}
```

**所有组件都不允许 import 具体页面**，只依赖 `pcl_theme.dart` 与 `assets/lucide`。

---

## 6. 外壳布局（`lib/pages/app_shell.dart`）

```
Material(背景 PclColors.background)
└─ Padding(10)                                   ← 四周留白
   └─ Container(圆角 6, clip, 白色底, 阴影)
      └─ Column
         ├─ 标题栏 (高度 48)
         │   ├─ 背景：PclColors.titleBarGradient 横向渐变（要求观感接近 PCL 蓝）
         │   ├─ 左侧：应用 Logo 形状 + 名称（自绘，不用 PCL 图片）
         │   ├─ 中间：4 个 PclTabButton —— 主页 / 下载 / 设置 / 工具
         │   └─ 右侧：3 个窗口按钮 PclIconButton(28) —— minus / box-select / x
         │        （注意：macOS 有原生红绿灯按钮，这三个按钮只做视觉还原，
         │          点击行为：x 关闭窗口 → 用 SystemNavigator.pop() 或留空并 tooltip 说明）
         └─ Expanded(Row)
            ├─ 可选左栏 (宽度 168)：PclGroupLabel + PclListItem 列表
            │   └─ 右侧 4px 阴影分隔
            └─ Expanded(主内容区，白底)
```

**页面映射**（PCL 的 4 个顶部标签 → UMango 的 4 个页面）：
| 标签 | 页面组件 | 文件 |
|------|----------|------|
| 主页 | `HomePage` | `lib/pages/home_page.dart`（已有，需按新设计重做视觉） |
| 下载 | `DownloadPage` | `lib/pages/download_page.dart`（带左栏分类） |
| 设置 | `SettingsPage` | `lib/pages/settings_page.dart` |
| 工具 | `ToolsPage` | `lib/pages/tools_page.dart`（**新建**：诊断 + 关于 + 合规声明） |

---

## 7. 合规声明（必须出现在工具页）

必须在界面上明确声明：

> UMango 是独立开发的第三方软件，与 PCL / PCL-CE 及其开发者无关联。
> 界面设计参照 PCL-CE（`Plain Craft Launcher 2/` 目录适用《PCL 分发有限许可》
> 与《PCL 存储库合理使用指南》），本项目未复制其源代码、XAML 或图片资源，
> 全部代码自行编写，图标使用 MIT 许可的 lucide。

---

## 8. 已知环境要点（供 agent 参考）

- 项目路径含空格与非 ASCII，**每次命令都要动态解析**：
  ```bash
  P=$(ls -d /Users/jonathan/Library/CloudStorage/*/"Deepseek Harness"/UMango | head -1)
  ```
  （OneDrive 会不定时改名挂载目录 `OneDrive-个人` ↔ `OneDrivePersonal`）
- 访问 GitHub raw 需加代理前缀：`http://gh-proxy.org/` + 原始 URL
- 构建产物在 `~/Library/Caches/umango-build/`，先 `source scripts/env.sh`
- 编译验证：`cd "$P/ui" && flutter build macos --debug`
- 单元测试：`cd "$P/ui" && flutter test`


---

## 9. 实测校验（用 PCL-CE 真实运行录屏逐像素验证）

参照物：一段 PCL-CE 在 Mac 上（Wine/CrossOver）运行的录屏，1106×664、55.9 秒、1612 帧。
通过窗口宽度反推出缩放比：**850 逻辑px × 1.3125 = 1115 ≈ 1106 视频px**，即
**视频px ÷ 1.3125 = 逻辑px**（用卡片行高交叉验证：63×1.3125 与实测 84 吻合）。

### 录屏内容时间线（OCR 逐帧读出）
| 时间段 | 页面 |
|--------|------|
| 0–6s | 使用协议弹窗（PCL CE 使用协议） |
| 6–20s | **下载页**：左栏分类（Minecraft/OptiFine/Forge/NeoForge/Cleanroom/Fabric/Legacy Fabric/Quilt/LabyMod/LiteLoader）+ 右侧 CurseForge / Modrinth 资源卡片列表 |
| 20–24s | Java 页 |
| 24–44s | **设置页**：左侧分类 + 右侧设置项 |
| 44s | 鸣谢/贡献者 |
| 45–56s | 设置页 + 日志 |

### 逐项校验结果

| 项目 | 实测（视频px → 逻辑px） | 规格声明 | 结论 |
|------|------------------------|----------|------|
| 标题栏颜色 | 横向 11 点采样，除两端圆角影响点外**其余 9 点完全一致** | 原写"横向渐变" | **修正为纯色**（`PclColors.titleBar`） |
| 标题栏高度 | 59px → 45.0 | XAML `PanTitle Height=48` | 接近，保留 48（XAML 为准） |
| 下载页左栏宽 | 218px → **167.7** | 168 | ✅ 吻合 |
| 列表行高 | 84px → **64.0** | `MyCompItem Height=64` | ✅ 吻合 |
| 列表项底色 | 纵向扫描无白色面板、无描边 | `Background=ColorBrushSemiTransparent` = `#01eaf2fe`（≈全透明） | ✅ 一致：**列表项默认透明，仅 hover/选中显色** |

### ⚠️ 颜色偏移说明（重要）

录屏采样的蓝色是 `#1b6bd3`，而 XAML 里 c3 是 `#1370f3`；白色采样为 `#f0f6f9`，XAML 里背景是 `#fbfbfb`。
全部颜色都呈同方向的系统性偏移（R+8 / G−5 / B−32 量级），这是 **Wine 渲染 + 录屏色彩管理**
带来的转换偏差，**不是应用本身的配色**。

因此：**调色板以 XAML token 为准**（那是 PCL-CE 的真实渲染值）。若照抄录屏里的偏移色，
在色彩管理正常的显示器上反而会偏色。几何与结构（宽度、高度、纯色/渐变、透明与否）
则以录屏实测为准 —— 这些不受色彩偏移影响。

### 仍未验证的部分（如实记录）

- 本次开发环境中**视觉大模型被限流不可用**，因此无法对截图做语义级描述；
  以上结论全部来自程序化像素扫描与本地 tesseract OCR，不含任何"凭印象"的判断。
- 选中态高亮的精确色值未测出（视频压缩 + 近白配色导致信噪比不足），
  沿用 PCL 的 `ColorBrush6 (#d5e6fd)` 作为选中底色。
- 设置页左栏实测宽 135.4 逻辑px（与下载页的 168 不同），当前实现未按此区分。
