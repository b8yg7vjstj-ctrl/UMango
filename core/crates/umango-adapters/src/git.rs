//! Git 仓库适配器。
//!
//! 把"包源"映射到 Git 仓库上：
//! - **搜索**走 GitHub 的 `search/repositories` API（真机实测可达，返回
//!   full_name / stars / description / license / topics）；
//! - **版本列表**用本机 `git ls-remote --tags` 读远端 tag，因此对任何 Git 主机
//!   （GitHub / GitLab / 自建）都适用，不依赖某个厂商的 API；
//! - **依赖解析**不适用（仓库没有统一的依赖元数据），能力位声明为 false，
//!   UI 对 git 源不展示依赖树，避免给出误导性结果；
//! - **下载量**这个指标对仓库不成立，直接使用 GitHub Stars（需求里指定的兜底指标
//!   在这里就是原生指标）。
//!
//! ⚠️ 本机环境特殊性：`github.com:443` 不可达，但 `api.github.com` 可达。
//! 因此"搜索"能用而"读 tag"会失败 —— 这正好验证了降级链路是否真的生效。

use serde_json::Value;
use umango_core::error::{CoreError, Result};
use umango_core::model::*;
use umango_core::process::{self, ProcessSpec};
use umango_core::{version as vcmp, PmAdapter};

use crate::http;

/// `git ls-remote` 的超时。GitHub 被墙时会一直挂着，必须设超时。
const LS_REMOTE_TIMEOUT_MS: u64 = 25_000;

pub struct GitAdapter;

impl GitAdapter {
    /// `owner/repo` 这类简写补全成可克隆的 URL。
    fn normalize_url(name: &str) -> String {
        let n = name.trim();
        if n.starts_with("http://")
            || n.starts_with("https://")
            || n.starts_with("git://")
            || n.starts_with("ssh://")
            || n.starts_with("git@")
            || n.starts_with("file://")
        {
            return n.to_string();
        }
        format!("https://github.com/{}", n.trim_matches('/'))
    }

    fn slug_of(name: &str) -> Option<String> {
        http::slug_from_repo_string(name)
    }

    /// 解析 `git ls-remote --tags --refs` 的输出。
    ///
    /// 输出形如：`<sha>\trefs/tags/v1.2.3`，`--refs` 会过滤掉 `^{}` 解引用行。
    fn parse_tags(stdout: &str) -> Vec<PackageVersion> {
        let mut versions: Vec<PackageVersion> = Vec::new();
        for line in stdout.lines() {
            let Some((_sha, reference)) = line.split_once('\t') else {
                continue;
            };
            let Some(tag) = reference.trim().strip_prefix("refs/tags/") else {
                continue;
            };
            let tag = tag.trim();
            if tag.is_empty() || tag.ends_with("^{}") {
                continue;
            }
            let mut pv = PackageVersion::new(tag);
            pv.note = Some(format!("git tag `{tag}`"));
            versions.push(pv);
        }
        versions.sort_by(|a, b| vcmp::compare(&b.version, &a.version));
        versions.dedup_by(|a, b| a.version == b.version);
        versions
    }
}

impl PmAdapter for GitAdapter {
    fn kind(&self) -> PackageSourceKind {
        PackageSourceKind::Git
    }

    fn capabilities(&self) -> SourceCapabilities {
        SourceCapabilities {
            search: true,
            list_versions: true,
            // 仓库没有统一依赖元数据，明确声明不支持
            resolve_deps: false,
            official_downloads: false,
            install: true,
            uninstall: false,
            upgrade: true,
        }
    }

    fn install_hint(&self) -> String {
        "安装 Git：https://git-scm.com/downloads（macOS 可执行 `xcode-select --install`）".to_string()
    }

    fn search(&self, query: &str, limit: usize) -> Result<Vec<PackageMeta>> {
        let url = format!(
            "https://api.github.com/search/repositories?q={}&sort=stars&order=desc&per_page={}",
            urlencode(query),
            limit.clamp(1, 50)
        );
        let json = http::get_json_cached(&url, 1800, 20_000)?;
        let items = json
            .get("items")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();

        let mut out = Vec::new();
        for item in items {
            let Some(full_name) = item.get("full_name").and_then(Value::as_str) else {
                continue;
            };
            let mut meta = PackageMeta::new(PackageId::new(PackageSourceKind::Git, full_name), full_name);
            meta.summary = item
                .get("description")
                .and_then(Value::as_str)
                .map(str::to_string);
            meta.homepage = item
                .get("homepage")
                .and_then(Value::as_str)
                .filter(|s| !s.is_empty())
                .map(str::to_string);
            meta.repository = Some(format!("https://github.com/{full_name}"));
            meta.license = item
                .get("license")
                .and_then(|l| l.get("spdx_id"))
                .and_then(Value::as_str)
                .map(str::to_string);
            meta.updated_at = item
                .get("pushed_at")
                .or_else(|| item.get("updated_at"))
                .and_then(Value::as_str)
                .map(str::to_string);
            meta.keywords = item
                .get("topics")
                .and_then(Value::as_array)
                .map(|a| {
                    a.iter()
                        .filter_map(Value::as_str)
                        .take(6)
                        .map(str::to_string)
                        .collect()
                })
                .unwrap_or_default();
            // 仓库的默认分支最近 tag 需要额外一次 ls-remote，这里不预先取；
            // 卡片正文用 star 数（仓库场景下的原生指标）
            meta.downloads = match item.get("stargazers_count").and_then(Value::as_u64) {
                Some(stars) => DownloadStats::GitHubStars {
                    count: stars,
                    repo: full_name.to_string(),
                },
                None => DownloadStats::unknown("GitHub 未返回 star 数"),
            };
            out.push(meta);
        }
        Ok(out)
    }

    fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
        if process::runner().which("git").is_none() {
            return Err(CoreError::cli_missing("git", Some(&self.install_hint())));
        }
        let url = Self::normalize_url(name);
        let spec = ProcessSpec::new("git")
            .args(["ls-remote", "--tags", "--refs", &url])
            .timeout_ms(LS_REMOTE_TIMEOUT_MS);
        let out = process::run(&spec)?;

        if out.timed_out {
            return Err(CoreError::Network {
                reason: format!("读取 {url} 的 tag 超时（{LS_REMOTE_TIMEOUT_MS}ms）"),
                url: Some(url),
            });
        }
        if !out.success() {
            // 网络不可达 / 仓库不存在 / 需要认证，都在这里区分
            let stderr = out.stderr.trim();
            let lower = stderr.to_ascii_lowercase();
            if lower.contains("could not resolve host")
                || lower.contains("connection")
                || lower.contains("timed out")
                || lower.contains("unable to access")
            {
                return Err(CoreError::Network {
                    reason: format!("无法访问 {url}：{}", process::preview(stderr, 200)),
                    url: Some(url),
                });
            }
            if lower.contains("repository not found") || lower.contains("not found") {
                return Err(CoreError::PackageNotFound { id: url });
            }
            return Err(CoreError::CommandFailed {
                context: format!("git ls-remote {url}"),
                code: Some(out.status),
                stderr: process::preview(stderr, 200),
                stdout: String::new(),
            });
        }

        let versions = Self::parse_tags(&out.stdout);
        if versions.is_empty() {
            // 仓库存在但没有任何 tag —— 这不是错误，UI 要能区分"没有 tag"与"失败"
            return Err(CoreError::Other(format!(
                "仓库 {url} 没有任何 tag（可能只用分支发布）"
            )));
        }
        Ok(versions)
    }

    fn downloads(&self, name: &str, repository: Option<&str>) -> DownloadStats {
        let slug = Self::slug_of(repository.unwrap_or(name)).or_else(|| Self::slug_of(name));
        match slug {
            Some(slug) => match http::github_stars(&slug) {
                Ok(stars) => DownloadStats::GitHubStars { count: stars, repo: slug },
                Err(e) => DownloadStats::unknown(format!("GitHub Stars 查询失败：{e}")),
            },
            None => DownloadStats::unknown("无法从仓库地址解析出 GitHub owner/repo"),
        }
    }
}

fn urlencode(s: &str) -> String {
    s.chars()
        .map(|c| match c {
            'a'..='z' | 'A'..='Z' | '0'..='9' | '-' | '_' | '.' | '~' => c.to_string(),
            ' ' => "+".to_string(),
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_repo_names_to_urls() {
        assert_eq!(
            GitAdapter::normalize_url("facebook/react"),
            "https://github.com/facebook/react"
        );
        assert_eq!(
            GitAdapter::normalize_url("https://gitlab.com/foo/bar.git"),
            "https://gitlab.com/foo/bar.git"
        );
        assert_eq!(
            GitAdapter::normalize_url("git@github.com:foo/bar.git"),
            "git@github.com:foo/bar.git"
        );
    }

    #[test]
    fn parses_ls_remote_tag_output() {
        // 真实 `git ls-remote --tags --refs` 的输出格式
        let stdout = "\
a1b2c3d4e5f6\trefs/tags/v1.0.0
b2c3d4e5f6a1\trefs/tags/v1.2.0
c3d4e5f6a1b2\trefs/tags/v1.10.0
d4e5f6a1b2c3\trefs/tags/v2.0.0-rc.1
e5f6a1b2c3d4\trefs/tags/nightly
";
        let versions = GitAdapter::parse_tags(stdout);
        assert_eq!(versions.len(), 5);
        // 版本号降序：1.10.0 必须排在 1.2.0 之前（数字比较而不是字符串比较）
        assert_eq!(versions[0].version, "v2.0.0-rc.1");
        assert_eq!(versions[1].version, "v1.10.0");
        assert_eq!(versions[2].version, "v1.2.0");
        // 通道判定
        assert_eq!(versions[0].channel, ReleaseChannel::Prerelease);
        let nightly = versions.iter().find(|v| v.version == "nightly").unwrap();
        assert_eq!(nightly.channel, ReleaseChannel::Snapshot);
    }

    #[test]
    fn parse_tags_ignores_dereference_and_junk_lines() {
        let stdout = "\
a1\trefs/tags/v1.0.0
b2\trefs/tags/v1.0.0^{}
乱码行没有制表符
c3\trefs/heads/main
";
        let versions = GitAdapter::parse_tags(stdout);
        assert_eq!(versions.len(), 1, "只应保留 refs/tags 且非解引用的行");
        assert_eq!(versions[0].version, "v1.0.0");
    }

    #[test]
    fn parse_tags_dedups_same_version() {
        let stdout = "a1\trefs/tags/v1.0.0\nb2\trefs/tags/v1.0.0\n";
        assert_eq!(GitAdapter::parse_tags(stdout).len(), 1);
    }

    /// 真机测试：GitHub 仓库搜索（api.github.com 在本机可达）。
    #[test]
    fn github_search_returns_repos_with_stars() {
        let adapter = GitAdapter;
        match adapter.search("winget", 3) {
            Ok(results) => {
                assert!(!results.is_empty());
                let first = &results[0];
                assert!(first.id.name.contains('/'), "仓库名应为 owner/repo 形式");
                assert!(matches!(first.downloads, DownloadStats::GitHubStars { .. }));
                // stars 是仓库场景的指标，不应被标成"下载量"
                assert_eq!(first.downloads.metric_name(), "GitHub Stars");
            }
            Err(e) => eprintln!("跳过：GitHub 搜索不可达（{e}）"),
        }
    }

    /// 真机测试：依赖树能力必须明确声明为不支持。
    #[test]
    fn git_declares_no_dependency_support() {
        let caps = GitAdapter.capabilities();
        assert!(!caps.resolve_deps, "git 仓库没有统一依赖元数据");
        assert!(caps.list_versions);
        assert!(!caps.official_downloads);
    }

    /// 真机测试：用真实远端仓库验证 `git ls-remote` 读取 tag 的完整链路。
    ///
    /// 这条测试此前长期无法验证 —— 开发环境一度访问不到 github.com，
    /// `ls-remote` 必然失败（那时它验证的是降级分支）。网络恢复后它才真正执行。
    #[test]
    fn git_versions_from_real_remote() {
        let adapter = GitAdapter;
        if adapter.probe().state != AvailabilityState::Available {
            eprintln!("跳过：本机没有可用的 git");
            return;
        }
        match adapter.versions("jonschlinkert/is-number") {
            Ok(versions) => {
                assert!(!versions.is_empty(), "真实仓库应有 tag");
                // 该仓库确实有 7.0.0 这个 tag
                assert!(
                    versions.iter().any(|v| v.version.contains("7.0.0")),
                    "应包含 7.0.0，实际: {:?}",
                    versions.iter().map(|v| &v.version).take(8).collect::<Vec<_>>()
                );
                // 必须按新到旧排序
                assert!(
                    vcmp::compare(&versions[0].version, &versions[versions.len() - 1].version)
                        != std::cmp::Ordering::Less
                );
                // tag 名不带 v 前缀时也应被正确分类
                assert!(versions
                    .iter()
                    .any(|v| v.channel == ReleaseChannel::Stable));
            }
            // 网络再次不可用时不算失败，但必须打印原因，避免"静默通过"
            Err(e) => eprintln!("跳过（远端不可达）：{e}"),
        }
    }

    /// 真机测试：短写法的仓库名应被自动补全成可克隆 URL。
    #[test]
    fn git_normalizes_short_name_for_real_remote() {
        let adapter = GitAdapter;
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        // 故意用 "owner/repo" 简写，适配器内部应补全为 https://github.com/...
        let result = adapter.versions("jonschlinkert/is-odd");
        match result {
            Ok(versions) => assert!(!versions.is_empty()),
            Err(e) => eprintln!("跳过（远端不可达）：{e}"),
        }
    }

    /// 真机测试：不存在的仓库应给出明确的错误分类，而不是含糊失败。
    #[test]
    fn git_missing_repo_is_classified() {
        let adapter = GitAdapter;
        if adapter.probe().state != AvailabilityState::Available {
            return;
        }
        let err = adapter
            .versions("umango-nonexistent-owner-xyz/nonexistent-repo-abc")
            .expect_err("不存在的仓库应报错");
        // 允许两种合理分类：仓库不存在，或远端访问失败（取决于远端如何回应）
        assert!(
            matches!(err.code(), "package_not_found" | "network_error" | "command_failed"),
            "错误分类不合理: {} / {}",
            err.code(),
            err
        );
    }
}
