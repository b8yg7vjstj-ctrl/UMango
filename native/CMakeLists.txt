cmake_minimum_required(VERSION 3.20)
project(umango_bridge LANGUAGES CXX)

# ---------------------------------------------------------------------------
# UMango C++ 桥接层
#
# 产物是一个动态库 libumango_bridge.{dylib,so,dll}，内部静态链接了
# Rust 核心层（libumango_ffi.a）。Flutter 侧通过 dart:ffi 加载它。
#
# 用法（由 scripts/build.sh 驱动）：
#   cmake -S native -B <build> -DUMANGO_RUST_LIB=<path>/libumango_ffi.a
#   cmake --build <build>
# ---------------------------------------------------------------------------

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# Rust 静态库位置：优先用显式传入的路径，否则从 CARGO_TARGET_DIR 推断
if(NOT UMANGO_RUST_LIB)
    if(DEFINED ENV{CARGO_TARGET_DIR})
        set(_cargo_target "$ENV{CARGO_TARGET_DIR}")
    else()
        set(_cargo_target "${CMAKE_CURRENT_SOURCE_DIR}/../core/target")
    endif()
    if(CMAKE_BUILD_TYPE STREQUAL "Debug")
        set(_profile "debug")
    else()
        set(_profile "release")
    endif()
    set(UMANGO_RUST_LIB "${_cargo_target}/${_profile}/libumango_ffi.a")
endif()

if(NOT EXISTS "${UMANGO_RUST_LIB}")
    message(FATAL_ERROR
        "找不到 Rust 核心层静态库: ${UMANGO_RUST_LIB}\n"
        "请先执行 scripts/build.sh rust，或用 -DUMANGO_RUST_LIB=... 指定路径。")
endif()

message(STATUS "UMango Rust 核心层: ${UMANGO_RUST_LIB}")

add_library(umango_bridge SHARED
    src/umango_bridge.cpp
    src/wire.cpp
    src/fs_ops.cpp
    src/process_posix.cpp
    src/process_windows.cpp
)

target_include_directories(umango_bridge
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_compile_options(umango_bridge PRIVATE
    $<$<CXX_COMPILER_ID:AppleClang,Clang,GNU>:-Wall -Wextra -Wno-unused-parameter>
)

# Rust 静态库必须放在依赖列表末尾（链接顺序敏感）
target_link_libraries(umango_bridge PRIVATE "${UMANGO_RUST_LIB}")

if(APPLE)
    # Rust 标准库与 chrono 需要的系统框架/库
    target_link_libraries(umango_bridge PRIVATE
        "-framework CoreFoundation"
        "-framework Security"
        "-framework SystemConfiguration"
        "-liconv"
        "-lresolv"
        "-lpthread"
        "-lm"
    )
elseif(UNIX)
    target_link_libraries(umango_bridge PRIVATE
        pthread dl m
    )
elseif(WIN32)
    target_link_libraries(umango_bridge PRIVATE
        ws2_32 userenv bcrypt ntdll
    )
endif()

# 让动态库与自检程序都落在构建目录根部，方便脚本定位
set_target_properties(umango_bridge PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
)

# ---------------------------------------------------------------------------
# 自检程序：验证 Dart 侧会用到的同一套 ABI
# ---------------------------------------------------------------------------
option(UMANGO_BUILD_TESTS "构建桥接层自检程序" ON)

if(UMANGO_BUILD_TESTS)
    enable_testing()
    add_executable(umango_bridge_smoke tests/umango_bridge_smoke.cpp)
    target_link_libraries(umango_bridge_smoke PRIVATE umango_bridge)
    set_target_properties(umango_bridge_smoke PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}"
        BUILD_RPATH "${CMAKE_BINARY_DIR}"
    )
    add_test(NAME bridge_smoke COMMAND umango_bridge_smoke)
endif()
