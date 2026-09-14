//! UMango 领域模型。
//!
//! 这一层是 Flutter 与 Rust 之间的**数据契约**：所有结构都经 serde 序列化成 JSON
//! 穿过 C ABI，因此字段名一旦发布就不要随意改动（前端按字段名取值）。

use serde::{Deserialize, Serialize};

/// 支持的目标平台。用于包源的能力门控（winget/scoop 仅 Windows，Homebrew 非 Windows）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Platform {
    Windows,
    Macos,
    Linux,
}

impl Platform {
    /// 编译期宿主平台。
    pub const fn host() -> Platform {
        #[cfg(target_os = "windows")]
        {
            Platform::Windows
        }
        #[cfg(target_os = "macos")]
        {
            Platform::Macos
        }
        #[cfg(all(not(target_os = "windows"), not(target_os = "macos")))]
        {
            Platform::Linux
        }
    }

    pub fn as_str(&self) -> &'static str {
        match self {
            Platform::Windows => "windows",
            Platform::Macos => "macos",
            Platform::Linux => "linux",
        }
    }

    pub fn display_name(&self) -> &'static str {
        match self {
            Platform::Windows => "Windows",
            Platform::Macos => "macOS",
            Platform::Linux => "Linux",
        }
    }
}

/// 包源种类。对应路线图里的「包管理器源列表」。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum PackageSourceKind {
    Npm,
    Pnpm,
    Git,
    Scoop,
    Winget,
    Homebrew,
}

impl PackageSourceKind {
    pub const ALL: [PackageSourceKind; 6] = [
        PackageSourceKind::Npm,
        PackageSourceKind::Pnpm,
        PackageSourceKind::Git,
        PackageSourceKind::Scoop,
        PackageSourceKind::Winget,
        PackageSourceKind::Homebrew,
    ];

    pub fn as_str(&self) -> &'static str {
        match self {
            PackageSourceKind::Npm => "npm",
            PackageSourceKind::Pnpm => "pnpm",
            PackageSourceKind::Git => "git",
            PackageSourceKind::Scoop => "scoop",
            PackageSourceKind::Winget => "winget",
            PackageSourceKind::Homebrew => "homebrew",
        }
    }

    pub fn display_name(&self) -> &'static str {
        match self {
            PackageSourceKind::Npm => "npm",
            PackageSourceKind::Pnpm => "pnpm",
            PackageSourceKind::Git => "Git",
            PackageSourceKind::Scoop => "Scoop",
            PackageSourceKind::Winget => "WinGet",
            PackageSourceKind::Homebrew => "Homebrew",
        }
    }

    /// 官方图标资源 key —— 前端据此从 assets/icons 里取图，卡片封面不再使用 mod 封面。
    pub fn icon_key(&self) -> &'static str {
        match self {
            PackageSourceKind::Npm => "icon_npm",
            PackageSourceKind::Pnpm => "icon_pnpm",
            PackageSourceKind::Git => "icon_git",
            PackageSourceKind::Scoop => "icon_scoop",
            PackageSourceKind::Winget => "icon_winget",
            PackageSourceKind::Homebrew => "icon_homebrew",
        }
    }

    /// 卡片强调色（取自各工具品牌主色，前端也可覆盖）。
    pub fn accent(&self) -> &'static str {
        match self {
            PackageSourceKind::Npm => "#CB3837",
            PackageSourceKind::Pnpm => "#F69220",
            PackageSourceKind::Git => "#F05032",
            PackageSourceKind::Scoop => "#5D5D5D",
            PackageSourceKind::Winget => "#0078D4",
            PackageSourceKind::Homebrew => "#FBB040",
        }
    }

    /// 该包源在哪些平台可用。这是「平台能力检测 + UI 自动隐藏」的单一事实来源。
    pub fn supported_platforms(&self) -> &'static [Platform] {
        match self {
            // npm / pnpm / git 全平台。
            PackageSourceKind::Npm | PackageSourceKind::Pnpm | PackageSourceKind::Git => &[
                Platform::Windows,
                Platform::Macos,
                Platform::Linux,
            ],
            // winget 仅 Windows。
            PackageSourceKind::Winget => &[Platform::Windows],
            // scoop 仅 Windows。
            PackageSourceKind::Scoop => &[Platform::Windows],
            // Homebrew 仅 macOS / Linux。
            PackageSourceKind::Homebrew => &[Platform::Macos, Platform::Linux],
        }
    }

    pub fn supports_platform(&self, platform: Platform) -> bool {
        self.supported_platforms().contains(&platform)
    }

    /// 该包源依赖的 CLI 可执行文件名。
    pub fn cli_name(&self) -> &'static str {
        match self {
            PackageSourceKind::Npm => "npm",
            PackageSourceKind::Pnpm => "pnpm",
            PackageSourceKind::Git => "git",
            PackageSourceKind::Scoop => "scoop",
            PackageSourceKind::Winget => "winget",
            PackageSourceKind::Homebrew => "brew",
        }
    }

    /// 默认安装作用域。
    pub fn default_scope(&self) -> InstallScope {
        match self {
            PackageSourceKind::Npm | PackageSourceKind::Pnpm => InstallScope::Global,
            PackageSourceKind::Git => InstallScope::ProjectLocal,
            PackageSourceKind::Scoop => InstallScope::UserSystem,
            PackageSourceKind::Winget => InstallScope::UserSystem,
            PackageSourceKind::Homebrew => InstallScope::UserSystem,
        }
    }

    pub fn parse(s: &str) -> Option<PackageSourceKind> {
        match s.trim().to_ascii_lowercase().as_str() {
            "npm" => Some(PackageSourceKind::Npm),
            "pnpm" => Some(PackageSourceKind::Pnpm),
            "git" | "github" => Some(PackageSourceKind::Git),
            "scoop" => Some(PackageSourceKind::Scoop),
            "winget" | "winget-cli" => Some(PackageSourceKind::Winget),
            "homebrew" | "brew" => Some(PackageSourceKind::Homebrew),
            _ => None,
        }
    }
}

/// 安装作用域。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum InstallScope {
    /// npm/pnpm -g
    Global,
    /// 当前项目 node_modules
    ProjectLocal,
    /// 当前用户（scoop / winget --scope user / brew）
    UserSystem,
    /// 全机器
    MachineSystem,
}

impl InstallScope {
    pub fn display_name(&self) -> &'static str {
        match self {
            InstallScope::Global => "全局",
            InstallScope::ProjectLocal => "项目本地",
            InstallScope::UserSystem => "当前用户",
            InstallScope::MachineSystem => "整机",
        }
    }
}

/// 版本通道。PCL-CE 用「正式版 / 快照 / 测试版」区分版本，
/// 这里把同一套区分逻辑搬到包版本上。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReleaseChannel {
    /// 正式版
    Stable,
    /// 测试版：alpha / beta / rc / next / canary / dev
    Prerelease,
    /// 快照：SNAPSHOT / nightly / 日期戳
    Snapshot,
    /// 无法判定（例如 git 里非语义化的 tag 名）
    Unknown,
}

impl ReleaseChannel {
    pub fn display_name(&self) -> &'static str {
        match self {
            ReleaseChannel::Stable => "正式版",
            ReleaseChannel::Prerelease => "测试版",
            ReleaseChannel::Snapshot => "快照",
            ReleaseChannel::Unknown => "未知通道",
        }
    }

    /// 通道优先级，越小越"稳定"。UI 排序时稳定通道优先。
    pub fn rank(&self) -> u8 {
        match self {
            ReleaseChannel::Stable => 0,
            ReleaseChannel::Prerelease => 1,
            ReleaseChannel::Snapshot => 2,
            ReleaseChannel::Unknown => 3,
        }
    }
}

/// 下载量统计。**规则：优先官方下载量，取不到时退化到 GitHub Stars。**
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum DownloadStats {
    /// 包管理器官方的下载量统计。
    Official {
        count: u64,
        period: StatsPeriod,
        /// 数据来源标识，例如 "api.npmjs.org"
        via: String,
    },
    /// 兜底：GitHub 仓库 Star 数（不是下载量，UI 必须标注出来）。
    ///
    /// 显式指定 `rename`：serde 的 snake_case 会把 `GitHubStars` 变成
    /// `git_hub_stars`，这种名字一旦成为前端契约就很难改，所以钉死为 `github_stars`。
    #[serde(rename = "github_stars")]
    GitHubStars { count: u64, repo: String },
    /// 两条链路都拿不到。必须保留原因，UI 显示"暂无数据"而不是 0。
    Unknown { reason: String },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StatsPeriod {
    Total,
    LastDay,
    LastWeek,
    LastMonth,
}

impl StatsPeriod {
    pub fn display_name(&self) -> &'static str {
        match self {
            StatsPeriod::Total => "总计",
            StatsPeriod::LastDay => "近 24 小时",
            StatsPeriod::LastWeek => "近 7 天",
            StatsPeriod::LastMonth => "近 30 天",
        }
    }
}

impl DownloadStats {
    pub fn count(&self) -> Option<u64> {
        match self {
            DownloadStats::Official { count, .. } => Some(*count),
            DownloadStats::GitHubStars { count, .. } => Some(*count),
            DownloadStats::Unknown { .. } => None,
        }
    }

    /// 是否是兜底数据（UI 要标注 "GitHub Stars 兜底"）。
    pub fn is_fallback(&self) -> bool {
        matches!(self, DownloadStats::GitHubStars { .. })
    }

    /// 指标的语义名称，避免把 Stars 当成下载量展示。
    pub fn metric_name(&self) -> &'static str {
        match self {
            DownloadStats::Official { .. } => "下载量",
            DownloadStats::GitHubStars { .. } => "GitHub Stars",
            DownloadStats::Unknown { .. } => "暂无数据",
        }
    }

    pub fn source_label(&self) -> String {
        match self {
            DownloadStats::Official { via, .. } => via.clone(),
            DownloadStats::GitHubStars { repo, .. } => format!("github.com/{repo}"),
            DownloadStats::Unknown { reason } => reason.clone(),
        }
    }

    pub fn unknown(reason: impl Into<String>) -> Self {
        DownloadStats::Unknown {
            reason: reason.into(),
        }
    }
}

/// 包的唯一标识：`<source>:<name>`。
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct PackageId {
    pub source: PackageSourceKind,
    pub name: String,
}

impl PackageId {
    pub fn new(source: PackageSourceKind, name: impl Into<String>) -> Self {
        PackageId {
            source,
            name: name.into(),
        }
    }

    /// 展示用全名，例如 `npm:react`。
    pub fn qualified(&self) -> String {
        format!("{}:{}", self.source.as_str(), self.name)
    }

    pub fn parse(s: &str) -> Option<PackageId> {
        let (src, name) = s.split_once(':')?;
        Some(PackageId::new(PackageSourceKind::parse(src)?, name))
    }
}

impl std::fmt::Display for PackageId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.qualified())
    }
}

/// 已安装状态（Phase 2 安装功能落地后填充）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InstalledState {
    pub version: String,
    pub scope: InstallScope,
    pub outdated: bool,
    pub latest: Option<String>,
}

/// 包元数据 —— 对应 UI 上的「下载卡片」。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PackageMeta {
    pub id: PackageId,
    /// 卡片标题
    pub display_name: String,
    /// 卡片副标题 / 简介
    pub summary: Option<String>,
    pub homepage: Option<String>,
    pub repository: Option<String>,
    pub license: Option<String>,
    pub latest_version: Option<String>,
    pub latest_channel: ReleaseChannel,
    /// 卡片上的下载量角标（含兜底与来源标注）
    pub downloads: DownloadStats,
    pub keywords: Vec<String>,
    pub updated_at: Option<String>,
    /// 卡片封面用的官方图标 key
    pub icon_key: String,
    pub accent: String,
    pub installed: Option<InstalledState>,
}

impl PackageMeta {
    pub fn new(id: PackageId, display_name: impl Into<String>) -> Self {
        let source = id.source;
        PackageMeta {
            id,
            display_name: display_name.into(),
            summary: None,
            homepage: None,
            repository: None,
            license: None,
            latest_version: None,
            latest_channel: ReleaseChannel::Unknown,
            downloads: DownloadStats::unknown("尚未查询"),
            keywords: Vec::new(),
            updated_at: None,
            icon_key: source.icon_key().to_string(),
            accent: source.accent().to_string(),
            installed: None,
        }
    }
}

/// 单个版本。版本列表页按通道分组展示。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PackageVersion {
    pub version: String,
    pub channel: ReleaseChannel,
    pub published_at: Option<String>,
    pub downloads: Option<u64>,
    pub deprecated: bool,
    pub note: Option<String>,
    /// 该版本的直接依赖声明（name -> 版本范围），版本列表页按需展开
    pub requires: Vec<DependencyRequirement>,
    /// 数据新鲜度：true 表示这是 24h 缓存内的结果
    pub from_cache: bool,
}

impl PackageVersion {
    pub fn new(version: impl Into<String>) -> Self {
        let version = version.into();
        let channel = crate::version::classify(&version);
        PackageVersion {
            version,
            channel,
            published_at: None,
            downloads: None,
            deprecated: false,
            note: None,
            requires: Vec::new(),
            from_cache: false,
        }
    }
}

/// 依赖声明（未解析）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DependencyRequirement {
    pub name: String,
    /// 原始版本范围字符串，例如 `^18.2.0`
    pub requirement: String,
    pub kind: DependencyKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DependencyKind {
    Runtime,
    Dev,
    Peer,
    Optional,
    Bundled,
}

impl DependencyKind {
    pub fn display_name(&self) -> &'static str {
        match self {
            DependencyKind::Runtime => "运行时依赖",
            DependencyKind::Dev => "开发依赖",
            DependencyKind::Peer => "同级依赖",
            DependencyKind::Optional => "可选依赖",
            DependencyKind::Bundled => "内置依赖",
        }
    }
}

/// 依赖节点状态 —— 依赖故障的兜底分支就落在这里。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum DependencyStatus {
    /// 已解析出确定版本
    Resolved,
    /// 声明存在但取不到元数据（网络/源故障）—— UI 显示黄色"未验证"
    Unverified { reason: String },
    /// 版本范围内找不到任何可满足的版本
    Missing { requirement: String },
    /// 多个父节点要求了互不兼容的版本
    Conflict {
        requirement: String,
        chosen: String,
        detail: String,
    },
    /// 依赖成环，已在此截断
    Cyclic { via: String },
    /// 因为深度限制被截断
    Truncated,
}

impl DependencyStatus {
    pub fn is_problem(&self) -> bool {
        !matches!(self, DependencyStatus::Resolved)
    }
}

/// 依赖关系树节点。UI 用缩进/连线做可视化。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DependencyNode {
    pub id: PackageId,
    /// 父节点给出的版本范围
    pub requirement: String,
    /// 解析后的具体版本
    pub resolved: Option<String>,
    pub kind: DependencyKind,
    pub status: DependencyStatus,
    /// 该依赖自身的下载量（用于界面上"安装依赖"时的参考）
    pub downloads: DownloadStats,
    pub depth: u32,
    pub children: Vec<DependencyNode>,
}

impl DependencyNode {
    pub fn new(id: PackageId, requirement: impl Into<String>, kind: DependencyKind) -> Self {
        DependencyNode {
            id,
            requirement: requirement.into(),
            resolved: None,
            kind,
            status: DependencyStatus::Truncated,
            downloads: DownloadStats::unknown("尚未查询"),
            depth: 0,
            children: Vec::new(),
        }
    }

    /// 递归统计节点总数与问题节点数，UI 显示"共 N 个依赖 / M 个待处理"。
    pub fn summarize(&self) -> DependencySummary {
        let mut summary = DependencySummary::default();
        self.walk(&mut summary);
        summary
    }

    fn walk(&self, summary: &mut DependencySummary) {
        summary.total += 1;
        match &self.status {
            DependencyStatus::Resolved => {}
            DependencyStatus::Unverified { .. } => summary.unverified += 1,
            DependencyStatus::Missing { .. } => summary.missing += 1,
            DependencyStatus::Conflict { .. } => summary.conflict += 1,
            DependencyStatus::Cyclic { .. } => summary.cyclic += 1,
            DependencyStatus::Truncated => summary.truncated += 1,
        }
        summary.max_depth = summary.max_depth.max(self.depth);
        for child in &self.children {
            child.walk(summary);
        }
    }

    /// 深度优先展平成列表，供 UI 做动画入场（列表纵向入场）。
    pub fn flatten(&self) -> Vec<(u32, &DependencyNode)> {
        let mut out = Vec::new();
        self.flatten_into(&mut out);
        out
    }

    fn flatten_into<'a>(&'a self, out: &mut Vec<(u32, &'a DependencyNode)>) {
        out.push((self.depth, self));
        for child in &self.children {
            child.flatten_into(out);
        }
    }
}

#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize)]
pub struct DependencySummary {
    pub total: u32,
    pub unverified: u32,
    pub missing: u32,
    pub conflict: u32,
    pub cyclic: u32,
    pub truncated: u32,
    pub max_depth: u32,
}

impl DependencySummary {
    /// 是否存在需要用户处理的问题。
    pub fn has_problems(&self) -> bool {
        self.unverified > 0 || self.missing > 0 || self.conflict > 0
    }
}

/// 包源可用性探测结果。
///
/// 「24 小时可用性保护」：结果带 TTL 缓存，刷新失败时若缓存未过期则降级使用，
/// 并在 UI 上标注数据可能过期 —— 与 PCL-CE 处理版本快照可用性的思路一致。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceAvailability {
    pub source: PackageSourceKind,
    pub state: AvailabilityState,
    pub cli_found: bool,
    pub cli_path: Option<String>,
    pub cli_version: Option<String>,
    /// ISO-8601 时间戳
    pub checked_at: String,
    /// 数据是否来自过期缓存（UI 需提示）
    pub stale: bool,
    pub detail: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AvailabilityState {
    /// 平台支持 + CLI 存在 + 能正常应答
    Available,
    /// CLI 存在但调用异常，或数据来自过期缓存
    Degraded,
    /// 当前平台不支持
    UnsupportedPlatform,
    /// CLI 未安装
    NotInstalled,
    /// 探测失败（网络/权限）
    Unreachable,
}

impl AvailabilityState {
    pub fn display_name(&self) -> &'static str {
        match self {
            AvailabilityState::Available => "可用",
            AvailabilityState::Degraded => "可用（数据可能过期）",
            AvailabilityState::UnsupportedPlatform => "当前平台不支持",
            AvailabilityState::NotInstalled => "未安装",
            AvailabilityState::Unreachable => "源不可达",
        }
    }

    pub fn is_usable(&self) -> bool {
        matches!(
            self,
            AvailabilityState::Available | AvailabilityState::Degraded
        )
    }
}

/// 包源的完整展示视图 —— 前端「包源列表」直接渲染这个。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceView {
    pub kind: PackageSourceKind,
    pub display_name: String,
    pub icon_key: String,
    pub accent: String,
    pub cli_name: String,
    pub default_scope: InstallScope,
    pub capabilities: SourceCapabilities,
    /// 平台门控结果：false 时 UI 必须隐藏该源
    pub supported_here: bool,
    pub supported_platforms: Vec<Platform>,
    pub availability: SourceAvailability,
    /// 诚实标注：该适配器的解析逻辑是否已在真机验证过。
    /// 例如 winget/scoop 只在 Windows 存在，在 macOS 上开发的解析器无法验证，
    /// 这时 UI 应显示"未验证"提示，而不是假装结果可靠。
    #[serde(default)]
    pub verification_note: Option<String>,
}

/// 各包源支持的能力，UI 据此决定按钮可用性。
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct SourceCapabilities {
    pub search: bool,
    pub list_versions: bool,
    pub resolve_deps: bool,
    pub official_downloads: bool,
    pub install: bool,
    pub uninstall: bool,
    pub upgrade: bool,
}

impl SourceCapabilities {
    pub const fn none() -> Self {
        SourceCapabilities {
            search: false,
            list_versions: false,
            resolve_deps: false,
            official_downloads: false,
            install: false,
            uninstall: false,
            upgrade: false,
        }
    }
}

/// 依赖树查询请求（同时用于 FFI 参数解析）。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DependencyQuery {
    pub source: PackageSourceKind,
    pub name: String,
    pub version: Option<String>,
    /// 最大递归深度，默认 3
    #[serde(default = "default_depth")]
    pub max_depth: u32,
    /// 是否包含 dev/peer/optional 依赖
    #[serde(default)]
    pub include_optional_kinds: bool,
}

fn default_depth() -> u32 {
    3
}

/// 带依赖树的完整查询结果。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DependencyTree {
    pub root: DependencyNode,
    pub summary: DependencySummary,
    /// 解析过程中遇到的非致命问题（网络异常、源访问失败等兜底记录）
    pub warnings: Vec<String>,
    pub truncated: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn platform_gating_matches_reality() {
        // winget / scoop 只在 Windows 可用。
        assert!(!PackageSourceKind::Winget.supports_platform(Platform::Macos));
        assert!(!PackageSourceKind::Scoop.supports_platform(Platform::Macos));
        assert!(PackageSourceKind::Winget.supports_platform(Platform::Windows));
        // Homebrew 不在 Windows。
        assert!(!PackageSourceKind::Homebrew.supports_platform(Platform::Windows));
        assert!(PackageSourceKind::Homebrew.supports_platform(Platform::Macos));
        // npm/pnpm/git 全平台
        for p in [Platform::Windows, Platform::Macos, Platform::Linux] {
            assert!(PackageSourceKind::Npm.supports_platform(p));
            assert!(PackageSourceKind::Pnpm.supports_platform(p));
            assert!(PackageSourceKind::Git.supports_platform(p));
        }
    }

    #[test]
    fn package_id_roundtrip() {
        let id = PackageId::new(PackageSourceKind::Npm, "react");
        assert_eq!(id.qualified(), "npm:react");
        assert_eq!(PackageId::parse("npm:react"), Some(id));
        assert_eq!(PackageId::parse("brew:jq").unwrap().source, PackageSourceKind::Homebrew);
        assert_eq!(PackageId::parse("nonsense"), None);
    }

    #[test]
    fn download_stats_semantics() {
        let official = DownloadStats::Official {
            count: 100,
            period: StatsPeriod::LastWeek,
            via: "api.npmjs.org".into(),
        };
        assert!(!official.is_fallback());
        assert_eq!(official.metric_name(), "下载量");

        let stars = DownloadStats::GitHubStars {
            count: 42,
            repo: "facebook/react".into(),
        };
        assert!(stars.is_fallback());
        assert_eq!(stars.metric_name(), "GitHub Stars");

        // 关键：拿不到数据时不能显示成 0
        let unknown = DownloadStats::unknown("两条链路均失败");
        assert_eq!(unknown.count(), None);
        assert_eq!(unknown.metric_name(), "暂无数据");
    }

    #[test]
    fn dependency_summary_counts_problems() {
        let mut root = DependencyNode::new(
            PackageId::new(PackageSourceKind::Npm, "root"),
            "^1.0.0",
            DependencyKind::Runtime,
        );
        root.status = DependencyStatus::Resolved;
        let mut child = DependencyNode::new(
            PackageId::new(PackageSourceKind::Npm, "flaky"),
            "^2.0.0",
            DependencyKind::Runtime,
        );
        child.depth = 1;
        child.status = DependencyStatus::Unverified {
            reason: "源访问失败".into(),
        };
        root.children.push(child);

        let summary = root.summarize();
        assert_eq!(summary.total, 2);
        assert_eq!(summary.unverified, 1);
        assert_eq!(summary.max_depth, 1);
        assert!(summary.has_problems());
    }
}
