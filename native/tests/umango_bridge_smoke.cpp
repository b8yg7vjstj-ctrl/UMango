/*
 * umango_bridge_smoke.cpp —— 桥接层自检程序。
 *
 * 它调用的是 **Dart 侧将来会调用的同一套 ABI**（umb_bridge_*），
 * 因此这个程序跑通就等于"Flutter ↔ C++ ↔ Rust"这条链路已经打通，
 * 不必等到 Flutter 工程建好才发现问题。
 *
 * 检查项：
 *   1. ABI 版本一致 + 初始化成功；
 *   2. Rust 侧报告的进程执行器必须是 cpp-bridge（否则 C++ 进程层没被用上）；
 *   3. 经 C++ 回调真的执行了一条命令；
 *   4. 业务调用（core.info / sources.list）返回合法 JSON 且 ok=true；
 *   5. 每个包源的平台门控字段自洽 —— 这是"UI 自动隐藏不支持的包管理器"的依据。
 */

#include "umango_bridge.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace {

int g_failures = 0;

void check(bool condition, const std::string &what, const std::string &detail = "") {
    if (condition) {
        std::printf("  \033[32m✓\033[0m %s\n", what.c_str());
    } else {
        ++g_failures;
        std::printf("  \033[31m✗\033[0m %s%s%s\n", what.c_str(), detail.empty() ? "" : " —— ",
                    detail.c_str());
    }
}

/* 极简 JSON 取值：自检只需要判断字段存在与字符串/布尔值，不引入依赖 */
bool json_has(const std::string &json, const std::string &needle) {
    return json.find(needle) != std::string::npos;
}

std::string json_string_field(const std::string &json, const std::string &key) {
    std::string pat = "\"" + key + "\":\"";
    size_t pos = json.find(pat);
    if (pos == std::string::npos) return "";
    pos += pat.size();
    size_t end = json.find('"', pos);
    if (end == std::string::npos) return "";
    return json.substr(pos, end - pos);
}

/*
 * 在包源数组里取某个包源的 availability.state。
 * 做法：先定位该包源对象的起点，再在下一个包源对象之前找 state 字段。
 * 这样避免"跨对象误匹配"—— 第一版就是在这里因为字段偏移算错而静默跳过了检查。
 */
std::string source_state(const std::string &sources_json, const std::string &kind) {
    const std::string marker = "\"kind\":\"" + kind + "\"";
    size_t pos = sources_json.find(marker);
    if (pos == std::string::npos) return "<包源不存在>";

    size_t next = sources_json.find("\"kind\":\"", pos + marker.size());
    size_t segment_end = (next == std::string::npos) ? sources_json.size() : next;
    std::string segment = sources_json.substr(pos, segment_end - pos);

    const std::string state_key = "\"state\":\"";
    size_t st = segment.find(state_key);
    if (st == std::string::npos) return "<无 state 字段>";
    st += state_key.size();
    size_t end = segment.find('"', st);
    if (end == std::string::npos) return "<state 未闭合>";
    return segment.substr(st, end - st);
}

}  // namespace

int main(void) {
    std::printf("\n=== UMango 桥接层自检（C++ ↔ Rust） ===\n\n");

    std::printf("[1] ABI 与初始化\n");
    unsigned int bridge_abi = umb_bridge_abi_version();
    check(bridge_abi == 1, "桥接层 ABI 版本为 1",
          "实际 " + std::to_string(bridge_abi));

    char *init_err = nullptr;
    int init_rc = umb_bridge_init(&init_err);
    if (init_rc != 0) {
        check(false, "umb_bridge_init 成功",
              init_err != nullptr ? init_err : ("返回码 " + std::to_string(init_rc)));
        if (init_err != nullptr) umb_bridge_free(init_err);
        std::printf("\n初始化失败，后续检查无法进行。\n");
        return 1;
    }
    check(true, "umb_bridge_init 成功");
    if (init_err != nullptr) umb_bridge_free(init_err);

    std::printf("\n[2] Rust 核心层信息（经 C++ 转发）\n");
    char *info = umb_bridge_call("core.info", nullptr);
    check(info != nullptr, "core.info 返回非空");
    std::string info_str = info != nullptr ? info : "";
    if (info != nullptr) umb_bridge_free(info);

    check(json_has(info_str, "\"ok\":true"), "core.info 响应 ok=true");
    std::string app = json_string_field(info_str, "app");
    check(app == "UMango", "应用名为 UMango", "实际 " + app);

    // 关键指标：进程执行器必须是 cpp-bridge
    std::string runner = umb_bridge_runner();
    check(runner == "cpp-bridge",
          "Rust 侧正在使用 C++ 进程执行器（cpp-bridge）",
          "实际为 " + runner + "，说明宿主回调没有被 Rust 接受");
    check(json_string_field(info_str, "process_runner") == "cpp-bridge",
          "core.info 也报告 process_runner=cpp-bridge");

    std::string platform = umb_bridge_platform();
    check(!platform.empty(), "平台标识非空", platform);

    std::printf("\n[3] 桥接层自检（真实进程调用 + which）\n");
    char *self = umb_bridge_self_test();
    std::string self_str = self != nullptr ? self : "";
    if (self != nullptr) umb_bridge_free(self);
    check(json_has(self_str, "\"ok\":true"), "自检返回 ok=true");

    std::string echo_out = json_string_field(self_str, "echo_stdout");
    check(echo_out.find("umango-bridge-self-test") != std::string::npos,
          "经 C++ posix_spawn 执行 /bin/echo 并拿到输出",
          "实际输出: [" + echo_out + "]");
    check(json_string_field(self_str, "which_sh_ok") == "true" ||
              json_has(self_str, "\"which_sh_ok\":true"),
          "which 能在 PATH 中找到 sh");

    std::printf("\n[4] 业务调用：包源列表（含平台门控）\n");
    char *sources = umb_bridge_call("sources.list", nullptr);
    std::string sources_str = sources != nullptr ? sources : "";
    if (sources != nullptr) umb_bridge_free(sources);

    check(json_has(sources_str, "\"ok\":true"), "sources.list 响应 ok=true");
    for (const char *kind : {"npm", "pnpm", "git", "homebrew", "winget", "scoop"}) {
        check(json_has(sources_str, std::string("\"kind\":\"") + kind + "\""),
              std::string("包含包源 ") + kind);
    }
    // 平台门控：macOS 上 winget/scoop 必须是 supported_here=false
    const bool macos = (platform == "macos");
    if (macos) {
        check(json_has(sources_str, "\"kind\":\"winget\",\"display_name\":\"WinGet\"") ||
                  json_has(sources_str, "\"display_name\":\"WinGet\""),
              "winget 出现在列表中（供 UI 判断并隐藏）");
        // 逐个检查 winget / scoop 的 supported_here 为 false
        size_t winget_pos = sources_str.find("\"kind\":\"winget\"");
        size_t scoop_pos = sources_str.find("\"kind\":\"scoop\"");
        auto supported_here_false_within = [&](size_t pos) {
            if (pos == std::string::npos) return false;
            size_t sup = sources_str.find("\"supported_here\":", pos);
            if (sup == std::string::npos) return false;
            return sources_str.compare(sup + 17, 5, "false") == 0;
        };
        check(supported_here_false_within(winget_pos), "macOS 上 winget 标记为当前平台不支持");
        check(supported_here_false_within(scoop_pos), "macOS 上 scoop 标记为当前平台不支持");
    }

    std::printf("\n[5] 错误路径：未知方法应返回结构化错误而不是崩溃\n");
    char *bad = umb_bridge_call("no.such.method", nullptr);
    std::string bad_str = bad != nullptr ? bad : "";
    if (bad != nullptr) umb_bridge_free(bad);
    check(json_has(bad_str, "\"ok\":false"), "未知方法返回 ok=false");
    check(json_has(bad_str, "\"code\":"), "错误信封带 code 字段");

    /*
     * [6] 最深的一条链路：真实业务查询。
     *
     * `packages.versions` 会走：Rust 适配器 -> 经 C++ posix_spawn 执行
     * `npm view <pkg> --json` -> C++ 回收输出并转 JSON -> Rust 解析 packument
     * -> 序列化回 JSON -> C++ 转交。
     * 前面几项只证明"能跑 echo"，这一项才证明真实业务真的通了。
     */
    std::printf("\n[6] 真实业务链路：npm 版本列表（经 C++ 执行 npm view）\n");
    const std::string npm_state = source_state(sources_str, "npm");
    std::printf("  npm 可用性: %s\n", npm_state.c_str());
    if (npm_state != "available" && npm_state != "degraded") {
        check(false, "npm 应在本机可用（否则这条链路没被真正验证）",
              "实际状态 " + npm_state);
    } else {
        char *versions = umb_bridge_call(
            "packages.versions", "{\"source\":\"npm\",\"name\":\"is-number\"}");
        std::string versions_str = versions != nullptr ? versions : "";
        if (versions != nullptr) umb_bridge_free(versions);

        check(json_has(versions_str, "\"ok\":true"), "packages.versions 响应 ok=true",
              versions_str.substr(0, 200));
        check(versions_str.find("\"version\":") != std::string::npos,
              "返回了具体版本号");
        check(versions_str.find("\"channel\":") != std::string::npos,
              "版本带通道字段（正式版/测试版/快照）");

        /*
         * 依赖树：走核心层通用递归器 + node-semver 范围解析。
         *
         * 注意断言强度：依赖树在单个依赖解析失败时**会降级返回 ok=true**，
         * 只检查 ok 会漏掉"整棵树其实没解析出来"的情况（第一版就漏了，
         * 因为底层 CLI 根本没跑起来）。所以这里必须检查：
         *   - 真有 resolved 节点；
         *   - warnings 为空（没有发生降级）。
         */
        char *deps = umb_bridge_call(
            "packages.dependencies",
            "{\"source\":\"npm\",\"name\":\"is-odd\",\"max_depth\":2}");
        std::string deps_str = deps != nullptr ? deps : "";
        if (deps != nullptr) umb_bridge_free(deps);
        check(json_has(deps_str, "\"ok\":true"), "packages.dependencies 响应 ok=true",
              deps_str.substr(0, 200));
        check(json_has(deps_str, "\"summary\":"), "依赖树带 summary 统计");
        check(json_has(deps_str, "\"state\":\"resolved\""),
              "依赖树真的解析出了节点（不是降级结果）",
              deps_str.substr(0, 300));
        check(json_has(deps_str, "\"warnings\":[]"),
              "依赖树没有降级警告", deps_str.substr(0, 300));

        // 下载量链路：npm 官方下载量
        char *stats = umb_bridge_call(
            "packages.downloads",
            "{\"source\":\"npm\",\"name\":\"is-number\"}");
        std::string stats_str = stats != nullptr ? stats : "";
        if (stats != nullptr) umb_bridge_free(stats);
        check(json_has(stats_str, "\"ok\":true"), "packages.downloads 响应 ok=true");
        check(json_has(stats_str, "\"kind\":\"official\""),
              "下载量走的是官方数据（非兜底）", stats_str.substr(0, 200));

        // 兜底链路：pnpm 没有官方下载量接口，应自动退到 GitHub Stars
        char *pnpm_stats = umb_bridge_call(
            "packages.downloads",
            "{\"source\":\"pnpm\",\"name\":\"is-number\","
            "\"repository\":\"git+https://github.com/jonschlinkert/is-number.git\"}");
        std::string pnpm_str = pnpm_stats != nullptr ? pnpm_stats : "";
        if (pnpm_stats != nullptr) umb_bridge_free(pnpm_stats);
        check(json_has(pnpm_str, "\"kind\":\"github_stars\""),
              "pnpm 下载量自动兜底为 GitHub Stars", pnpm_str.substr(0, 220));
    }

    std::printf("\n");
    if (g_failures == 0) {
        std::printf("\033[32m全部检查通过：Flutter → C++ → Rust 链路已打通。\033[0m\n\n");
        return 0;
    }
    std::printf("\033[31m有 %d 项检查失败。\033[0m\n\n", g_failures);
    return 1;
}
