#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
语言文件的**唯一真相源同步脚本**。

方向：**Shell 工程的 .txt（真相源）→ App 工程的 .xaml（派生产物）**。

为什么这样分：
  · 文案是界面内容，与 UI 框架无关，但 .xaml 资源字典只有 WPF 读得懂
    （`sys:String` / `mscorlib` 命名空间在 Avalonia 里不存在）。
  · 所以真相源放在跨框架可读的纯文本 `src/UMango.Shell/Localization/Languages/*.txt`，
    WPF 侧的 .xaml 由本脚本生成。两个壳读的是同一份内嵌 .txt，不可能一边改了另一边没改。

用法：
    python3 scripts/sync-language-xaml.py            # 生成 .xaml
    python3 scripts/sync-language-xaml.py --check     # 只校验 .xaml 与 .txt 是否一致（CI 用）

纯文本格式（刻意极简，任何语言都能手写）：
    # 普通注释（生成 .xaml 时丢弃）
    #! 原样保留的注释（生成 .xaml 时还原成 XML 注释）
    # ==== 分组标题 ====（生成 .xaml 时还原成 <!-- ==== 分组标题 ==== -->）
    键=值
    值里的换行写成 \\n，制表符写成 \\t，反斜杠写成 \\\\
"""

from __future__ import annotations

import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SRC_DIR = REPO / "src" / "UMango.Shell" / "Localization" / "Languages"
DST_DIR = REPO / "src" / "UMango.App" / "Localization" / "Languages"

XML_ESCAPES = {"&": "&amp;", "<": "&lt;", ">": "&gt;"}

# 分组标题：# ==== 标题 ====  （允许 - ─ — 等各种横线）
SECTION = re.compile(r'^#\s*(?P<marks>[=\-─—]{2,})\s*(?P<label>.*?)\s*[=\-─—]{2,}\s*$')


def xml_escape(value: str) -> str:
    # 换行必须写成字符引用 &#x0a;：XML 里元素内的裸换行会被 XAML 解析器
    # 规范化成空格（xml:space="preserve" 之外的默认行为），文案就断了。
    escaped = "".join(XML_ESCAPES.get(ch, ch) for ch in value)
    return escaped.replace("\n", "&#x0a;").replace("\r", "&#x0d;")


# XML 实体：5 个具名实体 + 数字字符引用（&#x0a; / &#10;）。
# 必须**一次扫描**替换，不能逐个 replace —— 否则 "&amp;lt;" 会被解两次变成 "<"，
# 而 "&amp;#x0a;" 会被解成字面的 "&#x0a;"（本该是换行）。
XML_ENTITY_RE = re.compile(r"&(?:#x(?P<hex>[0-9a-fA-F]+)|#(?P<dec>[0-9]+)|(?P<named>lt|gt|quot|apos|amp));")

XML_NAMED_ENTITIES = {"lt": "<", "gt": ">", "quot": '"', "apos": "'", "amp": "&"}


def xml_unescape(value: str) -> str:
    """把 XML 实体还原成原字符。

    换行在 .xaml 里写成数字字符引用 ``&#x0a;``（裸换行会被 XAML 解析器压成空格），
    所以**必须**能把它解回真正的换行，否则 --check 的诊断会把
    含换行的键（如 Tools.ProjectIntro）误报成"值不同"。
    """
    def replace(match: re.Match[str]) -> str:
        if match.group("named"):
            return XML_NAMED_ENTITIES[match.group("named")]
        code = int(match.group("hex"), 16) if match.group("hex") else int(match.group("dec"))
        return chr(code)

    return XML_ENTITY_RE.sub(replace, value)


def unescape(value: str) -> str:
    out: list[str] = []
    i = 0
    mapping = {"n": "\n", "r": "\r", "t": "\t", "\\": "\\"}
    while i < len(value):
        c = value[i]
        if c == "\\" and i + 1 < len(value) and value[i + 1] in mapping:
            out.append(mapping[value[i + 1]])
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def escape(value: str) -> str:
    return (value.replace("\\", "\\\\")
                 .replace("\n", "\\n")
                 .replace("\r", "\\r")
                 .replace("\t", "\\t"))


def parse_txt_ordered(path: pathlib.Path) -> list[tuple[str, str]]:
    """解析纯文本 → 有序的 [("e"|"c"|"s", 载荷)] 列表。

    · "e" 条目：载荷是 `键\\t值`
    · "c" 注释：载荷是注释正文（来自 `#!` 行）
    · "s" 分组：载荷是分组标题（来自 `# ==== 标题 ====` 行）
    """
    out: list[tuple[str, str]] = []
    seen: set[str] = set()
    for raw in path.read_text(encoding="utf-8").splitlines():
        # 只去行尾换行，**不能 strip 整行** —— 有些文案末尾空格有意义
        line = raw.rstrip("\r")
        if not line.strip():
            continue
        if line.lstrip().startswith("#!"):
            out.append(("c", line.lstrip()[2:].strip()))
            continue
        m = SECTION.match(line.strip())
        if m:
            out.append(("s", m.group("label").strip()))
            continue
        if line.lstrip().startswith("#"):
            continue

        separator = line.find("=")
        if separator < 0:
            raise SystemExit(f"{path.name}: 格式不对的行（缺 '='）：{raw!r}")
        key = line[:separator].strip()
        if not key:
            raise SystemExit(f"{path.name}: 空键：{raw!r}")
        if key in seen:
            raise SystemExit(f"{path.name}: 重复键 {key}")
        seen.add(key)
        out.append(("e", f"{key}\t{unescape(line[separator + 1:])}"))
    return out


def parse_xaml(path: pathlib.Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    entry = re.compile(r'<sys:String\s+x:Key="(?P<key>[^"]+)"\s*>(?P<value>.*?)</sys:String>',
                       re.DOTALL)
    out: dict[str, str] = {}
    for m in entry.finditer(text):
        key = m.group("key")
        if key in out:
            raise SystemExit(f"{path.name}: 重复键 {key}")
        out[key] = xml_unescape(m.group("value"))
    if not out:
        raise SystemExit(f"{path.name}: 没解析出任何条目（格式变了？）")
    return out


HEADER = """  UMango 界面文本（简体中文）—— 兜底层，始终加载。

  分层模型（沿用 PCL-CE LocalizationService 的设计）：
    ① 本文件 = 兜底层，键最全，**始终**加载；
    ② 其它语言文件（如 en-US.xaml）= 叠加层，只写"变了的键"，重复键覆盖兜底层。
  好处：新增语言不必整包复制整份文本，只写差异。

  键名规范：Page/区域.元素.语义，例如 Home.Title = 主页大标题。
  XAML 侧用 {DynamicResource 键名} 引用（必须是 DynamicResource 才能在
  运行时切换语言后自动刷新；StaticResource 会在加载时定死）。
  代码侧经 PCL.Lang 桥（UMango.Core/Pcl/App/Localization/Lang.cs）读取。

  ⚠ **本文件是自动生成的，不要手改**。
  真相源是 src/UMango.Shell/Localization/Languages/zh-CN.txt（跨框架纯文本），
  两个壳（WPF / Avalonia）读的是同一份内嵌 .txt。
  改文案请改 .txt，然后跑：python3 scripts/sync-language-xaml.py
"""

HEADER_EN = """  UMango UI text (English, US) — overlay layer.

  Only keys that DIFFER from the zh-CN fallback are listed here; the rest are
  inherited. See zh-CN.xaml header for the layering model (from PCL-CE's
  LocalizationService, Apache-2.0).

  ⚠ **AUTO-GENERATED — do not edit by hand.**
  Source of truth: src/UMango.Shell/Localization/Languages/en-US.txt
  (cross-framework plain text read by both shells).
  Edit the .txt, then run: python3 scripts/sync-language-xaml.py
"""


def render_xaml(ordered: list[tuple[str, str]], header: str) -> str:
    lines = [
        "<!--",
        header.rstrip("\n"),
        "-->",
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
        '                    xmlns:sys="clr-namespace:System;assembly=mscorlib">',
        "",
    ]
    for kind, payload in ordered:
        if kind == "s":
            if lines and lines[-1] != "":
                lines.append("")
            lines.append(f"    <!-- ============ {payload} ============ -->")
        elif kind == "c":
            lines.append(f"    <!-- {payload} -->")
        else:
            key, value = payload.split("\t", 1)
            lines.append(f'    <sys:String x:Key="{key}">{xml_escape(value)}</sys:String>')
    lines.append("</ResourceDictionary>")
    return "\n".join(lines) + "\n"


def main() -> int:
    check = "--check" in sys.argv
    names = ["zh-CN", "en-US"]
    problems: list[str] = []

    for name in names:
        txt = SRC_DIR / f"{name}.txt"
        xaml = DST_DIR / f"{name}.xaml"
        if not txt.exists():
            problems.append(f"{txt.relative_to(REPO)} 不存在")
            continue

        ordered = parse_txt_ordered(txt)
        header = HEADER if name == "zh-CN" else HEADER_EN
        expected = render_xaml(ordered, header)

        if check:
            if not xaml.exists():
                problems.append(f"{xaml.relative_to(REPO)} 不存在")
                continue
            actual = xaml.read_text(encoding="utf-8")
            if actual != expected:
                from_txt = {p.split("\t", 1)[0]: p.split("\t", 1)[1]
                            for k, p in ordered if k == "e"}
                try:
                    from_xaml = parse_xaml(xaml)
                except SystemExit as ex:
                    problems.append(str(ex))
                    continue
                only_x = sorted(set(from_xaml) - set(from_txt))
                only_t = sorted(set(from_txt) - set(from_xaml))
                diff = sorted(k for k in set(from_xaml) & set(from_txt)
                              if from_xaml[k] != from_txt[k])
                problems.append(
                    f"{name}: 只在 xaml 的键={only_x} 只在 txt 的键={only_t} 值不同={diff}"
                )
        else:
            xaml.write_text(expected, encoding="utf-8")
            count = sum(1 for k, _ in ordered if k == "e")
            print(f"✓ {txt.name} → {xaml.relative_to(REPO)}（{count} 键）")

    if problems:
        for p in problems:
            print(f"✗ {p}", file=sys.stderr)
        return 1
    if check:
        print("✓ 语言文件一致（.txt ↔ .xaml）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
