# 开发路线图与验证策略

## 阶段划分

### 阶段1 —— 三层架构骨架与只读能力 ✅ 已完成

- 三层架构打通（Flutter ⇄ C++ ⇄ Rust），C ABI 稳定，ABI 版本校验
- 空 UI 框架：侧边栏导航 + 主页 / 下载 / 设置 / 关于
- 全局动画配置系统（纵向入场 / 向右滑出，可切换、可预览）
- 包源列表（6 个源）+ 平台能力门控 + 24 小时可用性缓存保护
- 搜索、版本列表（通道区分）、依赖树解析与可视化
- 下载量：官方优先 → GitHub Stars 兜底（两条链路均实测通过）

### 阶段2 —— 适配器深化

优先补齐**能在开发机验证**的部分（macOS 可实测 npm / pnpm / git / Homebrew）：

- [ ] git 源在可联网环境下验证 `ls-remote` 分支；支持非 GitHub 主机
- [ ] npm 源补充 `dist-tags` 多通道展示（beta / next / canary）
- [ ] Homebrew 的 cask 支持
- [ ] 适配器级错误分类细化（区分"包不存在"与"源不可达"）
- [ ] Windows 真机验证 winget / scoop 适配器

### 阶段3 —— 安装 / 卸载 / 升级

- [ ] `packages.install` 方法：走 C++ `run_process` 流式输出
- [ ] 进度反馈（stdout 逐行推送到 UI）
- [ ] 已安装包检测与过期提示（`InstalledState` 模型已就绪）
- [ ] 卸载 / 升级
- [ ] 危险操作二次确认（沿用 `resolve_install` 的命令计划弹窗）

### 阶段4 —— 插件系统

- [ ] Cordis 风格插件内核：生命周期、钩子、事件总线、沙箱
- [ ] PCL-CE 插件格式兼容导入（解析模块复用 HMCL-CE / PCL-CE 代码，单独隔离）
- [ ] 插件扩展点：新增包源适配器、自定义 UI 面板

> ⚠️ **环境约束**：本开发环境 `github.com:443` 不可达，
> 取用 HMCL-CE / PCL-CE 源码需要镜像或离线包。

### 阶段5 —— 全量测试与兜底完善

- [ ] 网络异常 / 源不可用 / CLI 缺失的系统性覆盖
- [ ] 依赖树冲突场景测试（多父节点要求不兼容版本）
- [ ] 长列表与深依赖树的性能验证

## 验证策略（本项目的一条硬规矩）

**不写无法证伪的代码。** 每个能力都要能指出"怎么验证它是对的"：

| 层次 | 验证方式 | 当前用例数 |
|------|----------|-----------|
| Rust 业务逻辑 | 单元测试（含真实 CLI/网络的测试，离线时打印原因跳过） | 102 |
| C++ ↔ Rust 集成 | `native/tests/umango_bridge_smoke.cpp` 端到端自检 | 全部检查 |
| Dart ↔ C ABI 契约 | `ui/test/bridge_ffi_test.dart`（直连真实 dylib，秒级） | 15 |
| Dart 数据模型契约 | `ui/test/models_test.dart`（用真实抓取的 JSON 断言） | 17 |
| 动画配置逻辑 | `ui/test/animation_config_test.dart` | 19 |
| 核心层 JSON 契约 | `ui/integration_test/logic_test.dart`（用真实抓取的 JSON 做 assertions） | 20 |
| Dart ↔ C ABI（应用内） | `ui/integration_test/bridge_test.dart` | 15 |
| 界面行为 | `ui/integration_test/app_test.dart`（在真实 macOS 应用里驱动界面） | 14 |
| 平台受限代码 | **显式标注未验证**，并在 UI 上呈现给用户 | — |

一条经验：**"调用成功"不等于"功能正常"**。
依赖树在单点失败时会降级返回 `ok=true`，只看 `ok` 会把"整棵树其实没解析出来"
当成通过（这个坑在开发中真实踩到过）。因此断言必须落在**业务语义**上：
存在 `state=resolved` 节点、`warnings` 为空、界面上出现了正确的文字。

### 关于 `flutter test` 挂住这件事（一次错误诊断的纠正）

**结论先说**：`flutter test` 现在是**正常工作**的。本机曾出现"flutter_tester
启动后静默挂住、不报错也不退出"的现象，我最初归因于 Flutter 工具链在
macOS 26 上的 bug，**这个判断是错的** —— 真实原因是当时**网络中断**。

排查过程与证据（保留下来，因为排除过程本身有价值）：

- 排除了工程路径因素：把工程复制到 `/tmp` 纯 ASCII 路径下，同样挂住；
- 排除了 Gatekeeper 隔离属性：清掉 `com.apple.quarantine` 后仍挂住；
- 排除了回环网络被禁：本地 TCP 自测正常，测试框架的 socket server 也确实起来了；
- verbose 日志显示 `Started flutter_tester process at pid N` 之后有
  **148589 ms 的完全空档**，直到被外部超时杀掉。

**网络恢复后重新验证**：`flutter test` 立刻正常工作，
51 项单元测试 23 秒跑完。所以当时的挂起是 flutter_tester 在启动阶段
发起了某种网络访问（DNS/HTTP）并被阻塞，而不是工具链本身的缺陷。

### 但集成测试依然保留，两层各有分工

| 层次 | 命令 | 耗时 | 用途 |
|------|------|------|------|
| Flutter 单元测试 | `flutter test` | ~23s | 开发中快速回归：模型契约、动画逻辑、FFI 直连 |
| Flutter 集成测试 | `flutter test integration_test -d macos` | ~4min | 真实应用里驱动界面，验证"界面真的能用" |

集成测试不只是补充，它抓出过两个**单元测试抓不到**的真实缺陷
（浮层遮挡导致按钮点不动、下载卡片兜底标注缺失）——
因为这两个问题只在真实渲染与真实布局下才暴露。所以两层都要留。

**踩坑提醒**：`flutter test integration_test`（目录形式）在桌面端只启动一次应用，
后续测试文件会报 "Unable to start the app on the device"，
因此 `scripts/build.sh` 里是逐个文件单独调用的。

### 关于"未验证"的处理原则

winget / scoop 只存在于 Windows，开发机是 macOS，无法验证。
处理方式**不是**含糊带过，而是：

1. 用平台门控保证它们在非 Windows 上根本不会被调用；
2. `SourceView.verification_note` 向 UI 暴露"未在真机验证"，
   界面上显示黄色实验标记；
3. 解析器写成宽容多形态，遇到不认识的结构抛出**带原始输出片段**的解析错误，
   这样 Windows 用户第一次运行就能直接把真实格式贴出来，而不是默默给错数据；
4. 单测用官方文档描述的样本覆盖解析分支，真机验证后替换为真实抓取结果。

同样的原则适用于 `native/src/process_windows.cpp`（CreateProcess 分支）。

### 自检程序为什么重要

`umango_bridge_smoke` 调用的是 **Dart 侧将来会调用的同一套 ABI**。
它跑通就等于"Flutter ↔ C++ ↔ Rust"链路已打通，不必等 Flutter 工程建好才发现问题。

其中最关键的一项断言是：

```
Rust 侧报告的进程执行器必须是 cpp-bridge
```

如果显示 `native`，说明宿主回调没有被 Rust 接受 ——
也就是说 C++ 进程调用层其实没参与工作，只是"看起来能用"。
这类问题不会抛异常、不会崩，只会悄悄退化，所以必须有断言盯着。

## 阶段1 遗留的已知问题

| 问题 | 影响 | 计划 |
|------|------|------|
| 动态库未正式集成进 Xcode 工程 | 需脚本复制 dylib | 阶段2 改为 Copy Files 阶段或 Flutter plugin |
| 动画配置仅内存态 | 重启恢复默认 | 阶段2 用 C++ 文件 IO 回调落盘 |
| 安装功能未执行 | 只生成命令计划 | 阶段3 |
| 无 CI | 测试需手动跑 `scripts/build.sh test` | 阶段5 接 CI（注意 flutter_tester 问题会影响 CI 方案） |
| `flutter analyze` 在本项目路径下崩溃 | 无法用 analyze 做静态检查 | analysis server 处理含空格+非 ASCII 路径的 bug；改用 `flutter build macos` 验证编译 |
| `flutter test` 不可用 | 普通单元测试跑不了 | 已改用 integration_test；若换机器可恢复 `ui/test/` |
