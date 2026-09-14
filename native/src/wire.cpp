/*
 * wire.cpp —— Rust 侧进程请求的解码器。
 *
 * 对端实现见 core/crates/umango-ffi/src/wire.rs，布局必须逐字节一致：
 *
 *   magic "UMS1" | version u32 | program(len+bytes) | arg_count u32 | args...
 *   | cwd_present u8 [cwd] | timeout_ms u64 | env_count u32 | (key,value)...
 *   | stdin_present u8 [stdin]
 *
 * 这里对每一个读取都做**越界检查**：Rust 与本层是两个独立编译单元，
 * 一旦版本不匹配或数据被破坏，必须报错而不是读到缓冲区外面去。
 */

#include "umango_internal.h"

namespace umango {
namespace {

constexpr uint32_t kMaxArgs = 4096;
constexpr uint32_t kMaxEnv = 1024;
constexpr uint32_t kMaxFieldBytes = 32u * 1024u * 1024u;  /* 32MB 上限 */

class Reader {
public:
    Reader(const unsigned char *data, size_t len) : p_(data), len_(len) {}

    bool read_u8(uint8_t &v) {
        if (off_ + 1 > len_) return fail("读取 u8 越界");
        v = p_[off_++];
        return true;
    }

    bool read_u32(uint32_t &v) {
        if (off_ + 4 > len_) return fail("读取 u32 越界");
        v = static_cast<uint32_t>(p_[off_]) | (static_cast<uint32_t>(p_[off_ + 1]) << 8) |
            (static_cast<uint32_t>(p_[off_ + 2]) << 16) |
            (static_cast<uint32_t>(p_[off_ + 3]) << 24);
        off_ += 4;
        return true;
    }

    bool read_u64(uint64_t &v) {
        if (off_ + 8 > len_) return fail("读取 u64 越界");
        v = 0;
        for (int i = 0; i < 8; ++i) {
            v |= static_cast<uint64_t>(p_[off_ + i]) << (8 * i);
        }
        off_ += 8;
        return true;
    }

    bool read_str(std::string &out) {
        uint32_t n = 0;
        if (!read_u32(n)) return false;
        if (n > kMaxFieldBytes) return fail("字段长度超过上限");
        if (off_ + n > len_) return fail("读取字符串越界");
        out.assign(reinterpret_cast<const char *>(p_ + off_), n);
        off_ += n;
        return true;
    }

    bool expect_bytes(const char *expect, size_t n) {
        if (off_ + n > len_) return fail("读取头部越界");
        if (std::memcmp(p_ + off_, expect, n) != 0) return fail("magic 不匹配");
        off_ += n;
        return true;
    }

    size_t remaining() const { return len_ - off_; }

    bool fail(const char *msg) {
        if (error_.empty()) error_ = msg;
        return false;
    }

    const std::string &error() const { return error_; }

private:
    const unsigned char *p_ = nullptr;
    size_t len_ = 0;
    size_t off_ = 0;
    std::string error_;
};

}  // namespace

bool decode_process_spec(const unsigned char *data, size_t len, ProcessSpec &out,
                        std::string &error) {
    if (data == nullptr || len == 0) {
        error = "进程请求数据为空";
        return false;
    }
    Reader r(data, len);

    if (!r.expect_bytes("UMS1", 4)) {
        error = "进程请求缺少 UMS1 magic（Rust 与 C++ 的线格式版本不一致？）";
        return false;
    }
    uint32_t version = 0;
    if (!r.read_u32(version)) {
        error = r.error();
        return false;
    }
    if (version != 1) {
        error = "不支持的线格式版本 " + std::to_string(version);
        return false;
    }

    if (!r.read_str(out.program)) {
        error = r.error();
        return false;
    }
    if (out.program.empty()) {
        error = "program 为空";
        return false;
    }

    uint32_t arg_count = 0;
    if (!r.read_u32(arg_count)) {
        error = r.error();
        return false;
    }
    if (arg_count > kMaxArgs) {
        error = "参数数量超过上限";
        return false;
    }
    out.args.reserve(arg_count);
    for (uint32_t i = 0; i < arg_count; ++i) {
        std::string a;
        if (!r.read_str(a)) {
            error = r.error();
            return false;
        }
        out.args.push_back(std::move(a));
    }

    uint8_t cwd_present = 0;
    if (!r.read_u8(cwd_present)) {
        error = r.error();
        return false;
    }
    if (cwd_present) {
        if (!r.read_str(out.cwd)) {
            error = r.error();
            return false;
        }
    }

    if (!r.read_u64(out.timeout_ms)) {
        error = r.error();
        return false;
    }

    uint32_t env_count = 0;
    if (!r.read_u32(env_count)) {
        error = r.error();
        return false;
    }
    if (env_count > kMaxEnv) {
        error = "环境变量数量超过上限";
        return false;
    }
    out.env.reserve(env_count);
    for (uint32_t i = 0; i < env_count; ++i) {
        std::string k, v;
        if (!r.read_str(k) || !r.read_str(v)) {
            error = r.error();
            return false;
        }
        out.env.emplace_back(std::move(k), std::move(v));
    }

    uint8_t stdin_present = 0;
    if (!r.read_u8(stdin_present)) {
        error = r.error();
        return false;
    }
    if (stdin_present) {
        if (!r.read_str(out.stdin_data)) {
            error = r.error();
            return false;
        }
        out.has_stdin = true;
    }

    /* 末尾不允许多余数据：说明两端对布局的理解已经不一致了 */
    if (r.remaining() != 0) {
        error = "进程请求尾部存在多余字节（线格式已不一致）";
        return false;
    }

    return true;
}

}  // namespace umango
