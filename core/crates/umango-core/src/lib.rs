//! # umango-core
//!
//! UMango 的核心业务层（Rust）。这一层**完全不含 Minecraft 相关逻辑**，
//! 只做包管理业务：包源适配、版本解析、依赖树计算、可用性保护。
//!
//! 分层位置：
//! ```text
//! Flutter (UI/动画)
//!   └─ dart:ffi
//!        └─ C++ 桥接层（进程调用 / 文件 IO / 系统调用）
//!             └─ umango-core  ← 本 crate
//! ```
//!
//! 设计约束：
//! 1. 本层不直接创建进程，进程创建通过 [`process::ProcessRunner`] 抽象，
//!    由 C++ 宿主注入；纯 Rust 实现仅用于测试与开发。
//! 2. 所有跨语言数据结构都在 [`model`] 中定义，并经 serde 序列化。
//! 3. 任何外部依赖（CLI、网络）失败都必须降级成带错误码的结构化错误，
//!    绝不让 UI 拿到裸字符串。

pub mod adapter;
pub mod api;
pub mod clock;
pub mod error;
pub mod model;
pub mod process;
pub mod registry;
pub mod version;

pub use adapter::{build_dependency_tree, probe_cli, PmAdapter, TreeBuilder};
pub use error::{CoreError, RecoveryAdvice, Result};
pub use model::*;
pub use process::{ProcessOutput, ProcessRunner, ProcessSpec};
pub use registry::AdapterRegistry;

/// 核心层版本（编译期取自 Cargo.toml）。
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// 应用名（拼写为全局独立应用 Uneven Mango Application 的缩写）。
pub const APP_NAME: &str = "UMango";

/// 包源可用性缓存 TTL：**24 小时**。
///
/// 对应「24 小时可用性保护」：缓存期内不重复探测；探测失败但缓存未过期时
/// 降级沿用旧结果并标记 `stale`，而不是把包源直接判死。
pub const AVAILABILITY_TTL_SECS: i64 = 24 * 60 * 60;
