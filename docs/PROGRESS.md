# 进度快照

> # ⚠️ 本文档已作废
>
> 本文描述的是**已废弃的第一版（Flutter + Rust + C++ 三层架构）**，
> 该版本已在 commit `d01f071`（"技术路线切换：废弃 Flutter 版，改用 .NET 10 + WPF"）被取代。
>
> **当前技术栈是 .NET 10 + WPF**，请以以下文档为准：
> - [`README.md`](../README.md) —— 项目现状与开发方式
> - [`PCL_CE_FEATURE_PARITY.md`](PCL_CE_FEATURE_PARITY.md) —— 功能对照与完成度（逐项实测）
> - [`DESIGN_UI_DOTNET.md`](DESIGN_UI_DOTNET.md) —— 当前 UI 层规格
> - [`XAML_AUDIT.md`](XAML_AUDIT.md) —— XAML 静态审计工具
>
> 相关代码在 `ui/`、`core/`、`native/`，**不要在其上继续开发**。
> 本文保留仅作历史参考（其中的 PCL-CE 设计数值分析仍有参考价值）。


> 保存时间：阶段1 完成时。本文档记录**已实现什么、验证到什么程度、还差什么**，
> 便于下次接续时不必重新推导。

## 一、总体状态

**阶段1 目标已达成**：三层架构（Flutter + Rust + C++）打通，空 UI 框架可运行，
动画配置系统可切换，包源／版本／依赖树三项只读能力可用并已真机验证。

### 验证结果汇总

| 验证层 | 命令 | 结果 |
|--------|------|------|
| Rust 单元测试 | `cd core && cargo test` | **102 项通过**（core 44 + adapters 46 + ffi 12） |
| C++ ↔ Rust 端到端自检 | `scripts/build.sh smoke` | **全部检查通过** |
| Flutter 单元测试 | `scripts/build.sh flutter-test` | **51 项通过**（约 23 秒） |
| Flutter 集成测试（逻辑与契约） | `scripts/build.sh ui-test` | **20 项通过** |
| Flutter 集成测试（FFI，应用内） | 同上 | **15 项通过** |
| Flutter 集成测试（界面） | 同上 | **14 项通过** |
| 应用构建与运行 | `scripts/build.sh all` | 成功产出并启动 `umango.app` |

合计 **202 项自动化测试**（102 Rust + 51 Flutter 单测 + 49 Flutter 集成）+ 桥接层自检。
一条命令跑完：`scripts/build.sh test`。

### 界面测试实际断言了什么

这是"UI 能用"的直接证据，而不是"能编译"的推断：

- 启动后进入主界面，**不是**停留在加载页或错误页（错误路径是被优雅处理的，
  不会抛异常，因此必须断言屏幕上出现了正确内容）；
- 首页出现 `cpp-bridge`（该值由 Rust 经 C++ 报上来，证明三层真接通）；
- 包源列表隐藏被门控的源；打开开关后 6 个源全部可见且状态明确；
- 搜索真实包后渲染出卡片，含下载量角标与官方图标；
- 切换动画预设后 `AnimationScope.config` **真的改变**；
- 版本列表渲染出版本号与通道标签、通道筛选；
- 依赖树渲染出节点行、统计，且每个节点都有「直接安装」与「跳转详情」入口；
- 点击依赖节点确实压栈打开了该依赖的详情页；
- 兜底指标（GitHub Stars）在卡片上被明确标注。

应用启动日志（关键证据）：

```
[umango-core] 已挂载 C++ 宿主进程执行器（宿主：umango-cpp-bridge）
[umango-core] 已注册 6 个包源适配器: npm, pnpm, git, homebrew, winget, scoop
```

`process_runner = cpp-bridge` 这个值会被 UI 的「运行诊断」面板显示出来 ——
它是"进程调用真的经过 C++ 层"的判别依据，若显示 `native` 就说明宿主回调没生效。

## 二、已完成并验证的能力

### Rust 核心层（`core/crates/umango-core`）

- 领域模型：`PackageSourceKind`（6 个包源）、`DownloadStats`（官方 / Stars / 未知三态）、
  `ReleaseChannel`（正式版 / 测试版 / 快照 / 未知）、`DependencyNode/Status/Summary`、
  `SourceAvailability`、`SourceView`。
- 版本处理 `version.rs`：通道判定、宽松比较（兼容 git tag 等非 semver）、
  npm 范围匹配（复用 `node-semver`）、最优版本挑选。
- 依赖树 `adapter.rs`：通用递归器 —— 深度限制、节点预算、环检测、
  同包只查一次（缓存）、**单点失败降级为 `Unverified` 而不影响整棵树**。
- 注册表 `registry.rs`：平台门控 + **24 小时可用性缓存保护**
  （成功缓存 24h、失败退避 5min、刷新失败时降级沿用旧结果并标记 stale）。
- `api.rs`：传输无关的 JSON API，**永远返回合法信封**（`ok` / `error.code` / `advice` / `retryable`）。

### 适配器层（`core/crates/umango-adapters`）

| 适配器 | 搜索 | 版本列表 | 依赖解析 | 下载量 | 真机验证 |
|--------|------|----------|----------|--------|----------|
| npm | ✅ | ✅ | ✅ | 官方（api.npmjs.org，实测 react 周下载 1.28 亿） | ✅ |
| pnpm | ✅ | ✅ | ✅ | GitHub Stars 兜底（实测 284 stars） | ✅ |
| git | ✅（GitHub API） | ✅（ls-remote） | 不适用（能力位声明 false） | GitHub Stars | 部分（github.com 被墙） |
| homebrew | ✅ | ✅（仅 stable + versioned_formulae） | ✅ | 官方（formulae.brew.sh，实测 wget 30 天 11081） | ✅ |
| winget | ⚠️ | ⚠️ | ❌ | Stars 兜底 | ❌ 未验证（仅 Windows） |
| scoop | ⚠️ | ⚠️ | ✅ | Stars 兜底 | ❌ 未验证（仅 Windows） |

### C++ 桥接层（`native/`）

- 稳定 C ABI：`umb_bridge_init/call/free/core_version/platform/runner/self_test`。
- 向上回调表：`run_process` / `which` / `read_file` / `write_file` / `platform_info`。
- `process_posix.cpp`：`posix_spawn` + **双管道并发 poll 读取**（避免 stderr 写满导致死锁）
  + 超时 SIGKILL + 输出上限。
- `wire.cpp`：**长度前缀二进制分帧解码**，逐字段越界检查。
- `umango_internal.h`：JSON 写入器，含**非法 UTF-8 净化**（CLI 输出可能含非法字节，
  不净化会让 Rust 的 serde 解析失败）。

### Flutter 前端（`ui/`）

- 动画配置系统（`lib/animation/`）：4 套预设 + 列表方向 5 选项 + 弹窗方向 5 选项 +
  时长/延迟/幅度可调 + 实时预览 + 尊重系统"减少动态效果"。
- 页面：主页（能力矩阵）、下载（包源列表 + 搜索 + 卡片）、
  包详情（版本列表 + 依赖树）、设置（动画 + 包源检测 + 诊断）、关于。
- `lib/ffi/umango_bridge.dart`：dart:ffi 绑定，所有调用经 `Isolate.run` 避免卡 UI，
  动态库多路径探测 + 失败时给出可操作诊断页。

## 三、开发过程中发现并修复的真实问题

这些问题都是真机跑出来的，值得留档：

1. **`posix_spawn` 不做 PATH 查找**（不同于 `posix_spawnp`）。
   Rust 传的是裸命令名 `npm`，导致每次都报"CLI 未安装"。
   更隐蔽的是：依赖树查询**看起来成功了**（`ok=true`），因为树构建器会把单点失败
   降级成"未验证节点"并正常返回。→ 修复：C++ 侧先用 `which` 解析成绝对路径，
   与可用性探测走同一套查找逻辑。
   **测试教训**：只断言 `ok=true` 会漏掉"整棵树其实没解析出来"，
   必须同时断言存在 `state=resolved` 且 `warnings` 为空。
2. **24 小时缓存被绕过**：`source_view()` 内部会调用 `probe()`，
   导致每次刷新界面都真的执行一次 CLI，缓存形同虚设。
   → 修复：拆出不含探测的 `descriptor()`，可用性统一由注册表填充。
3. **`refresh` 清空缓存导致无法降级**：清空后丢失上一次成功结果，
   刷新失败时直接判死。→ 修复：改为把缓存标记过期而不是删除。
4. **版本比较器把 `nightly` 排在 `2.0.0-rc.1` 之前**：
   误把 semver 的"预发布段数字低于字母"规则用在了主版本段。
   → 修复：主版本段用"数字高于文本"，预发布段才遵循 semver。
5. **`thiserror` 把名为 `source` 的字段当成错误链的 source**：
   编译报 `as_dyn_error`。→ 修复：字段改名为 `source_name`。
6. **`node_semver::Version` 没有实现 `Ord`**：无法直接排序版本。
   → 自建比较器，并让 `PartialEq` 与 `Ord` 保持一致（否则 `v1.2 != 1.2`）。
7. **GitHub API 返回 301**：`api.github.com/repos/facebook/react` 会重定向。
   curl 不跟随就只拿到 `{"message":"Moved Permanently"}`。→ 修复：`-L` 跟随重定向。
8. **curl 在 HTTP 4xx/5xx 时退出码仍为 0**：不检查状态码会把错误体当数据解析。
   → 修复：用 `-w "%{http_code}"` 单独取状态码并校验。
9. **React 版本列表里上千个 `0.0.0-<哈希>`** 被误判成"测试版"。
   → 修复：主版本全零 + 无显式预发布标记 → 判为快照。
10. **serde 把 `GitHubStars` 序列化成 `git_hub_stars`**：会变成难看的前端契约。
    → 修复：显式 `#[serde(rename = "github_stars")]` 钉死。
11. **浮层遮挡导致「搜索」按钮点不动**（由界面集成测试抓出）：
    主题切换按钮用 `Positioned(right: 14, top: 10)` 浮在内容区右上角，
    正好压住了下载页的「搜索」按钮，点击落到浮层上。
    hit-test 报告 `derived an Offset ... that would not hit test on the specified widget`。
    这类"浮层遮挡"问题在视觉上完全看不出来（按钮看着好好的），
    只有 hit-test 能暴露。→ 修复：把主题切换移进侧边栏底部，取消浮层。
12. **下载卡片上的兜底指标没有标注**（由界面集成测试抓出）：
    `DownloadBadge` 的"兜底"标记只在非紧凑模式下渲染，
    而卡片用的是紧凑模式 —— 结果是 GitHub Stars 在卡片上看起来就像下载量，
    直接违反了"下载量优先、Stars 兜底"必须可区分的要求。
    → 修复：任何尺寸下都标注，并在紧凑模式补上短指标名（Stars）。
13. **误判：把 `flutter test` 挂住归因于工具链缺陷**。
    现象是 flutter_tester 启动后静默挂住 148 秒以上、不报错也不退出。
    我做了不少排除（换 ASCII 路径、清 quarantine、回环 TCP 自测）后
    错误地下了"flutter_tester 在 macOS 26 上不可用"的结论，
    并且**据此删除了 `ui/test/` 目录** —— 这是本次开发中最值得反省的一步：
    在证据不足时下了过强的结论，还顺手砍掉了正常的测试路径。
    真实原因是当时的**网络中断**：网络恢复后 `flutter test` 立刻正常
    （51 项单测 23 秒通过）。
    教训：给"不可用"下结论前，先确认**环境**因素（网络/DNS）已被排除 ——
    我排除了路径、权限、loopback，却漏了最容易出问题的外网访问。
    集成测试仍然保留，因为它的验证强度确实更高（抓出过第 11、12 两个缺陷）。

### 测试自身的三个教训

写测试时踩的坑同样值得记录（都是"测试写错了，不是代码错了"）：

- **只断言 `ok=true` 会漏掉整棵树的降级**：依赖树在单点失败时正常返回，
  必须同时断言存在 `state=resolved` 且 `warnings` 为空。
- **`find.byType` 默认跳过 offstage**：页面跳转后旧页面变 offstage，
  用"页面数量 ≥ 2"判断是否压栈必然失败；应断言当前可见页面的具体属性。
- **不要在无意义的量上做符号比较**：纵向入场的 `dx` 恒为 0，
  拿它和横向风格的 `dx` 比符号，期望值是 `-0.0` 而实际是 `-1.0`。

## 四、环境约束（会影响后续开发）

- **网络已恢复**：`github.com` / `api.github.com` / `codeload` / `raw.githubusercontent`
  现均可访问，`git ls-remote` 实测成功，git 适配器的 tag 读取链路已补上真机测试。
  注意 `objects.githubusercontent.com` 仍不可达（大文件下载可能受影响）。
- cargo 走清华 TUNA 镜像，npm 走 npmmirror，pub.dev 直连正常。
- 项目位于 OneDrive 同步目录，构建产物已重定向到 `~/Library/Caches/umango-build/`。
- **`flutter analyze` 在本项目路径下会崩溃**（analysis server 的 LSP 消息
  无法处理含空格与非 ASCII 的路径 → `FormatException`）。
  这不是代码问题，验证编译请改用 `flutter build macos`。
- **`flutter test` 可用**（曾因网络中断而静默挂住，易被误判为工具链故障；
  遇挂起先查网络）。Flutter 测试分两层：`ui/test/`（单测，秒级）与
  `ui/integration_test/`（真实应用集成测试）。
- **⚠️ OneDrive 会改挂载目录名**：开发过程中 `OneDrive-个人` 被 OneDrive 客户端
  改名为 `OneDrivePersonal`，项目路径随之变化。
  因此脚本一律用相对路径（`scripts/env.sh` 基于 `BASH_SOURCE` 推导项目根），
  不要在文档或脚本里写死绝对路径。

## 五、下一步（阶段2 建议起点）

1. **安装执行**（阶段3 提前做也可）：`api.rs` 的 `resolve_install` 已能生成命令计划，
   补一个 `packages.install` 走 C++ `run_process` 流式输出 + UI 进度反馈。
2. **补齐 git 源**：当前 github.com 不可达，可在有网络的环境验证 `ls-remote` 分支；
   另可支持非 GitHub 主机。
3. **把动态库正式集成进 Xcode 工程**（Copy Files 阶段或做成 Flutter plugin），
   替换掉当前的脚本复制方案。
4. **动画配置持久化**：当前仅内存态，重启即恢复默认。
   可用已实现的 C++ `write_file` / `read_file` 回调落盘到用户配置目录。
5. **Windows 真机验证 winget / scoop 适配器**：样本已在
   `native/../core/crates/umango-adapters/src/windows.rs` 的单测里，
   验证后应替换为真实抓取的输出。
