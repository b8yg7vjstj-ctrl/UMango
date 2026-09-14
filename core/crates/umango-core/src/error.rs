use thiserror::Error;

/// UMango 核心层统一错误类型。
///
/// 设计目标：每个错误都带一个**稳定的机器可读错误码**，供上层 UI
/// 做兜底分支决策（网络异常 / 源不可用 / CLI 缺失 / 解析失败），
/// 而不是把 Rust 的字符串错误直接抛给用户界面。
#[derive(Debug, Error)]
pub enum CoreError {
    #[error("未知包源: {0}")]
    UnknownSource(String),

    // 注意：字段不能叫 `source`，否则 thiserror 会把它当成错误链的 source。
    #[error("包源 {source_name} 在当前平台 {platform} 上不可用")]
    UnsupportedPlatform {
        source_name: String,
        platform: String,
        hint: Option<String>,
    },

    #[error("命令行工具 `{cli}` 未安装或不在 PATH 中")]
    CliMissing { cli: String, hint: Option<String> },

    #[error("{context} 执行失败（退出码 {code:?}）: {stderr}")]
    CommandFailed {
        context: String,
        code: Option<i32>,
        stderr: String,
        stdout: String,
    },

    #[error("{context} 执行超时（>{timeout_ms}ms）")]
    Timeout { context: String, timeout_ms: u64 },

    #[error("解析 `{context}` 的输出失败: {reason}")]
    Parse { context: String, reason: String },

    #[error("网络请求失败: {reason}")]
    Network { reason: String, url: Option<String> },

    #[error("找不到包 `{id}`")]
    PackageNotFound { id: String },

    #[error("该功能在包源 {source_name} 上不受支持: {feature}")]
    Unsupported {
        source_name: String,
        feature: String,
    },

    #[error("插件错误: {0}")]
    Plugin(String),

    #[error("JSON 序列化/反序列化失败: {0}")]
    Json(#[from] serde_json::Error),

    #[error("IO 错误: {0}")]
    Io(#[from] std::io::Error),

    #[error("{0}")]
    Other(String),
}

pub type Result<T> = std::result::Result<T, CoreError>;

/// UI 侧应该怎么应对这个错误。
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RecoveryAdvice {
    /// 可以原样重试（瞬时网络抖动、超时）。
    Retry,
    /// 引导用户安装对应 CLI。
    InstallCli,
    /// 建议切换到另一个包源（例如 npm 不可用时改用 pnpm 或镜像）。
    SwitchSource,
    /// 展示离线/缓存数据，并标注"数据可能过期"。
    ShowCached,
    /// 让用户检查网络/代理设置。
    CheckNetwork,
    /// 该平台根本不支持，UI 应隐藏入口。
    HideEntry,
    /// 需要人工介入，不要自动重试。
    Manual,
}

impl CoreError {
    /// 稳定的机器可读错误码。改动这个字符串等于破坏 UI 与核心层的契约。
    pub fn code(&self) -> &'static str {
        match self {
            CoreError::UnknownSource(_) => "unknown_source",
            CoreError::UnsupportedPlatform { .. } => "unsupported_platform",
            CoreError::CliMissing { .. } => "cli_missing",
            CoreError::CommandFailed { .. } => "command_failed",
            CoreError::Timeout { .. } => "timeout",
            CoreError::Parse { .. } => "parse_error",
            CoreError::Network { .. } => "network_error",
            CoreError::PackageNotFound { .. } => "package_not_found",
            CoreError::Unsupported { .. } => "unsupported_feature",
            CoreError::Plugin(_) => "plugin_error",
            CoreError::Json(_) => "json_error",
            CoreError::Io(_) => "io_error",
            CoreError::Other(_) => "internal_error",
        }
    }

    /// 是否值得自动重试。
    pub fn is_retryable(&self) -> bool {
        matches!(
            self,
            CoreError::Timeout { .. }
                | CoreError::Network { .. }
                | CoreError::CommandFailed { .. }
        )
    }

    /// 给 UI 的兜底建议。
    pub fn advice(&self) -> RecoveryAdvice {
        match self {
            CoreError::UnsupportedPlatform { .. } => RecoveryAdvice::HideEntry,
            CoreError::CliMissing { .. } => RecoveryAdvice::InstallCli,
            CoreError::Timeout { .. } | CoreError::Network { .. } => RecoveryAdvice::CheckNetwork,
            CoreError::CommandFailed { .. } => RecoveryAdvice::Retry,
            CoreError::Parse { .. } => RecoveryAdvice::SwitchSource,
            CoreError::PackageNotFound { .. } => RecoveryAdvice::Manual,
            CoreError::Unsupported { .. } => RecoveryAdvice::SwitchSource,
            _ => RecoveryAdvice::Manual,
        }
    }

    pub fn cli_missing(cli: &str, hint: Option<&str>) -> Self {
        CoreError::CliMissing {
            cli: cli.to_string(),
            hint: hint.map(|s| s.to_string()),
        }
    }

    pub fn network(reason: impl Into<String>) -> Self {
        CoreError::Network {
            reason: reason.into(),
            url: None,
        }
    }

    pub fn parse(context: &str, reason: impl Into<String>) -> Self {
        CoreError::Parse {
            context: context.to_string(),
            reason: reason.into(),
        }
    }

    pub fn other(msg: impl Into<String>) -> Self {
        CoreError::Other(msg.into())
    }
}

impl From<&str> for CoreError {
    fn from(value: &str) -> Self {
        CoreError::Other(value.to_string())
    }
}
