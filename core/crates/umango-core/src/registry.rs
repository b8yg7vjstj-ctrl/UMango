//! 包源注册表：适配器调度 + **24 小时可用性保护**缓存。
//!
//! 这一层是 UI 的唯一入口，负责：
//! - 平台能力门控（不支持的包源直接标记，UI 据此隐藏）；
//! - 可用性探测结果的 TTL 缓存与失败降级；
//! - 把包源字符串解析成具体适配器。

use std::collections::HashMap;
use std::sync::Mutex;

use crate::adapter::PmAdapter;
use crate::clock;
use crate::error::{CoreError, Result};
use crate::model::*;
use crate::AVAILABILITY_TTL_SECS;

/// 探测失败后的重试退避：避免 UI 轮询把不可达的源打成雪崩。
const FAILURE_BACKOFF_SECS: i64 = 300;

struct Cached {
    view: SourceAvailability,
    probed_at: i64,
    ok: bool,
}

/// 包源注册表。
pub struct AdapterRegistry {
    adapters: Vec<Box<dyn PmAdapter>>,
    cache: Mutex<HashMap<PackageSourceKind, Cached>>,
}

impl AdapterRegistry {
    pub fn new(adapters: Vec<Box<dyn PmAdapter>>) -> Self {
        AdapterRegistry {
            adapters,
            cache: Mutex::new(HashMap::new()),
        }
    }

    /// 已注册的包源种类。
    pub fn kinds(&self) -> Vec<PackageSourceKind> {
        self.adapters.iter().map(|a| a.kind()).collect()
    }

    /// 取适配器；包源未注册时给出可读错误。
    pub fn adapter(&self, kind: PackageSourceKind) -> Result<&dyn PmAdapter> {
        self.adapters
            .iter()
            .find(|a| a.kind() == kind)
            .map(|b| b.as_ref())
            .ok_or_else(|| {
                CoreError::UnknownSource(format!(
                    "{}（已注册: {}）",
                    kind.as_str(),
                    self.kinds()
                        .iter()
                        .map(|k| k.as_str())
                        .collect::<Vec<_>>()
                        .join(", ")
                ))
            })
    }

    /// 包源列表视图 —— UI 的「包源列表」直接渲染这个。
    pub fn source_views(&self) -> Vec<SourceView> {
        self.adapters
            .iter()
            .map(|a| self.view_of(a.as_ref()))
            .collect()
    }

    /// 强制重新探测（设置页的"重新检测包源"按钮）。
    ///
    /// 注意：这里**只把缓存标记为过期，不清空**。清空会丢掉上一次的成功结果，
    /// 导致刷新失败时无法降级沿用，用户会直接看到"源不可用"。
    pub fn refresh(&self, only: Option<PackageSourceKind>) -> Vec<SourceView> {
        {
            let mut cache = self.cache.lock().expect("cache lock poisoned");
            match only {
                Some(kind) => {
                    if let Some(entry) = cache.get_mut(&kind) {
                        entry.probed_at = 0;
                    }
                }
                None => {
                    for entry in cache.values_mut() {
                        entry.probed_at = 0;
                    }
                }
            }
        }
        self.source_views()
    }

    fn view_of(&self, adapter: &dyn PmAdapter) -> SourceView {
        // descriptor() 不含探测行为，可用性统一由带缓存的 availability_for 填充
        let mut view = adapter.descriptor();
        view.availability = self.availability_for(adapter);
        view
    }

    /// 带 24h 保护的可用性探测。
    ///
    /// - 成功的探测结果缓存 24 小时；
    /// - 失败的结果只缓存 5 分钟（退避），超时后允许重试；
    /// - **刷新失败但手上有历史结果时，降级沿用历史结果并标记 `stale`**，
    ///   让用户仍然能用（可能过期的）数据，而不是看到"源不可用"。
    fn availability_for(&self, adapter: &dyn PmAdapter) -> SourceAvailability {
        let kind = adapter.kind();
        let now = clock::now_epoch();

        let cached = self
            .cache
            .lock()
            .expect("cache lock poisoned")
            .get(&kind)
            .map(|c| (c.view.clone(), c.probed_at, c.ok));

        if let Some((view, probed_at, ok)) = &cached {
            let age = now - probed_at;
            let ttl = if *ok { AVAILABILITY_TTL_SECS } else { FAILURE_BACKOFF_SECS };
            if age >= 0 && age < ttl {
                return view.clone();
            }
        }

        // 平台不支持时无需执行 CLI（平台不会变），直接给出门控结果并缓存 24h
        let fresh = if kind.supports_platform(crate::process::runner().platform()) {
            adapter.probe()
        } else {
            crate::adapter::unsupported_availability(kind)
        };
        let success = matches!(
            fresh.state,
            AvailabilityState::Available
                | AvailabilityState::UnsupportedPlatform
                | AvailabilityState::NotInstalled
        );

        let to_store = if success {
            fresh.clone()
        } else if let Some((mut old, _, _)) = cached.clone() {
            // 降级：沿用旧结果，明确标记过期
            old.stale = true;
            if old.state == AvailabilityState::Available {
                old.state = AvailabilityState::Degraded;
            }
            old.detail = Some(format!(
                "刷新失败，沿用 {} 的检测结果。原因：{}",
                clock::humanize_age(&old.checked_at),
                fresh.detail.unwrap_or_else(|| "未知".into())
            ));
            old
        } else {
            fresh.clone()
        };

        self.cache.lock().expect("cache lock poisoned").insert(
            kind,
            Cached {
                view: to_store.clone(),
                probed_at: now,
                ok: success,
            },
        );

        to_store
    }

    /// 确认包源可用，否则给出结构化错误（UI 可据此提示"安装 CLI"或"隐藏入口"）。
    fn ensure_usable(&self, kind: PackageSourceKind) -> Result<&dyn PmAdapter> {
        let adapter = self.adapter(kind)?;
        let availability = self.availability_for(adapter);
        match availability.state {
            AvailabilityState::Available | AvailabilityState::Degraded => Ok(adapter),
            AvailabilityState::UnsupportedPlatform => Err(CoreError::UnsupportedPlatform {
                source_name: kind.as_str().to_string(),
                platform: crate::process::runner().platform().display_name().to_string(),
                hint: Some(format!(
                    "{} 支持：{}",
                    kind.display_name(),
                    kind.supported_platforms()
                        .iter()
                        .map(|p| p.display_name())
                        .collect::<Vec<_>>()
                        .join(" / ")
                )),
            }),
            AvailabilityState::NotInstalled => {
                Err(CoreError::cli_missing(&kind.cli_name(), Some(&adapter.install_hint())))
            }
            AvailabilityState::Unreachable => Err(CoreError::network(
                availability.detail.unwrap_or_else(|| "包源不可达".into()),
            )),
        }
    }

    pub fn search(
        &self,
        kind: PackageSourceKind,
        query: &str,
        limit: usize,
    ) -> Result<Vec<PackageMeta>> {
        let adapter = self.ensure_usable(kind)?;
        let limit = limit.clamp(1, 100);
        adapter.search(query, limit)
    }

    pub fn versions(&self, kind: PackageSourceKind, name: &str) -> Result<Vec<PackageVersion>> {
        let adapter = self.ensure_usable(kind)?;
        adapter.versions(name)
    }

    pub fn dependency_tree(&self, query: &DependencyQuery) -> Result<DependencyTree> {
        let adapter = self.ensure_usable(query.source)?;
        // 依赖树由核心层统一构建，适配器只管提供原语（versions / requirements）
        crate::adapter::build_dependency_tree(adapter, query)
    }

    /// 下载量：官方数据优先，失败自动兜底 GitHub Stars。
    /// 这个方法**永不失败** —— 拿不到就返回 `Unknown` 并带上原因。
    pub fn downloads(
        &self,
        kind: PackageSourceKind,
        name: &str,
        repository: Option<&str>,
    ) -> DownloadStats {
        match self.adapter(kind) {
            Ok(adapter) => adapter.downloads(name, repository),
            Err(e) => DownloadStats::unknown(e.to_string()),
        }
    }
}

/// 便捷构造：没有任何适配器（用于纯 UI 骨架调试）。
impl Default for AdapterRegistry {
    fn default() -> Self {
        AdapterRegistry::new(Vec::new())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::adapter::test_support::FakeAdapter;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;

    /// 可编排探测结果的适配器，用于验证 24h 保护与降级。
    struct FlakyProbeAdapter {
        kind: PackageSourceKind,
        ok: Arc<AtomicUsize>,
        probe_count: Arc<AtomicUsize>,
    }

    impl PmAdapter for FlakyProbeAdapter {
        fn kind(&self) -> PackageSourceKind {
            self.kind
        }
        fn capabilities(&self) -> SourceCapabilities {
            SourceCapabilities::none()
        }
        fn install_hint(&self) -> String {
            "测试".into()
        }
        fn probe(&self) -> SourceAvailability {
            self.probe_count.fetch_add(1, Ordering::SeqCst);
            let good = self.ok.load(Ordering::SeqCst) == 1;
            SourceAvailability {
                source: self.kind,
                state: if good {
                    AvailabilityState::Available
                } else {
                    AvailabilityState::Unreachable
                },
                cli_found: true,
                cli_path: Some("/usr/bin/fake".into()),
                cli_version: Some("1.0.0".into()),
                checked_at: clock::now_iso8601(),
                stale: false,
                detail: if good {
                    None
                } else {
                    Some("模拟源不可达".into())
                },
            }
        }
    }

    fn flaky(ok: bool) -> (AdapterRegistry, Arc<AtomicUsize>) {
        let probes = Arc::new(AtomicUsize::new(0));
        let adapter = FlakyProbeAdapter {
            kind: PackageSourceKind::Npm,
            ok: Arc::new(AtomicUsize::new(if ok { 1 } else { 0 })),
            probe_count: probes.clone(),
        };
        (AdapterRegistry::new(vec![Box::new(adapter)]), probes)
    }

    #[test]
    fn successful_probe_is_cached_for_24h() {
        let (registry, probes) = flaky(true);
        for _ in 0..5 {
            let views = registry.source_views();
            assert_eq!(views[0].availability.state, AvailabilityState::Available);
        }
        assert_eq!(
            probes.load(Ordering::SeqCst),
            1,
            "24h 缓存内不应重复探测 CLI"
        );
    }

    #[test]
    fn failed_refresh_degrades_to_stale_cached_result() {
        let probes = Arc::new(AtomicUsize::new(0));
        let flag = Arc::new(AtomicUsize::new(1));
        let adapter = FlakyProbeAdapter {
            kind: PackageSourceKind::Npm,
            ok: flag.clone(),
            probe_count: probes.clone(),
        };
        let registry = AdapterRegistry::new(vec![Box::new(adapter)]);

        // 第一次：成功
        let first = registry.source_views();
        assert_eq!(first[0].availability.state, AvailabilityState::Available);
        assert!(!first[0].availability.stale);

        // 源挂了 + 强制刷新 -> 应降级沿用旧结果而不是判死
        flag.store(0, Ordering::SeqCst);
        let after = registry.refresh(None);
        assert_eq!(after[0].availability.state, AvailabilityState::Degraded);
        assert!(after[0].availability.stale, "必须标记数据已过期");
        assert!(after[0].availability.cli_found, "CLI 仍然存在，不应丢失该信息");
        assert!(after[0]
            .availability
            .detail
            .as_deref()
            .unwrap_or("")
            .contains("刷新失败"));
    }

    #[test]
    fn failure_backoff_prevents_probe_storm() {
        let (registry, probes) = flaky(false);
        for _ in 0..6 {
            let _ = registry.source_views();
        }
        // 失败退避窗口内只探测一次
        assert_eq!(probes.load(Ordering::SeqCst), 1, "失败后应退避，避免雪崩");
    }

    #[test]
    fn unusable_source_yields_actionable_error() {
        let (registry, _) = flaky(false);
        // 无历史成功记录：应报网络错误且建议检查网络
        let err = registry
            .search(PackageSourceKind::Npm, "react", 10)
            .expect_err("不可达的源应拒绝查询");
        assert_eq!(err.code(), "network_error");
        assert_eq!(err.advice(), crate::error::RecoveryAdvice::CheckNetwork);
    }

    #[test]
    fn unknown_source_lists_registered_ones() {
        let registry = AdapterRegistry::default();
        let err = registry
            .versions(PackageSourceKind::Npm, "react")
            .expect_err("空注册表应报未知包源");
        assert_eq!(err.code(), "unknown_source");
    }

    #[test]
    fn downloads_never_fails() {
        let (registry, _) = flaky(true);
        let stats = registry.downloads(PackageSourceKind::Npm, "react", None);
        // FakeAdapter/FlakyProbeAdapter 未实现 downloads -> 应返回 Unknown 而不是 panic
        assert_eq!(stats.count(), None);
        assert_eq!(stats.metric_name(), "暂无数据");
    }

    #[test]
    fn search_delegates_to_adapter_with_clamped_limit() {
        let adapter = FakeAdapter::new().with_pkg("react", &["18.2.0"], &[]);
        let registry = AdapterRegistry::new(vec![Box::new(adapter)]);
        // limit 0 会被夹到 1；这里主要验证不会 panic 且走通调度
        let result = registry.search(PackageSourceKind::Npm, "react", 0);
        assert!(result.is_err(), "FakeAdapter 未实现 search -> 应报不支持");
        assert_eq!(result.unwrap_err().code(), "unsupported_feature");
    }

    /// 平台不支持的包源必须被门控掉，且**绝不能真的去执行它的 CLI**。
    struct NeverProbeAdapter(PackageSourceKind);

    impl PmAdapter for NeverProbeAdapter {
        fn kind(&self) -> PackageSourceKind {
            self.0
        }
        fn capabilities(&self) -> SourceCapabilities {
            SourceCapabilities::none()
        }
        fn install_hint(&self) -> String {
            "测试".into()
        }
        fn probe(&self) -> SourceAvailability {
            panic!("平台不支持的包源不应该被探测");
        }
    }

    #[test]
    fn unsupported_source_is_gated_without_probing() {
        let host = crate::model::Platform::host();
        // 找一个在当前平台明确不支持的包源（macOS 上是 winget/scoop）
        let Some(kind) = PackageSourceKind::ALL
            .into_iter()
            .find(|k| !k.supports_platform(host))
        else {
            return; // 极端情况下所有源都支持，跳过
        };

        let registry = AdapterRegistry::new(vec![Box::new(NeverProbeAdapter(kind))]);
        let views = registry.source_views();
        assert_eq!(views[0].availability.state, AvailabilityState::UnsupportedPlatform);
        assert!(!views[0].supported_here, "UI 必须据此隐藏该包源");
        assert!(!views[0].availability.cli_found);
        // 查询该源应得到"平台不支持"的结构化错误
        let err = registry
            .versions(kind, "whatever")
            .expect_err("平台不支持的源应拒绝查询");
        assert_eq!(err.code(), "unsupported_platform");
        assert_eq!(err.advice(), crate::error::RecoveryAdvice::HideEntry);
    }
}
