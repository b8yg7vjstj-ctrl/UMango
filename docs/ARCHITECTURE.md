# 架构说明

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


## 一、三层划分与职责边界

```
┌──────────────────────────────────────────────────────────────┐
│ Flutter 前端层（ui/）                                         │
│  页面渲染 · 动画引擎 · 交互逻辑 · 全局动画配置模块             │
└───────────────────────────┬──────────────────────────────────┘
                            │ dart:ffi（C ABI，字符串)
┌───────────────────────────▼──────────────────────────────────┐
│ C++ 桥接层（native/）                                         │
│  进程创建（posix_spawn / CreateProcess）· 本地文件读写 ·       │
│  系统信息 · ABI 版本校验 · JSON 写入与 UTF-8 净化              │
└───────────────────────────┬──────────────────────────────────┘
                            │ 静态链接 libumango_ffi.a
┌───────────────────────────▼──────────────────────────────────┐
│ Rust 核心业务层（core/）                                      │
│  领域模型 · 包源适配器 · 版本解析 · 依赖树计算 ·              │
│  可用性缓存 · JSON API 分发 · （阶段4）插件调度                │
└──────────────────────────────────────────────────────────────┘
```

**职责边界的关键约定**：Rust 层**不直接创建进程**，也不做文件 IO。
它通过 `ProcessRunner` trait 描述"要跑什么命令"，由 C++ 层真正执行。
纯 Rust 实现（`NativeProcessRunner`）只用于单元测试与开发，
发布路径上一定走 `HostProcessRunner`（C++ 回调）。

这样的好处是：核心业务逻辑（版本解析、依赖树）可以在没有 GUI、
没有 C++ 的情况下完整单测。

## 二、两个方向的调用链

### 向下：业务调用

```
Dart  UmangoBridge.invoke('packages.versions', {...})
  → C   umb_bridge_call(method, args_json)
  → R   umango_call()  →  catch_unwind 包裹
  → R   api::dispatch()  按方法名分发
  → R   AdapterRegistry  →  平台门控 + 可用性缓存  →  NpmAdapter
  → R   需要执行命令 → HostProcessRunner  →  长度前缀二进制分帧
  → C   host_run_process()  →  解码 →  posix_spawn  →  回收输出
  → R   解析 CLI 输出 → 序列化 JSON 信封
  → C   转交（复制一份并释放 Rust 的那份内存）
  → D   jsonDecode → 类型化模型 / UmangoError
```

### 向上：进程执行回调

Rust 通过 `umango_init` 接收一张函数指针表（`umango_host_vtable`），
需要跑命令时回调 C++。字段顺序必须与 Rust 侧结构体逐字节一致，
且 `UMANGO_ABI_VERSION` 必须匹配，否则 `umango_init` 直接拒绝初始化
（宁可启动失败，也不要让两个不匹配的二进制悄悄跑起来出错数据）。

## 三、为什么用二进制分帧而不是 JSON 传进程请求

进程请求由 Rust 生成、C++ 消费。若用 JSON，就需要在 C++ 里**手写一个 JSON 解析器**，
而命令行参数里可能出现引号、反斜杠、换行、NUL、任意 Unicode ——
这是最容易写出静默 bug 的地方（转义少一个字符就出错数据）。

因此请求方向改用**长度前缀二进制分帧**（见 `core/crates/umango-ffi/src/wire.rs`）：

```
magic "UMS1" | version u32 | program(len+bytes) | arg_count u32 | args…
| cwd_present u8 [cwd] | timeout_ms u64 | env_count u32 | (k,v)…
| stdin_present u8 [stdin]
```

优点：零转义、任何字节序列都能安全传输、每个字段都能做严格越界检查、
带 magic 与版本号可立刻发现两端不一致。

反方向（C++ → Rust 返回 `ProcessOutput`）是**写入** JSON，风险低得多，
用一个带 UTF-8 净化的转义器就够了 —— 但仍然必须净化非法字节，
因为 CLI 输出受 locale 影响可能含非法 UTF-8，会让 serde 直接解析失败。

## 四、错误处理契约

Rust 侧 `CoreError` 每个变体都有：

- `code()` —— 稳定的机器可读错误码（如 `cli_missing`、`unsupported_platform`）
- `advice()` —— 给 UI 的兜底建议（`Retry` / `InstallCli` / `SwitchSource` /
  `ShowCached` / `CheckNetwork` / `HideEntry` / `Manual`）
- `is_retryable()`

跨 FFI 时统一折叠成信封，**任何情况下都是合法 JSON**：

```json
{"ok": true, "data": …}
{"ok": false, "error": {"code": "cli_missing", "message": "…",
                        "advice": "install_cli", "retryable": false}}
```

Rust 侧所有导出函数都包在 `catch_unwind` 里 —— panic 会变成错误信封，
绝不允许 unwind 穿越 FFI 边界（那会直接终结宿主进程）。

## 五、内存所有权规则

这是跨语言最容易出错的地方，规则写死在代码注释里：

| 缓冲区 | 分配方 | 释放方 |
|--------|--------|--------|
| `umango_call` 返回的字符串 | Rust | Rust（`umango_free_string`），C++ 转交后立刻释放 |
| `umb_bridge_call` 返回的字符串 | C++（`strdup`） | Dart 调 `umb_bridge_free`（内部 `free`） |
| 宿主回调返回的缓冲区 | C++ | Rust 通过 `free_buffer` / `free_bytes` 回调释放 |
| `read_file` 的数据块 | C++（`malloc`） | Rust 经 `free_bytes` → `free`（统一用 malloc/free 而非 new[]/delete[]） |

简单说：**谁分配谁释放，且释放逻辑由分配方提供**，
任何一方都不去 free 对方分配的内存。

## 六、平台集成

### 平台能力门控（单一事实来源）

`PackageSourceKind::supported_platforms()` 是唯一事实来源：

| 包源 | 平台 |
|------|------|
| npm / pnpm / git | Windows · macOS · Linux |
| winget | 仅 Windows |
| scoop | 仅 Windows |
| Homebrew | macOS · Linux |

注册表**注册全部适配器**，不做注册期过滤 —— 否则 UI 无法表达
"该包源在当前平台不可用"这一信息。不可用的源在 UI 上被隐藏
（下载页有开关可以看到被隐藏的项）。

### App Sandbox 已关闭（macOS）

Flutter macOS 应用默认开启 App Sandbox。但 UMango 必须：

- 执行 `npm` / `pnpm` / `git` / `brew` 等外部 CLI；
- 让这些 CLI 写入 `~/.npm`、`/opt/homebrew`、用户项目目录等**沙箱外**路径；
- 读取用户的项目文件与全局配置。

沙箱内这些操作会被系统拒绝，核心功能直接不可用。
因此 `DebugProfile.entitlements` 与 `Release.entitlements` 都显式设置为
`com.apple.security.app-sandbox = false`，并加了
`com.apple.security.cs.disable-library-validation`（加载随包分发的 dylib）。

**代价**：无法上架 Mac App Store。对这类开发者工具是可接受的取舍。

### 动态库加载

`ui/lib/ffi/umango_bridge.dart` 按固定顺序尝试多个路径：

1. 环境变量 `UMANGO_BRIDGE_LIB`
2. 与可执行文件同级（`.app/Contents/MacOS/`）
3. `.app/Contents/Frameworks/`
4. `~/Library/Caches/umango-build/native/`（开发期构建缓存）
5. 仓库内相对路径

全部失败时**不抛异常白屏**，而是显示诊断页：列出尝试过的路径、
错误原因、修复命令（`scripts/build.sh`）。

阶段1 尚未改 Xcode 工程加入正式的 Copy Files 阶段，
由 `scripts/build.sh bundle` 显式复制 —— 行为可见、可重复，
后续应替换为正式方案。

## 七、目录职责

| 路径 | 职责 |
|------|------|
| `core/crates/umango-core/src/model.rs` | 前后端数据契约（改字段名 = 破坏契约） |
| `core/crates/umango-core/src/version.rs` | 版本通道判定、比较、npm 范围匹配 |
| `core/crates/umango-core/src/adapter.rs` | 适配器 trait + 通用依赖树递归器 |
| `core/crates/umango-core/src/registry.rs` | 平台门控 + 24h 可用性缓存 |
| `core/crates/umango-core/src/api.rs` | JSON API 分发与错误信封 |
| `core/crates/umango-adapters/src/npm.rs` | npm/pnpm 共用 packument 解析 |
| `core/crates/umango-ffi/src/wire.rs` | 进程请求线格式（与 C++ 逐字节对应） |
| `native/include/umango_bridge.h` | **ABI 契约**，两套接口都定义在这里 |
| `native/src/process_posix.cpp` | 进程创建（已验证路径） |
| `ui/lib/animation/animation_config.dart` | 全局动画配置（唯一配置入口） |
| `ui/lib/ffi/umango_bridge.dart` | dart:ffi 绑定 + 类型化 API |
