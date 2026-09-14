/*
 * process_posix.cpp —— POSIX 平台的进程执行实现（macOS / Linux）。
 *
 * 这是" C++ 负责进程调用"这条分层的实际落点：Rust 只描述要跑什么命令，
 * 由这里用 posix_spawn 真正创建进程并收集输出。
 *
 * 几个必须处理正确的地方（都是踩过才知道的）：
 *
 *  1. **不能只读一路管道**：stdout 与 stderr 必须并发读取。若先读 stdout 到 EOF
 *     再读 stderr，子进程写满 stderr 管道缓冲（约 64KB）就会阻塞，
 *     而父进程正等着 stdout 的 EOF —— 经典死锁。这里用 poll() 同时监听两路。
 *
 *  2. **超时必须真的杀掉进程**：超时后 SIGKILL，并继续把管道里已有的数据读干净，
 *     否则会丢错误信息或留下僵尸进程。
 *
 *  3. **环境变量要显式传完整 envp**：posix_spawn 不继承环境。
 *
 *  4. **stdin 不给时要接 /dev/null**：否则 CLI 可能等着读输入挂在那里。
 */

#include "umango_internal.h"

#if !defined(_WIN32)

#include <spawn.h>
#include <sys/wait.h>
#include <sys/types.h>
#include <unistd.h>
#include <poll.h>
#include <signal.h>
#include <fcntl.h>
#include <errno.h>
#include <string.h>
#include <time.h>

#if defined(__APPLE__)
#include <AvailabilityMacros.h>
#endif

#include <map>
#include <mutex>

extern char **environ;

namespace umango {
namespace {

/*
 * 设置子进程工作目录。
 *
 * macOS 26 起 `posix_spawn_file_actions_addchdir_np` 被
 * `posix_spawn_file_actions_addchdir` 取代（前者标记为 deprecated），
 * 这里按 SDK 版本选择，避免在新 SDK 上编译出弃用告警。
 * 返回 false 表示当前平台不支持该能力 —— 此时必须报错，
 * 绝不能静默在错误的目录里执行命令。
 */
bool add_chdir_action(posix_spawn_file_actions_t *actions, const char *cwd) {
#if defined(__APPLE__) && defined(__MAC_OS_X_VERSION_MAX_ALLOWED) && \
    (__MAC_OS_X_VERSION_MAX_ALLOWED >= 260000)
    return posix_spawn_file_actions_addchdir(actions, cwd) == 0;
#elif defined(__APPLE__) || \
    (defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 29)))
    return posix_spawn_file_actions_addchdir_np(actions, cwd) == 0;
#else
    (void)actions;
    (void)cwd;
    return false;
#endif
}

/* 单次 poll 的等待上限：在超时精度与 CPU 占用之间取平衡 */
constexpr int kPollSliceMs = 50;
/* 一次读取的块大小 */
constexpr size_t kReadChunk = 64 * 1024;
/* 单个流的输出上限：防止某个 CLI 疯狂输出吃光内存（16MB 足够放下 react 的 packument） */
constexpr size_t kMaxStreamBytes = 16u * 1024u * 1024u;

/* posix_spawn 会读取全局 environ，因此这一步做全局串行化，绝不引入竞态。 */
std::mutex g_spawn_mutex;

long long now_ms() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<long long>(ts.tv_sec) * 1000LL + ts.tv_nsec / 1000000LL;
}

void set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags >= 0) fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

void close_fd(int &fd) {
    if (fd >= 0) {
        ::close(fd);
        fd = -1;
    }
}

std::vector<std::string> build_env(
    const std::vector<std::pair<std::string, std::string>> &overrides) {
    std::map<std::string, std::string> merged;
    for (char **e = environ; e != nullptr && *e != nullptr; ++e) {
        const char *eq = std::strchr(*e, '=');
        if (eq == nullptr) continue;
        merged[std::string(*e, static_cast<size_t>(eq - *e))] = std::string(eq + 1);
    }
    for (const auto &kv : overrides) merged[kv.first] = kv.second;

    std::vector<std::string> out;
    out.reserve(merged.size());
    for (const auto &kv : merged) out.push_back(kv.first + "=" + kv.second);
    return out;
}

void cleanup_pipes(int *out_pipe, int *err_pipe, int *in_pipe, bool with_in) {
    close_fd(out_pipe[0]);
    close_fd(out_pipe[1]);
    close_fd(err_pipe[0]);
    close_fd(err_pipe[1]);
    if (with_in) {
        close_fd(in_pipe[0]);
        close_fd(in_pipe[1]);
    }
}

}  // namespace

int run_process(const ProcessSpec &spec, ProcessOutput &out, std::string &error) {
    out = ProcessOutput{};

    if (spec.program.empty()) {
        error = "program 为空";
        return -1;
    }

    /*
     * 关键：**posix_spawn 不做 PATH 查找**（那是 posix_spawnp 的行为）。
     * Rust 传过来的是裸命令名（"npm"、"git"、"curl"），直接交给 posix_spawn
     * 会得到 ENOENT —— 表现为"CLI 明明装了却报未安装"。
     *
     * 这里显式用本层的 which 解析成绝对路径，好处是：
     *   1. 与包源可用性探测走**同一套查找逻辑**，不会出现"探测说有、执行说没有"；
     *   2. 不依赖子进程环境里的 PATH（这个环境是我们自己拼的）；
     *   3. 与纯 Rust 执行器（std::process::Command 自带 PATH 查找）行为一致，
     *      两条路径可以互相替换。
     */
    std::string program = spec.program;
    if (program.find('/') == std::string::npos) {
        std::string resolved;
        if (!umango_which(program, resolved)) {
            error = "ENOENT: 找不到可执行文件 " + spec.program;
            return -2;
        }
        program = resolved;
    }

    int out_pipe[2] = {-1, -1};
    int err_pipe[2] = {-1, -1};
    int in_pipe[2] = {-1, -1};
    const bool with_in = spec.has_stdin;

    if (pipe(out_pipe) != 0) {
        error = std::string("创建 stdout 管道失败: ") + std::strerror(errno);
        return -1;
    }
    if (pipe(err_pipe) != 0) {
        cleanup_pipes(out_pipe, err_pipe, in_pipe, false);
        error = std::string("创建 stderr 管道失败: ") + std::strerror(errno);
        return -1;
    }
    if (with_in && pipe(in_pipe) != 0) {
        cleanup_pipes(out_pipe, err_pipe, in_pipe, false);
        error = std::string("创建 stdin 管道失败: ") + std::strerror(errno);
        return -1;
    }

    const long long started = now_ms();
    pid_t pid = -1;
    int spawn_rc = 0;

    {
        std::lock_guard<std::mutex> guard(g_spawn_mutex);

        std::vector<std::string> env_storage = build_env(spec.env);

        std::vector<char *> argv;
        argv.reserve(spec.args.size() + 2);
        argv.push_back(const_cast<char *>(program.c_str()));
        for (const auto &a : spec.args) argv.push_back(const_cast<char *>(a.c_str()));
        argv.push_back(nullptr);

        std::vector<char *> envp;
        envp.reserve(env_storage.size() + 1);
        for (auto &s : env_storage) envp.push_back(const_cast<char *>(s.c_str()));
        envp.push_back(nullptr);

        posix_spawn_file_actions_t actions;
        posix_spawn_file_actions_init(&actions);

        /* 子进程侧接线：标准流 -> 管道，并关掉不需要的 fd */
        posix_spawn_file_actions_adddup2(&actions, out_pipe[1], STDOUT_FILENO);
        posix_spawn_file_actions_adddup2(&actions, err_pipe[1], STDERR_FILENO);
        if (with_in) {
            posix_spawn_file_actions_adddup2(&actions, in_pipe[0], STDIN_FILENO);
        } else {
            posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0);
        }
        posix_spawn_file_actions_addclose(&actions, out_pipe[0]);
        posix_spawn_file_actions_addclose(&actions, out_pipe[1]);
        posix_spawn_file_actions_addclose(&actions, err_pipe[0]);
        posix_spawn_file_actions_addclose(&actions, err_pipe[1]);
        if (with_in) {
            posix_spawn_file_actions_addclose(&actions, in_pipe[0]);
            posix_spawn_file_actions_addclose(&actions, in_pipe[1]);
        }

        bool cwd_ok = true;
        if (!spec.cwd.empty()) {
            cwd_ok = add_chdir_action(&actions, spec.cwd.c_str());
        }

        if (!cwd_ok) {
            posix_spawn_file_actions_destroy(&actions);
            cleanup_pipes(out_pipe, err_pipe, in_pipe, with_in);
            error = "当前平台不支持指定工作目录（缺少 posix_spawn_file_actions_addchdir_np）";
            return -1;
        }

        spawn_rc = posix_spawn(&pid, program.c_str(), &actions, nullptr, argv.data(),
                               envp.data());
        posix_spawn_file_actions_destroy(&actions);
    }

    /* 父进程侧先关掉写端，否则永远读不到 EOF */
    close_fd(out_pipe[1]);
    close_fd(err_pipe[1]);
    if (with_in) close_fd(in_pipe[0]);

    if (spawn_rc != 0) {
        cleanup_pipes(out_pipe, err_pipe, in_pipe, with_in);
        if (spawn_rc == ENOENT) {
            /* 用 "ENOENT" 措辞让 Rust 侧映射成 cli_missing 错误码 */
            error = "ENOENT: 找不到可执行文件 " + spec.program;
            return -2;
        }
        error = std::string("posix_spawn 失败: ") + std::strerror(spawn_rc);
        return -1;
    }

    /* 写入 stdin 后关闭，让子进程读到 EOF */
    if (with_in) {
        const char *data = spec.stdin_data.data();
        size_t remaining = spec.stdin_data.size();
        while (remaining > 0) {
            ssize_t n = ::write(in_pipe[1], data, remaining);
            if (n > 0) {
                data += n;
                remaining -= static_cast<size_t>(n);
            } else if (errno == EINTR) {
                continue;
            } else {
                break; /* 子进程提前退出导致写失败是正常的 */
            }
        }
        close_fd(in_pipe[1]);
    }

    set_nonblocking(out_pipe[0]);
    set_nonblocking(err_pipe[0]);

    const long long deadline = started + static_cast<long long>(spec.timeout_ms);
    bool open_out = true;
    bool open_err = true;
    bool timed_out = false;
    char buf[kReadChunk];

    while (open_out || open_err) {
        if (!timed_out && now_ms() >= deadline) {
            timed_out = true;
            kill(pid, SIGKILL); /* 超时必须真的杀掉 */
        }

        struct pollfd fds[2];
        fds[0].fd = open_out ? out_pipe[0] : -1;
        fds[0].events = POLLIN;
        fds[1].fd = open_err ? err_pipe[0] : -1;
        fds[1].events = POLLIN;
        fds[0].revents = fds[1].revents = 0;

        int poll_rc = poll(fds, 2, timed_out ? 0 : kPollSliceMs);
        if (poll_rc < 0 && errno != EINTR) break;

        bool progressed = false;
        for (int i = 0; i < 2; ++i) {
            const bool is_out = (i == 0);
            bool &open = is_out ? open_out : open_err;
            if (!open) continue;
            if (poll_rc > 0 && !(fds[i].revents & (POLLIN | POLLHUP | POLLERR))) continue;

            ssize_t n = ::read(fds[i].fd, buf, sizeof(buf));
            if (n > 0) {
                progressed = true;
                std::string &sink = is_out ? out.stdout_text : out.stderr_text;
                /* 超过上限就继续读但丢弃，避免子进程被管道写满而阻塞 */
                if (sink.size() < kMaxStreamBytes) sink.append(buf, static_cast<size_t>(n));
            } else if (n == 0) {
                open = false;
            } else if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
                open = false;
            } else {
                progressed = true; /* EAGAIN 也算"活着"，继续等 */
            }
        }

        /* 已超时且这一轮没有任何进展：管道不会再给我们数据了，收工 */
        if (timed_out && !progressed) break;
    }

    close_fd(out_pipe[0]);
    close_fd(err_pipe[0]);

    int status = 0;
    while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {
        /* 重试 */
    }

    out.status = -1;
    if (WIFEXITED(status)) {
        out.status = WEXITSTATUS(status);
    } else if (WIFSIGNALED(status)) {
        /* 被信号终止：按 shell 习惯用 128+signal 表示 */
        out.status = 128 + WTERMSIG(status);
        if (timed_out) {
            out.stderr_text += "\n[umango] 进程因超时被强制终止（SIGKILL）";
        }
    }
    out.timed_out = timed_out;
    out.duration_ms = static_cast<uint64_t>(now_ms() - started);
    return 0;
}

}  // namespace umango

#endif /* !_WIN32 */
