#!/usr/bin/env bash
# XAML 静态审计。
#
# 为什么需要它：本项目在 macOS 上开发，而 WPF 只能在 Windows 上运行 ——
# XAML 里那些"只有运行时才暴露"的错误（绑定路径拼错、资源键不存在、
# 事件处理器没写、四套主题字典键不一致）在本机既编译不过关也测不出来。
# 这个脚本把它们变成可在 macOS 上检查的静态问题。
#
# 用法：scripts/xaml-audit.sh [--dump-keys]
# 退出码：0 = 无 ERROR（可用于 CI），1 = 有 ERROR
set -euo pipefail

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

P=$(cd "$(dirname "$0")/.." && pwd)

dotnet build "$P/tools/XamlAudit" -v q --nologo >/dev/null

DLL=$(find "$P/tools/XamlAudit/bin" -name 'xaml-audit.dll' | head -1)
if [ -z "$DLL" ]; then
  echo "构建产物未找到" >&2
  exit 2
fi

dotnet "$DLL" "$P/src/UMango.App" "$@"
