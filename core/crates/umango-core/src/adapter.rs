//! 包源适配器契约。
//!
//! 对应路线图风险点 1「多包管理器命令差异巨大」的应对方案：
//! 每个包管理器只实现**自己原生的那几个命令**（search / versions / requirements），
//! 而依赖树递归、环检测、深度限制、预算控制、故障兜底这些**共性复杂逻辑**
//! 全部由核心层统一实现，适配器不重复写。
//!
//! 这样新增一个包管理器（Phase 4 的插件式适配器）只需要实现 3~4 个方法。

use std::collections::HashMap;

use crate::error::{CoreError, Result};
use crate::model::*;
use crate::process::{self, ProcessSpec};
use crate::version;

/// 依赖树单次构建的节点预算。防止 `npm` 这种依赖爆炸的生态把 CLI 调用打爆。
pub const DEFAULT_MAX_NODES: usize = 120;

/// 包源适配器。
///
/// 所有方法默认返回「不支持」，适配器只覆盖自己真正实现的能力——
/// 这样 platform-gated 的适配器（macOS 上的 winget/scoop）无需写任何空实现。
pub trait PmAdapter: Send + Sync {
    /// 该适配器对应的包源。
    fn kind(&self) -> PackageSourceKind;

    /// 能力声明。UI 据此决定按钮是否可点、搜索框是否存在。
    fn capabilities(&self) -> SourceCapabilities;

    /// 未安装时给用户的安装指引。
    fn install_hint(&self) -> String;

    /// 把 CLI 的版本探测参数交给默认探测器。
    fn version_args(&self) -> &'static [&'static str] {
        &["--version"]
    }

    /// 探测该源当前是否可用（CLI 是否存在、能否应答）。
    ///
    /// 默认实现已经覆盖绝大多数情况：平台门控 -> PATH 查找 -> 执行 `--version`。
    /// **注册表会带 24 小时缓存地调用它**，适配器不要自己在别处探测。
    fn probe(&self) -> SourceAvailability {
        probe_cli(self.kind(), self.version_args())
    }

    /// 组装给 UI 的包源视图（**不含可用性探测结果**）。
    ///
    /// 可用性必须由 [`crate::registry::AdapterRegistry`] 统一填充：
    /// 只有它掌握 24 小时缓存与失败降级策略。若在这里探测，
    /// 每次刷新 UI 都会真的去执行一次 CLI，缓存形同虚设。
    fn descriptor(&self) -> SourceView {
        self.default_descriptor()
    }

    /// 默认视图构造。覆盖了 [`PmAdapter::descriptor`] 的适配器应当调用它，
    /// 只在自己关心的字段上做修改（例如补充 `verification_note`）。
    fn default_descriptor(&self) -> SourceView {
        let kind = self.kind();
        let platform = process::runner().platform();
        SourceView {
            kind,
            display_name: kind.display_name().to_string(),
            icon_key: kind.icon_key().to_string(),
            accent: kind.accent().to_string(),
            cli_name: kind.cli_name().to_string(),
            default_scope: kind.default_scope(),
            capabilities: self.capabilities(),
            supported_here: kind.supports_platform(platform),
            supported_platforms: kind.supported_platforms().to_vec(),
            // 默认视为已验证；未在真机验证过的适配器自行覆盖此字段
            verification_note: None,
            // 占位：注册表随后用带缓存的真实探测结果覆盖
            availability: SourceAvailability {
                source: kind,
                state: AvailabilityState::Unreachable,
                cli_found: false,
                cli_path: None,
                cli_version: None,
                checked_at: crate::clock::now_iso8601(),
                stale: false,
                detail: Some("尚未探测".to_string()),
            },
        }
    }

    /// 搜索包。
    fn search(&self, _query: &str, _limit: usize) -> Result<Vec<PackageMeta>> {
        Err(self.unsupported("search"))
    }

    /// 列出某个包的全部版本。
    fn versions(&self, _name: &str) -> Result<Vec<PackageVersion>> {
        Err(self.unsupported("versions"))
    }

    /// 某个包在指定版本下的直接依赖声明。
    fn requirements(
        &self,
        _name: &str,
        _version: Option<&str>,
    ) -> Result<Vec<DependencyRequirement>> {
        Err(self.unsupported("requirements"))
    }

    /// 下载量。约定：**优先官方数据，取不到时兜底 GitHub Stars**。
    fn downloads(&self, _name: &str, _repository: Option<&str>) -> DownloadStats {
        DownloadStats::unknown("该包源尚未实现下载量统计")
    }

    /// 构建依赖树。
    ///
    /// **故意不做成 trait 方法**：默认实现需要把 `&Self` 转成 `&dyn PmAdapter`，
    /// 会让 trait 失去对象安全性（object safety），而注册表里存的正是
    /// `Box<dyn PmAdapter>`。因此依赖树构建是核心层的通用逻辑，
    /// 由 [`crate::registry::AdapterRegistry`] 调用 [`build_dependency_tree`]，
    /// 适配器只负责实现上面的原语方法，自动获得依赖树能力。

    fn unsupported(&self, feature: &str) -> CoreError {
        CoreError::Unsupported {
            source_name: self.kind().as_str().to_string(),
            feature: feature.to_string(),
        }
    }
}

/// 平台不支持时的可用性结构。UI 据此隐藏该包源入口。
pub fn unsupported_availability(kind: PackageSourceKind) -> SourceAvailability {
    SourceAvailability {
        source: kind,
        state: AvailabilityState::UnsupportedPlatform,
        cli_found: false,
        cli_path: None,
        cli_version: None,
        checked_at: crate::clock::now_iso8601(),
        stale: false,
        detail: Some(format!(
            "{} 仅在 {} 上可用",
            kind.display_name(),
            kind.supported_platforms()
                .iter()
                .map(|p| p.display_name())
                .collect::<Vec<_>>()
                .join(" / ")
        )),
    }
}

/// 通用 CLI 可用性探测：平台门控 -> PATH 查找 -> 执行 `--version`。
pub fn probe_cli(kind: PackageSourceKind, version_args: &[&str]) -> SourceAvailability {
    let runner = process::runner();
    let platform = runner.platform();
    let now = crate::clock::now_iso8601();

    if !kind.supports_platform(platform) {
        return unsupported_availability(kind);
    }

    let cli = kind.cli_name();
    let Some(path) = runner.which(cli) else {
        return SourceAvailability {
            source: kind,
            state: AvailabilityState::NotInstalled,
            cli_found: false,
            cli_path: None,
            cli_version: None,
            checked_at: now,
            stale: false,
            detail: Some(format!("在 PATH 中找不到 `{cli}`")),
        };
    };

    let spec = ProcessSpec::new(&path)
        .args(version_args.iter().copied())
        .timeout_ms(8_000);
    match runner.run(&spec) {
        Ok(out) if out.success() => SourceAvailability {
            source: kind,
            state: AvailabilityState::Available,
            cli_found: true,
            cli_path: Some(path),
            cli_version: Some(first_line(&out.stdout)),
            checked_at: now,
            stale: false,
            detail: None,
        },
        Ok(out) => {
            // CLI 在，但调用异常 —— 降级而不是直接判死。
            SourceAvailability {
                source: kind,
                state: AvailabilityState::Degraded,
                cli_found: true,
                cli_path: Some(path),
                cli_version: None,
                checked_at: now,
                stale: false,
                detail: Some(format!(
                    "`{cli} {}` 退出码 {}: {}",
                    version_args.join(" "),
                    out.status,
                    process::preview(&out.stderr, 200)
                )),
            }
        }
        Err(e) => SourceAvailability {
            source: kind,
            state: AvailabilityState::Unreachable,
            cli_found: true,
            cli_path: Some(path),
            cli_version: None,
            checked_at: now,
            stale: false,
            detail: Some(e.to_string()),
        },
    }
}

fn first_line(s: &str) -> String {
    s.lines()
        .map(str::trim)
        .find(|l| !l.is_empty())
        .unwrap_or("")
        .to_string()
}

// ---------------------------------------------------------------------------
// 通用依赖树构建
// ---------------------------------------------------------------------------

/// 依赖树递归构建器。
///
/// 关键行为（都是路线图明确要求的能力）：
/// - 深度限制 + 节点预算，避免依赖爆炸；
/// - 环检测：同一祖先链上重复出现的包标记 `Cyclic` 并截断；
/// - **单个依赖解析失败不会让整棵树失败**，而是把该节点标成 `Unverified`
///   并累积一条 warning —— 这就是「网络异常/源访问失败兜底」的落点；
/// - 版本范围解析复用 `node-semver`，默认不选预发布版本。
pub struct TreeBuilder<'a> {
    adapter: &'a dyn PmAdapter,
    include_pre_rule: bool,
    max_depth: u32,
    max_nodes: usize,
    warnings: Vec<String>,
    node_count: usize,
    truncated: bool,
    /// name -> 可用版本列表（缓存，避免对同一包反复调用 CLI）
    version_cache: HashMap<String, std::result::Result<Vec<String>, String>>,
    /// "name@version" -> 直接依赖声明
    req_cache: HashMap<String, std::result::Result<Vec<DependencyRequirement>, String>>,
    /// 当前祖先链，用于环检测
    ancestors: Vec<String>,
}

impl<'a> TreeBuilder<'a> {
    pub fn new(adapter: &'a dyn PmAdapter, query: &DependencyQuery) -> Self {
        TreeBuilder {
            adapter,
            include_pre_rule: query.include_optional_kinds,
            max_depth: query.max_depth.min(8),
            max_nodes: DEFAULT_MAX_NODES,
            warnings: Vec::new(),
            node_count: 0,
            truncated: false,
            version_cache: HashMap::new(),
            req_cache: HashMap::new(),
            ancestors: Vec::new(),
        }
    }

    fn versions_of(&mut self, name: &str) -> std::result::Result<Vec<String>, String> {
        if let Some(hit) = self.version_cache.get(name) {
            return hit.clone();
        }
        let result = match self.adapter.versions(name) {
            Ok(list) => {
                let mut v: Vec<String> = list.into_iter().map(|x| x.version).collect();
                v.sort_by(|a, b| version::compare(a, b));
                Ok(v)
            }
            Err(e) => Err(format!("{}: {}", e.code(), e)),
        };
        self.version_cache
            .insert(name.to_string(), result.clone());
        result
    }

    fn requirements_of(
        &mut self,
        name: &str,
        version: Option<&str>,
    ) -> std::result::Result<Vec<DependencyRequirement>, String> {
        let key = format!("{name}@{}", version.unwrap_or("*"));
        if let Some(hit) = self.req_cache.get(&key) {
            return hit.clone();
        }
        let result = self
            .adapter
            .requirements(name, version)
            .map_err(|e| format!("{}: {}", e.code(), e));
        self.req_cache.insert(key, result.clone());
        result
    }

    /// 构建以 `query.name` 为根的依赖树。
    pub fn build(mut self, query: &DependencyQuery) -> Result<DependencyTree> {
        let kind = self.adapter.kind();
        let mut root = DependencyNode::new(
            PackageId::new(kind, query.name.clone()),
            query.version.clone().unwrap_or_else(|| "*".to_string()),
            DependencyKind::Runtime,
        );
        self.node_count = 1;

        // 1) 解析根版本
        match self.versions_of(&query.name) {
            Ok(all) => {
                let resolved = match query.version.as_deref() {
                    Some(v) if !v.is_empty() && v != "*" => version::pick_best(
                        all.iter().map(|s| s.as_str()),
                        v,
                    )
                    .map(|s| s.to_string())
                    .or_else(|| Some(v.to_string())),
                    _ => version::pick_latest_stable(all.iter().map(|s| s.as_str()))
                        .map(|s| s.to_string()),
                };
                root.resolved = resolved;
                root.status = if root.resolved.is_some() {
                    DependencyStatus::Resolved
                } else if all.is_empty() {
                    DependencyStatus::Missing {
                        requirement: root.requirement.clone(),
                    }
                } else {
                    DependencyStatus::Missing {
                        requirement: root.requirement.clone(),
                    }
                };
            }
            Err(reason) => {
                // 根包拿不到版本列表：整棵树没法展开，但要把结果**降级返回**给 UI
                root.status = DependencyStatus::Unverified {
                    reason: reason.clone(),
                };
                self.warnings
                    .push(format!("无法获取 `{}` 的版本列表：{reason}", query.name));
            }
        }

        // 2) 递归展开
        self.ancestors.push(query.name.clone());
        self.expand(&mut root);
        self.ancestors.pop();

        let summary = root.summarize();
        Ok(DependencyTree {
            root,
            summary,
            warnings: self.warnings,
            truncated: self.truncated,
        })
    }

    fn expand(&mut self, node: &mut DependencyNode) {
        let Some(resolved) = node.resolved.clone() else {
            return; // 版本没解析出来，不再往下走
        };

        if node.depth >= self.max_depth {
            if self.requirements_of(&node.id.name, Some(&resolved)).is_ok() {
                // 确实还有下一层，但因为深度限制没展开
                node.status = DependencyStatus::Resolved;
                self.truncated = true;
            }
            return;
        }

        let requirements = match self.requirements_of(&node.id.name, Some(&resolved)) {
            Ok(r) => r,
            Err(reason) => {
                // 依赖元数据取不到：不清空节点，保留已解析的版本 + 标记未验证
                self.warnings.push(format!(
                    "无法获取 `{}@{}` 的依赖声明：{reason}",
                    node.id.name, resolved
                ));
                if node.children.is_empty() {
                    node.status = DependencyStatus::Unverified { reason };
                }
                return;
            }
        };

        for req in requirements {
            if self.node_count >= self.max_nodes {
                self.truncated = true;
                break;
            }
            if !self.kind_included(req.kind) {
                continue;
            }
            self.node_count += 1;

            let mut child = DependencyNode::new(
                PackageId::new(self.adapter.kind(), req.name.clone()),
                req.requirement.clone(),
                req.kind,
            );
            child.depth = node.depth + 1;

            // 环检测
            if self.ancestors.iter().any(|a| a == &req.name) {
                child.status = DependencyStatus::Cyclic {
                    via: self.ancestors.join(" -> "),
                };
                node.children.push(child);
                continue;
            }

            // 解析版本范围
            match self.versions_of(&req.name) {
                Ok(all) => {
                    match version::pick_best(all.iter().map(|s| s.as_str()), &req.requirement) {
                        Some(v) => {
                            child.resolved = Some(v.to_string());
                            child.status = DependencyStatus::Resolved;
                            self.ancestors.push(req.name.clone());
                            self.expand(&mut child);
                            self.ancestors.pop();
                        }
                        None => {
                            child.status = DependencyStatus::Missing {
                                requirement: req.requirement.clone(),
                            };
                            self.warnings.push(format!(
                                "`{}` 找不到满足 `{}` 的版本（共 {} 个候选）",
                                req.name,
                                req.requirement,
                                all.len()
                            ));
                        }
                    }
                }
                Err(reason) => {
                    child.status = DependencyStatus::Unverified {
                        reason: reason.clone(),
                    };
                    self.warnings.push(format!(
                        "依赖 `{}` 解析失败，已标记为未验证：{reason}",
                        req.name
                    ));
                }
            }

            node.children.push(child);
        }
    }

    fn kind_included(&self, kind: DependencyKind) -> bool {
        match kind {
            DependencyKind::Runtime | DependencyKind::Bundled => true,
            DependencyKind::Dev | DependencyKind::Peer | DependencyKind::Optional => {
                self.include_pre_rule
            }
        }
    }
}

/// 用适配器构建依赖树。
pub fn build_dependency_tree(
    adapter: &dyn PmAdapter,
    query: &DependencyQuery,
) -> Result<DependencyTree> {
    TreeBuilder::new(adapter, query).build(query)
}

/// 把「依赖安装」这类动作解析成可执行命令 —— Phase 3 落地安装功能时复用。
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct PlannedCommand {
    pub program: String,
    pub args: Vec<String>,
    pub scope: InstallScope,
    /// 人话解释，UI 在执行前给用户确认
    pub explanation: String,
}

#[cfg(test)]
pub(crate) mod test_support {
    use super::*;
    use std::sync::Mutex;

    /// 测试用假适配器：可编排版本表与依赖表，并可注入故障。
    pub struct FakeAdapter {
        pub kind: PackageSourceKind,
        /// name -> versions
        pub versions: HashMap<String, Vec<String>>,
        /// "name@version" -> [(dep_name, requirement)]
        pub deps: HashMap<String, Vec<(String, String)>>,
        /// 这些包名在查询版本列表时抛错，用于验证兜底分支
        pub fail_versions: Vec<String>,
        /// 这些 "name@version" 在查询依赖时抛错
        pub fail_deps: Vec<String>,
        pub calls: Mutex<Vec<String>>,
    }

    impl FakeAdapter {
        pub fn new() -> Self {
            FakeAdapter {
                kind: PackageSourceKind::Npm,
                versions: HashMap::new(),
                deps: HashMap::new(),
                fail_versions: Vec::new(),
                fail_deps: Vec::new(),
                calls: Mutex::new(Vec::new()),
            }
        }

        pub fn with_pkg(mut self, name: &str, versions: &[&str], deps: &[(&str, &str)]) -> Self {
            self.versions
                .insert(name.to_string(), versions.iter().map(|s| s.to_string()).collect());
            // 依赖表按**每一个**版本登记：测试里最终解析到的版本未必是列表最后一个
            // （受版本范围约束），按版本全量登记可以避免测试桩自身成为干扰源。
            let dep_list: Vec<(String, String)> = deps
                .iter()
                .map(|(n, r)| (n.to_string(), r.to_string()))
                .collect();
            for v in versions {
                self.deps
                    .insert(format!("{name}@{v}"), dep_list.clone());
            }
            self
        }

        pub fn failing(mut self, name: &str) -> Self {
            self.fail_versions.push(name.to_string());
            self
        }
    }

    impl PmAdapter for FakeAdapter {
        fn kind(&self) -> PackageSourceKind {
            self.kind
        }

        fn capabilities(&self) -> SourceCapabilities {
            SourceCapabilities {
                search: true,
                list_versions: true,
                resolve_deps: true,
                official_downloads: false,
                install: false,
                uninstall: false,
                upgrade: false,
            }
        }

        fn install_hint(&self) -> String {
            "无需安装（测试桩）".into()
        }

        fn probe(&self) -> SourceAvailability {
            SourceAvailability {
                source: self.kind,
                state: AvailabilityState::Available,
                cli_found: true,
                cli_path: Some("/usr/bin/fake".into()),
                cli_version: Some("0.0.0-test".into()),
                checked_at: crate::clock::now_iso8601(),
                stale: false,
                detail: None,
            }
        }

        fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
            self.calls
                .lock()
                .unwrap()
                .push(format!("versions:{name}"));
            if self.fail_versions.iter().any(|f| f == name) {
                return Err(CoreError::network("模拟网络故障"));
            }
            match self.versions.get(name) {
                Some(list) => Ok(list.iter().map(PackageVersion::new).collect()),
                None => Err(CoreError::PackageNotFound {
                    id: name.to_string(),
                }),
            }
        }

        fn requirements(
            &self,
            name: &str,
            version: Option<&str>,
        ) -> Result<Vec<DependencyRequirement>> {
            let key = format!("{name}@{}", version.unwrap_or("*"));
            self.calls.lock().unwrap().push(format!("deps:{key}"));
            if self.fail_deps.iter().any(|f| f == &key) {
                return Err(CoreError::network("模拟依赖元数据源故障"));
            }
            Ok(self
                .deps
                .get(&key)
                .cloned()
                .unwrap_or_default()
                .into_iter()
                .map(|(n, r)| DependencyRequirement {
                    name: n,
                    requirement: r,
                    kind: DependencyKind::Runtime,
                })
                .collect())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::test_support::FakeAdapter;
    use super::*;

    fn query(name: &str, depth: u32) -> DependencyQuery {
        DependencyQuery {
            source: PackageSourceKind::Npm,
            name: name.to_string(),
            version: None,
            max_depth: depth,
            include_optional_kinds: false,
        }
    }

    #[test]
    fn builds_nested_tree_and_resolves_versions() {
        let adapter = FakeAdapter::new()
            .with_pkg("app", &["1.0.0"], &[("lib-a", "^2.0.0"), ("lib-b", "^1.0.0")])
            .with_pkg("lib-a", &["2.0.0", "2.1.0", "3.0.0"], &[("shared", "^1.0.0")])
            .with_pkg("lib-b", &["1.0.0"], &[])
            .with_pkg("shared", &["1.0.0", "1.2.0"], &[]);

        let tree = build_dependency_tree(&adapter, &query("app", 3)).expect("tree should build");

        assert_eq!(tree.root.resolved.as_deref(), Some("1.0.0"));
        assert_eq!(tree.root.children.len(), 2);
        // lib-a 应解析到满足 ^2.0.0 的最高版 2.1.0，而不是 3.0.0
        let lib_a = tree
            .root
            .children
            .iter()
            .find(|c| c.id.name == "lib-a")
            .unwrap();
        assert_eq!(lib_a.resolved.as_deref(), Some("2.1.0"));
        assert_eq!(lib_a.depth, 1);
        // 再下一层
        let shared = lib_a
            .children
            .iter()
            .find(|c| c.id.name == "shared")
            .unwrap();
        assert_eq!(shared.resolved.as_deref(), Some("1.2.0"));
        assert_eq!(shared.depth, 2);
        assert_eq!(tree.summary.total, 4);
        assert!(!tree.summary.has_problems());
    }

    #[test]
    fn respects_depth_limit_and_flags_truncation() {
        let adapter = FakeAdapter::new()
            .with_pkg("a", &["1.0.0"], &[("b", "^1.0.0")])
            .with_pkg("b", &["1.0.0"], &[("c", "^1.0.0")])
            .with_pkg("c", &["1.0.0"], &[]);

        let tree = build_dependency_tree(&adapter, &query("a", 1)).expect("tree should build");
        let b = &tree.root.children[0];
        assert_eq!(b.id.name, "b");
        // 深度 1 意味着不再展开 b 的子节点
        assert!(b.children.is_empty());
        assert!(tree.truncated);
    }

    #[test]
    fn detects_cycles_instead_of_looping_forever() {
        let adapter = FakeAdapter::new()
            .with_pkg("x", &["1.0.0"], &[("y", "^1.0.0")])
            .with_pkg("y", &["1.0.0"], &[("x", "^1.0.0")]);

        let tree = build_dependency_tree(&adapter, &query("x", 5)).expect("tree should build");
        let y = &tree.root.children[0];
        let x_again = &y.children[0];
        assert_eq!(x_again.id.name, "x");
        assert!(
            matches!(x_again.status, DependencyStatus::Cyclic { .. }),
            "重复出现的祖先包应标记为成环，实际为 {:?}",
            x_again.status
        );
        assert_eq!(tree.summary.cyclic, 1);
    }

    #[test]
    fn network_failure_degrades_node_without_killing_tree() {
        let adapter = FakeAdapter::new()
            .with_pkg("app", &["1.0.0"], &[("good", "^1.0.0"), ("flaky", "^1.0.0")])
            .with_pkg("good", &["1.0.0"], &[])
            .failing("flaky");

        let tree = build_dependency_tree(&adapter, &query("app", 2)).expect("树本身不应失败");

        // 好依赖正常解析
        let good = tree
            .root
            .children
            .iter()
            .find(|c| c.id.name == "good")
            .unwrap();
        assert_eq!(good.status, DependencyStatus::Resolved);
        // 故障依赖降级为"未验证"，并留下 warning
        let flaky = tree
            .root
            .children
            .iter()
            .find(|c| c.id.name == "flaky")
            .unwrap();
        assert!(matches!(
            flaky.status,
            DependencyStatus::Unverified { .. }
        ));
        assert!(tree.summary.has_problems());
        assert!(tree.warnings.iter().any(|w| w.contains("flaky")));
    }

    #[test]
    fn unreachable_root_still_returns_a_usable_tree() {
        let adapter = FakeAdapter::new().failing("ghost");
        let tree = build_dependency_tree(&adapter, &query("ghost", 2)).expect("应降级返回");
        assert!(matches!(
            tree.root.status,
            DependencyStatus::Unverified { .. }
        ));
        assert!(tree.root.children.is_empty());
        assert!(!tree.warnings.is_empty());
    }

    #[test]
    fn missing_version_range_is_reported_not_silently_dropped() {
        let adapter = FakeAdapter::new()
            .with_pkg("app", &["1.0.0"], &[("lib", "^9.0.0")])
            .with_pkg("lib", &["1.0.0", "1.1.0"], &[]);

        let tree = build_dependency_tree(&adapter, &query("app", 2)).expect("tree should build");
        let lib = &tree.root.children[0];
        assert!(matches!(lib.status, DependencyStatus::Missing { .. }));
        assert_eq!(tree.summary.missing, 1);
    }

    #[test]
    fn repeated_package_is_fetched_once_per_build() {
        let adapter = FakeAdapter::new()
            .with_pkg("app", &["1.0.0"], &[("common", "^1.0.0"), ("other", "^1.0.0")])
            .with_pkg("common", &["1.0.0"], &[])
            .with_pkg("other", &["1.0.0"], &[("common", "^1.0.0")]);

        build_dependency_tree(&adapter, &query("app", 3)).expect("tree should build");
        let calls = adapter.calls.lock().unwrap().clone();
        let common_calls = calls.iter().filter(|c| c.as_str() == "versions:common").count();
        assert_eq!(common_calls, 1, "同一包在一次构建中只应查询一次: {calls:?}");
    }

    #[test]
    fn descriptor_reports_platform_gating_and_never_probes() {
        // probe() 直接 panic：只要 descriptor() 不小心触发了探测，这个测试就会炸。
        struct WingetStub;
        impl PmAdapter for WingetStub {
            fn kind(&self) -> PackageSourceKind {
                PackageSourceKind::Winget
            }
            fn capabilities(&self) -> SourceCapabilities {
                SourceCapabilities::none()
            }
            fn install_hint(&self) -> String {
                "内置".into()
            }
            fn probe(&self) -> SourceAvailability {
                panic!("descriptor() 不应触发可用性探测（探测必须走注册表的 24h 缓存）");
            }
        }

        let view = WingetStub.descriptor();
        assert_eq!(view.kind, PackageSourceKind::Winget);
        assert_eq!(view.icon_key, "icon_winget");
        assert_eq!(
            view.supported_here,
            PackageSourceKind::Winget.supports_platform(Platform::host()),
            "平台门控结果必须与 supported_platforms 一致"
        );
        // 占位可用性，真实结果由注册表填充
        assert_eq!(view.availability.state, AvailabilityState::Unreachable);
    }
}
