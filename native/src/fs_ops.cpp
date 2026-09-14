/*
 * fs_ops.cpp —— 文件读写与可执行文件查找。
 *
 * 对应项目分层里" C++ 负责本地文件读写、系统调用"。
 * Rust 侧需要知道的只有"能不能做"，具体怎么做留在这里。
 */

#include "umango_internal.h"

#include <sys/stat.h>
#include <sys/types.h>
#include <sys/utsname.h>
#include <unistd.h>
#include <cerrno>
#include <cstdlib>
#include <fcntl.h>

namespace umango {

namespace {

bool is_executable_file(const std::string &path) {
    struct stat st;
    if (stat(path.c_str(), &st) != 0) return false;
    if (!S_ISREG(st.st_mode)) return false;
    return access(path.c_str(), X_OK) == 0;
}

/* 递归创建父目录（等价于 mkdir -p $(dirname path)） */
bool ensure_parent_dir(const std::string &path, std::string &error) {
    size_t slash = path.rfind('/');
    if (slash == std::string::npos || slash == 0) return true;  /* 当前目录或根目录 */
    std::string dir = path.substr(0, slash);

    std::string built;
    size_t start = 0;
    if (!dir.empty() && dir[0] == '/') {
        built = "/";
        start = 1;
    }
    while (start <= dir.size()) {
        size_t next = dir.find('/', start);
        std::string part = dir.substr(start, next == std::string::npos ? std::string::npos
                                                                      : next - start);
        if (!part.empty()) {
            if (built.empty() || built.back() != '/') built += '/';
            built += part;
            if (mkdir(built.c_str(), 0755) != 0 && errno != EEXIST) {
                error = "创建目录失败 " + built + ": " + std::strerror(errno);
                return false;
            }
        }
        if (next == std::string::npos) break;
        start = next + 1;
    }
    return true;
}

}  // namespace

bool umango_which(const std::string &program, std::string &out_path) {
    if (program.empty()) return false;

    /* 已经带路径分隔符：直接判定，不走 PATH */
    if (program.find('/') != std::string::npos) {
        if (is_executable_file(program)) {
            out_path = program;
            return true;
        }
        return false;
    }

    const char *path_env = std::getenv("PATH");
    if (path_env == nullptr) return false;

    std::string path(path_env);
    size_t start = 0;
    while (start <= path.size()) {
        size_t next = path.find(':', start);
        std::string dir = path.substr(start, next == std::string::npos ? std::string::npos
                                                                      : next - start);
        if (!dir.empty()) {
            std::string candidate = dir + "/" + program;
            if (is_executable_file(candidate)) {
                out_path = candidate;
                return true;
            }
        }
        if (next == std::string::npos) break;
        start = next + 1;
    }
    return false;
}

bool fs_read_file(const std::string &path, std::string &out_data, std::string &error) {
    FILE *f = std::fopen(path.c_str(), "rb");
    if (f == nullptr) {
        error = "打开文件失败 " + path + ": " + std::strerror(errno);
        return false;
    }
    out_data.clear();
    char buf[65536];
    size_t n = 0;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) {
        out_data.append(buf, n);
    }
    bool failed = std::ferror(f) != 0;
    std::fclose(f);
    if (failed) {
        error = "读取文件失败 " + path;
        return false;
    }
    return true;
}

bool fs_write_file(const std::string &path, const unsigned char *data, size_t len,
                   std::string &error) {
    if (!ensure_parent_dir(path, error)) return false;

    /* 先写临时文件再原子改名：避免写到一半崩溃留下半个配置文件 */
    std::string tmp = path + ".umango-tmp";
    FILE *f = std::fopen(tmp.c_str(), "wb");
    if (f == nullptr) {
        error = "创建文件失败 " + tmp + ": " + std::strerror(errno);
        return false;
    }
    if (len > 0 && std::fwrite(data, 1, len, f) != len) {
        error = "写入文件失败 " + tmp + ": " + std::strerror(errno);
        std::fclose(f);
        std::remove(tmp.c_str());
        return false;
    }
    if (std::fflush(f) != 0) {
        error = "刷新文件缓冲失败 " + tmp;
        std::fclose(f);
        std::remove(tmp.c_str());
        return false;
    }
    std::fclose(f);

    if (std::rename(tmp.c_str(), path.c_str()) != 0) {
        error = "替换文件失败 " + path + ": " + std::strerror(errno);
        std::remove(tmp.c_str());
        return false;
    }
    return true;
}

std::string platform_info_json() {
    JsonWriter w;
    w.begin_object();

#if defined(_WIN32)
    w.member_string("os", "windows");
#elif defined(__APPLE__)
    w.member_string("os", "macos");
#else
    w.member_string("os", "linux");
#endif

    struct utsname u;
    if (uname(&u) == 0) {
        w.member_string("os_version", u.release);
        w.member_string("arch", u.machine);
        w.member_string("kernel", u.sysname);
    } else {
        w.member_string("os_version", "unknown");
        w.member_string("arch", "unknown");
        w.member_string("kernel", "unknown");
    }

    char host[256] = {0};
    if (gethostname(host, sizeof(host) - 1) == 0) {
        w.member_string("hostname", host);
    } else {
        w.member_string("hostname", "unknown");
    }

    w.member_int("pid", static_cast<long long>(getpid()));
    w.end_object();
    return w.take();
}

}  // namespace umango
