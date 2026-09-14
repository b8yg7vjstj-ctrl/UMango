# UMango（Uneven Mango Application）

跨平台**包管理器图形界面**。交互设计对标 PCL-CE，但所有业务代码自研；
目标是用图形界面管理 `npm` / `pnpm` / `git` / `scoop` / `winget` / `Homebrew`
等命令行包管理器。

> 后端**完全不含** Minecraft 启动与资源下载逻辑，只保留包管理业务。

```
Flutter（界面 / 动画）
   └── dart:ffi ──► C++ 桥接层（进程创建、文件读写、系统调用）
                        └──► Rust 核心层（适配器、版本解析、依赖树、可用性缓存）
                              └── 反向回调 C++ 执行 CLI（posix_spawn / CreateProcess）
```

## 当前状态：阶段1 已完成

| 能力 | 状态 |
|------|------|
| 三层架构打通（Flutter ⇄ C++ ⇄ Rust） | ✅ 已在真机验证 |
| 包源列表 / 平台能力门控 | ✅ |
| 搜索、版本列表（正式版/测试版/快照区分） | ✅ |
| 依赖树解析与可视化（缩进连接线 + 可点操作） | ✅ |
| 下载量：官方优先 → GitHub Stars 兜底 | ✅ 两条链路均实测 |
| 全局可切换动画风格（纵向入场 / 向右滑出） | ✅ |
| 包源可用性 24 小时缓存保护 | ✅ |
| 安装 / 卸载 / 升级的**实际执行** | ❌ 阶段3（当前只生成命令计划） |
| winget / scoop 适配器 | ⚠️ 已实现但**无法在 macOS 上验证** |
| 插件系统 | ❌ 阶段4 |

详细进度快照见 [docs/PROGRESS.md](docs/PROGRESS.md)。

## 快速开始

```bash
# 构建全部（Rust + C++ + Flutter），并把动态库打进 .app
scripts/build.sh all

# 构建并启动
scripts/build.sh run

# 跑全部测试（Rust 单测 + C++↔Rust 桥接自检）
scripts/build.sh test
```

### 环境要求

- Rust ≥ 1.82、CMake ≥ 3.20、Xcode（macOS 桌面目标）
- Flutter ≥ 3.44（Dart ≥ 3.12）
- 各包管理器的 CLI 按需安装（`npm` / `pnpm` / `git` / `brew` 等）

### 关于构建产物位置

项目源码位于 OneDrive 同步目录内，而 Rust `target/` 与 Flutter `build/`
会产生数万个小文件，留在同步目录里会拖慢构建并偶发文件锁冲突。
因此 `scripts/env.sh` 把 `CARGO_TARGET_DIR` 等重定向到
`~/Library/Caches/umango-build/`。**先 `source scripts/env.sh` 再手动跑 cargo/cmake。**

## 目录结构

```
core/                    Rust 核心业务层（Cargo workspace）
  crates/umango-core/      领域模型、适配器契约、版本/依赖树、注册表、JSON API
  crates/umango-adapters/  各包管理器适配器 + HTTP 工具
  crates/umango-ffi/       C ABI 导出层 + 线格式（wire）
native/                  C++ 桥接层
  include/umango_bridge.h     ABI 契约（两套接口：向下给 Dart、向上给 Rust）
  src/process_posix.cpp       posix_spawn、双管道并发读取、超时强杀
  src/process_windows.cpp     CreateProcess（⚠️ 未在真机验证）
  src/wire.cpp                进程请求分帧解码
  src/fs_ops.cpp              which / 文件读写 / 平台信息
  tests/umango_bridge_smoke.cpp  端到端自检
ui/                      Flutter 前端
  lib/animation/              全局动画配置系统
  lib/ffi/                    dart:ffi 绑定与类型化客户端
  lib/models/                 与 Rust JSON 契约一一对应的数据模型
  lib/pages/                  页面
  lib/widgets/                包卡片、依赖树、包源图标
docs/                    架构、动画、路线图、进度快照
scripts/                 构建脚本
```

## 硬性约束（红线）

1. **代码隔离**：仅观摩 PCL-CE 的交互设计，业务代码全部手写，不复制其源码。
2. **插件模块例外**：插件解析／导入模块可复用 HMCL-CE / PCL-CE 的插件加载代码，单独隔离存放。
3. **技术栈**：Rust + C++ + Flutter，不使用 Electron 网页套壳。
4. **后端剥离 MC**：不含任何 Minecraft 逻辑。
5. **动画向量改造**：列表入场改为纵向、弹窗改为向右滑出，降低视觉代码相似度。

## 已知限制（如实记录）

- **github.com:443 在本开发环境不可达**，但 `api.github.com` 可达。
  因此 git 源的搜索（走 API）可用，而 `git ls-remote` 读 tag 会失败 ——
  这反而验证了降级链路确实生效。
- **winget / scoop 的解析逻辑未经真机验证**，界面上以「未在真机验证」标记显式告知。
- **动态库尚未正式集成进 Xcode 工程**，当前由 `scripts/build.sh bundle` 复制进 .app。
- **App Sandbox 已关闭**：包管理器 GUI 需要执行外部 CLI 并写入沙箱外路径，
  沙箱内核心功能不可用。代价是无法上架 Mac App Store。

## 第三方资源

- `npm` / `pnpm` / `git` / `Homebrew` 图标来自 [simple-icons](https://github.com/simple-icons/simple-icons)，
  授权 **CC0 1.0**（公有领域）。
- `winget` / `scoop` 无可用官方图标，界面使用自绘字母徽章。
- 版本范围匹配复用 [`node-semver`](https://crates.io/crates/node-semver) crate。
