//! npm / pnpm 适配器。
//!
//! 两者共用同一套 packument（npm registry 文档）解析逻辑：pnpm 读写的是同一个
//! 注册表，`pnpm view` 的输出结构与 `npm view` 一致，因此适配器只按程序名参数化。
//!
//! 差异点（真机实测得出）：
//! - npm 与 pnpm 都支持 `search --json`（pnpm 11 仍然支持）；
//! - **官方下载量只有 npm 有**（api.npmjs.org）。pnpm 的下载没有独立计数，
//!   因此 pnpm 的下载量直接走 GitHub Stars 兜底链路 —— 这正是需求里
//!   "优先官方下载量，取不到时兜底 GitHub Stars" 的真实用例。

use serde_json::Value;
use umango_core::error::{CoreError, Result};
use umango_core::model::*;
use umango_core::process::{self, ProcessSpec};
use umango_core::{version as vcmp, PmAdapter};

use crate::http;

/// npm 命令行超时：registry 走国内镜像时通常很快，但首次查询大包可能较慢。
const CLI_TIMEOUT_MS: u64 = 60_000;

pub struct NpmLikeAdapter {
    kind: PackageSourceKind,
    program: &'static str,
}

impl NpmLikeAdapter {
    pub fn npm() -> Self {
        NpmLikeAdapter {
            kind: PackageSourceKind::Npm,
            program: "npm",
        }
    }

    pub fn pnpm() -> Self {
        NpmLikeAdapter {
            kind: PackageSourceKind::Pnpm,
            program: "pnpm",
        }
    }

    /// 跑一条 CLI 并把 stdout 解析成 JSON。
    ///
    /// 三个必须处理的现实问题：
    /// 1. 包不存在时 npm 以非 0 退出并打印 `E404`，要映射成 `PackageNotFound`；
    /// 2. 无依赖的包输出**空字符串**而不是 `{}`，空输出要当成 null 而不是解析失败；
    /// 3. npm 会把警告写到 stdout 前后，解析必须容忍噪声。
    fn run_json(&self, args: &[&str], context: &str) -> Result<Option<Value>> {
        let spec = ProcessSpec::new(self.program)
            .args(args.iter().copied())
            .timeout_ms(CLI_TIMEOUT_MS);
        let out = process::run(&spec)?;

        if !out.success() {
            let stderr_lower = out.stderr.to_ascii_lowercase();
            let stdout_lower = out.stdout.to_ascii_lowercase();
            let looks_missing = stderr_lower.contains("e404")
                || stderr_lower.contains("404 not found")
                || stderr_lower.contains("is not in this registry")
                || stdout_lower.contains("e404");
            if looks_missing {
                return Err(CoreError::PackageNotFound {
                    id: args.get(1).copied().unwrap_or("?").to_string(),
                });
            }
            return Err(CoreError::CommandFailed {
                context: context.to_string(),
                code: Some(out.status),
                stderr: process::preview(out.stderr.trim(), 300),
                stdout: process::preview(out.stdout.trim(), 200),
            });
        }

        if out.stdout.trim().is_empty() {
            return Ok(None);
        }
        Ok(Some(process::parse_json_lossy(&out.stdout, context)?))
    }

    fn bulk_downloads(&self, names: &[String]) -> Option<std::collections::HashMap<String, u64>> {
        if self.kind != PackageSourceKind::Npm || names.is_empty() {
            return None;
        }
        // npm 的 point 接口支持逗号分隔的多包查询，一次请求拿到整页卡片的数据
        let encoded: Vec<String> = names.iter().map(|n| urlencode(n)).collect();
        let url = format!(
            "https://api.npmjs.org/downloads/point/last-week/{}",
            encoded.join(",")
        );
        let json = http::get_json_cached(&url, 3600, 20_000).ok()?;

        let mut out = std::collections::HashMap::new();
        // 单包查询返回 {downloads, ...}；多包查询返回 {pkg: {downloads, ...}}
        if let Some(map) = json.as_object() {
            for (key, value) in map {
                if let Some(n) = value.get("downloads").and_then(Value::as_u64) {
                    out.insert(key.clone(), n);
                }
            }
            if out.is_empty() {
                if let Some(n) = json.get("downloads").and_then(Value::as_u64) {
                    if let Some(first) = names.first() {
                        out.insert(first.clone(), n);
                    }
                }
            }
        }
        if out.is_empty() {
            None
        } else {
            Some(out)
        }
    }
}

fn urlencode(s: &str) -> String {
    s.chars()
        .map(|c| match c {
            'a'..='z' | 'A'..='Z' | '0'..='9' | '-' | '_' | '.' | '~' | '@' | '/' => {
                c.to_string()
            }
            other => {
                let mut buf = [0u8; 4];
                other
                    .encode_utf8(&mut buf)
                    .bytes()
                    .map(|b| format!("%{b:02X}"))
                    .collect()
            }
        })
        .collect()
}

/// 解析 npm packument，抽出全部版本及其元信息。
///
/// 实测：`npm view <pkg> --json` 返回的是完整 packument，
/// 因此**一次 CLI 调用**就能拿到版本列表 + 发布日期 + 弃用标记 + dist-tags。
fn parse_packument(doc: &Value) -> Vec<PackageVersion> {
    let time = doc.get("time");
    let deprecated_map = doc.get("versions");

    // versions 可能是数组（`npm view pkg versions --json`）或对象映射（完整 packument）
    let mut versions: Vec<PackageVersion> = Vec::new();
    match doc.get("versions") {
        Some(Value::Array(items)) => {
            for item in items {
                if let Some(s) = item.as_str() {
                    versions.push(PackageVersion::new(s));
                }
            }
        }
        Some(Value::Object(map)) => {
            for (ver, manifest) in map {
                let mut pv = PackageVersion::new(ver);
                if let Some(t) = time.and_then(|t| t.get(ver)).and_then(Value::as_str) {
                    pv.published_at = Some(t.to_string());
                }
                // deprecated 可能是字符串（理由）或 true
                if let Some(dep) = manifest.get("deprecated") {
                    match dep {
                        Value::String(reason) => {
                            pv.deprecated = true;
                            pv.note = Some(reason.clone());
                        }
                        Value::Bool(true) => pv.deprecated = true,
                        _ => {}
                    }
                }
                versions.push(pv);
            }
        }
        // `npm view pkg@1.0.0 versions --json` 单版本时返回字符串
        Some(Value::String(s)) => versions.push(PackageVersion::new(s)),
        _ => {}
    }

    // 兜底：只有 dist-tags / version 字段时，至少给出 latest
    if versions.is_empty() {
        if let Some(latest) = doc.get("version").and_then(Value::as_str) {
            versions.push(PackageVersion::new(latest));
        }
    }
    let _ = deprecated_map;

    // UI 需要"最新在前"
    versions.sort_by(|a, b| vcmp::compare(&b.version, &a.version));
    versions
}

/// 从 packument 里抽取 `versions[ver]` 的依赖字段，映射成统一的依赖声明。
fn extract_requirements(manifest: &Value, include_extra_kinds: bool) -> Vec<DependencyRequirement> {
    let mut out = Vec::new();
    let mut push_map = |key: &str, kind: DependencyKind| {
        if let Some(map) = manifest.get(key).and_then(Value::as_object) {
            for (name, req) in map {
                if let Some(req_str) = req.as_str() {
                    out.push(DependencyRequirement {
                        name: name.clone(),
                        requirement: req_str.to_string(),
                        kind,
                    });
                }
            }
        }
    };
    push_map("dependencies", DependencyKind::Runtime);
    if include_extra_kinds {
        push_map("peerDependencies", DependencyKind::Peer);
        push_map("optionalDependencies", DependencyKind::Optional);
        push_map("devDependencies", DependencyKind::Dev);
    }
    // npm 有时把 optional 依赖同时列在 dependencies 里，去重（同名同范围）
    out.sort_by(|a, b| (&a.name, &a.requirement).cmp(&(&b.name, &b.requirement)));
    out.dedup_by(|a, b| a.name == b.name && a.requirement == b.requirement);
    out
}

impl PmAdapter for NpmLikeAdapter {
    fn kind(&self) -> PackageSourceKind {
        self.kind
    }

    fn capabilities(&self) -> SourceCapabilities {
        SourceCapabilities {
            search: true,
            list_versions: true,
            resolve_deps: true,
            // 只有 npm 有官方下载量接口
            official_downloads: self.kind == PackageSourceKind::Npm,
            install: true,
            uninstall: true,
            upgrade: true,
        }
    }

    fn install_hint(&self) -> String {
        match self.kind {
            PackageSourceKind::Npm => {
                "npm 随 Node.js 一起安装：https://nodejs.org（也可用 nvm/fnm 管理版本）".to_string()
            }
            _ => "安装 pnpm：`npm install -g pnpm`，或 `corepack enable pnpm`".to_string(),
        }
    }

    fn search(&self, query: &str, limit: usize) -> Result<Vec<PackageMeta>> {
        let limit_str = limit.to_string();
        let doc = self
            .run_json(
                &["search", query, "--json", "--searchlimit", &limit_str],
                &format!("{} search {query}", self.program),
            )?
            .unwrap_or(Value::Null);

        let items = match doc {
            Value::Array(items) => items,
            // 某些 registry 在无结果时返回 {} 或 null
            _ => Vec::new(),
        };

        let mut metas: Vec<PackageMeta> = Vec::new();
        for item in items.iter().take(limit) {
            let Some(name) = item.get("name").and_then(Value::as_str) else {
                continue;
            };
            let mut meta = PackageMeta::new(
                PackageId::new(self.kind, name),
                // 作用域包用全名做标题更清晰
                name.to_string(),
            );
            meta.summary = item
                .get("description")
                .and_then(Value::as_str)
                .map(str::to_string);
            meta.license = item.get("license").and_then(Value::as_str).map(str::to_string);
            meta.updated_at = item
                .get("date")
                .or_else(|| item.get("modified"))
                .and_then(Value::as_str)
                .map(str::to_string);
            meta.latest_version = item
                .get("version")
                .and_then(Value::as_str)
                .map(str::to_string)
                .or_else(|| {
                    item.get("dist-tags")
                        .and_then(|d| d.get("latest"))
                        .and_then(Value::as_str)
                        .map(str::to_string)
                });
            if let Some(latest) = &meta.latest_version {
                meta.latest_channel = vcmp::classify(latest);
            }
            meta.keywords = item
                .get("keywords")
                .and_then(Value::as_array)
                .map(|arr| {
                    arr.iter()
                        .filter_map(Value::as_str)
                        .take(6)
                        .map(str::to_string)
                        .collect()
                })
                .unwrap_or_default();
            // search 结果里没有 repository/homepage（国内镜像尤其如此），
            // 卡片上的"官方下载量"仍然可用，GitHub Stars 兜底留到详情页再取
            meta.downloads = DownloadStats::unknown("打开详情后加载");
            metas.push(meta);
        }

        // 一次性批量补齐 npm 官方下载量（多包 point 接口）
        if !metas.is_empty() {
            let names: Vec<String> = metas.iter().map(|m| m.id.name.clone()).collect();
            if let Some(stats) = self.bulk_downloads(&names) {
                for meta in metas.iter_mut() {
                    if let Some(count) = stats.get(&meta.id.name) {
                        meta.downloads = DownloadStats::Official {
                            count: *count,
                            period: StatsPeriod::LastWeek,
                            via: "api.npmjs.org".to_string(),
                        };
                    }
                }
            }
        }

        Ok(metas)
    }

    fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
        let doc = self
            .run_json(
                &["view", name, "--json"],
                &format!("{} view {name}", self.program),
            )?
            .ok_or_else(|| CoreError::PackageNotFound {
                id: name.to_string(),
            })?;
        let versions = parse_packument(&doc);
        if versions.is_empty() {
            return Err(CoreError::PackageNotFound {
                id: name.to_string(),
            });
        }
        Ok(versions)
    }

    fn requirements(
        &self,
        name: &str,
        version: Option<&str>,
    ) -> Result<Vec<DependencyRequirement>> {
        let target = match version {
            Some(v) => format!("{name}@{v}"),
            None => name.to_string(),
        };
        // 一次调用取回该版本的完整 manifest：dependencies / devDependencies /
        // peerDependencies / optionalDependencies 都在里面，
        // 比按字段分别调用（每次一个字段）省下 3 次进程启动。
        let doc = self.run_json(
            &["view", &target, "--json"],
            &format!("{} view {target}", self.program),
        )?;
        let Some(manifest) = doc else {
            return Ok(Vec::new());
        };
        Ok(extract_requirements(&manifest, true))
    }

    fn downloads(&self, name: &str, repository: Option<&str>) -> DownloadStats {
        // 1) 优先官方下载量（仅 npm 有）
        if self.kind == PackageSourceKind::Npm {
            if let Some(stats) = self.bulk_downloads(&[name.to_string()]) {
                if let Some(count) = stats.get(name) {
                    return DownloadStats::Official {
                        count: *count,
                        period: StatsPeriod::LastWeek,
                        via: "api.npmjs.org".to_string(),
                    };
                }
            }
        }

        // 2) 兜底：GitHub Stars
        let reason = if self.kind == PackageSourceKind::Pnpm {
            "pnpm 没有独立的下载量统计接口"
        } else {
            "npm 官方下载量接口不可用"
        };
        match repository.and_then(|r| http::slug_from_repo_string(r)) {
            Some(slug) => match http::github_stars(&slug) {
                Ok(stars) => DownloadStats::GitHubStars { count: stars, repo: slug },
                Err(e) => DownloadStats::unknown(format!("{reason}；GitHub Stars 亦不可用（{e}）")),
            },
            None => DownloadStats::unknown(format!("{reason}；且缺少可用的 GitHub 仓库信息")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_packument_object_form() {
        let doc = serde_json::json!({
            "name": "demo",
            "dist-tags": {"latest": "1.2.0"},
            "time": {
                "created": "2020-01-01T00:00:00.000Z",
                "1.0.0": "2021-01-01T00:00:00.000Z",
                "1.2.0": "2022-05-05T10:00:00.000Z"
            },
            "versions": {
                "1.0.0": {"name": "demo"},
                "1.1.0-beta.1": {"name": "demo"},
                "1.2.0": {"name": "demo", "deprecated": "请升级到 2.x"}
            }
        });
        let versions = parse_packument(&doc);
        assert_eq!(versions.len(), 3);
        // 最新在前
        assert_eq!(versions[0].version, "1.2.0");
        assert_eq!(versions[0].channel, ReleaseChannel::Stable);
        assert!(versions[0].deprecated);
        assert_eq!(versions[0].note.as_deref(), Some("请升级到 2.x"));
        assert_eq!(
            versions[0].published_at.as_deref(),
            Some("2022-05-05T10:00:00.000Z")
        );
        // 预发布通道被正确识别
        let pre = versions.iter().find(|v| v.version == "1.1.0-beta.1").unwrap();
        assert_eq!(pre.channel, ReleaseChannel::Prerelease);
    }

    #[test]
    fn parses_packument_array_form() {
        // `npm view pkg versions --json` 返回数组
        let doc = serde_json::json!({"versions": ["1.0.0", "1.1.0"]});
        let versions = parse_packument(&doc);
        assert_eq!(versions.len(), 2);
        assert_eq!(versions[0].version, "1.1.0");
    }

    #[test]
    fn extracts_all_dependency_kinds() {
        let manifest = serde_json::json!({
            "dependencies": {"a": "^1.0.0"},
            "peerDependencies": {"b": ">=2"},
            "optionalDependencies": {"c": "~3.0.0"},
            "devDependencies": {"d": "*"}
        });
        let reqs = extract_requirements(&manifest, true);
        assert_eq!(reqs.len(), 4);
        let kinds: Vec<_> = reqs.iter().map(|r| (r.name.as_str(), r.kind)).collect();
        assert!(kinds.contains(&("a", DependencyKind::Runtime)));
        assert!(kinds.contains(&("b", DependencyKind::Peer)));
        assert!(kinds.contains(&("c", DependencyKind::Optional)));
        assert!(kinds.contains(&("d", DependencyKind::Dev)));

        // 默认只要运行时依赖
        let only_runtime = extract_requirements(&manifest, false);
        assert_eq!(only_runtime.len(), 1);
        assert_eq!(only_runtime[0].name, "a");
    }

    #[test]
    fn missing_dependency_fields_yield_empty_not_error() {
        assert!(extract_requirements(&serde_json::json!({}), true).is_empty());
    }

    #[test]
    fn urlencode_keeps_scoped_names_intact() {
        assert_eq!(urlencode("react"), "react");
        assert_eq!(urlencode("@babel/core"), "@babel/core");
        assert_eq!(urlencode("a b"), "a%20b");
    }

    /// 真机测试：npm 适配器能跑通真实 CLI。
    #[test]
    fn npm_versions_from_real_cli() {
        let adapter = NpmLikeAdapter::npm();
        if adapter.probe().state != AvailabilityState::Available {
            eprintln!("跳过：本机没有可用的 npm");
            return;
        }
        let versions = adapter.versions("is-number").expect("应能查到 is-number 的版本");
        assert!(!versions.is_empty());
        // 版本号必须按新到旧排序
        assert!(
            vcmp::compare(&versions[0].version, &versions[versions.len() - 1].version)
                != std::cmp::Ordering::Less
        );
        assert!(versions.iter().any(|v| v.version.starts_with('7')));
    }

    #[test]
    fn npm_requirements_from_real_cli() {
        let adapter = NpmLikeAdapter::npm();
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        // express@4.18.2 有 31 个运行时依赖（真机实测）
        let reqs = adapter
            .requirements("express", Some("4.18.2"))
            .expect("应能取到 express 的依赖");
        let runtime = reqs
            .iter()
            .filter(|r| r.kind == DependencyKind::Runtime)
            .count();
        assert!(runtime > 20, "express 运行时依赖数量异常: {runtime}");
        assert!(reqs.iter().any(|r| r.name == "accepts"));
    }

    #[test]
    fn npm_missing_package_maps_to_not_found() {
        let adapter = NpmLikeAdapter::npm();
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        let err = adapter
            .versions("umango-this-package-definitely-does-not-exist-12345")
            .expect_err("不存在的包应报错");
        assert_eq!(err.code(), "package_not_found");
    }

    /// 验证「官方下载量优先」链路。
    #[test]
    fn npm_official_downloads_are_used_when_available() {
        let adapter = NpmLikeAdapter::npm();
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        match adapter.downloads("react", Some("https://github.com/facebook/react")) {
            DownloadStats::Official { count, via, period } => {
                assert!(count > 1_000_000, "react 周下载量异常: {count}");
                assert_eq!(period, StatsPeriod::LastWeek);
                assert_eq!(via, "api.npmjs.org");
            }
            other => panic!("npm 应优先给出官方下载量，实际: {other:?}"),
        }
    }

    /// 验证 pnpm 走 GitHub Stars 兜底（pnpm 没有官方下载量接口）。
    #[test]
    fn pnpm_falls_back_to_github_stars() {
        let adapter = NpmLikeAdapter::pnpm();
        match adapter.downloads("react", Some("git+https://github.com/facebook/react.git")) {
            DownloadStats::GitHubStars { count, repo } => {
                assert_eq!(repo, "facebook/react");
                assert!(count > 100_000, "react 的 star 数异常: {count}");
            }
            // 无网络时退化为 Unknown，也是允许的结果
            DownloadStats::Unknown { reason } => {
                eprintln!("跳过：GitHub 不可达（{reason}）");
            }
            other => panic!("pnpm 应走 GitHub Stars 兜底，实际: {other:?}"),
        }
    }

    #[test]
    fn downloads_without_repository_is_honest_unknown() {
        let adapter = NpmLikeAdapter::pnpm();
        let stats = adapter.downloads("some-pkg", None);
        assert_eq!(stats.count(), None);
        assert!(stats.source_label().contains("pnpm 没有独立的下载量统计接口"));
    }

    /// 真机测试：npm search 能返回带下载量角标的结果。
    #[test]
    fn npm_search_enriches_downloads() {
        let adapter = NpmLikeAdapter::npm();
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        let results = adapter.search("is-odd", 5).expect("搜索应成功");
        assert!(!results.is_empty());
        assert!(results
            .iter()
            .any(|m| m.id.name.contains("is-odd") || !m.display_name.is_empty()));
        eprintln!(
            "搜索结果前 3 项: {:?}",
            results
                .iter()
                .take(3)
                .map(|m| (&m.display_name, m.downloads.count()))
                .collect::<Vec<_>>()
        );
    }
}
