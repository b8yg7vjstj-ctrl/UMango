#!/usr/bin/env bash
# UMango 构建/运行/测试脚本。
#
# 用法：
#   scripts/build.sh all          构建全部（Rust + C++ + Flutter），并打包 dylib 进 .app
#   scripts/build.sh rust         仅构建 Rust 核心层
#   scripts/build.sh native       仅构建 C++ 桥接层
#   scripts/build.sh ui           仅构建 Flutter macOS 应用
#   scripts/build.sh bundle       把 dylib 复制进已构建的 .app
#   scripts/build.sh run          构建全部并启动应用
#   scripts/build.sh test         跑全部测试（Rust 单测 + 桥接自检 + Flutter 集成测试）
#   scripts/build.sh ui-test      只跑 Flutter 集成测试（在真实 macOS 应用里）
#   scripts/build.sh smoke        只跑桥接层自检（最快的端到端验证）
#
# 环境变量：
#   UMANGO_PROFILE=release|debug   构建配置，默认 release（Rust/C++）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=env.sh
source "${SCRIPT_DIR}/env.sh"

PROFILE="${UMANGO_PROFILE:-release}"
if [ "${PROFILE}" = "debug" ]; then
    CARGO_PROFILE_FLAG=""
    CARGO_OUT_DIR="${CARGO_TARGET_DIR}/debug"
    CMAKE_BUILD_TYPE="Debug"
else
    CARGO_PROFILE_FLAG="--release"
    CARGO_OUT_DIR="${CARGO_TARGET_DIR}/release"
    CMAKE_BUILD_TYPE="Release"
fi

RUST_STATICLIB="${CARGO_OUT_DIR}/libumango_ffi.a"
NATIVE_BUILD_DIR="${UMANGO_BUILD_CACHE}/native"
DYLIB="${NATIVE_BUILD_DIR}/libumango_bridge.dylib"
APP_BUNDLE="${UMANGO_ROOT}/ui/build/macos/Build/Products/Debug/umango.app"

log() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }
fail() { printf '\n\033[1;31m失败：%s\033[0m\n' "$*" >&2; exit 1; }

cmd_rust() {
    log "构建 Rust 核心层（${PROFILE}）"
    (cd "${UMANGO_ROOT}/core" && cargo build ${CARGO_PROFILE_FLAG} -p umango-ffi)
    [ -f "${RUST_STATICLIB}" ] || fail "未生成静态库 ${RUST_STATICLIB}"
    echo "产物：${RUST_STATICLIB}"
}

cmd_native() {
    log "构建 C++ 桥接层（依赖 Rust 静态库）"
    [ -f "${RUST_STATICLIB}" ] || cmd_rust
    cmake -S "${UMANGO_ROOT}/native" -B "${NATIVE_BUILD_DIR}" \
        -DUMANGO_RUST_LIB="${RUST_STATICLIB}" \
        -DCMAKE_BUILD_TYPE="${CMAKE_BUILD_TYPE}" >/dev/null
    cmake --build "${NATIVE_BUILD_DIR}" -j "$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"
    [ -f "${DYLIB}" ] || fail "未生成动态库 ${DYLIB}"
    echo "产物：${DYLIB}"
}

cmd_ui() {
    log "构建 Flutter macOS 应用"
    (cd "${UMANGO_ROOT}/ui" && flutter build macos --debug)
    [ -d "${APP_BUNDLE}" ] || fail "未生成应用包 ${APP_BUNDLE}"
    echo "产物：${APP_BUNDLE}"
}

# 把动态库放进应用包。
#
# 说明：更规范的做法是改 Xcode 工程加一个 Copy Files 阶段（或做成 Flutter plugin），
# 但那样需要手改 project.pbxproj，容易出错且难以在脚本里复审。
# 阶段1 选择用脚本显式复制，行为可见、可重复，也便于后续替换为正式方案。
cmd_bundle() {
    log "把桥接层动态库打包进 .app"
    [ -f "${DYLIB}" ] || fail "动态库不存在，请先执行 scripts/build.sh native"
    [ -d "${APP_BUNDLE}" ] || fail "应用包不存在，请先执行 scripts/build.sh ui"
    mkdir -p "${APP_BUNDLE}/Contents/Frameworks"
    cp "${DYLIB}" "${APP_BUNDLE}/Contents/Frameworks/"
    cp "${DYLIB}" "${APP_BUNDLE}/Contents/MacOS/"
    echo "已复制到 Contents/Frameworks 与 Contents/MacOS"
}

cmd_test() {
    log "Rust 单元测试"
    (cd "${UMANGO_ROOT}/core" && cargo test)
    log "C++ ↔ Rust 桥接层自检"
    cmd_smoke
    cmd_ui_test
}

cmd_smoke() {
    [ -f "${NATIVE_BUILD_DIR}/umango_bridge_smoke" ] || cmd_native
    "${NATIVE_BUILD_DIR}/umango_bridge_smoke"
}

# Flutter 集成测试：在**真实运行的 macOS 应用**里跑，而不是 flutter_tester。
#
# 本机的 flutter_tester 启动后无法连回测试框架的 socket（会静默挂住不返回），
# 因此走集成测试路线；这条路更接近真实运行状态，验证强度反而更高。
cmd_ui_test() {
    log "Flutter 集成测试（真实 macOS 应用）"
    cmake --build "${NATIVE_BUILD_DIR}" -j "$(sysctl -n hw.ncpu 2>/dev/null || echo 4)" >/dev/null 2>&1 || cmd_native
    cmd_bundle
    # 同一个 bundle 不能被两个实例同时占用，先清掉正在运行的实例
    pkill -f "umango.app/Contents/MacOS/umango" 2>/dev/null || true
    sleep 1
    (cd "${UMANGO_ROOT}/ui" && flutter test integration_test -d macos)
}

cmd_run() {
    cmd_rust
    cmd_native
    cmd_ui
    cmd_bundle
    log "启动 UMango"
    "${APP_BUNDLE}/Contents/MacOS/umango"
}

case "${1:-all}" in
    rust)   cmd_rust ;;
    native) cmd_native ;;
    ui)     cmd_ui ;;
    bundle) cmd_bundle ;;
    run)    cmd_run ;;
    test)   cmd_test ;;
    ui-test) cmd_ui_test ;;
    smoke)  cmd_smoke ;;
    all)
        cmd_rust
        cmd_native
        cmd_ui
        cmd_bundle
        log "全部构建完成"
        echo "启动：${APP_BUNDLE}/Contents/MacOS/umango"
        ;;
    *)
        echo "未知子命令：$1" >&2
        sed -n '2,20p' "${BASH_SOURCE[0]}"
        exit 1
        ;;
esac
