/*
 * umango_internal.h —— C++ 桥接层内部共享定义（不对外暴露）
 *
 * 包含三部分：
 *   1. ProcessSpec / ProcessOutput —— 与 Rust 侧 umango_core::process 对应的结构；
 *   2. JsonWriter —— 本层唯一需要"写 JSON"的地方，转义与 UTF-8 净化都集中在这里；
 *   3. 各实现文件之间的函数声明。
 */

#ifndef UMANGO_INTERNAL_H
#define UMANGO_INTERNAL_H

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace umango {

/* ------------------------------------------------------------------ */
/* 进程描述与结果                                                       */
/* ------------------------------------------------------------------ */

struct ProcessSpec {
    std::string program;
    std::vector<std::string> args;
    std::string cwd;                                  /* 空表示继承当前目录 */
    std::vector<std::pair<std::string, std::string>> env; /* 追加/覆盖的环境变量 */
    uint64_t timeout_ms = 30000;
    bool has_stdin = false;
    std::string stdin_data;
};

struct ProcessOutput {
    int status = -1;
    std::string stdout_text;
    std::string stderr_text;
    bool timed_out = false;
    uint64_t duration_ms = 0;
};

/* wire.cpp —— 解析 Rust 传来的长度前缀分帧（见 umango-ffi/src/wire.rs） */
bool decode_process_spec(const unsigned char *data, size_t len, ProcessSpec &out,
                         std::string &error);

/* process_posix.cpp / process_windows.cpp —— 真正创建进程 */
int run_process(const ProcessSpec &spec, ProcessOutput &out, std::string &error);

/* 把 ProcessOutput 序列化成 JSON（Rust 侧用 serde 解析） */
std::string process_output_to_json(const ProcessOutput &out);

/* fs_ops.cpp */
bool umango_which(const std::string &program, std::string &out_path);
bool fs_read_file(const std::string &path, std::string &out_data, std::string &error);
bool fs_write_file(const std::string &path, const unsigned char *data, size_t len,
                   std::string &error);
std::string platform_info_json();

/* ------------------------------------------------------------------ */
/* JsonWriter                                                          */
/* ------------------------------------------------------------------ */

/**
 * 最小 JSON 写入器。
 *
 * 存在的意义不只是"拼字符串"：**命令输出必须被正确转义**。
 * CLI 的输出里会出现引号、反斜杠、换行、制表符，甚至因为 locale 问题
 * 出现非法 UTF-8 字节序列 —— 后者会让 Rust 侧的 serde 直接解析失败。
 * 因此这里做两件事：
 *   1. 按 JSON 规范转义控制字符与 \ " ；
 *   2. **净化非法 UTF-8**：非法字节替换为 U+FFFD，保证输出永远是合法 UTF-8。
 */
class JsonWriter {
public:
    JsonWriter() { buf_.reserve(8192); }

    void begin_object() { before_value(); buf_ += '{'; push_level(true); }
    void end_object() { pop_level(); buf_ += '}'; after_value(); }
    void begin_array() { before_value(); buf_ += '['; push_level(false); }
    void end_array() { pop_level(); buf_ += ']'; after_value(); }

    void key(const char *k) {
        before_comma();
        write_escaped(k, std::strlen(k));
        buf_ += ':';
        expecting_value_ = true;
    }

    void value_string(const std::string &s) { before_value(); write_escaped(s.data(), s.size()); after_value(); }
    void value_string(const char *s) { before_value(); write_escaped(s, std::strlen(s)); after_value(); }
    void value_int(long long v) { before_value(); buf_ += std::to_string(v); after_value(); }
    void value_uint(unsigned long long v) { before_value(); buf_ += std::to_string(v); after_value(); }
    void value_bool(bool v) { before_value(); buf_ += (v ? "true" : "false"); after_value(); }
    void value_null() { before_value(); buf_ += "null"; after_value(); }

    /* 便捷组合：对象成员 */
    void member_string(const char *k, const std::string &v) { key(k); value_string(v); }
    void member_string(const char *k, const char *v) { key(k); value_string(v); }
    void member_int(const char *k, long long v) { key(k); value_int(v); }
    void member_uint(const char *k, unsigned long long v) { key(k); value_uint(v); }
    void member_bool(const char *k, bool v) { key(k); value_bool(v); }

    std::string take() { return std::move(buf_); }

private:
    struct Level {
        bool is_object;
        bool first;
    };

    void push_level(bool is_object) {
        stack_.push_back(Level{is_object, true});
        expecting_value_ = false;
    }

    void pop_level() {
        if (!stack_.empty()) stack_.pop_back();
        expecting_value_ = false;
    }

    void before_comma() {
        if (!stack_.empty()) {
            Level &lv = stack_.back();
            if (!lv.first) buf_ += ',';
            lv.first = false;
        }
    }

    void before_value() {
        if (!expecting_value_) before_comma();
    }

    void after_value() { expecting_value_ = false; }

    /* UTF-8 序列长度判定；返回 0 表示非法字节 */
    static int utf8_seq_len(unsigned char c) {
        if (c < 0x80) return 1;
        if ((c & 0xE0) == 0xC0) return 2;
        if ((c & 0xF0) == 0xE0) return 3;
        if ((c & 0xF8) == 0xF0) return 4;
        return 0;
    }

    /* 校验一个完整序列是否合法（含过长编码、代理区、超范围检查） */
    static bool valid_utf8(const unsigned char *p, int n) {
        if (n == 1) return p[0] < 0x80;
        for (int i = 1; i < n; ++i) {
            if ((p[i] & 0xC0) != 0x80) return false;
        }
        if (n == 2) {
            unsigned cp = ((p[0] & 0x1Fu) << 6) | (p[1] & 0x3Fu);
            return cp >= 0x80;  /* 拒绝过长编码 */
        }
        if (n == 3) {
            unsigned cp = ((p[0] & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu);
            if (cp < 0x800) return false;
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;  /* 拒绝 UTF-16 代理区 */
            return true;
        }
        unsigned cp = ((p[0] & 0x07u) << 18) | ((p[1] & 0x3Fu) << 12) |
                      ((p[2] & 0x3Fu) << 6) | (p[3] & 0x3Fu);
        return cp >= 0x10000 && cp <= 0x10FFFF;
    }

    void write_escaped(const char *data, size_t len) {
        buf_ += '"';
        size_t i = 0;
        while (i < len) {
            unsigned char c = static_cast<unsigned char>(data[i]);
            switch (c) {
                case '"': buf_ += "\\\""; ++i; continue;
                case '\\': buf_ += "\\\\"; ++i; continue;
                case '\n': buf_ += "\\n"; ++i; continue;
                case '\r': buf_ += "\\r"; ++i; continue;
                case '\t': buf_ += "\\t"; ++i; continue;
                case '\b': buf_ += "\\b"; ++i; continue;
                case '\f': buf_ += "\\f"; ++i; continue;
                default: break;
            }
            if (c < 0x20) {
                /* 其余控制字符用 \u00XX */
                char tmp[8];
                std::snprintf(tmp, sizeof(tmp), "\\u%04X", c);
                buf_ += tmp;
                ++i;
                continue;
            }
            if (c < 0x80) {
                buf_ += static_cast<char>(c);
                ++i;
                continue;
            }
            /* 多字节：校验后再原样输出，非法则用 U+FFFD 顶替 */
            int n = utf8_seq_len(c);
            if (n > 1 && i + static_cast<size_t>(n) <= len &&
                valid_utf8(reinterpret_cast<const unsigned char *>(data + i), n)) {
                buf_.append(data + i, static_cast<size_t>(n));
                i += static_cast<size_t>(n);
            } else {
                buf_ += "\xEF\xBF\xBD";  /* U+FFFD */
                ++i;
            }
        }
        buf_ += '"';
    }

    std::string buf_;
    std::vector<Level> stack_;
    bool expecting_value_ = false;
};

}  // namespace umango

#endif /* UMANGO_INTERNAL_H */
