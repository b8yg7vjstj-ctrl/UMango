#!/usr/bin/env bash
# UMango 构建环境。
#
# 项目源码落在 OneDrive 同步目录里，而 Rust/Flutter 的构建产物是数万个
# 小文件，留在同步目录里会拖慢构建并偶发文件锁冲突，因此统一重定向到
# 本地缓存盘。用法： source scripts/env.sh
set -euo pipefail

UMANGO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export UMANGO_ROOT
export UMANGO_BUILD_CACHE="${HOME}/Library/Caches/umango-build"
export CARGO_TARGET_DIR="${UMANGO_BUILD_CACHE}/cargo"
export CARGO_NET_RETRY=5
# OneDrive 的文件时间戳精度不稳定，关掉增量编译以避免"改了不重编"的怪现象
export CARGO_INCREMENTAL=0

mkdir -p "$CARGO_TARGET_DIR"
