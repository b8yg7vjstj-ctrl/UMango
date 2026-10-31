#!/usr/bin/env bash
# 本地化占位符元数静态审计。
#
# 为什么需要它：文案模板里的占位符个数与代码里传的实参个数对不上时，
# 编译不报、单元测试也不报（测试用的是 KeyOnlyTextProvider，拿不到真实模板），
# 只有真机跑到那一行才炸 —— 实际后果是 Avalonia 壳**启动即崩溃**。
# 这个脚本把「切语言才暴露」的那一类也提前拦住：同一个键在不同语言里的
# 占位符集合必须一致。
#
# 用法：scripts/localization-audit.sh
# 退出码：0 = 无 ERROR（可用于 CI），1 = 有 ERROR
set -euo pipefail

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

P=$(cd "$(dirname "$0")/.." && pwd)

dotnet build "$P/tools/LocalizationAudit" -v q --nologo >/dev/null

DLL=$(find "$P/tools/LocalizationAudit/bin" -name 'localization-audit.dll' | head -1)
if [ -z "$DLL" ]; then
  echo "构建产物未找到" >&2
  exit 2
fi

dotnet "$DLL" "$P"
