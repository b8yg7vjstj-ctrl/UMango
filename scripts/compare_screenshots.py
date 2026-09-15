#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""UMango 截图 ↔ PCL-CE 参照数值 的程序化比对。

只用 Python 标准库 + 调用 ffmpeg 解码 PNG（不解码 PNG 本身：自己写 zlib/滤波器
还原只对非隔行真彩色成立，交给 ffmpeg 更稳，也和参照数据的取法一致）。
本机没有 Chrome，视觉大模型也被限流不可用 —— 所以这里**一个模型都不调用**，
所有结论都来自逐像素扫描。

用法::

    python3 scripts/compare_screenshots.py                 # 用默认目录
    python3 scripts/compare_screenshots.py --dir ui/build/screenshots
    python3 scripts/compare_screenshots.py --json out.json --no-report

默认截图目录：``ui/build/screenshots``（相对于本脚本所在的仓库根目录；
也可以用 ``--dir`` 指定绝对路径，路径里的空格与非 ASCII 一律由调用方负责引号，
脚本内部全部用 pathlib 处理，不做字符串拼接）。

=============================================================================
参照数值的来源（重要：请勿把这里的数字当成"凭印象的估计"）
=============================================================================
参照值有两个独立来源，并且两者**交叉验证一致**：

1. **PCL-CE 真实运行录屏的逐像素测量**
   · 素材：PCL-CE 在 Mac 上（Wine/CrossOver）运行的录屏，1106×664、55.9 秒、
     1612 帧。原始记录见 ``docs/DESIGN_PCL_CE.md`` 第 9 节「实测校验」。
   · 缩放比：窗口逻辑宽 850px × 1.3125 = 1115 ≈ 1106 视频px，
     即 **视频px ÷ 1.3125 = 逻辑px**。
     该比值用卡片行高交叉验证过：63 逻辑px × 1.3125 ≈ 82.7，与录屏实测 84 吻合。
   · 由它测出的几何量：
       - 标题栏颜色：横向 11 点采样，「除两端圆角影响点外其余 9 点完全一致」
         ⇒ 标题栏是**纯色**（原规格初稿写的"横向渐变"因此被修正）。
       - 下载页左栏宽：218 视频px ÷ 1.3125 = **167.7 逻辑px**。
       - 列表行高：84 视频px ÷ 1.3125 = **64.0 逻辑px**。
       - 标题栏高度：59 视频px ÷ 1.3125 = 45.0 逻辑px（与 XAML 的 48 接近，
         取 XAML 的 48，见下）。

2. **PCL-CE 的 XAML 资源定义**（``docs/DESIGN_PCL_CE.md`` 第 2 节 / 第 6 节）
   · ``PanTitle Height=48``       ⇒ 标题栏高度 48 逻辑px
   · ``MyCompItem Height=64``     ⇒ 列表行（包卡片）高 64 逻辑px
   · 左栏宽度 168                 ⇒ ``PclMetrics.leftRailWidth``
   · 背景 ``#fbfbfb``             ⇒ ``PclColors.background``

  ⚠️ 颜色有一个已知的系统性偏移：录屏采样到的蓝色是 ``#1b6bd3``，而 XAML 里
  ``c3`` 是 ``#1370f3``；白色采样 ``#f0f6f9`` 而 XAML 背景是 ``#fbfbfb``。
  全部颜色同方向偏移（R+8 / G−5 / B−32 量级），这是 **Wine 渲染 + 录屏色彩
  管理**造成的，不是应用配色。
  ⇒ 因此本脚本的规矩是：**颜色参照一律以 XAML token 为准**（那是 PCL-CE 的真实
  渲染值），**几何/结构量（宽、高、纯色与否、透明与否）以录屏实测为准** ——
  几何量不受色彩偏移影响。

=============================================================================
本脚本测什么、怎么测
=============================================================================
· 标题栏纯色性：在 y=6 处横向均匀取 9 点，判断是否同色（容差 ≤6/通道）。
  参照：PCL-CE 实测为纯色（所以我们要求也必须是纯色）。
  说明：UMango 窗口四周有 10 逻辑px 的留白（``PclMetrics.windowMargin``），
  所以 y=6 大概率落在留白（``#fbfbfb``）而不是标题栏里。脚本因此**同时**用
  颜色带检测定位真正的标题栏带，并分别报告"y=6 采样"与"标题栏带采样"两组
  结果 —— 不把没测到的东西写成测到了。
· 标题栏高度：先定位标题栏色带（避免把"留白→标题栏"的外边界误当成栏高），
  在窗口水平中点纵向扫描该色带的上下边界。
  参照：逻辑 48px（允许 44~52）。
  截图是物理像素：脚本先用「图片宽度 / 850」推断缩放比并在输出里说明，
  再用逻辑尺寸比对。若该比值明显不是整/半值，会额外用 1280 逻辑宽度
  （集成测试钉死的视口）复核，并把两个候选都写出来。
· 左侧导航宽度：在内容区中部高度处横向扫描，找第一个颜色边界。
  参照：逻辑 168px（允许 160~176）。**仅下载页适用**，其他页输出"不适用"。
· 列表行高：在下载页卡片区纵向扫描，统计"颜色变化位置"的重复间距；
  参照：逻辑 64px（允许 60~68）。测不到就如实写"无法测量"，不编造。
· 主背景色：取内容区外的留白处颜色，参照 ``PclColors.background = #fbfbfb``
  （容差 ≤10/通道）。

退出码：0 = 所有"可测量项"都 PASS；1 = 有可测量项 FAIL；
      2 = 连截图都没拿到 / 解码失败（无法评估）。
"""

from __future__ import annotations

import argparse
import json
import platform
import shutil
import statistics
import struct
import subprocess
import sys
from pathlib import Path
from typing import Iterable, Sequence

# ---------------------------------------------------------------------------
# 参照值（来源见模块 docstring：PCL-CE 录屏 1106×664 @1.3125 + XAML token）
# ---------------------------------------------------------------------------

#: PCL-CE 录屏里的窗口逻辑宽度；用来从截图宽度反推缩放比。
REFERENCE_WINDOW_LOGICAL_WIDTH = 850.0

#: 集成测试 ``screenshot_test.dart`` 钉死的逻辑视口尺寸（缩放比推断的基准）。
#: 由 ``main()`` 依据 ``--logical-width/--logical-height`` 覆盖，所以是可变全局。
INTEGRATION_TEST_VIEWPORT_WIDTH = 1280.0
INTEGRATION_TEST_VIEWPORT_HEIGHT = 800.0
EXPECTED_LOGICAL_WIDTH = INTEGRATION_TEST_VIEWPORT_WIDTH
EXPECTED_LOGICAL_HEIGHT = INTEGRATION_TEST_VIEWPORT_HEIGHT

#: 标题栏高度：逻辑 48，允许 44~52。
REF_TITLE_BAR_HEIGHT = 48.0
TOL_TITLE_BAR_HEIGHT = (44.0, 52.0)

#: 左侧导航宽度：逻辑 168，允许 160~176（仅下载页适用）。
REF_LEFT_RAIL_WIDTH = 168.0
TOL_LEFT_RAIL_WIDTH = (160.0, 176.0)

#: 列表行高：逻辑 64，允许 60~68。
REF_ROW_HEIGHT = 64.0
TOL_ROW_HEIGHT = (60.0, 68.0)

#: 主背景色 ``PclColors.background``（0xFFFBFBFB），容差 ≤10/通道。
REF_BACKGROUND = (0xFB, 0xFB, 0xFB)
TOL_BACKGROUND = 10

#: 标题栏纯色判定的通道容差。
TOL_UNIFORM = 6

#: 判定"颜色变化"的阈值：相邻像素的通道差之和超过它就认为跨过了边界。
EDGE_THRESHOLD = 24

#: 默认输入/输出路径（相对于本脚本所在的仓库根目录）。
DEFAULT_SCREENSHOT_DIR = Path(__file__).resolve().parent.parent / "ui" / "build" / "screenshots"
DEFAULT_REPORT_PATH = Path(__file__).resolve().parent.parent / "docs" / "SCREENSHOT_CHECK.md"
#: 手工维护的叙述段（"我实测到的 / 我推断的 / 卡在哪一步"）。
#: 报告每次运行都会重写，但这一段是人的分析，脚本只负责把它原样附在末尾。
MANUAL_SECTION = r"""# 附：人工分析（我实测到的 / 我推断的 / 卡在哪一步）

> 上面第 0~4 节由脚本自动生成。这一节**写死在脚本里**，负责区分
> 「**实测**」（命令/脚本真的量出来的数字）、「**推断**」（依据代码/文档/像素规律
> 推出的结论，没有直接量到）与「**做不到**」（环境限制，如实记录）。
> 三类内容绝不混写。

## A. 实测到的

### A1. 截图能力（`flutter drive` 真的跑通了）

运行命令（本机实测可用，退出码 0）：

```bash
P=$(ls -d /Users/jonathan/Library/CloudStorage/*/"Deepseek Harness"/UMango | head -1)
source "$P/scripts/env.sh"
cd "$P/ui"
flutter drive \
  --driver=test_driver/integration_test.dart \
  --target=integration_test/screenshot_test.dart \
  -d macos
```

产物（每次运行会重建目录）：

| 文件 | 尺寸 | 字节 | 说明 |
|------|------|------|------|
| `ui/build/screenshots/umango_home.png` | 1280×800 | 163515 | 主页 |
| `ui/build/screenshots/umango_download.png` | 1280×800 | 173369 | 下载页（**含真实搜索出的包卡片**） |
| `ui/build/screenshots/umango_settings.png` | 1280×800 | 104268 | 设置页 |
| `ui/build/screenshots/umango_tools.png` | 1280×800 | 168762 | 工具页 |

`flutter drive` 在 macOS 上**可用**，不需要退到 `screencapture` 兜底。

### A2. `binding.takeScreenshot` 在本机 macOS 上一定失败（实测异常文本）

应用侧日志原文：

```
[shot] ✗ binding.takeScreenshot("umango_home") 同步抛出：
MissingPluginException(No implementation found for method captureScreenshot
on channel plugins.flutter.io/integration_test)
```

这不是配置错误，而是上游 macOS 实现的缺口。**实测/代码核实**三条证据：

1. `integration_test_macos` 的 `IntegrationTestPlugin.swift` 只实现 `allTestsFinished`，
   `default` 分支返回 `FlutterMethodNotImplemented`；
2. 该包的 `pubspec.yaml` 没有声明 `macos` 平台，所以本项目解析出的
   `.flutter-plugins-dependencies` 里 `"macos": []` 为空，`GeneratedPluginRegistrant.swift` 也不注册它；
3. 运行日志与之完全一致（`MissingPluginException`）。

### A3. 第一次运行时应用被这个异常打死（实测崩溃链）

第一次 `flutter drive` 的日志顺序：

```
[shot] ✓ umango_home → .../umango_home.png (163515 字节)
[shot] ✗ binding.takeScreenshot("umango_home") 失败：MissingPluginException(...)
Unhandled exception:
DriverError: Failed to fulfill RequestData due to remote error
Original error: ext.flutter.driver: (112) Service has disappeared
```

**推断→实测的修正过程**：最初用 `try/catch` 包 `takeScreenshot`，仍然崩。
说明该 `MissingPluginException` 是**异步未捕获异常**（从平台消息回调里抛出），
`try/catch` 抓不到，会逃到根 zone 打死 isolate。

**修法（已实测有效）**：把它放进 `runZonedGuarded` 提供的子 zone，
异常在源头的 zone 就被接住。改完之后同一条命令跑完四个页面、退出码 0。

### A4. 第一次"能拿到截图"时，设置页触发了真实的布局断言（实测）

第二次运行的日志（当时 `lib/pages/settings_page.dart` 还是旧版）：

```
flutter: [shot] ✓ umango_settings → .../umango_settings.png (13288 字节)
flutter: !! FlutterError 被兜底捕获：BoxConstraints forces an infinite height.
flutter:   The offending constraints were: BoxConstraints(0.0<=w<=Infinity, h=Infinity)
flutter:   The relevant error-causing widget was: Row
flutter:   Row:.../ui/lib/pages/settings_page.dart:624:12
```

**实测结论**：`_PresetRow` 的 `Row(crossAxisAlignment: stretch)` 处在
`SingleChildScrollView` 的无界高度里，`stretch` 要求子项填一个无限高度 ⇒ 断言失败。
这就是"只有真实渲染才会暴露、单元测试全绿也发现不了"的那类问题。
（我当时**没有**改 `ui/lib/**` —— 那是界面负责人的文件；该文件在我这轮运行期间被
并行运行的另一个 agent 加上了 `IntrinsicHeight` 修好了，重跑后设置页截图从
13288 字节变成 104268 字节，说明它真的渲染出了内容。）

### A5. 五个指标全部测到（最终一次运行，退出码 0）

| 指标 | 参照 | 主页 | 下载页 | 设置页 | 工具页 |
|------|------|------|--------|--------|--------|
| 标题栏高度 | 逻辑 48px（44~52） | 48.0 ✅ | 48.0 ✅ | 48.0 ✅ | 48.0 ✅ |
| 标题栏纯色性（y=6，9 点） | 纯色 | 同色（差 0）✅ | 同色 ✅ | 同色 ✅ | 同色 ✅ |
| 标题栏色带纯色性 | 纯色 | 97.9% 本色 ✅ | 97.9% ✅ | 97.9% ✅ | 97.9% ✅ |
| 左侧导航宽度 | 逻辑 168px（160~176） | 不适用 | **164.0**（口径A）/ 168.0（口径B）✅ | 不适用 | 不适用 |
| 列表行高 | 逻辑 64px（60~68） | 不适用 | **64.0** ✅ | 不适用 | 不适用 |
| 主背景色 | `#fbfbfb`（容差 ≤10） | `#f4f4f4`，差 7 ✅ | 同 ✅ | 同 ✅ | 同 ✅ |

汇总：**PASS 18 · FAIL 0 · 无法测量 0 · 不适用 6 · 仅记录 4**。

标题栏本色实测 **`#1370f3`**，与 PCL-CE 的 XAML token `c3 = #1370f3` **完全一致**
（录屏里因为 Wine + 色彩管理偏成了 `#1b6bd3`，进一步印证了"颜色以 XAML 为准"这条规则）。

## B. 推断的（没有直接量到，写清推断依据）

1. **缩放比 = 1.0×（推断，非实测）**。
   截图 1280×800，与集成测试钉死的逻辑视口 1280×800 一一对应；同时实测
   "窗口容器 = 1260×780，四周留白 10px"正好等于 `(1280−20) × (800−20)`。
   由此推断物理px : 逻辑px = 1:1，于是上面所有"÷scale"换算实际上是原值。
   （脚本仍按 `图片宽 ÷ (逻辑视口宽 + 2×留白)` 计算，得 0.9846 —— 差异来自
   "圆角窗口的边界像素归属"这种量化分歧，量级 1.6%，不影响 44~52 / 160~176 /
   60~68 这些容差的判定。脚本把两个候选都打印出来，没有藏。）
2. **`umango_download.png` 里那些深色块是包卡片（推断）**。
   依据：该截图是在测试真实输入 `express` 并点击「搜索」之后拍的（`npm search express`
   本机实测可联网返回 JSON），且脚本在该图上量到了**重复 3 次的 64.0 逻辑px 间距**，
   正好等于 XAML `MyCompItem Height=64`。
   **没有**用视觉模型确认卡片的内容/文字（本机视觉模型被限流），所以是推断。
3. **主背景色 `#f4f4f4` 偏暗的原因（推断）**。
   `#fbfbfb` 只出现在窗口容器外围那 10px 留白里，而窗口自己的
   `BoxShadow(blurRadius 12, offset (0,2))` 会把这一圈从 `#fbfbfb` 压到约 `#e0e0e0`。
   我采样的是"最外侧 5px"（实测 `#f4f4f4`，差 7，通过），内侧那一圈是 `#eaeaea`（差 17）。
   推断 `#fbfbfb` 与 `#f4f4f4` 的残余差异来自阴影在采样带内的**未完全衰减的余量**，
   而不是应用配色不同 —— 依据是：容器内部是纯白 `#ffffff`（实测），
   窗口外没有第二处能露出 `#fbfbfb` 的空地。**这一条是推断，不是实测**：
   我没有量到纯粹的、完全不受阴影影响的 `#fbfbfb` 区域（它不存在于这张截图里）。
4. **左栏宽度的两个口径（推断）**。
   实测左栏最右侧可见的像素是 x=174..177 这 4 个物理px，它们是 `_RailShadow`
   的"由黑渐隐到全透明"的 4 逻辑px 渐变带（代码依据：`_RailShadow` 宽
   `PclMetrics.leftShadowWidth = 4`，`Positioned(right: 0)`）。因此
   "渐变起点"= 164 逻辑px、"渐变终点 + 1"= 168 逻辑px。
   参照值 168 的口径是"内容盒宽度"，所以**推断** 168 对应渐变终点那一侧；
   判定取了更保守的起点口径（164），两个都在 160~176 内。
5. **y=6 那一行落在留白带里（推断→已被数据支持）**。
   实测 y=6 处 9 点全是 `#f0f0f0`（留白被阴影压暗后的灰），且第一行蓝色像素出现在
   y=10 —— 与 `PclMetrics.windowMargin = 10` 完全吻合。所以任务里"y=6 采样判断
   标题栏是否纯色"在本实现上测到的其实是**留白**；脚本因此额外在真正的标题栏色带里
   取 9 点并按"本色覆盖率"判定纯色性，两组结果都列出来。

## C. 做不到 / 卡住过的，如实记录

1. **`binding.takeScreenshot` 的产物（`driver_*.png`）拿不到** ——
   原因见 A2/A3，是上游 macOS 插件缺口，不是本项目配置问题。
   真正的截图走的是应用内 `RenderView.debugLayer.toImage()` + `dart:ui` PNG 编码，
   **完全不依赖平台插件、不依赖 Chrome、不经过视觉模型**。
   （`test_driver/integration_test.dart` 里的 `onScreenshot` 回调按标准写法保留：
   将来上游补齐 macOS 实现，同一条链路会自动开始产出 `driver_*.png`，
   两份产物可用像素比对互相验证。）
2. **视觉大模型不可用** —— 所以本报告里没有任何"看图说话"式的描述；
   全部结论来自逐像素扫描。这也意味着"卡片长得像不像 PCL"这类**语义**问题
   不在本报告的覆盖范围内。
3. **`flutter test integration_test/screenshot_test.dart -d macos` 这条路没走** ——
   任务里把它列为"退而求其次"的选项，但既然 `flutter drive` 实测可用（A1），
   就没有必要走它；`takeScreenshot` 需要 driver 才能把字节取回 host 这一限制依然成立。
4. **`screencapture` 兜底没走** —— 同上，主路径已通。
   仅记录一条实测事实：`screencapture -x -o -l $(GetWindowID umango)` 需要额外的
   `GetWindowID` 工具（本机未安装），而 `-R x,y,w,h` 需要窗口屏幕坐标，
   在无人值守的测试里不如应用内取帧稳。
5. **列表行高在"空态"下确实测不到** —— 第一次跑（未加搜索步骤）时脚本如实输出了
   "无法测量"，原因写得明明白白：下载页当时是空态、没有可测的行。
   后来在 `screenshot_test.dart` 里加了一步**真实搜索**（输入 `express` 并点「搜索」）
   才让行高变得可测。**这是测试设计的改进，不是把"测不到"改成"测到了"** ——
   两种状态下的真实输出都记录在案。

## D. 这套环怎么用

```bash
P=$(ls -d /Users/jonathan/Library/CloudStorage/*/"Deepseek Harness"/UMango | head -1)
source "$P/scripts/env.sh"

# 1) 抓图（真实运行的应用自己截图）
cd "$P/ui"
flutter drive --driver=test_driver/integration_test.dart \
              --target=integration_test/screenshot_test.dart -d macos

# 2) 程序化比对（退出码 0 = 全 PASS，1 = 有 FAIL，2 = 没截到图/解码失败）
cd "$P"
python3 scripts/compare_screenshots.py
```

改界面代码之后重复这两条即可：截图像素变了、某项超容差，报告里会直接标出来。
"""

#: 采样行的 y（物理像素）。参照做法就是 y=6。
SAMPLE_ROW_Y = 6
SAMPLE_POINTS = 9

PAGE_ORDER = ("umango_home", "umango_download", "umango_settings", "umango_tools")
PAGE_LABEL = {
    "umango_home": "主页",
    "umango_download": "下载",
    "umango_settings": "设置",
    "umango_tools": "工具",
}


# ---------------------------------------------------------------------------
# PNG 解码（ffmpeg → raw RGB24）
# ---------------------------------------------------------------------------


def png_size(path: Path) -> tuple[int, int]:
    """从 IHDR 读 PNG 的像素宽高（不依赖 ffmpeg）。"""
    with path.open("rb") as fh:
        head = fh.read(33)
    if len(head) < 33 or head[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path.name} 不是 PNG（签名不匹配）")
    width, height = struct.unpack(">II", head[16:24])
    return int(width), int(height)


class RgbImage:
    """一张已解码的 RGB24 图：``pixels[y * width * 3 + x * 3 + c]``。"""

    def __init__(self, width: int, height: int, pixels: bytes) -> None:
        self.width = width
        self.height = height
        self.pixels = pixels

    def px(self, x: int, y: int) -> tuple[int, int, int]:
        if x < 0 or y < 0 or x >= self.width or y >= self.height:
            raise IndexError(f"({x},{y}) 超出 {self.width}×{self.height}")
        i = (y * self.width + x) * 3
        return (self.pixels[i], self.pixels[i + 1], self.pixels[i + 2])

    def row(self, y: int) -> list[tuple[int, int, int]]:
        return [self.px(x, y) for x in range(self.width)]


def decode_png_via_ffmpeg(path: Path) -> RgbImage:
    """用 ffmpeg 把 PNG 解成 raw RGB24 再读进内存。

    与参照数据（录屏抽帧）走同一条解码路径：都是 ``ffmpeg -pix_fmt rgb24``。
    """
    if shutil.which("ffmpeg") is None:
        raise RuntimeError("找不到 ffmpeg（本脚本依赖它解码 PNG）")
    width, height = png_size(path)
    proc = subprocess.run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel", "error",
            "-i", str(path),
            "-f", "rawvideo",
            "-pix_fmt", "rgb24",
            "-",
        ],
        capture_output=True,
        check=False,
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"ffmpeg 解码失败（{path.name}）：{proc.stderr.decode('utf-8', 'replace').strip()}"
        )
    data = proc.stdout
    expected = width * height * 3
    if len(data) < expected:
        raise RuntimeError(
            f"ffmpeg 输出字节不足：拿到 {len(data)}，期望 {expected}（{width}×{height}×3）"
        )
    # 某些构建会补到偶数行，多出来的尾部直接截掉。
    return RgbImage(width, height, data[:expected])


# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------


def dist(a: Sequence[int], b: Sequence[int]) -> int:
    """两个颜色的最大通道差（用于容差判定）。"""
    return max(abs(int(x) - int(y)) for x, y in zip(a, b))


def dist_sum(a: Sequence[int], b: Sequence[int]) -> int:
    """两个颜色的通道差之和（用于边界检测，比最大值更不容易漏微小渐变）。"""
    return sum(abs(int(x) - int(y)) for x, y in zip(a, b))


def hexc(c: Sequence[int]) -> str:
    return "#{:02x}{:02x}{:02x}".format(int(c[0]), int(c[1]), int(c[2]))


def median_color(colors: Iterable[Sequence[int]]) -> tuple[int, int, int]:
    items = [tuple(int(v) for v in c) for c in colors]
    if not items:
        raise ValueError("median_color 收到空序列")
    return (
        int(statistics.median([c[0] for c in items])),
        int(statistics.median([c[1] for c in items])),
        int(statistics.median([c[2] for c in items])),
    )


def run_lengths_top(colors: Sequence[tuple[int, int, int]], limit: int = 6) -> list[tuple[tuple[int, int, int], int]]:
    """把一段颜色序列压成 [(颜色, 连续长度), ...]，按长度降序取前 limit 个。"""
    if not colors:
        return []
    runs: list[tuple[tuple[int, int, int], int]] = []
    current = colors[0]
    count = 1
    for c in colors[1:]:
        if dist_sum(c, current) <= EDGE_THRESHOLD:
            # 同一段内取均值，避免 PNG 压缩噪声把长段切碎
            current = (
                (current[0] * count + c[0]) // (count + 1),
                (current[1] * count + c[1]) // (count + 1),
                (current[2] * count + c[2]) // (count + 1),
            )
            count += 1
        else:
            runs.append((current, count))
            current = c
            count = 1
    runs.append((current, count))
    runs.sort(key=lambda item: -item[1])
    return runs[:limit]


def edge_positions(values: Sequence[tuple[int, int, int]]) -> list[int]:
    """返回索引列表：``values[i]`` 与 ``values[i-1]`` 的差超过阈值的位置 i。"""
    out: list[int] = []
    for i in range(1, len(values)):
        if dist_sum(values[i], values[i - 1]) > EDGE_THRESHOLD:
            out.append(i)
    return out


def format_range(lo: float, hi: float, unit: str = "px") -> str:
    return f"{lo:g}~{hi:g} {unit}"


# ---------------------------------------------------------------------------
# 指标测量
# ---------------------------------------------------------------------------


class Measurement:
    """单项测量的结果（含"为什么没测出来"的说明）。"""

    def __init__(
        self,
        key: str,
        title: str,
        *,
        measured_logical: float | None = None,
        measured_text: str = "",
        reference_text: str = "",
        lo: float | None = None,
        hi: float | None = None,
        detail: str = "",
        applicable: bool = True,
        note: str = "",
    ) -> None:
        self.key = key
        self.title = title
        self.measured_logical = measured_logical
        self.measured_text = measured_text
        self.reference_text = reference_text
        self.lo = lo
        self.hi = hi
        self.detail = detail
        self.applicable = applicable
        self.note = note

    @property
    def status(self) -> str:
        if not self.applicable:
            return "N/A"
        if self.measured_logical is None:
            return "无法测量"
        if self.lo is None or self.hi is None:
            return "仅记录"
        return "PASS" if self.lo <= self.measured_logical <= self.hi else "FAIL"

    def as_dict(self) -> dict:
        return {
            "key": self.key,
            "title": self.title,
            "measured_logical": self.measured_logical,
            "measured_text": self.measured_text,
            "reference": self.reference_text,
            "range": [self.lo, self.hi],
            "status": self.status,
            "detail": self.detail,
            "applicable": self.applicable,
            "note": self.note,
        }



# ===========================================================================
# 参照值常量补充
# ===========================================================================

#: 窗口四周留白（PclMetrics.windowMargin）：用来把"窗口内的坐标"换算成
#: "窗口容器内的坐标"。**被测量验证过的**：截图上留白带正好是 10 物理px，
#: 且标题栏色带从第 10 个像素开始（标题栏就在窗口容器顶部）。
REF_WINDOW_MARGIN = 10.0

#: 窗口容器右下角阴影的水平/垂直外扩范围（BoxShadow blurRadius 12 + offset (0,2)）。
#: 用来解释"为什么留白带里越靠近窗口越暗"，并从没被阴影吃掉的角落取真实底色。
SHADOW_REACH_HINT = 16.0

#: 判定"某一列/行基本是白底"的阈值。
WHITE_MARGIN_TOL = 6

#: 估算窗口容器（白色圆角窗口）内边界时的"非白计数"门限。
NONWHITE_RATIO = 0.9


def color_hex(c: tuple[int, int, int]) -> str:
    return "#{:02x}{:02x}{:02x}".format(*c)


def is_grayish(c: tuple[int, int, int], tol: int = 12) -> bool:
    """是否是"灰/白"（R≈G≈B）—— 用来把蓝色标题栏、彩色卡片从底色里区分出来。"""
    return max(c) - min(c) <= tol


def is_near_white(c: tuple[int, int, int], tol: int = WHITE_MARGIN_TOL) -> bool:
    return all(v >= 255 - tol for v in c)


# ===========================================================================
# 窗口几何
# ===========================================================================


class WindowGeometry:
    """从截图上量出来的窗口几何（物理像素，0 起）。"""

    def __init__(
        self,
        left: int,
        right: int,
        top: int,
        bottom: int,
        image_size: tuple[int, int],
        margin: tuple[int, int, int, int],
    ) -> None:
        self.left = left
        self.right = right
        self.top = top
        self.bottom = bottom
        self.image_size = image_size
        self.margin = margin  # (左, 上, 右, 下) 物理px

    @property
    def width(self) -> int:
        return self.right - self.left + 1

    @property
    def height(self) -> int:
        return self.bottom - self.top + 1

    def as_dict(self) -> dict:
        return {
            "left": self.left, "right": self.right,
            "top": self.top, "bottom": self.bottom,
            "width": self.width, "height": self.height,
            "image_size": list(self.image_size),
            "margin_px": list(self.margin),
        }


def _is_flat_gray(c: tuple[int, int, int]) -> bool:
    """R≈G≈B（灰阶）。

    窗口外的留白带（``#fbfbfb`` 被窗口阴影压暗的各级灰）与阴影都是灰阶；
    ``#fbfbfb``/``#f0f0f0`` 这类浅色与黑色阴影都算。而内容区的白色面板、
    PCL 蓝标题栏、彩色卡片都**不是**灰阶 —— 这是把"窗口内部"与"窗口之外"
    区分开的关键判据（只比亮度会被浅色阴影骗到）。
    """
    return max(c) - min(c) <= 10


def _widest_span(flags: list[bool]) -> tuple[int, int] | None:
    """返回 flags 里最长的 True 连续区间 (start, end)，含端点。"""
    best: tuple[int, int] | None = None
    start: int | None = None
    for i, f in enumerate(list(flags) + [False]):
        if f and start is None:
            start = i
        elif not f and start is not None:
            if best is None or (i - 1 - start) > (best[1] - best[0]):
                best = (start, i - 1)
            start = None
    return best


def _window_axis_spans(img: RgbImage, y: int) -> tuple[int, int] | None:
    """在 y 行上找窗口容器的水平跨度 = 非灰像素的最长连续区间。

    窗口内部是白色面板（非灰）+ PCL 蓝标题栏（非灰），而两侧留白带是灰阶；
    左栏那种整列纯白的区域并不会把跨度切断 —— 它的左右邻列都有非灰像素
    （标题栏/内容），所以"最长连续非灰区间"正好等于窗口容器的水平范围。
    """
    flags = [not _is_flat_gray(c) for c in img.row(y)]
    return _widest_span(flags)


def _horizontal_window_span(img: RgbImage) -> tuple[int, int]:
    """窗口容器的水平范围 = 所有行里「非灰区间」最宽的那一行的跨度。"""
    best: tuple[int, int, int] | None = None  # (width, start, end)
    for y in range(0, img.height, 2):
        span = _window_axis_spans(img, y)
        if span is None:
            continue
        width = span[1] - span[0] + 1
        if best is None or width > best[0]:
            best = (width, span[0], span[1])
    if best is None:
        return (0, img.width - 1)
    return (best[1], best[2])


def _is_shadowy(c: tuple[int, int, int], threshold: int = 250) -> bool:
    """是否是"被窗口阴影压暗的留白"。

    关键事实：窗口容器是纯白 ``#ffffff``，窗口外的留白是 ``#fbfbfb`` 被自己的
    BoxShadow 逐级压暗（越靠近窗口越暗，实测 246→224）。两者只差 4/通道，
    所以不能靠"灰不灰"区分 —— 靠**亮度**：容器内 ≥250，阴影带 <250。
    """
    return is_grayish(c) and max(c) < threshold


def _scan_for_dark_ramp(img: RgbImage, x: int, y_start: int, step: int) -> int | None:
    """从 y_start 沿 step 方向扫描，返回"持续变暗的留白带"开始的位置。

    "持续"用连续 5 行判定，避免把内容里偶然的一个偏灰像素当成边界。
    """
    run = 0
    y = y_start
    while 0 <= y < img.height:
        if _is_shadowy(img.px(x, y)):
            run += 1
            if run >= 5:
                return y - step * 4
        else:
            run = 0
        y += step
    return None


def detect_window(img: RgbImage, band: dict | None) -> WindowGeometry:
    """定位窗口容器（白色圆角窗口）在截图里的位置，单位物理像素。

    · **水平**：取所有行里「非灰区间」最宽的那一行的跨度。那一行要么在标题栏里
      （蓝色），要么在页面内容里（白色面板 + 彩色元素）；两侧留白是灰阶。
    · **竖直**：色带（标题栏）从容器顶边开始 ⇒ 从色带顶边向上扫、从色带底边向下扫，
      各自找到"持续变暗的留白带"（阴影斜坡）作为容器的上下边界。
    """
    h = img.height
    left, right = _horizontal_window_span(img)
    x_probe = left + max(1, (right - left) // 100)

    if band is not None:
        top = _scan_for_dark_ramp(img, x_probe, band["y0"], -1)
        bottom = _scan_for_dark_ramp(img, x_probe, band["y1"], 1)
        top = band["y0"] if top is None else min(top, band["y0"])
        bottom = band["y1"] if bottom is None else max(bottom, band["y1"])
    else:
        ys = [y for y in range(h) if not _is_shadowy(img.px(x_probe, y))]
        top, bottom = (ys[0], ys[-1]) if ys else (0, h - 1)

    # 圆角窗口的抗锯齿边会各占 1 行，实测到的斜坡起点因此比真实边界外扩 1px；
    # 收回来，让"容器边界"对得准 10 逻辑px 的留白（实测 top/bottom 都是 9/790）。
    top += 1
    bottom -= 1

    margin = (left, top, img.width - 1 - right, h - 1 - bottom)
    return WindowGeometry(left, right, top, bottom, (img.width, h), margin)


def find_title_bar_band(img: RgbImage, left: int, right: int) -> dict | None:
    """定位标题栏色带（PCL 蓝横贯整窗的那一条），返回它的颜色与竖直区间。

    判据：
    1. 候选色 = 图片顶部 25% 区域内出现次数最多的**非灰**颜色
       （窗口底色 #fbfbfb、面板白、阴影灰都算灰阶，被排除）；
    2. 该色在某一行的覆盖必须 ≥60% 窗口宽度 —— 标题栏横贯整窗，
       是唯一满足这个条件的元素（页面里的彩色卡片/按钮都只是局部）；
    3. 满足 (2) 的行里取**最长连续区间**作为色带 y 范围；
    4. 交叉校验：色带必须紧贴窗口容器顶部（顶部 25% 以内）。
    """
    h = img.height
    limit = max(1, int(h * 0.25))
    counts: dict[tuple[int, int, int], int] = {}
    for y in range(0, limit):
        for c in img.row(y)[left:right + 1:2]:
            if is_grayish(c):
                continue
            counts[c] = counts.get(c, 0) + 1
    if not counts:
        return None
    color, _ = max(counts.items(), key=lambda kv: kv[1])

    width = right - left + 1
    row_flags: list[bool] = []
    row_coverage: dict[int, float] = {}
    for y in range(0, limit):
        hit = 0
        total = 0
        for x in range(left, right + 1, 2):
            total += 1
            if dist(img.px(x, y), color) <= 12:
                hit += 1
        cov = hit / total if total else 0.0
        row_coverage[y] = cov
        row_flags.append(cov >= 0.6)

    span = _widest_span(row_flags)
    if span is None:
        return None
    y0, y1 = span
    # 色带必须贴窗口顶部：允许 y0 之前有几行是圆角/抗锯齿过渡
    if y0 > max(4, int(0.08 * h)):
        return None

    return {
        "color": color,
        "y0": y0,
        "y1": y1,
        "height_px": y1 - y0 + 1,
        "coverage": row_coverage.get((y0 + y1) // 2, 0.0),
        "width": width,
        "row_coverage": {str(k): round(v, 3) for k, v in row_coverage.items()},
    }


def detect_scale(img: RgbImage, geom: WindowGeometry) -> dict:
    """推断缩放比（物理px ÷ 逻辑px）。

    三条独立线索，全部写进报告：

    1. 图片宽度 = 窗口容器宽 + 2×留白（逻辑 10）
       ⇒ scale = 图片宽 / (窗口逻辑宽 + 20)；窗口逻辑宽未知，故这里只作为"反解"用：
       已知集成测试钉死的逻辑视口宽（``--logical-width``，默认 1280）时最干净。
    2. 留白带宽度：实测左右留白都是 10 物理px ⇒ scale = 10 / 10 = 1。
    3. 交叉校验：窗口内容高度 = 逻辑高 − 20。
    """
    margins = geom.margin
    margin_scale = margins[0] / REF_WINDOW_MARGIN if margins[0] else None

    width_scale = img.width / (EXPECTED_LOGICAL_WIDTH + 2 * REF_WINDOW_MARGIN)
    height_scale = img.height / (EXPECTED_LOGICAL_HEIGHT + 2 * REF_WINDOW_MARGIN)

    candidates: list[dict] = []
    if margin_scale:
        candidates.append({
            "basis": f"外层留白实测 {margins[0]} 物理px ÷ 逻辑 {REF_WINDOW_MARGIN:g}px",
            "ratio": margin_scale,
        })
    candidates.append({
        "basis": f"图片宽 {img.width}px ÷ (逻辑视口宽 {EXPECTED_LOGICAL_WIDTH:g}px "
                 f"+ 2×留白 {2 * REF_WINDOW_MARGIN:g}px)",
        "ratio": width_scale,
    })
    candidates.append({
        "basis": f"图片高 {img.height}px ÷ (逻辑视口高 {EXPECTED_LOGICAL_HEIGHT:g}px "
                 f"+ 2×留白 {2 * REF_WINDOW_MARGIN:g}px)",
        "ratio": height_scale,
    })

    # 采用"最接近 0.5 整数倍"的候选（Retina 常见 1.0 / 1.5 / 2.0）
    def tidiness(r: float) -> float:
        return abs(r - round(r * 2) / 2)

    picked = min(candidates, key=lambda c: (tidiness(c["ratio"]), abs(c["ratio"] - 1.0)))
    for c in candidates:
        c["text"] = (f"{c['basis']} = {c['ratio']:.4f}"
                     + ("（整数/半整数倍，符合常规缩放）" if tidiness(c["ratio"]) <= 0.01
                        else "（不是 0.5 的整数倍）"))
    return {
        "chosen": picked["ratio"],
        "candidates": candidates,
        "agreement": max(c["ratio"] for c in candidates) - min(c["ratio"] for c in candidates),
    }


# ===========================================================================
# 逐项测量
# ===========================================================================


def measure_title_bar_height(img: RgbImage, geom: WindowGeometry, band: dict | None) -> tuple[Measurement, dict]:
    """标题栏高度 = 已定位到的色带竖直跨度。

    为什么不直接找「第一个色变」：UMango 窗口四周有 10 逻辑px 留白
    （``PclMetrics.windowMargin``），从图片顶端往下扫，第一个色变是
    「留白 → 标题栏」的**外**边界（10 逻辑px），不是栏高。
    所以先按颜色把横贯整窗的色带找出来，再量它自己的高度。
    """
    ref = (f"逻辑 {REF_TITLE_BAR_HEIGHT:g}px（{format_range(*TOL_TITLE_BAR_HEIGHT)}）；"
           "来源：XAML `PanTitle Height=48`（录屏实测 59 视频px ÷ 1.3125 = 45.0，取 XAML）")
    if band is None:
        return Measurement(
            "title_bar_height", "标题栏高度", reference_text=ref,
            detail="在窗口顶部没有找到横贯整窗（≥60% 宽度）的纯色色带 —— 无法定位标题栏，故不测高度",
        ), {"band_found": False}

    span_px = band["height_px"]
    return Measurement(
        "title_bar_height", "标题栏高度",
        measured_logical=float(span_px),  # 物理px，由 analyse_page 按缩放比换算
        measured_text=f"{span_px} 物理px",
        reference_text=ref,
        lo=None, hi=None,
        detail=f"标题栏色 {color_hex(band['color'])}，色带 y 区间 = {band['y0']}..{band['y1']}"
               f"（物理px，共 {span_px} 行），该色在色带中段行的覆盖 = "
               f"{band['coverage'] * 100:.1f}% 窗口宽度。",
    ), {
        "band_color": color_hex(band["color"]), "band_y0": band["y0"], "band_y1": band["y1"],
        "band_coverage": band["coverage"], "band_span_px": span_px,
    }


def sample_uniform_row(img: RgbImage, y: int, points: int = SAMPLE_POINTS) -> tuple[list[tuple[int, int, int]], list[int], int]:
    """在 y 行横向均匀取 points 个点，返回 (色值, x 列表, 最大通道差)。"""
    y = max(0, min(img.height - 1, y))
    step = img.width / (points + 1)
    xs = [min(img.width - 1, max(0, int(round(step * (i + 1))))) for i in range(points)]
    colors = [img.px(x, y) for x in xs]
    base = colors[0]
    dev = max(dist(c, base) for c in colors)
    return colors, xs, dev


def measure_title_bar_uniform(
    img: RgbImage, band: dict | None, band_y: int | None
) -> tuple[Measurement, Measurement, dict]:
    """标题栏纯色性 —— 两组证据：

    A. **任务指定的口径**：在 y=6 处横向均匀取 9 点，判断是否同色。
    B. **真正的标题栏**：在标题栏色带上取 9 点，逐点判断"该点是不是色带本色"，
       并统计"色带本色的像素占整条色带的比例"。第 B 组是为了回答 A 组回答不了的
       问题 —— y=6 落在窗口外的留白带里，它测的是留白，不是标题栏。
       9 点里会有若干点落在标题栏的**前景元素**（Logo 方块、4 个标签文字、
       主题/窗口按钮）上，因此"9 点全都等于本色"本来就不是正确的判据；
       正确的判据是「除前景元素占据的位置外，整条色带都是同一个纯色」。
    """
    colors6, xs6, dev6 = sample_uniform_row(img, SAMPLE_ROW_Y)
    band_color = band["color"] if band else None

    y6_m = Measurement(
        "title_bar_uniform_y6",
        f"y={SAMPLE_ROW_Y} 处 9 点是否同色",
        measured_logical=float(dev6),
        measured_text=("同色（最大通道差 {}）" if dev6 <= TOL_UNIFORM
                       else "**不同色**（最大通道差 {}）").format(dev6)
                      + f"；9 点色值 {[color_hex(c) for c in colors6]}",
        reference_text=f"纯色（PCL-CE 录屏横向 9 点实测完全一致；容差 ≤{TOL_UNIFORM}/通道）",
        lo=0.0, hi=float(TOL_UNIFORM),
        detail=(f"y={SAMPLE_ROW_Y}（物理px）的主色 {color_hex(colors6[0])} 与标题栏色 "
                f"{color_hex(band_color) if band_color else '（未测到）'}"
                + ("一致 —— 该行确实落在标题栏内。" if band_color is not None
                   and dist(colors6[0], band_color) <= TOL_UNIFORM else
                   "**不一致**：这一行落在外层留白带（PclMetrics.windowMargin = 10 逻辑px）里，"
                   "所以这一项验证的是**留白底色是否均匀**，而不是标题栏；"
                   "标题栏本身见下一行。")
                + f" 采样 x = {xs6}"),
    )

    if band_color is None or band_y is None:
        return y6_m, Measurement(
            "title_bar_uniform_band", "标题栏色带是否为纯色",
            reference_text=f"纯色（容差 ≤{TOL_UNIFORM}/通道）",
            detail="没有定位到标题栏色带，无法测量",
        ), {"band_dev": None}

    # --- B 组：色带本色占比 + 9 点判定 ---
    band_colors, band_xs, _ = sample_uniform_row(img, band_y)
    matching = [dist(c, band_color) <= TOL_UNIFORM for c in band_colors]
    n_match = sum(1 for m in matching if m)
    band_dev = max(dist(c, band_color) for c in band_colors)

    # 整条色带的统计：所有带内像素里，本色占比是多少
    y0, y1 = band["y0"], band["y1"]
    total = 0
    same = 0
    for y in range(y0, y1 + 1):
        for x in range(img.width // 8, img.width * 7 // 8, 3):
            total += 1
            if dist(img.px(x, y), band_color) <= TOL_UNIFORM:
                same += 1
    ratio = same / total if total else 0.0

    # 判据：色带里"要么是本色、要么是前景元素"，不存在第二种底色。
    # 做法：统计带内出现的**非前景**颜色 —— 前景是白色系（文字/图标/半透明白），
    # 因此把所有"非本色"像素按是否偏白分类，偏白的算前景，其余算可疑底色。
    suspicious: dict[tuple[int, int, int], int] = {}
    for y in range(y0, y1 + 1, 2):
        for x in range(img.width // 8, img.width * 7 // 8, 3):
            c = img.px(x, y)
            if dist(c, band_color) <= TOL_UNIFORM:
                continue
            if is_near_white(c, tol=0) or min(c) >= 200:
                continue  # 白色系前景
            suspicious[c] = suspicious.get(c, 0) + 1
    top_suspicious = sorted(suspicious.items(), key=lambda kv: -kv[1])[:3]
    suspicious_share = sum(suspicious.values()) / (total / 1.0) if total else 0.0

    ok = suspicious_share < 0.02  # 非前景、非本色的像素 <2%
    detail = (
        f"色带 y={y0}..{y1}（{y1 - y0 + 1} 行），本色 {color_hex(band_color)}。"
        f"在色带中部（x 取中间 3/4 宽度，每 3px 采样）统计：本色像素占 {ratio * 100:.1f}%，"
        f"其余为白色系前景（Logo / 标签文字 / 按钮图标）。"
        f"排除白色系前景后，**其他颜色**（即「如果标题栏不是纯色就会出现的东西」）"
        f"占比 {suspicious_share * 100:.2f}%"
        + (f"，最多的三种是 {[(color_hex(c), n) for c, n in top_suspicious]}" if top_suspicious else "")
        + "。"
        f" 另在 y={band_y}（带中段）取 9 点（x = {band_xs}）：{n_match}/9 点等于本色，"
        f"最偏离点与本色差 {band_dev}（90 点里落在前景元素上的那几点不计入纯色性判定）。"
    )
    return y6_m, Measurement(
        "title_bar_uniform_band", "标题栏色带是否为纯色",
        measured_logical=0.0 if ok else 1.0,
        measured_text=(f"纯色 —— 本色 {color_hex(band_color)} 覆盖色带的 {ratio * 100:.1f}%，"
                       f"非前景的其他颜色仅 {suspicious_share * 100:.2f}%"
                       if ok else
                       f"**非纯色** —— 排除白色系前景后仍有 {suspicious_share * 100:.2f}% "
                       f"的像素既不是本色也不是前景：{[(color_hex(c), n) for c, n in top_suspicious]}"),
        reference_text=(f"纯色（PCL-CE 录屏横向采样：除两端圆角影响点外完全一致；"
                        f"容差 ≤{TOL_UNIFORM}/通道）"),
        lo=0.0, hi=0.0,
        detail=detail,
    ), {"band_dev": band_dev, "band_same_ratio": ratio,
        "suspicious_share": suspicious_share,
        "n_match_of_9": n_match}


def measure_left_rail(
    img: RgbImage, geom: WindowGeometry, scale: float, applicable: bool
) -> tuple[Measurement, dict]:
    """左侧导航宽度 = 下载页左栏的宽度。

    做法（列结构法，不依赖任何单行扫描的运气）：
    1. 候选 x 范围：窗口容器左边界 → 窗口水平中点（左栏一定在左半边）。
    2. 对每个 x 统计"该列在窗口竖直范围内有多少非白像素"。
       · 左栏的 4 逻辑px 阴影渐变是**贯穿整个窗口高度**的竖直带 ⇒ 计数接近满值；
       · 内容区里偶发的彩色元素（按钮/图标/文字）只占几十行 ⇒ 计数很小；
       · 中间留白 ⇒ 计数 0。
    3. 因此取"高计数"列组成的**最后**一段连续区间 = 左栏阴影；它的上界即
       "左栏最右侧的可见像素"。
    4. 宽度参照值 168 的口径是"左栏内容盒宽度"：阴影渐变在 4 逻辑px 内落到
       全透明，实测的"可见上界"会落在该渐变段内部 1~4px 处，所以报告里同时给出
       「渐变起点」与「渐变终点（外推）」两个口径，并说明取哪一个作为判定值。
    """
    ref = (f"逻辑 {REF_LEFT_RAIL_WIDTH:g}px（{format_range(*TOL_LEFT_RAIL_WIDTH)}）；"
           f"来源：录屏左栏 218 视频px ÷ 1.3125 = 167.7，与规格 §6 的 168 交叉验证一致")
    if not applicable:
        return Measurement(
            "left_rail_width", "左侧导航宽度", reference_text=ref, applicable=False,
            detail="PCL-CE 规格里的「左栏」是**下载页**版面的一部分"
                   "（`docs/DESIGN_PCL_CE.md` §6；设置页另按 §9 实测的 135.4 口径）。"
                   "本页形面上没有左栏，故不适用",
        ), {}

    y0, y1 = geom.top, geom.bottom
    x_lo = geom.left
    x_hi = (geom.left + geom.right) // 2
    counts: dict[int, int] = {}
    for x in range(x_lo, x_hi + 1):
        n = 0
        for y in range(y0, y1 + 1, 2):
            if not is_near_white(img.px(x, y), tol=2):
                n += 1
        counts[x] = n

    total_rows = len(range(y0, y1 + 1, 2))
    high = max(2, int(total_rows * 0.8))
    high_cols = [x for x in range(x_lo, x_hi + 1) if counts[x] >= high]

    detail = (
        f"窗口容器 x={geom.left}..{geom.right}（y={geom.top}..{geom.bottom}）；"
        f"在左半边逐列统计非白像素数（共 {total_rows} 个采样行），"
        f"「高计数」门限 = {high}（≥80%）。"
    )
    if not high_cols:
        return Measurement(
            "left_rail_width", "左侧导航宽度", reference_text=ref,
            detail=detail + "：**没有任何列达到高计数门限** —— 说明该页左栏不存在或整列全白，"
                            "无法测量（不猜）",
        ), {"counts_nonzero": sum(1 for x in counts if counts[x] > 0)}

    # 高计数列里最后一段连续区间 = 左栏阴影带
    best: tuple[int, int] | None = None
    run_start: int | None = None
    for x in range(x_lo, x_hi + 2):
        is_high = x <= x_hi and counts.get(x, 0) >= high
        if is_high and run_start is None:
            run_start = x
        elif not is_high and run_start is not None:
            best = (run_start, x - 1)
            run_start = None
    assert best is not None
    shadow0, shadow1 = best

    # 口径 A：阴影渐变起点（第一个可见像素）
    width_a_px = shadow0 - geom.left
    # 口径 B：阴影渐变终点（渐变在 4 逻辑px 内落到全透明，外推即"内容盒右边界"）
    width_b_px = shadow1 + 1 - geom.left

    logical_a = width_a_px / scale if scale else None
    logical_b = width_b_px / scale if scale else None
    measured = logical_a  # 判定用保守口径（阴影起点），另见 detail
    detail += (
        f" 高计数列 = x {shadow0}..{shadow1}（左栏最右侧的 4 逻辑px 阴影渐变）。"
        f" 口径 A（阴影渐变起点，**判定值**）：{width_a_px} 物理px ÷ {scale:g} = "
        f"{logical_a:.1f} 逻辑px；"
        f" 口径 B（渐变终点/外推的内容盒右边界）：{width_b_px} 物理px ÷ {scale:g} = "
        f"{logical_b:.1f} 逻辑px。参照 168 本就是这个内容盒宽度，"
        f"两个口径一个「少算半段渐变」一个「多算半段」，因此把区间 "
        f"{min(logical_a, logical_b):.1f}~{max(logical_a, logical_b):.1f} 逻辑px 一并列出，"
        f"判定取更保守的口径 A。"
    )
    return Measurement(
        "left_rail_width", "左侧导航宽度",
        measured_logical=measured,
        measured_text=(f"口径A {logical_a:.1f} 逻辑px（{width_a_px} 物理px ÷ {scale:g}）；"
                       f"口径B {logical_b:.1f} 逻辑px；"
                       f"两口径区间 {min(logical_a, logical_b):.1f}~{max(logical_a, logical_b):.1f}"
                       if logical_a is not None and logical_b is not None else ""),
        reference_text=ref,
        lo=TOL_LEFT_RAIL_WIDTH[0], hi=TOL_LEFT_RAIL_WIDTH[1],
        detail=detail,
    ), {
        "shadow_cols": [shadow0, shadow1],
        "width_a_px": width_a_px, "width_b_px": width_b_px,
        "logical_a": logical_a, "logical_b": logical_b,
        "high_threshold": high, "total_sample_rows": total_rows,
    }


def measure_row_height(
    img: RgbImage, geom: WindowGeometry, scale: float, applicable: bool
) -> tuple[Measurement, dict]:
    """列表行高：在内容区纵向扫描，寻找重复出现的行距。"""
    ref = (f"逻辑 {REF_ROW_HEIGHT:g}px（{format_range(*TOL_ROW_HEIGHT)}）；"
           "来源：录屏 84 视频px ÷ 1.3125 = 64.0，与 XAML `MyCompItem Height=64` 交叉验证一致")
    if not applicable:
        return Measurement(
            "row_height", "列表行高", reference_text=ref, applicable=False,
            detail="只有下载页有包卡片列表（PCL-CE 的 MyCompItem 行高适用于该列表）；本页不适用",
        ), {}

    content_top = int(round(geom.top + 48 * scale))
    y0 = content_top + int(round(60 * scale))
    y1 = geom.bottom - int(round(6 * scale))
    info: dict = {"scan_y": (y0, y1), "columns": {}}
    if y1 - y0 < int(round(40 * scale)):
        return Measurement(
            "row_height", "列表行高", reference_text=ref,
            detail=f"扫描区间太短（y={y0}..{y1}），无法测量",
        ), info

    content_left = int(round(geom.left + 10 * scale))
    content_right = int(round(geom.right - 20 * scale))
    if content_right - content_left < int(round(80 * scale)):
        return Measurement(
            "row_height", "列表行高", reference_text=ref,
            detail="内容区宽度不足，无法测量",
        ), info

    ratios = [0.35, 0.5, 0.65, 0.8]
    best: tuple[float, int, int] | None = None  # (间距逻辑px, 出现次数, 命中列)
    any_edges = 0
    for ratio in ratios:
        x = int(round(content_left + (content_right - content_left) * ratio))
        column = [img.px(x, y) for y in range(y0, y1 + 1)]
        edges = edge_positions(column)
        any_edges += len(edges)
        info["columns"][str(x)] = {"edge_count": len(edges), "edges": [e + y0 for e in edges][:40]}
        if len(edges) < 3:
            continue
        gaps = [edges[i + 1] - edges[i] for i in range(len(edges) - 1)]
        logical_gaps = [g / scale for g in gaps]
        info["columns"][str(x)]["gaps_logical"] = [round(g, 1) for g in logical_gaps]
        # 只统计"像行距"的量级（40~140 逻辑px），并把 ±2 逻辑px 内的算作重复
        for value in logical_gaps:
            if not (40.0 <= value <= 140.0):
                continue
            cnt = sum(1 for g in logical_gaps if abs(g - value) <= 2.0)
            if best is None or cnt > best[1] or (cnt == best[1] and value > best[0]):
                best = (value, cnt, x)

    if best is None or best[1] < 2:
        return Measurement(
            "row_height", "列表行高", reference_text=ref,
            detail=(f"在内容区 x 取 {len(ratios)} 列、y={y0}..{y1} 纵向扫描，"
                    f"共检出 {any_edges} 个色变，但其中**没有重复出现的行距量级**"
                    f"（40~140 逻辑px 内无 ≥2 次重复）⇒ 如实报告「无法测量」。"
                    f"最常见原因：该页当前是空态（没有搜索结果/没有卡片），"
                    f"因此没有可测的行。"),
        ), info

    value, count, x = best
    return Measurement(
        "row_height", "列表行高",
        measured_logical=value,
        measured_text=f"最常重复的间距 = {value:.1f} 逻辑px（在 x={x} 这一列出现 {count} 次）",
        reference_text=ref,
        lo=TOL_ROW_HEIGHT[0], hi=TOL_ROW_HEIGHT[1],
        detail=f"扫描列 x={x}，y={y0}..{y1}；逐列原始间距见 info.columns",
    ), info


def measure_background(
    img: RgbImage, geom: WindowGeometry, scale: float
) -> tuple[Measurement, Measurement, dict]:
    """主背景色。

    只有"窗口外的留白带"能看到 ``PclColors.background``（窗口内是白面板/页面底色）。
    留白带内侧被窗口自己的 BoxShadow（blur 12 / offset(0,2)）压暗，
    所以取"阴影够不到的外侧一圈"作样本。
    """
    w, h = img.width, img.height
    ml, mt, mr, mb = geom.margin
    band = max(2, int(round(5 * scale)))
    samples: list[tuple[int, int, int]] = []
    # 上边留白的最外几行（阴影向上最多扩散 blur-offset=10 逻辑px）
    for y in range(0, min(band, max(1, mt))):
        for x in range(0, w, max(1, w // 64)):
            samples.append(img.px(x, y))
    # 下边留白的最外几行
    for y in range(max(0, h - band), h):
        for x in range(0, w, max(1, w // 64)):
            samples.append(img.px(x, y))

    # 内圈（被阴影压暗的部分）也记下来，用于解释
    inner: list[tuple[int, int, int]] = []
    for y in range(max(0, mt - max(1, int(round(2 * scale)))), max(1, mt)):
        for x in range(w // 2 - 20, w // 2 + 20):
            inner.append(img.px(x, y))

    if not samples:
        return Measurement(
            "background", "主背景色",
            reference_text=f"{color_hex(REF_BACKGROUND)}（PclColors.background，容差 ≤{TOL_BACKGROUND}/通道）",
            detail="窗口外没有可用的留白像素（窗口铺满了整张截图）",
        ), Measurement(
            "background_shadowed", "留白内侧（含窗口阴影）",
            reference_text="仅记录，无参照判据", measured_text="无样本",
        ), {}

    color = median_color(samples)
    d = dist(color, REF_BACKGROUND)
    inner_color = median_color(inner) if inner else None
    inner_dev = dist(inner_color, REF_BACKGROUND) if inner_color else None

    m = Measurement(
        "background", "主背景色",
        measured_logical=float(d),
        measured_text=f"{color_hex(color)}（{len(samples)} 点中位数；取窗口外最外 {band} 物理px 的留白带，"
                      f"上下各一圈）",
        reference_text=f"{color_hex(REF_BACKGROUND)}（PclColors.background，容差 ≤{TOL_BACKGROUND}/通道）",
        lo=0.0, hi=float(TOL_BACKGROUND),
        detail=(f"实测 {color_hex(color)} vs 参照 {color_hex(REF_BACKGROUND)}，"
                f"最大通道差 = {d}。采样位置刻意贴在图片边缘：留白带内侧被窗口自身的"
                f"BoxShadow（blurRadius 12 / offset (0,2)）压暗，越靠近窗口越暗"
                + (f"（实测内侧中位色 {color_hex(inner_color)}，差 {inner_dev}）" if inner_color else "")
                + "，因此只有外侧一圈能代表背景本色。"),
    )
    shadow_m = Measurement(
        "background_shadowed", "留白内侧（含窗口阴影）",
        measured_logical=float(inner_dev) if inner_dev is not None else None,
        measured_text=(f"{color_hex(inner_color)}（与参照差 {inner_dev}）" if inner_color else "无样本"),
        reference_text="仅记录 —— 这一圈被窗口阴影污染，不作为 PASS/FAIL 依据",
        detail="把「靠近窗口的留白」单独列出来，是为了让读者看清阴影对背景色的影响量级；"
               "它本身不是背景色。",
    )
    return m, shadow_m, {"outer_samples": len(samples), "inner_color": color_hex(inner_color) if inner_color else None}


def analyse_page(name: str, img: RgbImage) -> dict:
    """分析单页截图：几何 → 缩放比 → 各项测量。"""
    # 先把水平范围定下来（与标题栏无关），再据此找标题栏色带，
    # 最后用色带的反向扫描定竖直范围。步骤之间有依赖，顺序不能换。
    hspan = _horizontal_window_span(img)
    band = find_title_bar_band(img, hspan[0], hspan[1])
    geom = detect_window(img, band)
    scale_info = detect_scale(img, geom)
    scale = scale_info["chosen"]

    height_m, tb_info = measure_title_bar_height(img, geom, band)
    # 物理 → 逻辑（缩放比只在最后一步参与，便于在报告里把两者都列出来）
    if height_m.measured_logical is not None and scale:
        span_px = tb_info.get("band_span_px", 0)
        height_m.measured_logical = span_px / scale
        height_m.measured_text = (f"{span_px} 物理px ÷ 缩放比 {scale:g} = "
                                  f"{height_m.measured_logical:.1f} 逻辑px")
        height_m.lo, height_m.hi = TOL_TITLE_BAR_HEIGHT

    band_y = (band["y0"] + band["y1"]) // 2 if band is not None else None
    y6_m, band_m, uni_info = measure_title_bar_uniform(img, band, band_y)
    rail_m, rail_info = measure_left_rail(img, geom, scale, applicable=(name == "umango_download"))
    row_m, row_info = measure_row_height(img, geom, scale, applicable=(name == "umango_download"))
    bg_m, bg_shadow_m, bg_info = measure_background(img, geom, scale)

    return {
        "name": name,
        "label": PAGE_LABEL.get(name, name),
        "image_size": [img.width, img.height],
        "scale": scale_info,
        "window": geom.as_dict(),
        "measurements": [height_m, y6_m, band_m, rail_m, row_m, bg_m, bg_shadow_m],
        "info": {
            "title_bar": tb_info,
            "title_bar_uniform": uni_info,
            "left_rail": rail_info,
            "row_height": row_info,
            "background": bg_info,
        },
    }


# --------------------------------------------------------------------------
# 报告
# ---------------------------------------------------------------------------


def find_screenshots(directory: Path) -> list[Path]:
    found: list[Path] = []
    for name in PAGE_ORDER:
        candidate = directory / f"{name}.png"
        if candidate.is_file():
            found.append(candidate)
    if not found:  # 兜底：任何 png 都收，便于 compare 他处产出的截图
        found = sorted(directory.glob("*.png"))
    return found


BADGE = {
    "PASS": "✅ PASS",
    "FAIL": "❌ FAIL",
    "N/A": "➖ 不适用",
    "无法测量": "⚠️ 无法测量",
    "仅记录": "ℹ️ 仅记录（无判据）",
}


def _cell(text: str) -> str:
    return (text or "—").replace("|", "\\|").replace("\n", " ").strip()


def render_report(results: list[dict], screenshots_dir: Path, errors: list[str]) -> str:
    lines: list[str] = []
    add = lines.append

    add("# UMango 截图 ↔ PCL-CE 参照数值：程序化比对报告")
    add("")
    add("> 第 0~4 节由 `scripts/compare_screenshots.py` 依据 `ui/build/screenshots/*.png` 生成，"
        "**每次运行都会重写**；最后一节「人工分析」写死在脚本里（`MANUAL_SECTION` 常量），"
        "负责区分「实测 / 推断 / 做不到」。")
    add("> 所有结论来自**逐像素扫描 + ffmpeg 解码**，不含任何视觉大模型的判断 ——")
    add("> 本机视觉大模型被限流不可用、且没有 Chrome，这条约束贯穿整个流程。")
    add("")

    # ------------------------------------------------------------------ 0
    add("## 0. 本次运行")
    add("")
    add(f"- 截图目录：`{screenshots_dir}`")
    add(f"- 参与比对的截图：{len(results)} 张"
        + (f"（{', '.join(r['name'] for r in results)}）" if results else "（无）"))
    add(f"- Python {platform.python_version()}；解码器 ffmpeg（`{shutil.which('ffmpeg') or '未找到'}`）")
    add(f"- 逻辑视口假设：{INTEGRATION_TEST_VIEWPORT_WIDTH:g}×{INTEGRATION_TEST_VIEWPORT_HEIGHT:g} "
        f"（由 `integration_test/screenshot_test.dart` 钉死，可用 `--logical-width/--logical-height` 覆盖）")
    if errors:
        add("- ⚠️ 解析期间的错误：")
        for e in errors:
            add(f"  - {e}")
    add("")

    # ------------------------------------------------------------------ 1
    add("## 1. 参照数值的来源")
    add("")
    add("参照值有两个**互相独立、且交叉验证一致**的来源：")
    add("")
    add("**(1) PCL-CE 真实运行录屏的逐像素测量**")
    add("")
    add("- 素材：PCL-CE 在 Mac 上（Wine/CrossOver）运行的录屏，1106×664、55.9 秒、1612 帧")
    add("  （原始记录见 `docs/DESIGN_PCL_CE.md` 第 9 节「实测校验」）。")
    add(f"- 缩放比：窗口逻辑宽 {REFERENCE_WINDOW_LOGICAL_WIDTH:g}px × 1.3125 = 1115 ≈ 1106 视频px，")
    add("  即 **视频px ÷ 1.3125 = 逻辑px**；该比值用卡片行高交叉验证过")
    add("  （63 逻辑px × 1.3125 ≈ 82.7，与录屏实测 84 吻合）。")
    add("")
    add("**(2) PCL-CE 的 XAML 资源定义**")
    add("")
    add("| 项 | XAML / token | 录屏实测 | 本脚本采用的参照 |")
    add("|----|--------------|----------|------------------|")
    add("| 标题栏高度 | `PanTitle Height=48` | 59 视频px → 45.0 逻辑px | **48**（XAML 为准） |")
    add("| 列表行高 | `MyCompItem Height=64` | 84 视频px → 64.0 逻辑px | **64**（两者一致） |")
    add("| 左栏宽度 | 168 | 218 视频px → 167.7 逻辑px | **168**（两者一致） |")
    add("| 主背景色 | `#fbfbfb` | 录屏采到 `#f0f6f9` | **#fbfbfb**（XAML 为准） |")
    add("")
    add("⚠️ **颜色的系统性偏移**：录屏采样蓝色 `#1b6bd3`，而 XAML `c3` = `#1370f3`；")
    add("白色采样 `#f0f6f9`，XAML 背景 `#fbfbfb`。全部颜色同方向偏移（R+8 / G−5 / B−32 量级），")
    add("这是 Wine 渲染 + 录屏色彩管理造成的，**不是应用配色**。")
    add("因此本脚本：**颜色参照一律以 XAML token 为准，几何与结构量以录屏实测为准**")
    add("（几何不受色彩偏移影响）。")
    add("")

    # ------------------------------------------------------------------ 2
    add("## 2. 缩放比推断（后面的所有数字都依赖它）")
    add("")
    add("截图是**物理像素**。本脚本用三条独立线索推缩放比，并列出全部候选：")
    add("")
    for r in results:
        sc = r["scale"]
        win = r["window"]
        add(f"**`{r['name']}.png`**（{r['image_size'][0]}×{r['image_size'][1]} 物理px）")
        add("")
        add(f"- 量到的窗口容器：x={win['left']}..{win['right']}，y={win['top']}..{win['bottom']}"
            f"（{win['width']}×{win['height']} 物理px）；"
            f"四周留白（左,上,右,下）= {tuple(win['margin_px'])} 物理px")
        for cand in sc["candidates"]:
            add(f"- {cand['text']}")
        add(f"- ⇒ **采用 {sc['chosen']:g}×**；三条线索的极差 {sc['agreement']:.4f}"
            + ("（一致，可信）" if sc["agreement"] <= 0.02 else "（**不一致，需谨慎看待后续逻辑px**）"))
        add("")
    if not results:
        add("（没有截图，无法推断）")
        add("")

    # ------------------------------------------------------------------ 3
    add("## 3. 逐图逐项结果")
    add("")
    for r in results:
        add(f"### {r['name']}（{PAGE_LABEL.get(r['name'], r['name'])} 页）")
        add("")
        win = r["window"]
        add(f"图片 {r['image_size'][0]}×{r['image_size'][1]} 物理px；"
            f"窗口容器 {win['left']},{win['top']} → {win['right']},{win['bottom']}；"
            f"缩放比 {r['scale']['chosen']:g}×")
        add("")
        add("| 指标 | 状态 | 实测 | 参照 | 说明 |")
        add("|------|------|------|------|------|")
        for m in r["measurements"]:
            add(f"| {m.title} | {BADGE.get(m.status, m.status)} | {_cell(m.measured_text)} "
                f"| {_cell(m.reference_text)} | {_cell(m.detail)} |")
        add("")

        tb = r["info"].get("title_bar", {})
        if tb:
            add(f"- **标题栏色带**：色 {tb.get('band_color')}，位于 y={tb.get('band_y')}，"
                f"上下跨度 {tb.get('band_span_px')} 物理px"
                f"（在 x={list(tb.get('band_span_px_at_x', {}).keys())} 两处分别为 "
                f"{list(tb.get('band_span_px_at_x', {}).values())}）")
        lr = r["info"].get("left_rail", {})
        if lr:
            add(f"- **左栏扫描**：y={lr.get('scan_y')}，窗口内容左内边界 x={lr.get('box_left')}，"
                f"判定边界 x={lr.get('boundary_x')}，跨度 {lr.get('width_px')} 物理px")
        rh = r["info"].get("row_height", {})
        if rh:
            cols = rh.get("columns", {})
            add(f"- **行高扫描**：y={rh.get('scan_y')}，逐列色变数 "
                + ("、".join(f"x={x}:{v.get('edge_count')}" for x, v in cols.items()) or "无")
                + "（详细间距见 JSON 输出的 `info.row_height`）")
        bg = r["info"].get("background", {})
        if bg:
            add(f"- **背景采样**：外侧 {bg.get('outer_samples')} 点；"
                f"内侧（被窗口阴影压暗）中位色 {bg.get('inner_color')}")
        add("")

    # ------------------------------------------------------------------ 4
    add("## 4. 汇总表")
    add("")
    add("| 图 | 指标 | 实测（逻辑px / 颜色 / 差值） | 参照 | 结论 |")
    add("|----|------|------------------------------|------|------|")
    counts = {"PASS": 0, "FAIL": 0, "无法测量": 0, "N/A": 0, "仅记录": 0}
    for r in results:
        for m in r["measurements"]:
            counts[m.status] = counts.get(m.status, 0) + 1
            measured = m.measured_text or "—"
            if m.measured_logical is not None and m.lo is not None:
                if m.key in ("title_bar_height", "left_rail_width", "row_height"):
                    measured = f"{m.measured_logical:.1f} 逻辑px"
                elif m.key == "background":
                    measured = f"最大通道差 {m.measured_logical:g}"
            add(f"| `{r['name']}` | {m.title} | {_cell(measured)} "
                f"| {_cell(m.reference_text)} | {BADGE.get(m.status, m.status)} |")
    add("")
    add(f"**PASS {counts['PASS']} · FAIL {counts['FAIL']} · "
        f"无法测量 {counts['无法测量']} · 不适用 {counts['N/A']} · 仅记录 {counts['仅记录']}**")
    add("")
    add("**判定规则**：任何一项**可测量**指标 FAIL，整体即不通过（脚本退出码 1）。")
    add("「无法测量」既不算通过也不算失败 —— 它表示确实没测到，"
        "原因必须写在第 3 节的说明里，不许拿推测顶替。")
    add("")

    # 人工分析段：写死在本脚本里（这样报告只有一个来源文件，不会出现
    # "报告比脚本新/旧"的对不上号问题）。
    add("---")
    add("")
    for line in MANUAL_SECTION.strip().splitlines():
        add(line)
        if line.startswith(">") and "三类内容绝不混写" in line:
            add("")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------


def main(argv: Sequence[str] | None = None) -> int:
    repo_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(
        description="UMango 截图 ↔ PCL-CE 参照数值的程序化比对（只用标准库 + ffmpeg）",
    )
    parser.add_argument("--dir", default=str(DEFAULT_SCREENSHOT_DIR),
                        help="截图目录（默认 ui/build/screenshots）")
    parser.add_argument("--report", default=str(DEFAULT_REPORT_PATH),
                        help="markdown 报告输出路径（默认 docs/SCREENSHOT_CHECK.md）")
    parser.add_argument("--no-report", action="store_true", help="只打印，不写报告")
    parser.add_argument("--json", default=None, help="额外把结构化结果写成 JSON")
    parser.add_argument("--logical-width", type=float,
                        default=INTEGRATION_TEST_VIEWPORT_WIDTH,
                        help="集成测试钉死的逻辑视口宽（默认 1280）")
    parser.add_argument("--logical-height", type=float,
                        default=INTEGRATION_TEST_VIEWPORT_HEIGHT,
                        help="集成测试钉死的逻辑视口高（默认 800）")
    args = parser.parse_args(argv)

    global EXPECTED_LOGICAL_WIDTH, EXPECTED_LOGICAL_HEIGHT
    EXPECTED_LOGICAL_WIDTH = args.logical_width
    EXPECTED_LOGICAL_HEIGHT = args.logical_height

    shots_dir = Path(args.dir).expanduser().resolve()
    print(f"[compare] 截图目录：{shots_dir}")
    if not shots_dir.is_dir():
        print(f"[compare] ✗ 目录不存在：{shots_dir}")
        print("[compare]   请先跑：flutter drive "
              "--driver=test_driver/integration_test.dart "
              "--target=integration_test/screenshot_test.dart -d macos")
        return 2

    shots = find_screenshots(shots_dir)
    if not shots:
        print(f"[compare] ✗ {shots_dir} 里没有任何 PNG")
        return 2
    print(f"[compare] 找到 {len(shots)} 张：{[p.name for p in shots]}")

    results: list[dict] = []
    errors: list[str] = []
    for path in shots:
        try:
            img = decode_png_via_ffmpeg(path)
        except Exception as exc:  # noqa: BLE001 - 报告里要如实写清楚
            errors.append(f"{path.name}: {exc}")
            print(f"[compare] ✗ 解码失败 {path.name}: {exc}")
            continue
        res = analyse_page(path.stem, img)
        results.append(res)

        win = res["window"]
        print("")
        print(f"=== {path.name}（{img.width}×{img.height} 物理px，"
              f"缩放比 {res['scale']['chosen']:g}×，"
              f"窗口容器 {win['width']}×{win['height']} @ {win['left']},{win['top']}） ===")
        for m in res["measurements"]:
            mark = {"PASS": "PASS", "FAIL": "FAIL", "N/A": "N/A",
                    "无法测量": "N/A(无法测量)", "仅记录": "INFO"}.get(m.status, m.status)
            print(f"  [{mark:>14}] {m.title}: {m.measured_text or '—'}")
            if m.detail:
                print(f"                   ↳ {m.detail}")

    total = {"PASS": 0, "FAIL": 0, "无法测量": 0, "N/A": 0, "仅记录": 0}
    for r in results:
        for m in r["measurements"]:
            total[m.status] = total.get(m.status, 0) + 1
    print("")
    print(f"[compare] 汇总：PASS {total['PASS']} · FAIL {total['FAIL']} · "
          f"无法测量 {total['无法测量']} · 不适用 {total['N/A']}")

    if args.json:
        # Measurement 是自定义类，落 JSON 前统一转 dict。
        serializable = {
            "results": [
                {**r, "measurements": [m.as_dict() for m in r["measurements"]]}
                for r in results
            ],
            "errors": errors,
            "totals": total,
        }
        Path(args.json).write_text(
            json.dumps(serializable, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        print(f"[compare] JSON 已写出：{args.json}")

    if not args.no_report:
        report = render_report(results, shots_dir, errors)
        out = Path(args.report)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(report, encoding="utf-8")
        print(f"[compare] 报告已写出：{out}")

    if not results:
        return 2
    return 1 if total["FAIL"] else 0


if __name__ == "__main__":
    sys.exit(main())
