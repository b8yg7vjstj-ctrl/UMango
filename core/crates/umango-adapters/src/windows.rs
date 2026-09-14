//! Windows 专属包源适配器：Scoop 与 WinGet。
//!
//! # ⚠️ 验证状态声明（重要，不要在别处含糊其辞）
//!
//! 本模块在 macOS 上开发，而 `scoop` 与 `winget` **只存在于 Windows**，
//! 因此这里的命令调用与解析逻辑**没有在真机上验证过**。
//!
//! 处理方式（而不是假装它是对的）：
//! 1. 两个适配器都受平台门控保护（[`PackageSourceKind::supported_platforms`]），
//!    在非 Windows 上根本不会被调用，不会污染其他平台的任何结果；
//! 2. [`PmAdapter::descriptor`] 被覆盖，向 UI 暴露 `verification_note`，
//!    界面上会显示"解析逻辑未在真机验证"的提示；
//! 3. 解析器写成**宽容多形态**：同时接受几种已知的输出结构，
//!    遇到完全不认识的输出时抛出带**原始输出片段**的 Parse 错误，
//!    这样 Windows 用户第一次运行就能直接把真实格式贴出来，而不是默默给错数据；
//! 4. 单测用"文档描述的输出样本"覆盖解析分支（见文件末尾），
//!    真机验证后应把这些样本替换成真实抓取结果。

use serde_json::Value;
use umango_core::error::{CoreError, Result};
use umango_core::model::*;
use umango_core::process::{self, ProcessSpec};
use umango_core::{version as vcmp, PmAdapter};

use crate::http;

/// Windows 上首次调用 winget/scoop 可能触发源更新，超时给足。
const WIN_CLI_TIMEOUT_MS: u64 = 120_000;

const UNVERIFIED_NOTE: &str =
    "该包源的解析逻辑尚未在 Windows 真机验证（本项目在 macOS 上开发）。若结果异常，请把原始 CLI 输出反馈给项目。";

// ---------------------------------------------------------------------------
// WinGet
// ---------------------------------------------------------------------------

pub struct WingetAdapter;

impl WingetAdapter {
    fn run(&self, args: &[&str], context: &str) -> Result<String> {
        let spec = ProcessSpec::new("winget")
            .args(args.iter().copied())
            .timeout_ms(WIN_CLI_TIMEOUT_MS);
        let out = process::run(&spec)?;
        if !out.success() {
            let stderr = out.stderr.to_ascii_lowercase();
            if stderr.contains("no package found") || stderr.contains("找不到") {
                return Err(CoreError::PackageNotFound {
                    id: context.to_string(),
                });
            }
            return Err(CoreError::CommandFailed {
                context: context.to_string(),
                code: Some(out.status),
                stderr: process::preview(out.stderr.trim(), 300),
                stdout: process::preview(out.stdout.trim(), 200),
            });
        }
        Ok(out.stdout)
    }

    /// 宽容解析 winget 的 JSON 输出。
    ///
    /// 已知可能出现的三种结构（不同 winget 版本/子命令不一致）：
    /// 1. `{"Sources":[{"Packages":[{...}]}]}`  —— `winget search --output json`
    /// 2. `{"Packages":[{...}]}`
    /// 3. `[{...}]` 扁平数组
    fn extract_packages(json: &Value) -> Vec<Value> {
        if let Some(arr) = json.as_array() {
            return arr.clone();
        }
        if let Some(sources) = json.get("Sources").and_then(Value::as_array) {
            let mut out = Vec::new();
            for source in sources {
                if let Some(pkgs) = source.get("Packages").and_then(Value::as_array) {
                    out.extend(pkgs.iter().cloned());
                }
            }
            if !out.is_empty() {
                return out;
            }
        }
        if let Some(pkgs) = json.get("Packages").and_then(Value::as_array) {
            return pkgs.clone();
        }
        Vec::new()
    }

    /// 字段名在不同 winget 版本里拼写不一致，逐个尝试。
    fn pick<'a>(item: &'a Value, keys: &[&str]) -> Option<&'a str> {
        for key in keys {
            if let Some(s) = item.get(*key).and_then(Value::as_str) {
                if !s.trim().is_empty() {
                    return Some(s);
                }
            }
        }
        None
    }
}

impl PmAdapter for WingetAdapter {
    fn kind(&self) -> PackageSourceKind {
        PackageSourceKind::Winget
    }

    fn capabilities(&self) -> SourceCapabilities {
        SourceCapabilities {
            search: true,
            list_versions: true,
            // winget 不暴露依赖关系
            resolve_deps: false,
            // winget 没有公开的下载量接口 -> 走 GitHub Stars 兜底
            official_downloads: false,
            install: true,
            uninstall: true,
            upgrade: true,
        }
    }

    fn install_hint(&self) -> String {
        "Windows 11 与较新的 Windows 10 已内置 winget（应用安装程序）；\
         旧版可从 Microsoft Store 安装「应用安装程序」"
            .to_string()
    }

    fn descriptor(&self) -> SourceView {
        let mut view = self.default_descriptor();
        view.verification_note = Some(UNVERIFIED_NOTE.to_string());
        view
    }

    fn search(&self, query: &str, limit: usize) -> Result<Vec<PackageMeta>> {
        let stdout = self.run(
            &[
                "search",
                "--query",
                query,
                "--output",
                "json",
                "--disable-interactivity",
                "--accept-source-agreements",
            ],
            &format!("winget search {query}"),
        )?;
        let json: Value = process::parse_json_lossy(&stdout, "winget search")?;
        let packages = Self::extract_packages(&json);
        if packages.is_empty() {
            return Err(CoreError::parse(
                "winget search",
                format!(
                    "未能从输出中识别出包列表（winget 版本的 JSON 结构可能不同）；原始输出片段：{}",
                    process::preview(&stdout, 400)
                ),
            ));
        }

        let mut out = Vec::new();
        for item in packages.into_iter().take(limit) {
            let Some(id) = Self::pick(&item, &["PackageIdentifier", "PackageId", "Id"]) else {
                continue;
            };
            let mut meta = PackageMeta::new(PackageId::new(PackageSourceKind::Winget, id), id);
            if let Some(name) = Self::pick(&item, &["PackageName", "Name"]) {
                meta.display_name = name.to_string();
            }
            meta.summary = Self::pick(&item, &["Description", "ShortDescription"]).map(str::to_string);
            meta.latest_version =
                Self::pick(&item, &["Version", "PackageVersion"]).map(str::to_string);
            if let Some(v) = &meta.latest_version {
                meta.latest_channel = vcmp::classify(v);
            }
            if let Some(publisher) = Self::pick(&item, &["Publisher", "PublisherName"]) {
                meta.summary = Some(match &meta.summary {
                    Some(s) => format!("{s}（发布者：{publisher}）"),
                    None => format!("发布者：{publisher}"),
                });
            }
            // winget 没有官方下载量：明确留空，等详情页再用 GitHub Stars 兜底
            meta.downloads = DownloadStats::unknown("winget 未提供下载量数据");
            out.push(meta);
        }
        Ok(out)
    }

    fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
        let stdout = self.run(
            &[
                "show",
                "--id",
                name,
                "--exact",
                "--output",
                "json",
                "--disable-interactivity",
                "--accept-source-agreements",
            ],
            name,
        )?;
        let json: Value = process::parse_json_lossy(&stdout, "winget show")?;

        let mut versions = Vec::new();
        // 新版 winget 的 show --output json 带 Versions 数组
        if let Some(arr) = json.get("Versions").and_then(Value::as_array) {
            for item in arr {
                if let Some(v) = Self::pick(item, &["PackageVersion", "Version"]) {
                    versions.push(PackageVersion::new(v));
                }
            }
        }
        // 退化为单个版本
        if versions.is_empty() {
            if let Some(v) = Self::pick(&json, &["PackageVersion", "Version"]) {
                versions.push(PackageVersion::new(v));
            }
        }

        if versions.is_empty() {
            return Err(CoreError::parse(
                "winget show",
                format!(
                    "未能识别版本字段；原始输出片段：{}",
                    process::preview(&stdout, 400)
                ),
            ));
        }
        versions.sort_by(|a, b| vcmp::compare(&b.version, &a.version));
        versions.dedup_by(|a, b| a.version == b.version);
        Ok(versions)
    }

    fn downloads(&self, name: &str, repository: Option<&str>) -> DownloadStats {
        // winget 包标识不是 GitHub 仓库；只有在调用方明确给出仓库时才走 Stars 兜底
        match repository.and_then(http::slug_from_repo_string) {
            Some(slug) => match http::github_stars(&slug) {
                Ok(stars) => DownloadStats::GitHubStars { count: stars, repo: slug },
                Err(e) => DownloadStats::unknown(format!("winget 无下载量接口；GitHub Stars 亦不可用（{e}）")),
            },
            None => DownloadStats::unknown(format!(
                "winget 没有公开的下载量接口，且 `{name}` 没有关联的 GitHub 仓库可兜底"
            )),
        }
    }
}

// ---------------------------------------------------------------------------
// Scoop
// ---------------------------------------------------------------------------

pub struct ScoopAdapter;

impl ScoopAdapter {
    fn run(&self, args: &[&str], context: &str) -> Result<String> {
        let spec = ProcessSpec::new("scoop")
            .args(args.iter().copied())
            .timeout_ms(WIN_CLI_TIMEOUT_MS);
        let out = process::run(&spec)?;
        if !out.success() {
            let stderr = out.stderr.to_ascii_lowercase();
            if stderr.contains("couldn't find") || stderr.contains("not installed") {
                return Err(CoreError::PackageNotFound {
                    id: context.to_string(),
                });
            }
            return Err(CoreError::CommandFailed {
                context: context.to_string(),
                code: Some(out.status),
                stderr: process::preview(out.stderr.trim(), 300),
                stdout: String::new(),
            });
        }
        Ok(out.stdout)
    }

    /// 解析 `scoop search <q>` 的表格输出。
    ///
    /// 形态（Scoop 官方输出）：
    /// ```text
    /// Results from local buckets...
    ///
    /// Name  Version  Source  Updated  Info
    /// ----  -------  ------  -------  ----
    /// wget  1.21.3   main    2023-01-01
    /// ```
    fn parse_search_table(stdout: &str) -> Vec<String> {
        let mut names = Vec::new();
        let mut seen_separator = false;
        for line in stdout.lines() {
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            // 表头与分隔线：出现 "----" 之后才是数据行
            if trimmed.starts_with("----") || trimmed.starts_with("---- ") {
                seen_separator = true;
                continue;
            }
            if !seen_separator {
                continue;
            }
            // 列之间用 2 个以上空格分隔
            let first_col = trimmed
                .split("  ")
                .map(str::trim)
                .find(|s| !s.is_empty())
                .unwrap_or("");
            if !first_col.is_empty()
                && first_col
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || "-._+".contains(c))
            {
                names.push(first_col.to_string());
            }
        }
        names.dedup();
        names
    }

    /// 解析 `scoop cat <app>` 输出的 manifest JSON。
    ///
    /// Scoop 的 manifest 里 `version` 是字符串，`depends` 是依赖名数组。
    fn parse_manifest(json: &Value) -> (Option<String>, Vec<String>) {
        let version = json
            .get("version")
            .and_then(Value::as_str)
            .map(str::to_string);
        let depends = json
            .get("depends")
            .and_then(Value::as_array)
            .map(|arr| {
                arr.iter()
                    .filter_map(Value::as_str)
                    .map(str::to_string)
                    .collect()
            })
            .unwrap_or_default();
        (version, depends)
    }
}

impl PmAdapter for ScoopAdapter {
    fn kind(&self) -> PackageSourceKind {
        PackageSourceKind::Scoop
    }

    fn capabilities(&self) -> SourceCapabilities {
        SourceCapabilities {
            search: true,
            list_versions: true,
            // manifest 的 depends 字段可以解析成运行时依赖
            resolve_deps: true,
            official_downloads: false,
            install: true,
            uninstall: true,
            upgrade: true,
        }
    }

    fn install_hint(&self) -> String {
        "安装 Scoop（PowerShell）：`Set-ExecutionPolicy RemoteSigned -Scope CurrentUser` \
         然后 `irm get.scoop.sh | iex`"
            .to_string()
    }

    fn descriptor(&self) -> SourceView {
        let mut view = self.default_descriptor();
        view.verification_note = Some(UNVERIFIED_NOTE.to_string());
        view
    }

    fn search(&self, query: &str, limit: usize) -> Result<Vec<PackageMeta>> {
        let stdout = self.run(&["search", query], &format!("scoop search {query}"))?;
        let names = Self::parse_search_table(&stdout);
        if names.is_empty() {
            return Err(CoreError::parse(
                "scoop search",
                format!(
                    "未能从表格中识别出包名；原始输出片段：{}",
                    process::preview(&stdout, 400)
                ),
            ));
        }

        let mut out = Vec::new();
        for name in names.into_iter().take(limit) {
            let mut meta = PackageMeta::new(PackageId::new(PackageSourceKind::Scoop, &name), &name);
            // 详情用 manifest 补齐（失败不影响搜索本身返回）
            match self.run(&["cat", &name], &name) {
                Ok(manifest_stdout) => {
                    if let Ok(json) = process::parse_json_lossy::<Value>(&manifest_stdout, "scoop cat")
                    {
                        let (version, _deps) = Self::parse_manifest(&json);
                        meta.latest_version = version.clone();
                        if let Some(v) = &version {
                            meta.latest_channel = vcmp::classify(v);
                        }
                        meta.summary = json
                            .get("description")
                            .and_then(Value::as_str)
                            .map(str::to_string);
                        meta.homepage = json
                            .get("homepage")
                            .and_then(Value::as_str)
                            .map(str::to_string);
                        meta.license = json
                            .get("license")
                            .and_then(|l| {
                                if l.is_string() {
                                    l.as_str().map(str::to_string)
                                } else {
                                    l.get("identifier")
                                        .and_then(Value::as_str)
                                        .map(str::to_string)
                                }
                            });
                    }
                }
                Err(e) => {
                    meta.summary = Some(format!("（manifest 读取失败：{e}）"));
                }
            }
            meta.downloads = self.downloads(&name, meta.homepage.as_deref());
            out.push(meta);
        }
        Ok(out)
    }

    fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
        // scoop 的 bucket 只保留 manifest 中声明的当前版本，
        // 不存在历史版本列表 —— 这一点必须如实反映，而不是伪造版本。
        let stdout = self.run(&["cat", name], name)?;
        let json: Value = process::parse_json_lossy(&stdout, "scoop cat")?;
        let (version, _) = Self::parse_manifest(&json);
        let Some(version) = version else {
            return Err(CoreError::parse(
                "scoop cat",
                format!(
                    "manifest 中没有 version 字段；原始输出片段：{}",
                    process::preview(&stdout, 300)
                ),
            ));
        };
        let mut pv = PackageVersion::new(&version);
        pv.note = Some("Scoop bucket 中声明的当前版本（Scoop 不保留历史版本）".to_string());
        pv.requires = Self::parse_manifest(&json)
            .1
            .into_iter()
            .map(|dep| DependencyRequirement {
                name: dep,
                requirement: "*".to_string(),
                kind: DependencyKind::Runtime,
            })
            .collect();
        Ok(vec![pv])
    }

    fn requirements(
        &self,
        name: &str,
        _version: Option<&str>,
    ) -> Result<Vec<DependencyRequirement>> {
        let stdout = self.run(&["cat", name], name)?;
        let json: Value = process::parse_json_lossy(&stdout, "scoop cat")?;
        let (_, depends) = Self::parse_manifest(&json);
        Ok(depends
            .into_iter()
            .map(|dep| DependencyRequirement {
                name: dep,
                requirement: "*".to_string(),
                kind: DependencyKind::Runtime,
            })
            .collect())
    }

    fn downloads(&self, name: &str, homepage: Option<&str>) -> DownloadStats {
        match homepage.and_then(http::slug_from_repo_string) {
            Some(slug) => match http::github_stars(&slug) {
                Ok(stars) => DownloadStats::GitHubStars { count: stars, repo: slug },
                Err(e) => {
                    DownloadStats::unknown(format!("Scoop 无下载量接口；GitHub Stars 不可用（{e}）"))
                }
            },
            None => DownloadStats::unknown(format!(
                "Scoop 没有公开的下载量接口，且 `{name}` 未声明 GitHub 仓库地址"
            )),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // 注意：以下样本来自官方文档描述的输出结构，**不是真机抓取**。
    // Windows 真机验证后应替换为真实输出，并保留这些用例作为回归测试。

    #[test]
    fn winget_parses_sources_wrapped_shape() {
        let json = serde_json::json!({
            "Sources": [{
                "Name": "winget",
                "Packages": [
                    {"PackageIdentifier": "Microsoft.PowerToys", "PackageName": "PowerToys", "Version": "0.70.1", "Publisher": "Microsoft"},
                    {"PackageIdentifier": "Git.Git", "PackageName": "Git", "Version": "2.42.0"}
                ]
            }]
        });
        let pkgs = WingetAdapter::extract_packages(&json);
        assert_eq!(pkgs.len(), 2);
        assert_eq!(
            WingetAdapter::pick(&pkgs[0], &["PackageIdentifier"]),
            Some("Microsoft.PowerToys")
        );
    }

    #[test]
    fn winget_parses_flat_and_packages_shapes() {
        let flat = serde_json::json!([{"PackageIdentifier": "A.B", "Version": "1.0"}]);
        assert_eq!(WingetAdapter::extract_packages(&flat).len(), 1);

        let wrapped = serde_json::json!({"Packages": [{"PackageIdentifier": "C.D"}]});
        assert_eq!(WingetAdapter::extract_packages(&wrapped).len(), 1);

        // 完全不认识的结构 -> 空列表（由调用方转成 Parse 错误并附原始输出）
        let unknown = serde_json::json!({"Unexpected": 1});
        assert!(WingetAdapter::extract_packages(&unknown).is_empty());
    }

    #[test]
    fn winget_pick_tolerates_alternate_field_names() {
        let item = serde_json::json!({"PackageId": "X.Y", "PackageVersion": "9.9"});
        assert_eq!(WingetAdapter::pick(&item, &["PackageIdentifier", "PackageId"]), Some("X.Y"));
        assert_eq!(WingetAdapter::pick(&item, &["Version", "PackageVersion"]), Some("9.9"));
        // 空字符串应被视为缺失
        let blank = serde_json::json!({"PackageName": "   "});
        assert_eq!(WingetAdapter::pick(&blank, &["PackageName"]), None);
    }

    #[test]
    fn scoop_parses_search_table() {
        let stdout = "\
Results from local buckets...

Name    Version  Source  Updated     Info
----    -------  ------  -------     ----
wget    1.21.3   main    2023-01-01
curl    8.4.0    main    2024-01-05
";
        let names = ScoopAdapter::parse_search_table(stdout);
        assert_eq!(names, vec!["wget".to_string(), "curl".to_string()]);
    }

    #[test]
    fn scoop_search_table_ignores_header_without_separator() {
        // 没有分隔线时不应误把表头当成包名
        let stdout = "Name  Version  Source\n";
        assert!(ScoopAdapter::parse_search_table(stdout).is_empty());
    }

    #[test]
    fn scoop_parses_manifest_version_and_depends() {
        let json = serde_json::json!({
            "version": "1.21.3",
            "description": "A command-line utility for retrieving files",
            "homepage": "https://www.gnu.org/software/wget/",
            "license": "GPL-3.0-or-later",
            "depends": ["lessmsi", "innounp"]
        });
        let (version, depends) = ScoopAdapter::parse_manifest(&json);
        assert_eq!(version.as_deref(), Some("1.21.3"));
        assert_eq!(depends, vec!["lessmsi".to_string(), "innounp".to_string()]);
    }

    #[test]
    fn scoop_manifest_without_version_is_reported() {
        let json = serde_json::json!({"description": "no version field"});
        let (version, depends) = ScoopAdapter::parse_manifest(&json);
        assert!(version.is_none());
        assert!(depends.is_empty());
    }

    #[test]
    fn windows_sources_declare_their_verification_gap() {
        // 这是本模块最重要的契约：未验证状态必须能被 UI 看到
        assert!(WingetAdapter.descriptor().verification_note.is_some());
        assert!(ScoopAdapter.descriptor().verification_note.is_some());
        assert!(WingetAdapter.descriptor().verification_note.unwrap().contains("真机"));
    }

    #[test]
    fn windows_sources_are_platform_gated() {
        assert_eq!(
            PackageSourceKind::Winget.supported_platforms(),
            &[Platform::Windows]
        );
        assert_eq!(
            PackageSourceKind::Scoop.supported_platforms(),
            &[Platform::Windows]
        );
    }

    #[test]
    fn winget_downloads_are_honest_about_missing_data() {
        let stats = WingetAdapter.downloads("Git.Git", None);
        assert_eq!(stats.count(), None);
        assert!(stats.source_label().contains("winget"));
    }
}
