#!/usr/bin/env python3
"""从 WPF 侧的主题 token 字典生成 Avalonia 侧的同名字典。

## 为什么以 WPF 侧为源头

WPF 的 `src/UMango.App/Themes/Tokens.*.xaml` 是**逐像素校验过的权威定义**
（数值取自 PCL-CE 的 Application.xaml，并用真实运行录屏逐像素比对过，
见 docs/DESIGN_PCL_CE.md）。那些文件里的注释还记录了"为什么是这个值"
（例如深色主题下 G3/G4 为了 WCAG AA 做过对比度提升）——
这些是**不能丢的信息**。

所以这里不去重建真相源，而是**单向生成**：WPF 字典 → Avalonia 字典。
Avalonia 侧是派生产物，改主题色只改 WPF 侧那份，两个壳必然一致。

## 为什么需要生成

两个 UI 框架的资源语法不同：
* WPF：`<SolidColorBrush x:Key="BrC1" Color="{DynamicResource C1}"/>` + `<sys:Double>` + `<FontFamily>`
* Avalonia：`<SolidColorBrush x:Key="BrC1" Color="#343D4A" />`，尺寸用 `<x:Double>` 之类的写法

而且 Avalonia **没有** `<sys:Double>` 这种资源类型，尺寸 token 必须换个写法
（`x:Double` 走的是 XAML 里的 `double` 转换，Avalonia 11 支持把 double 作为资源）。

本脚本把 WPF 的「画笔引用颜色 token」这一层间接**展开成字面值**：
Avalonia 侧少一层间接 = 少一个可能分叉的地方。

## 用法

```bash
python3 scripts/sync-theme-tokens.py           # 生成
python3 scripts/sync-theme-tokens.py --check   # 只校验（CI 用）
```
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent

WPF_THEMES = REPO / "src" / "UMango.App" / "Themes"
AVALONIA_THEMES = REPO / "src" / "UMango.Avalonia" / "Themes"

FAMILIES = ("Pcl", "Fluent10")
SHADES = ("Light", "Dark")

COLOR_RE = re.compile(r"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")
RESOURCE_REF_RE = re.compile(r"^\{(?:Dynamic|Static)Resource\s+([^}]+)\}$")


class TokenError(Exception):
    """源文件格式错误。"""


def parse_wpf(path: pathlib.Path) -> tuple[dict[str, str], dict[str, str], dict[str, str]]:
    """解析 WPF token 字典。

    返回 (颜色字面值, 画笔 → 颜色字面值, 尺寸/字体字面值)。

    画笔的 Color 属性可能是：
      * 字面颜色（``#343D4A``）
      * 资源引用（``{DynamicResource C1}`` / ``{StaticResource Bg}``）
    后者会解析成对应颜色的字面值。
    """
    text = path.read_text(encoding="utf-8")

    colors: dict[str, str] = {}
    for match in re.finditer(r'<Color x:Key="([^"]+)">([^<]+)</Color>', text):
        colors[match.group(1)] = match.group(2).strip()

    brushes: dict[str, str] = {}
    for match in re.finditer(r'<SolidColorBrush x:Key="([^"]+)"\s+Color="([^"]+)"\s*/>', text):
        key, value = match.group(1), match.group(2).strip()
        ref = RESOURCE_REF_RE.match(value)
        if ref:
            target = ref.group(1).strip()
            if target not in colors:
                raise TokenError(f"{path.name}：画笔 {key} 引用了不存在的颜色 token '{target}'")
            value = colors[target]
        if not COLOR_RE.match(value):
            raise TokenError(f"{path.name}：画笔 {key} 的颜色不是十六进制字面值：{value}")
        brushes[key] = value

    scalars: dict[str, str] = {}
    for match in re.finditer(r'<sys:Double x:Key="([^"]+)">([^<]+)</sys:Double>', text):
        scalars[match.group(1)] = match.group(2).strip()
    for match in re.finditer(r'<FontFamily x:Key="([^"]+)">([^<]+)</FontFamily>', text):
        scalars[match.group(1)] = match.group(2).strip()

    return colors, brushes, scalars


def render_avalonia(
    colors: dict[str, str],
    brushes: dict[str, str],
    scalars: dict[str, str],
    family: str,
    dark: bool,
) -> str:
    """生成 Avalonia 资源字典。

    颜色 token 也一并生成：WPF 侧是 `<Color x:Key="C1">`，Avalonia 侧同样是
    `<Color x:Key="C1">`（Avalonia.Media.Color 有 "#RRGGBB" 转换器）。
    两侧 key 集合因此完全一致 —— 少一个 key 就意味着「某个 {DynamicResource C1}
    在 Avalonia 侧取不到」，那是个静默的取不到（回退到 null → 不显示），
    所以键集合必须严格对齐。
    """
    lines: list[str] = []
    lines.append("<!--")
    lines.append(f"  主题 token：{family} · {'深色' if dark else '浅色'}（Avalonia）")
    lines.append("  ============================================================================")
    lines.append("  ⚠ 本文件是**生成产物**，请勿手工编辑。")
    lines.append(f"    源头：src/UMango.App/Themes/Tokens.{'Dark' if dark else 'Light'}.{family}.xaml")
    lines.append("    重新生成：python3 scripts/sync-theme-tokens.py")
    # ⚠ 注释里不能出现 "--"（XML 规范：注释内不得含 "--"），所以这里不写
    #   原样的双横线 check 参数，否则整个文件不是合法 XML，
    #   Avalonia 的 XAML 预处理器会直接报 AVLN1001。
    lines.append("    校验（CI）：python3 scripts/sync-theme-tokens.py 的 check 模式")
    lines.append("  ============================================================================")
    lines.append("-->")
    lines.append('<ResourceDictionary xmlns="https://github.com/avaloniaui"')
    lines.append('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
    lines.append('                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">')
    lines.append("")

    for key, value in colors.items():
        lines.append(f'  <Color x:Key="{key}">{value}</Color>')

    for key, value in brushes.items():
        lines.append(f'  <SolidColorBrush x:Key="{key}" Color="{value}" />')

    for key, value in scalars.items():
        try:
            float(value)
        except ValueError:
            # 字体链：Avalonia 用逗号分隔的字体族列表，WPF 的逗号形式语义相同
            lines.append(f'  <FontFamily x:Key="{key}">{value}</FontFamily>')
        else:
            # 尺寸：Avalonia 没有 <sys:Double> 资源类型，用 <x:Double>（系统类型别名）
            lines.append(f'  <x:Double x:Key="{key}">{value}</x:Double>')

    lines.append("</ResourceDictionary>")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="只校验，不写入")
    args = parser.parse_args()

    problems: list[str] = []
    written = 0

    for family in FAMILIES:
        for shade in SHADES:
            source = WPF_THEMES / f"Tokens.{shade}.{family}.xaml"
            if not source.is_file():
                problems.append(f"缺少源文件：{source}")
                continue

            try:
                colors, brushes, scalars = parse_wpf(source)
            except TokenError as ex:
                problems.append(str(ex))
                continue

            content = render_avalonia(colors, brushes, scalars, family, shade == "Dark")
            target = AVALONIA_THEMES / f"Tokens.{shade}.{family}.axaml"

            if args.check:
                current = target.read_text(encoding="utf-8") if target.is_file() else None
                if current != content:
                    problems.append(f"生成产物与源文件不一致：{target.relative_to(REPO)}")
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                if not target.is_file() or target.read_text(encoding="utf-8") != content:
                    target.write_text(content, encoding="utf-8")
                    written += 1

    if problems:
        for problem in problems:
            print(f"[不一致] {problem}", file=sys.stderr)
        return 1

    if args.check:
        print("主题 token（Avalonia 侧）：全部一致 ✓")
    else:
        print(f"主题 token（Avalonia 侧）：已生成（更新 {written} 个文件）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
