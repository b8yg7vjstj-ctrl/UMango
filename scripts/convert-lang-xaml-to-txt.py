#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 WPF 的 .xaml 语言资源字典转成跨框架可读的纯文本格式（Shell 工程的唯一真相源）。

为什么要有这个转换：
  · 语言文案是**界面内容**，不是 WPF 特有的东西 —— 但 .xaml 资源字典只有 WPF 读得懂
    （`sys:String` / `mscorlib` 命名空间在 Avalonia 里不存在）。
  · 于是把语言文件降级成 `键=值` 的纯文本，嵌进 UMango.Shell：
      - WPF 壳在启动时把它灌进 ResourceDictionary（DynamicResource 照常工作）；
      - Avalonia 壳把它灌进 Application.Resources；
    两边读**同一份文件**，不可能再出现"一边改了另一边没改"。

用法：
    python3 scripts/convert-lang-xaml-to-txt.py            # xaml -> txt（生成）
    python3 scripts/convert-lang-xaml-to-txt.py --check     # 只校验 txt 与 xaml 是否一致

格式约定（刻意极简，任何语言都能手写）：
    # 以 # 开头的行是注释，空行忽略
    # 分组注释（形如 `# ==== 通用 ====`）在生成 .xaml 时还原成 XML 注释
    键=值
    值里的换行写成 \\n（反斜杠+n），制表符写成 \\t
"""

from __future__ import annotations

import html
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
LANG_DIR = REPO / "src" / "UMango.App" / "Localization" / "Languages"
OUT_DIR = REPO / "src" / "UMango.Shell" / "Localization" / "Languages"

# <sys:String x:Key="K">V</sys:String>
ENTRY = re.compile(
    r'<sys:String\s+x:Key="(?P<key>[^"]+)"\s*>(?P<value>.*?)</sys:String>',
    re.DOTALL,
)
# <!-- 注释 -->（单行）
COMMENT = re.compile(r'<!--\s*(?P<body>.*?)\s*-->', re.DOTALL)

# 分组注释的形态：以 = 或 - 包围的短横线/等号串，用来在 .xaml 里还原成 XML 注释
SECTION = re.compile(r'^#\s*[=\-─—]{2,}\s*(?P<label>.*?)\s*[=\-─—]{2,}\s*$')


def parse_xaml_ordered(path: pathlib.Path) -> list[tuple[str, str]]:
    """解析 .xaml 语言字典 → 有序的 [("e"|"c", 键或注释)] 列表。"""
    text = path.read_text(encoding="utf-8")
    out: list[tuple[str, str]] = []
    seen: set[str] = set()

    # 先按位置把注释和条目都标出来，再按原顺序排序
    marks: list[tuple[int, str, str]] = []
    for m in COMMENT.finditer(text):
        body = m.group("body").strip()
        if not body:
            continue
        # 顶部那段大注释（文件头）是模板，不作为分组注释
        marks.append((m.start(), "c", body))
    for m in ENTRY.finditer(text):
        key = m.group("key")
        if key in seen:
            raise SystemExit(f"{path.name}: 重复键 {key}")
        seen.add(key)
        marks.append((m.start(), "e", f"{key}\t{html.unescape(m.group('value'))}"))

    marks.sort(key=lambda t: t[0])
    for _, kind, payload in marks:
        out.append((kind, payload))
    return out


def encode(value: str) -> str:
    return (value.replace("\\", "\\\\")
                 .replace("\n", "\\n")
                 .replace("\r", "\\r")
                 .replace("\t", "\\t"))


def decode(value: str) -> str:
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


HEADER = [
    "# UMango 界面文案 —— **唯一真相源**（跨 WPF / Avalonia 共用）。",
    "#",
    "# 格式：`键=值`；值里的换行写成 \\n，制表符写成 \\t，反斜杠写成 \\\\。",
    "# 空行与以 # 开头的行是注释；形如 `# ==== 标题 ====` 的分组注释在生成",
    "# WPF 的 .xaml 时会还原成 XML 注释（便于阅读）。",
    "#",
    "# 两个壳都从**内嵌的这份文本**读文案：",
    "#   · WPF 壳启动时把它灌进 Application.Resources（DynamicResource 照常工作）；",
    "#   · Avalonia 壳把它灌进 Application.Resources。",
    "# 因此改文案只改这里，不会出现\"改了 WPF 没改 Avalonia\"。",
    "#",
    "# 分层模型（沿用 PCL-CE 的 LocalizationService 设计）：",
    "#   ① zh-CN = 兜底层，**始终**加载，键最全；",
    "#   ② en-US = 叠加层，只写\"变了的键\"，重复键覆盖兜底层。",
]


def main() -> int:
    check = "--check" in sys.argv
    names = ["zh-CN", "en-US"]
    problems: list[str] = []

    for name in names:
        xaml = LANG_DIR / f"{name}.xaml"
        txt = OUT_DIR / f"{name}.txt"
        if not xaml.exists():
            raise SystemExit(f"找不到 {xaml}")

        ordered = parse_xaml_ordered(xaml)
        entries = {k: v for kind, payload in ordered if kind == "e"
                   for k, v in [payload.split("\t", 1)]}

        if check:
            if not txt.exists():
                problems.append(f"{txt.relative_to(REPO)} 不存在")
                continue
            from_txt = parse_txt(txt)
            if from_txt != entries:
                only_x = sorted(set(entries) - set(from_txt))
                only_t = sorted(set(from_txt) - set(entries))
                diff = sorted(k for k in set(entries) & set(from_txt)
                              if entries[k] != from_txt[k])
                problems.append(f"{name}: 只在 xaml 的键={only_x} 只在 txt 的键={only_t} 值不同={diff}")
        else:
            OUT_DIR.mkdir(parents=True, exist_ok=True)
            lines = list(HEADER)
            lines.append("")
            first_comment = True
            for kind, payload in ordered:
                if kind == "c":
                    # 第一个注释是 .xaml 的**文件头**，由 sync 脚本的模板负责，跳过
                    if first_comment:
                        first_comment = False
                        continue
                    label = payload.strip()
                    if re.match(r'^[=\-─—]{2,}\s*.*\s*[=\-─—]{2,}$', label):
                        lines.append(f"# ==== {label.strip('=─—- ')} ====")
                    else:
                        # 其它注释在 .txt 里用 `#!` 前缀标出（sync 脚本会还原成 XML 注释）
                        lines.append(f"#!{label}")
                else:
                    key, value = payload.split("\t", 1)
                    lines.append(f"{key}={encode(value)}")
            txt.write_text("\n".join(lines) + "\n", encoding="utf-8")
            print(f"✓ {xaml.name} → {txt.relative_to(REPO)}（{len(entries)} 键）")

    if problems:
        for p in problems:
            print(f"✗ {p}", file=sys.stderr)
        return 1
    if check:
        print("✓ 语言文件一致（xaml ↔ txt）")
    return 0


def parse_txt(path: pathlib.Path) -> dict[str, str]:
    entries: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        # 注意：**只去掉行尾换行，不能 strip 整行** —— 有些文案的末尾空格是有意义的
        # （例如 "其他版本： " 这种要跟后面拼的提示语）。
        line = raw.rstrip("\r")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if "=" not in line:
            raise SystemExit(f"{path.name}: 格式不对的行（缺 '='）：{raw!r}")
        key, _, value = line.partition("=")
        key = key.strip()
        if not key:
            raise SystemExit(f"{path.name}: 空键：{raw!r}")
        if key in entries:
            raise SystemExit(f"{path.name}: 重复键 {key}")
        entries[key] = decode(value)
    return entries


if __name__ == "__main__":
    raise SystemExit(main())
