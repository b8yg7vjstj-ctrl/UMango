/*
 * umango_bridge.cpp —— C++ 桥接层主体。
 *
 * 两个方向都在这一个文件里汇合：
 *
 *   【向上】把 host_vtable 注册给 Rust，Rust 需要跑命令时回调到这里。
 *   【向下】把 Rust 导出的 umango_call 包装成 umb_bridge_call 给 Dart 用。
 *
 * 内存所有权规则（跨语言最容易出错的地方，这里写清楚）：
 *
 *   - Rust 字符串：由 Rust 分配（umango_call），必须由 Rust 释放
 *     （umango_free_string）。本层调用它之后立刻释放，绝不外传。
 *   - 本层字符串：用 malloc 分配（strdup），由 umb_bridge_free（free）释放。
 *     交给 Dart 的只有这一类，Dart 侧只需配对调用 umb_bridge_free。
 *   - 回调返回的缓冲区：本层分配，Rust 通过 free_buffer / free_bytes 回调释放，
 *     因为释放逻辑属于分配方。
 */

#include "umango_bridge.h"
#include "umango_internal.h"

#include <cstdlib>
#include <cstring>
#include <string>

using namespace umango;

namespace {

/* 一次性初始化的状态 */
bool g_initialized = false;

/* ------------------------------------------------------------------ */
/* 向上：Rust 核心层调用的回调实现                                      */
/* ------------------------------------------------------------------ */

int host_run_process(const unsigned char *spec_data, size_t spec_len, char **out_json,
                     char **out_err) {
    if (out_json != nullptr) *out_json = nullptr;
    if (out_err != nullptr) *out_err = nullptr;

    ProcessSpec spec;
    std::string error;
    if (!decode_process_spec(spec_data, spec_len, spec, error)) {
        if (out_err != nullptr) *out_err = strdup(error.c_str());
        return -1;
    }

    ProcessOutput output;
    int rc = run_process(spec, output, error);
    if (rc != 0) {
        if (out_err != nullptr) *out_err = strdup(error.c_str());
        /* 保留原始错误语义：-2 表示"找不到可执行文件"，Rust 据此映射错误码 */
        return rc;
    }

    std::string json = process_output_to_json(output);
    if (out_json != nullptr) *out_json = strdup(json.c_str());
    return 0;
}

int host_which(const char *program, char **out_path) {
    if (out_path != nullptr) *out_path = nullptr;
    if (program == nullptr) return -1;

    std::string found;
    if (!umango_which(program, found)) return -1;
    if (out_path != nullptr) *out_path = strdup(found.c_str());
    return 0;
}

int host_read_file(const char *path, unsigned char **out_data, size_t *out_len) {
    if (out_data != nullptr) *out_data = nullptr;
    if (out_len != nullptr) *out_len = 0;
    if (path == nullptr || out_data == nullptr || out_len == nullptr) return -1;

    std::string data;
    std::string error;
    if (!fs_read_file(path, data, error)) return -1;

    /* 用 malloc 而不是 new[]：释放走 free_bytes -> free，规则统一 */
    unsigned char *buf = static_cast<unsigned char *>(std::malloc(data.size() + 1));
    if (buf == nullptr) return -1;
    std::memcpy(buf, data.data(), data.size());
    buf[data.size()] = '\0';
    *out_data = buf;
    *out_len = data.size();
    return 0;
}

int host_write_file(const char *path, const unsigned char *data, size_t len, char **out_err) {
    if (out_err != nullptr) *out_err = nullptr;
    if (path == nullptr) return -1;

    std::string error;
    if (!fs_write_file(path, data, len, error)) {
        if (out_err != nullptr) *out_err = strdup(error.c_str());
        return -1;
    }
    return 0;
}

int host_platform_info(char **out_json) {
    if (out_json != nullptr) *out_json = nullptr;
    std::string json = platform_info_json();
    if (out_json != nullptr) *out_json = strdup(json.c_str());
    return 0;
}

void host_free_buffer(void *ptr) {
    if (ptr != nullptr) std::free(ptr);
}

void host_free_bytes(unsigned char *ptr, size_t /*len*/) {
    if (ptr != nullptr) std::free(ptr);
}

/* 回调表。字段顺序必须与 umango_ffi::UmangoHostVTable 完全一致。 */
umango_host_vtable g_vtable = {
    UMANGO_ABI_VERSION,
    "umango-cpp-bridge",
    host_run_process,
    host_which,
    host_read_file,
    host_write_file,
    host_platform_info,
    host_free_buffer,
    host_free_bytes,
};

/* ProcessOutput -> JSON（Rust 侧用 serde 解析） */
std::string output_json(const ProcessOutput &out) {
    JsonWriter w;
    w.begin_object();
    w.member_int("status", out.status);
    w.member_string("stdout", out.stdout_text);
    w.member_string("stderr", out.stderr_text);
    w.member_bool("timed_out", out.timed_out);
    w.member_uint("duration_ms", out.duration_ms);
    w.end_object();
    return w.take();
}

}  // namespace

/* 前面两个函数在匿名命名空间里，这里补一个可见的包装供成员函数复用 */
namespace umango {
std::string process_output_to_json(const ProcessOutput &out) {
    return output_json(out);
}
}  // namespace umango

/* ------------------------------------------------------------------ */
/* 向下：导出给 Dart 的接口                                             */
/* ------------------------------------------------------------------ */

extern "C" {

unsigned int umb_bridge_abi_version(void) {
    return UMANGO_ABI_VERSION;
}

int umb_bridge_init(char **out_err) {
    if (out_err != nullptr) *out_err = nullptr;
    if (g_initialized) return 0;

    /* 先做 ABI 自检：Rust 与 C++ 是两个独立编译单元，版本不一致必须立刻发现 */
    unsigned int rust_abi = umango_abi_version();
    if (rust_abi != UMANGO_ABI_VERSION) {
        std::string msg = "ABI 版本不匹配：C++=" + std::to_string(UMANGO_ABI_VERSION) +
                          " Rust=" + std::to_string(rust_abi);
        if (out_err != nullptr) *out_err = strdup(msg.c_str());
        return -1;
    }

    int rc = umango_init(&g_vtable);
    if (rc != 0) {
        std::string msg = "umango_init 失败，返回码 " + std::to_string(rc);
        if (out_err != nullptr) *out_err = strdup(msg.c_str());
        return rc;
    }

    g_initialized = true;
    return 0;
}

char *umb_bridge_call(const char *method, const char *args_json) {
    if (!g_initialized) {
        /* 未初始化也要给出合法 JSON，让 UI 能显示明确原因而不是崩溃 */
        char *err = nullptr;
        int rc = umb_bridge_init(&err);
        if (rc != 0) {
            std::string msg = err != nullptr ? std::string(err) : std::string("桥接层初始化失败");
            if (err != nullptr) std::free(err);
            std::string escaped;
            /* 简单转义：错误信息里可能有引号 */
            for (char c : msg) {
                if (c == '"' || c == '\\') escaped += '\\';
                escaped += c;
            }
            std::string json =
                "{\"ok\":false,\"error\":{\"code\":\"bridge_init_failed\",\"message\":\"" +
                escaped + "\",\"advice\":\"manual\",\"retryable\":false}}";
            return strdup(json.c_str());
        }
    }

    char *rust_owned = umango_call(method, args_json);
    if (rust_owned == nullptr) {
        return strdup(
            "{\"ok\":false,\"error\":{\"code\":\"ffi_error\",\"message\":\"核心层返回空指针\","
            "\"advice\":\"manual\",\"retryable\":false}}");
    }

    /* 立刻转成 C++ 拥有的字符串并释放 Rust 的那份，绝不让 Dart 碰 Rust 的内存 */
    std::string copy(rust_owned);
    umango_free_string(rust_owned);

    if (copy.empty()) {
        return strdup(
            "{\"ok\":false,\"error\":{\"code\":\"ffi_error\",\"message\":\"核心层返回空字符串\","
            "\"advice\":\"manual\",\"retryable\":false}}");
    }
    return strdup(copy.c_str());
}

void umb_bridge_free(char *ptr) {
    if (ptr != nullptr) std::free(ptr);
}

const char *umb_bridge_core_version(void) {
    return umango_core_version();
}

const char *umb_bridge_platform(void) {
    return umango_host_platform();
}

const char *umb_bridge_runner(void) {
    return umango_process_runner_name();
}

void umb_bridge_force_native_runner(void) {
    umango_use_native_runner();
}

/*
 * 自检：验证整条 FFI 回路真的通了。
 *
 * 判断标准不是"函数没报错"，而是 **Rust 侧报告的进程执行器必须是 cpp-bridge**
 * —— 如果它显示 native，说明宿主回调没有被 Rust 接受，
 * 也就是说 C++ 的进程调用层其实根本没参与工作（只是看起来"能用"）。
 */
char *umb_bridge_self_test(void) {
    JsonWriter w;
    w.begin_object();

    char *init_err = nullptr;
    int init_rc = umb_bridge_init(&init_err);
    w.member_int("init_rc", init_rc);
    if (init_err != nullptr) {
        w.member_string("init_error", init_err);
        std::free(init_err);
    }

    w.member_uint("bridge_abi", UMANGO_ABI_VERSION);
    w.member_uint("rust_abi", umango_abi_version());
    w.member_string("core_version", umango_core_version());
    w.member_string("platform", umango_host_platform());
    w.member_string("runner", umango_process_runner_name());

    const char *runner = umango_process_runner_name();
    const bool bridge_active = (runner != nullptr && std::strcmp(runner, "cpp-bridge") == 0);
    w.member_bool("cpp_process_layer_active", bridge_active);

    /* 直接经回调跑一条命令，确认宿主 run_process 真的可用 */
    ProcessSpec spec;
    spec.program = "/bin/echo";
    spec.args.push_back("umango-bridge-self-test");
    spec.timeout_ms = 5000;
    ProcessOutput out;
    std::string err;
    int rc = run_process(spec, out, err);
    w.member_int("echo_rc", rc);
    w.member_int("echo_status", out.status);
    w.member_string("echo_stdout", out.stdout_text);
    if (!err.empty()) w.member_string("echo_error", err);

    /* 顺带验证 which 可用（后续包源探测依赖它） */
    std::string sh_path;
    w.member_bool("which_sh_ok", umango_which("sh", sh_path));
    w.member_string("which_sh_path", sh_path);

    w.member_bool("ok", bridge_active && rc == 0 && out.status == 0);
    w.end_object();
    return strdup(w.take().c_str());
}

}  /* extern "C" */
