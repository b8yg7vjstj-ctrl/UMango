/*
 * process_windows.cpp —— Windows 平台的进程执行实现。
 *
 * # ⚠️ 验证状态声明
 *
 * 本文件在 macOS 上编写，**未在 Windows 真机编译或运行过**。
 * 与 winget/scoop 适配器同样的处理原则：
 *
 *  1. 只在 `_WIN32` 下参与编译，不会影响 macOS/Linux 上已验证的代码路径；
 *  2. `run_process` 的实现若在真机上失败，返回的是明确的错误字符串，
 *     而不是让 Rust 侧拿到错误的数据（宁可失败也不要静默出错）；
 *  3. 真机验证时应优先确认这三件事：
 *     - 管道并发读取是否仍能避免 stdout/stderr 双管道死锁；
 *     - 超时后 TerminateProcess 是否真的回收了子进程；
 *     - 命令行拼装（尤其是含空格与引号的参数）是否符合 CreateProcess 的规则。
 *
 * 之所以现在就写而不是留空：接口与行为约定需要先固定下来，
 * Windows 目标一旦有机器就能直接进入验证阶段，而不是从零开始。
 */

#include "umango_internal.h"

#if defined(_WIN32)

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <string>
#include <vector>

namespace umango {
namespace {

constexpr size_t kReadChunk = 64 * 1024;
constexpr size_t kMaxStreamBytes = 16u * 1024u * 1024u;

/* 按 CreateProcess 的规则给参数加引号：
 * 含空格/制表符/引号的参数必须用双引号包裹，内部的双引号用反斜杠转义。 */
std::string quote_arg(const std::string &arg) {
    if (!arg.empty() && arg.find_first_of(" \t\n\v\"") == std::string::npos) {
        return arg;
    }
    std::string out = "\"";
    for (size_t i = 0; i < arg.size(); ++i) {
        size_t backslashes = 0;
        while (i < arg.size() && arg[i] == '\\') {
            ++backslashes;
            ++i;
        }
        if (i == arg.size()) {
            out.append(backslashes * 2, '\\');
            break;
        }
        if (arg[i] == '"') {
            out.append(backslashes * 2 + 1, '\\');
            out += '"';
        } else {
            out.append(backslashes, '\\');
            out += arg[i];
        }
    }
    out += '"';
    return out;
}

std::string build_command_line(const ProcessSpec &spec) {
    std::string cl = quote_arg(spec.program);
    for (const auto &a : spec.args) {
        cl += ' ';
        cl += quote_arg(a);
    }
    return cl;
}

/* 把一段 UTF-8 字符串转换成宽字符命令行的辅助（Windows 原生是 UTF-16） */
std::wstring utf8_to_wide(const std::string &s) {
    if (s.empty()) return std::wstring();
    int need = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (need <= 0) return std::wstring();
    std::wstring out(static_cast<size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), &out[0], need);
    return out;
}

void drain(HANDLE h, std::string &sink) {
    char buf[kReadChunk];
    DWORD avail = 0;
    while (PeekNamedPipe(h, nullptr, 0, nullptr, &avail, nullptr) && avail > 0) {
        DWORD got = 0;
        if (!ReadFile(h, buf, static_cast<DWORD>(sizeof(buf)), &got, nullptr) || got == 0) break;
        if (sink.size() < kMaxStreamBytes) sink.append(buf, got);
    }
}

}  // namespace

int run_process(const ProcessSpec &spec, ProcessOutput &out, std::string &error) {
    out = ProcessOutput{};

    if (spec.program.empty()) {
        error = "program 为空";
        return -1;
    }

    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    sa.lpSecurityDescriptor = nullptr;

    HANDLE out_rd = nullptr, out_wr = nullptr;
    HANDLE err_rd = nullptr, err_wr = nullptr;
    HANDLE in_rd = nullptr, in_wr = nullptr;

    if (!CreatePipe(&out_rd, &out_wr, &sa, 0) || !CreatePipe(&err_rd, &err_wr, &sa, 0)) {
        error = "创建管道失败";
        return -1;
    }
    /* 读端不能被继承，否则子进程退出后父进程读不到 EOF */
    SetHandleInformation(out_rd, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(err_rd, HANDLE_FLAG_INHERIT, 0);

    const bool with_in = spec.has_stdin;
    if (with_in) {
        if (!CreatePipe(&in_rd, &in_wr, &sa, 0)) {
            CloseHandle(out_rd); CloseHandle(out_wr);
            CloseHandle(err_rd); CloseHandle(err_wr);
            error = "创建 stdin 管道失败";
            return -1;
        }
        SetHandleInformation(in_wr, HANDLE_FLAG_INHERIT, 0);
    } else {
        in_rd = CreateFileA("NUL", GENERIC_READ, FILE_SHARE_READ, &sa, OPEN_EXISTING, 0, nullptr);
    }

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = out_wr;
    si.hStdError = err_wr;
    si.hStdInput = in_rd;

    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof(pi));

    std::wstring cmdline = utf8_to_wide(build_command_line(spec));
    std::vector<wchar_t> cmd_buf(cmdline.begin(), cmdline.end());
    cmd_buf.push_back(L'\0'); /* CreateProcessW 可能就地修改命令行，必须可写 */

    std::wstring cwd_w;
    LPCWSTR cwd_ptr = nullptr;
    if (!spec.cwd.empty()) {
        cwd_w = utf8_to_wide(spec.cwd);
        cwd_ptr = cwd_w.c_str();
    }

    /* 环境变量：继承父进程并叠加覆盖项 */
    std::string env_block;
    {
        /* CreateProcess 要求 "k=v\0k=v\0\0" 格式 */
        LPWCH parent_env = GetEnvironmentStringsW();
        if (parent_env != nullptr) {
            for (LPWCH p = parent_env; *p != L'\0'; p += wcslen(p) + 1) {
                std::wstring entry(p);
                std::string narrow;
                int need = WideCharToMultiByte(CP_UTF8, 0, entry.c_str(),
                                               static_cast<int>(entry.size()), nullptr, 0, nullptr,
                                               nullptr);
                if (need > 0) {
                    narrow.resize(static_cast<size_t>(need));
                    WideCharToMultiByte(CP_UTF8, 0, entry.c_str(),
                                        static_cast<int>(entry.size()), &narrow[0], need, nullptr,
                                        nullptr);
                    env_block += narrow;
                    env_block.push_back('\0');
                }
            }
            FreeEnvironmentStringsW(parent_env);
        }
        for (const auto &kv : spec.env) {
            env_block += kv.first + "=" + kv.second;
            env_block.push_back('\0');
        }
        env_block.push_back('\0');
    }

    const char *no_color = "NO_COLOR";
    (void)no_color; /* 说明：Windows 下同样由 Rust 侧通过 env 覆盖传入 NO_COLOR */

    const long long started = GetTickCount64();

    BOOL ok = CreateProcessW(nullptr, cmd_buf.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                             env_block.empty() ? nullptr : env_block.data(), cwd_ptr, &si, &pi);

    /* 父进程侧关闭写端 */
    CloseHandle(out_wr);
    CloseHandle(err_wr);
    if (with_in) CloseHandle(in_rd);
    else if (in_rd != nullptr) CloseHandle(in_rd);

    if (!ok) {
        CloseHandle(out_rd);
        CloseHandle(err_rd);
        if (with_in) CloseHandle(in_wr);
        DWORD err = GetLastError();
        if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND) {
            error = "ENOENT: 找不到可执行文件 " + spec.program;
            return -2;
        }
        error = "CreateProcess 失败，错误码 " + std::to_string(err);
        return -1;
    }

    if (with_in) {
        DWORD written = 0;
        if (!spec.stdin_data.empty()) {
            WriteFile(in_wr, spec.stdin_data.data(),
                      static_cast<DWORD>(spec.stdin_data.size()), &written, nullptr);
        }
        CloseHandle(in_wr);
    }

    const unsigned long long deadline =
        static_cast<unsigned long long>(started) + static_cast<unsigned long long>(spec.timeout_ms);
    bool timed_out = false;

    for (;;) {
        drain(out_rd, out.stdout_text);
        drain(err_rd, out.stderr_text);

        DWORD wait = WaitForSingleObject(pi.hProcess, 40);
        if (wait == WAIT_OBJECT_0) break;

        if (GetTickCount64() >= deadline) {
            timed_out = true;
            TerminateProcess(pi.hProcess, 1);
            WaitForSingleObject(pi.hProcess, 2000);
            break;
        }
    }

    /* 收尾：把残留输出读干净 */
    drain(out_rd, out.stdout_text);
    drain(err_rd, out.stderr_text);

    DWORD exit_code = 1;
    GetExitCodeProcess(pi.hProcess, &exit_code);
    out.status = static_cast<int>(exit_code);
    out.timed_out = timed_out;
    out.duration_ms = static_cast<uint64_t>(GetTickCount64() - started);
    if (timed_out) {
        out.stderr_text += "\n[umango] 进程因超时被强制终止（TerminateProcess）";
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    CloseHandle(out_rd);
    CloseHandle(err_rd);
    return 0;
}

}  // namespace umango

#endif /* _WIN32 */
