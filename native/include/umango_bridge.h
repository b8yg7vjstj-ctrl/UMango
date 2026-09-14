/*
 * umango_bridge.h —— UMango C++ 桥接层的 ABI 契约
 *
 * 本头文件定义两套接口，方向相反，不要混淆：
 *
 *   1. 【向下】umb_bridge_*  —— 供 Flutter/Dart 通过 dart:ffi 调用。
 *      Dart 只能传字符串，所以业务调用统一走 `umb_bridge_call(method, json)`。
 *
 *   2. 【向上】umango_host_vtable —— 由本层实现、注册给 Rust 核心层的回调表。
 *      Rust 负责"要跑什么命令"，真正的进程创建由本层完成（posix_spawn /
 *      CreateProcess），对应项目分层" C++ 负责系统调用、进程调用、文件读写"。
 *
 * ⚠️ 改这个结构体等于改 ABI：字段顺序必须与 Rust 侧
 * `umango_ffi::UmangoHostVTable` 完全一致，且必须同步提升
 * UMANGO_ABI_VERSION，否则 Rust 会拒绝初始化（这是有意设计的保护）。
 */

#ifndef UMANGO_BRIDGE_H
#define UMANGO_BRIDGE_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 必须与 Rust 侧 const UMANGO_ABI_VERSION 保持一致 */
#define UMANGO_ABI_VERSION 1u

/*
 * 宿主回调用特殊返回值表示"本平台未实现"。
 * Rust 侧收到它时会退回纯 Rust 执行器，而不是直接报错 ——
 * 这样桥接层可以分平台逐步实现而不影响可用性。
 */
#define UMANGO_HOST_NOT_IMPLEMENTED (-1001)

/* ------------------------------------------------------------------ */
/* 【向上】Rust 核心层调用的宿主回调表                                  */
/* ------------------------------------------------------------------ */

typedef struct umango_host_vtable {
    /* ABI 版本，必须等于 UMANGO_ABI_VERSION */
    unsigned int abi_version;
    /* 宿主名称，仅用于日志 */
    const char *host_name;

    /*
     * 执行进程。
     *   spec/spec_len : 长度前缀二进制分帧的进程请求
     *                   （定义见 core/crates/umango-ffi/src/wire.rs）
     *   out_json      : 成功时写入 ProcessOutput 的 JSON（本层分配）
     *   out_err       : 失败时写入错误描述（本层分配）
     * 返回 0 成功；UMANGO_HOST_NOT_IMPLEMENTED 表示本平台未实现；其它非 0 为错误。
     *
     * 注意：这里刻意**不用 JSON 传请求**。C++ 侧解析 JSON 需要手写解析器，
     * 而命令行参数里可能含引号、换行、NUL，容易写出静默错误；
     * 分帧格式无需转义且可严格做越界检查。
     */
    int (*run_process)(const unsigned char *spec, size_t spec_len, char **out_json,
                       char **out_err);

    /* 在 PATH 中查找可执行文件。找到返回 0 并写入 out_path，否则返回非 0。 */
    int (*which)(const char *program, char **out_path);

    /* 读取文件。成功返回 0，out_data 由本层分配（用 free_bytes 释放）。 */
    int (*read_file)(const char *path, unsigned char **out_data, size_t *out_len);

    /* 写入文件（自动创建父目录）。成功返回 0。 */
    int (*write_file)(const char *path, const unsigned char *data, size_t len,
                      char **out_err);

    /* 平台信息 JSON：{"os":..,"os_version":..,"arch":..,"hostname":..} */
    int (*platform_info)(char **out_json);

    /* 释放以上回调返回的字符串缓冲区 */
    void (*free_buffer)(void *ptr);

    /* 释放 read_file 返回的数据缓冲区 */
    void (*free_bytes)(unsigned char *ptr, size_t len);
} umango_host_vtable;

/* ------------------------------------------------------------------ */
/* 【向下】Rust 核心层导出的函数（本层调用它们）                         */
/* ------------------------------------------------------------------ */

extern unsigned int umango_abi_version(void);
extern const char *umango_core_version(void);
extern int umango_init(const umango_host_vtable *host);
extern char *umango_call(const char *method, const char *args_json);
extern void umango_free_string(char *ptr);
extern const char *umango_host_platform(void);
extern const char *umango_process_runner_name(void);
extern void umango_use_native_runner(void);

/* ------------------------------------------------------------------ */
/* 【向下】本层导出给 Dart 的接口                                       */
/* ------------------------------------------------------------------ */

/*
 * 初始化桥接层：注册宿主回调并调用 umango_init。
 * 成功返回 0；失败返回负值，并把原因写入 out_err（可为 NULL）。
 * 可重复调用，第二次起为幂等空操作。
 */
int umb_bridge_init(char **out_err);

/* 本层的 ABI 版本，供 Dart 侧做兼容性检查。 */
unsigned int umb_bridge_abi_version(void);

/*
 * 统一业务调用入口。
 *   method    : 例如 "sources.list" / "packages.search"
 *   args_json : 参数对象的 JSON 字符串，可为 NULL 或 ""
 * 返回**保证非空**的 JSON 字符串（失败时也是合法 JSON 信封），
 * 必须用 umb_bridge_free 释放。
 */
char *umb_bridge_call(const char *method, const char *args_json);

/* 释放 umb_bridge_call 返回的字符串。 */
void umb_bridge_free(char *ptr);

/* 核心层版本，静态生命周期，不要释放。 */
const char *umb_bridge_core_version(void);

/* 宿主平台标识："macos" / "windows" / "linux"，静态生命周期。 */
const char *umb_bridge_platform(void);

/*
 * 当前进程执行器名字。
 * 期望值 "cpp-bridge" —— 若返回 "native"，说明 Rust 侧没有接受宿主回调，
 * C++ 进程调用层其实没被用上（这就是端到端自检的关键指标）。
 */
const char *umb_bridge_runner(void);

/* 诊断开关：强制 Rust 使用内置执行器，便于对比两条路径的行为。 */
void umb_bridge_force_native_runner(void);

/* 桥接层自检：跑一条最小命令验证整条 FFI 回路，返回 JSON 结果。 */
char *umb_bridge_self_test(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* UMANGO_BRIDGE_H */
