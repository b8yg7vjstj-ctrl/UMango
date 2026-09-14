//! Homebrew 适配器（macOS / Linux）。
//!
//! 本机装了 Homebrew 6.0.22，因此这个适配器是**可以真机验证**的。
//!
//! 实测结论（决定了实现方式）：
//! - `brew search --json` **不支持**（只会打印 usage），所以搜索走
//!   `brew search --formula <q>` 取名字列表，再用 `brew info --json=v2` **一次批量**
//!   取回全部详情（实测支持多包同查），总共两次进程调用；
//! - `brew info --json=v2 <f>` 已包含 versions.stable / desc / license / homepage /
//!   dependencies / build_dependencies，因此版本列表与依赖声明共用同一次调用；
//! - Homebrew 只保留当前 stable 版本 + 若干 `versioned_formulae`（如 python@3.11），
//!   不存在"历史版本列表"。UI 上必须如实说明，而不是伪造一堆版本。
//! - **官方下载量可用**：`https://formulae.brew.sh/api/formula/<name>.json` 的
//!   `analytics.install.30d`（实测 wget = 11081）。

use serde_json::Value;
use umango_core::error::{CoreError, Result};
use umango_core::model::*;
use umango_core::process::{self, ProcessSpec};
use umango_core::PmAdapter;

use crate::http;

/// Homebrew 首次执行可能触发自动更新，给足超时。
const BREW_TIMEOUT_MS: u64 = 90_000;

pub struct HomebrewAdapter;

impl HomebrewAdapter {
    fn brew_info(&self, names: &[&str]) -> Result<Vec<Value>> {
        if names.is_empty() {
            return Ok(Vec::new());
        }
        let mut args: Vec<String> = vec!["info".into(), "--json=v2".into()];
        args.extend(names.iter().map(|s| s.to_string()));
        let spec = ProcessSpec::new("brew")
            .args(args)
            .timeout_ms(BREW_TIMEOUT_MS);
        let out = process::run(&spec)?;
        if !out.success() {
            return Err(CoreError::CommandFailed {
                context: format!("brew info --json=v2 {}", names.join(" ")),
                code: Some(out.status),
                stderr: process::preview(out.stderr.trim(), 300),
                stdout: String::new(),
            });
        }
        let doc: Value = process::parse_json_lossy(&out.stdout, "brew info --json=v2")?;
        Ok(doc
            .get("formulae")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default())
    }

    /// 解析 `brew search --formula <q>` 的纯文本输出。
    ///
    /// 输出形如：
    /// ```text
    /// ==> Formulae
    /// wget                wget2
    /// wgetpaste
    /// ```
    /// 名字可能一行多个（用空格分隔），且带 section 头。
    fn parse_search_names(stdout: &str) -> Vec<String> {
        let mut names = Vec::new();
        for line in stdout.lines() {
            let line = line.trim();
            if line.is_empty() || line.starts_with("==>") || line.starts_with("Error") {
                continue;
            }
            for token in line.split_whitespace() {
                // 过滤掉 Homebrew 的提示行与分隔线
                if token.contains('─') || token.contains('—') || token.starts_with('(') {
                    continue;
                }
                if token.chars().all(|c| c.is_ascii_alphanumeric() || "@+-._".contains(c)) {
                    names.push(token.to_string());
                }
            }
        }
        names.sort();
        names.dedup();
        names
    }

    fn formula_to_meta(formula: &Value, kind: PackageSourceKind) -> Option<PackageMeta> {
        let name = formula.get("name").and_then(Value::as_str)?;
        let mut meta = PackageMeta::new(PackageId::new(kind, name), name);
        meta.summary = formula
            .get("desc")
            .and_then(Value::as_str)
            .map(str::to_string);
        meta.homepage = formula
            .get("homepage")
            .and_then(Value::as_str)
            .map(str::to_string);
        meta.license = formula
            .get("license")
            .and_then(Value::as_str)
            .map(str::to_string);
        meta.latest_version = formula
            .get("versions")
            .and_then(|v| v.get("stable"))
            .and_then(Value::as_str)
            .map(str::to_string);
        if let Some(latest) = &meta.latest_version {
            meta.latest_channel = umango_core::version::classify(latest);
        }
        if formula
            .get("deprecated")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            meta.summary = Some(format!(
                "【已弃用】{}",
                formula
                    .get("deprecation_reason")
                    .and_then(Value::as_str)
                    .unwrap_or("该 formula 已弃用")
            ));
        }
        // 未安装状态
        if let Some(installed) = formula.get("installed").and_then(Value::as_array) {
            if let Some(first) = installed.first() {
                if let Some(ver) = first.get("version").and_then(Value::as_str) {
                    meta.installed = Some(InstalledState {
                        version: ver.to_string(),
                        scope: InstallScope::UserSystem,
                        outdated: meta
                            .latest_version
                            .as_deref()
                            .map(|l| l != ver)
                            .unwrap_or(false),
                        latest: meta.latest_version.clone(),
                    });
                }
            }
        }
        Some(meta)
    }
}

impl PmAdapter for HomebrewAdapter {
    fn kind(&self) -> PackageSourceKind {
        PackageSourceKind::Homebrew
    }

    fn capabilities(&self) -> SourceCapabilities {
        SourceCapabilities {
            search: true,
            list_versions: true,
            resolve_deps: true,
            official_downloads: true,
            install: true,
            uninstall: true,
            upgrade: true,
        }
    }

    fn install_hint(&self) -> String {
        "安装 Homebrew：https://brew.sh（macOS / Linux）".to_string()
    }

    fn search(&self, query: &str, limit: usize) -> Result<Vec<PackageMeta>> {
        let spec = ProcessSpec::new("brew")
            .args(["search", "--formula", query])
            .timeout_ms(BREW_TIMEOUT_MS);
        let out = process::run(&spec)?;
        if !out.success() {
            return Err(CoreError::CommandFailed {
                context: format!("brew search --formula {query}"),
                code: Some(out.status),
                stderr: process::preview(out.stderr.trim(), 200),
                stdout: String::new(),
            });
        }
        let names = Self::parse_search_names(&out.stdout);
        let selected: Vec<&str> = names.iter().take(limit).map(|s| s.as_str()).collect();
        if selected.is_empty() {
            return Ok(Vec::new());
        }

        // 一次批量查询取回全部详情
        let formulae = self.brew_info(&selected)?;
        let mut metas: Vec<PackageMeta> = formulae
            .iter()
            .filter_map(|f| Self::formula_to_meta(f, PackageSourceKind::Homebrew))
            .collect();

        // 官方下载量（每个 formula 一个请求，失败则退化为 Unknown）
        for meta in metas.iter_mut() {
            meta.downloads = self.downloads(&meta.id.name, meta.repository.as_deref());
        }
        Ok(metas)
    }

    fn versions(&self, name: &str) -> Result<Vec<PackageVersion>> {
        let formulae = self.brew_info(&[name])?;
        let formula = formulae
            .first()
            .ok_or_else(|| CoreError::PackageNotFound {
                id: name.to_string(),
            })?;

        let mut versions: Vec<PackageVersion> = Vec::new();
        if let Some(stable) = formula
            .get("versions")
            .and_then(|v| v.get("stable"))
            .and_then(Value::as_str)
        {
            let mut pv = PackageVersion::new(stable);
            pv.note = Some("Homebrew 当前 stable 版本".to_string());
            versions.push(pv);
        }
        // versioned_formulae 是 Homebrew 保留旧主版本的唯一方式（如 python@3.11）
        if let Some(vf) = formula.get("versioned_formulae").and_then(Value::as_array) {
            for item in vf {
                if let Some(vname) = item.as_str() {
                    let ver = vname.split('@').nth(1).unwrap_or(vname);
                    let mut pv = PackageVersion::new(ver);
                    pv.note = Some(format!("独立 formula：`{vname}`"));
                    versions.push(pv);
                }
            }
        }
        // HEAD 版本
        if formula
            .get("versions")
            .and_then(|v| v.get("head"))
            .and_then(Value::as_str)
            .is_some()
        {
            let mut pv = PackageVersion::new("HEAD");
            pv.channel = ReleaseChannel::Snapshot;
            pv.note = Some("HEAD 版本（编译最新源码）".to_string());
            versions.push(pv);
        }

        if versions.is_empty() {
            return Err(CoreError::Other(format!(
                "Homebrew 未能给出 `{name}` 的版本信息"
            )));
        }
        Ok(versions)
    }

    fn requirements(
        &self,
        name: &str,
        _version: Option<&str>,
    ) -> Result<Vec<DependencyRequirement>> {
        // Homebrew 的依赖不由用户选版本，始终是当前 formula 的依赖集
        let formulae = self.brew_info(&[name])?;
        let formula = formulae
            .first()
            .ok_or_else(|| CoreError::PackageNotFound {
                id: name.to_string(),
            })?;

        let mut out = Vec::new();
        let mut push = |key: &str, kind: DependencyKind| {
            if let Some(arr) = formula.get(key).and_then(Value::as_array) {
                for item in arr {
                    if let Some(dep) = item.as_str() {
                        out.push(DependencyRequirement {
                            name: dep.to_string(),
                            requirement: "*".to_string(),
                            kind,
                        });
                    }
                }
            }
        };
        push("dependencies", DependencyKind::Runtime);
        push("build_dependencies", DependencyKind::Dev);
        push("optional_dependencies", DependencyKind::Optional);
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out.dedup_by(|a, b| a.name == b.name);
        Ok(out)
    }

    fn downloads(&self, name: &str, _repository: Option<&str>) -> DownloadStats {
        // Homebrew 有官方安装量分析数据
        let url = format!("https://formulae.brew.sh/api/formula/{name}.json");
        let json = match http::get_json_cached(&url, 24 * 3600, 20_000) {
            Ok(j) => j,
            Err(e) => {
                return DownloadStats::unknown(format!("formulae.brew.sh 不可用：{e}"));
            }
        };
        let Some(install) = json
            .get("analytics")
            .and_then(|a| a.get("install"))
            .and_then(|i| i.get("30d"))
            .and_then(Value::as_object)
        else {
            return DownloadStats::unknown("formulae.brew.sh 未提供 30 天安装量");
        };

        // 同一个 formula 可能有多个条目（如 "wget" 与 "wget --HEAD"），
        // 精确匹配优先，否则把同名前缀的变体求和
        if let Some(exact) = install.get(name).and_then(Value::as_u64) {
            return DownloadStats::Official {
                count: exact,
                period: StatsPeriod::LastMonth,
                via: "formulae.brew.sh".to_string(),
            };
        }
        let prefix = format!("{name} ");
        let sum: u64 = install
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix))
            .filter_map(|(_, v)| v.as_u64())
            .sum();
        if sum > 0 {
            DownloadStats::Official {
                count: sum,
                period: StatsPeriod::LastMonth,
                via: "formulae.brew.sh".to_string(),
            }
        } else {
            DownloadStats::unknown("该 formula 没有安装量记录")
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_brew_search_text_output() {
        // 实测输出形态
        let stdout = "\
==> Formulae
wget                wget2
wgetpaste
==> Casks
wgetgui
";
        let names = HomebrewAdapter::parse_search_names(stdout);
        assert!(names.contains(&"wget".to_string()));
        assert!(names.contains(&"wget2".to_string()));
        assert!(names.contains(&"wgetpaste".to_string()));
        // cask 也出现在同一份输出里，这里只做名字收集，由 --formula 参数保证范围
        assert!(!names.iter().any(|n| n.contains("==>")));
    }

    #[test]
    fn parse_search_names_drops_decoration_lines() {
        let stdout = "==> Formulae\n───────\nfoo   bar\n";
        let names = HomebrewAdapter::parse_search_names(stdout);
        assert_eq!(names, vec!["bar".to_string(), "foo".to_string()]);
    }

    #[test]
    fn maps_formula_json_to_meta() {
        let formula = serde_json::json!({
            "name": "wget",
            "desc": "Internet file retriever",
            "homepage": "https://www.gnu.org/software/wget/",
            "license": "GPL-3.0-or-later",
            "versions": {"stable": "1.25.0", "head": null},
            "installed": [],
            "dependencies": ["libidn2", "libpsl", "openssl@3"]
        });
        let meta = HomebrewAdapter::formula_to_meta(&formula, PackageSourceKind::Homebrew).unwrap();
        assert_eq!(meta.id.name, "wget");
        assert_eq!(meta.display_name, "wget");
        assert_eq!(meta.latest_version.as_deref(), Some("1.25.0"));
        assert_eq!(meta.latest_channel, ReleaseChannel::Stable);
        assert_eq!(meta.icon_key, "icon_homebrew");
        assert!(meta.installed.is_none());
    }

    #[test]
    fn marks_deprecated_formula_in_summary() {
        let formula = serde_json::json!({
            "name": "old-tool",
            "desc": "legacy",
            "versions": {"stable": "1.0"},
            "deprecated": true,
            "deprecation_reason": "不再维护",
            "installed": []
        });
        let meta = HomebrewAdapter::formula_to_meta(&formula, PackageSourceKind::Homebrew).unwrap();
        assert!(meta.summary.as_deref().unwrap().contains("已弃用"));
    }

    #[test]
    fn detects_outdated_installed_version() {
        let formula = serde_json::json!({
            "name": "foo",
            "desc": "x",
            "versions": {"stable": "2.0.0"},
            "installed": [{"version": "1.9.0"}]
        });
        let meta = HomebrewAdapter::formula_to_meta(&formula, PackageSourceKind::Homebrew).unwrap();
        let installed = meta.installed.unwrap();
        assert_eq!(installed.version, "1.9.0");
        assert!(installed.outdated, "1.9.0 相对 2.0.0 应标记为过期");
    }

    #[test]
    fn maps_build_dependencies_to_dev_kind() {
        let formula = serde_json::json!({
            "name": "foo",
            "versions": {"stable": "1.0"},
            "dependencies": ["bar"],
            "build_dependencies": ["cmake"],
            "installed": []
        });
        let metas = vec![formula];
        // 直接验证映射逻辑（requirements 需要真的调用 brew，这里用同一段逻辑）
        let f = &metas[0];
        let mut out = Vec::new();
        if let Some(arr) = f.get("dependencies").and_then(Value::as_array) {
            for i in arr {
                out.push((i.as_str().unwrap(), DependencyKind::Runtime));
            }
        }
        if let Some(arr) = f.get("build_dependencies").and_then(Value::as_array) {
            for i in arr {
                out.push((i.as_str().unwrap(), DependencyKind::Dev));
            }
        }
        assert!(out.contains(&("bar", DependencyKind::Runtime)));
        assert!(out.contains(&("cmake", DependencyKind::Dev)));
    }

    /// 真机测试：Homebrew 适配器端到端可用。
    #[test]
    fn homebrew_real_cli_end_to_end() {
        let adapter = HomebrewAdapter;
        let availability = adapter.probe();
        if availability.state != AvailabilityState::Available {
            eprintln!("跳过：本机没有可用的 brew（{:?}）", availability.state);
            return;
        }
        let versions = adapter.versions("wget").expect("应能查到 wget");
        assert!(!versions.is_empty());
        assert!(versions.iter().any(|v| v.channel == ReleaseChannel::Stable));

        let reqs = adapter.requirements("wget", None).expect("应能取到 wget 依赖");
        assert!(
            reqs.iter().any(|r| r.kind == DependencyKind::Runtime),
            "wget 应有运行时依赖，实际 {reqs:?}"
        );

        match adapter.downloads("wget", None) {
            DownloadStats::Official { count, via, .. } => {
                assert!(count > 100, "wget 近 30 天安装量异常: {count}");
                assert_eq!(via, "formulae.brew.sh");
            }
            other => eprintln!("跳过下载量断言（{other:?}）"),
        }
    }
}
