#!/usr/bin/env bash
# 统计 docs/PCL_CE_FEATURE_PARITY.md 第 2.A–2.K 节**功能表**数据行的完成度。
#
# 口径（刻意写死，避免"估算"）：
#   * 只统计 `### 2.X` 小节内的表格；每张表以**表头行**判定是否为功能表
#     （表头含「功能」列），从而排除第 2 节里夹带的「缺陷清单」等辅助表。
#   * 状态列 = 第 4 列（索引 3）；MC 列 = 第 3 列（索引 2）。
#   * 状态值归一化：含「已完成」→ 已完成；含「部分」→ 部分；含「未做」→ 未做；
#     含「不适用」→ 不适用；否则算「其他」。
#
# 用法： bash scripts/parity-stats.sh
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - <<'PY'
import collections, pathlib

text = pathlib.Path('docs/PCL_CE_FEATURE_PARITY.md').read_text(encoding='utf-8')
lines = text.splitlines()

# 1) 切出第 2.A–2.K 小节内的所有表格
tables = []          # 每个元素是 (小节标题, 该表的行列表)
current = None
in_section = False
section = None
for line in lines:
    if line.startswith('### 2.'):
        # 只统计 2.A–2.K 功能表；2.L 是「已修缺陷」辅助表，列结构不同。
        in_section = not line.strip().startswith('### 2.L')
        section = line.strip()
        current = None
        continue
    if line.startswith('### ') or line.startswith('## '):
        in_section = False
        current = None
        continue
    if not in_section:
        continue
    s = line.strip()
    if s.startswith('|'):
        if current is None:
            current = []
            tables.append((section, current))
        current.append([c.strip() for c in s.strip('|').split('|')])
    else:
        current = None

def is_feature_table(t):
    """表头行含「功能」列才算功能表。"""
    head = t[0]
    return any('功能' in c for c in head)

def is_separator(cells):
    return bool(cells[0]) and set(cells[0]) <= set('-: ')

# 功能表在同一个 `### 2.X` 小节里可能被空行拆成多张：
# 第一张带表头（含「功能」列），后续的是**续表**（没有表头行）。
# 规则：某个小节里一旦出现功能表，该小节后续的表都按功能表处理。
rows = []
seen_feature_section = False
for sec, t in tables:
    if is_feature_table(t):
        seen_feature_section = True
        body = t[1:]
    elif seen_feature_section:
        body = t          # 续表：首行也是数据
    else:
        continue
    for cells in body:
        if len(cells) < 4:
            continue
        if is_separator(cells):
            continue
        if not any(cells):
            continue
        rows.append(cells)

def norm(v):
    if '不适用' in v: return '不适用'
    if '已完成' in v: return '已完成'
    if '未做' in v: return '未做'
    if '部分' in v: return '部分'
    return '其他'

status = collections.Counter(norm(r[3]) for r in rows)
mc = collections.Counter('是' if r[2] == '是' else ('部分' if r[2] == '部分' else '否') for r in rows)

print(f'数据行总数: {len(rows)}')
print(f'MC 专属(是): {mc["是"]}  部分MC(部分): {mc["部分"]}  非MC(否): {mc["否"]}')
print('--- 按状态 ---')
for k in ('已完成', '部分', '未做', '不适用', '其他'):
    print(f'  {k}: {status.get(k, 0)}')
print('--- 仍为「部分」的条目 ---')
for r in rows:
    if norm(r[3]) == '部分':
        print(f'  * {r[0][:60]}')
if status.get('其他'):
    print('--- 状态列无法归类的行（应为空） ---')
    for r in rows:
        if norm(r[3]) == '其他':
            print(f'  ! {r[0][:50]} => {r[3][:40]}')
PY
