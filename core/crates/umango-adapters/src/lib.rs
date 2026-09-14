//! # umango-adapters
//!
//! UMango 的包管理器适配器集合：把各命令行包管理器的原生输出，翻译成
//! [`umango_core::model`] 里定义的统一结构。
//!
//! | 包源 | 平台 | 搜索 | 版本列表 | 依赖解析 | 下载量 | 本机可验证 |
//! |------|------|------|----------|----------|--------|-----------|
//! | npm | 全平台 | ✅ | ✅ | ✅ | 官方（api.npmjs.org） | ✅ |
//! | pnpm | 全平台 | ✅ | ✅ | ✅ | GitHub Stars 兜底 | ✅ |
//! | git | 全平台 | ✅（GitHub API） | ✅（ls-remote） | ❌ 不适用 | GitHub Stars | 部分（github.com 被墙） |
//! | homebrew | macOS/Linux | ✅ | ✅（仅 stable） | ✅ | 官方（formulae.brew.sh） | ✅ |
//! | winget | 仅 Windows | ⚠️ | ⚠️ | ❌ | GitHub Stars 兜底 | ❌ 未验证 |
//! | scoop | 仅 Windows | ⚠️ | ⚠️ | ✅ | GitHub Stars 兜底 | ❌ 未验证 |
//!
//! ⚠️ = 代码已按官方格式实现，但受平台限制无法在开发机（macOS）上验证，
//! 会通过 `SourceView.verification_note` 在界面上明确标注。

pub mod git;
pub mod homebrew;
pub mod http;
pub mod npm;
pub mod windows;

use umango_core::model::PackageSourceKind;
use umango_core::PmAdapter;

pub use git::GitAdapter;
pub use homebrew::HomebrewAdapter;
pub use npm::NpmLikeAdapter;
pub use windows::{ScoopAdapter, WingetAdapter};

/// 全部内置适配器。
///
/// **注意**：这里注册所有平台的适配器，由核心层的平台门控决定谁能用。
/// 不要在注册阶段按平台过滤 —— 否则 UI 无法展示"该包源在当前平台不可用"
/// 这一信息，用户会以为功能缺失。
pub fn builtin_adapters() -> Vec<Box<dyn PmAdapter>> {
    vec![
        Box::new(NpmLikeAdapter::npm()),
        Box::new(NpmLikeAdapter::pnpm()),
        Box::new(GitAdapter),
        Box::new(HomebrewAdapter),
        Box::new(WingetAdapter),
        Box::new(ScoopAdapter),
    ]
}

/// 只注册能在指定平台工作的适配器（供"精简模式"或测试使用）。
pub fn platform_adapters(platform: umango_core::model::Platform) -> Vec<Box<dyn PmAdapter>> {
    builtin_adapters()
        .into_iter()
        .filter(|a| a.kind().supports_platform(platform))
        .collect()
}

/// 按 `PackageSourceKind` 找一个内置适配器。
pub fn adapter_for(kind: PackageSourceKind) -> Option<Box<dyn PmAdapter>> {
    builtin_adapters().into_iter().find(|a| a.kind() == kind)
}

#[cfg(test)]
mod tests {
    use super::*;
    use umango_core::model::Platform;

    #[test]
    fn registers_all_documented_sources() {
        let adapters = builtin_adapters();
        let kinds: Vec<PackageSourceKind> = adapters.iter().map(|a| a.kind()).collect();
        // 需求里的源列表：npm / git / scoop / winget / pnpm（外加 Homebrew）
        for expected in PackageSourceKind::ALL {
            assert!(kinds.contains(&expected), "缺少包源 {expected:?}");
        }
        assert_eq!(adapters.len(), PackageSourceKind::ALL.len());
    }

    #[test]
    fn platform_filter_keeps_only_supported_sources() {
        let macos = platform_adapters(Platform::Macos);
        let kinds: Vec<PackageSourceKind> = macos.iter().map(|a| a.kind()).collect();
        assert!(kinds.contains(&PackageSourceKind::Npm));
        assert!(kinds.contains(&PackageSourceKind::Pnpm));
        assert!(kinds.contains(&PackageSourceKind::Git));
        assert!(kinds.contains(&PackageSourceKind::Homebrew));
        // winget / scoop 必须被过滤掉
        assert!(!kinds.contains(&PackageSourceKind::Winget));
        assert!(!kinds.contains(&PackageSourceKind::Scoop));

        let windows = platform_adapters(Platform::Windows);
        let win_kinds: Vec<PackageSourceKind> = windows.iter().map(|a| a.kind()).collect();
        assert!(win_kinds.contains(&PackageSourceKind::Winget));
        assert!(win_kinds.contains(&PackageSourceKind::Scoop));
        assert!(
            !win_kinds.contains(&PackageSourceKind::Homebrew),
            "Homebrew 不支持 Windows"
        );
    }

    #[test]
    fn every_adapter_declares_consistent_metadata() {
        for adapter in builtin_adapters() {
            let kind = adapter.kind();
            let view = adapter.descriptor();
            assert_eq!(view.kind, kind);
            assert_eq!(view.icon_key, kind.icon_key(), "图标 key 必须与包源一致");
            assert_eq!(view.accent, kind.accent());
            assert_eq!(view.cli_name, kind.cli_name());
            assert!(!view.display_name.is_empty());
            assert!(!adapter.install_hint().is_empty(), "{kind:?} 缺少安装指引");
            assert_eq!(
                view.supported_platforms,
                kind.supported_platforms().to_vec(),
                "{kind:?} 的平台支持列表与模型定义不一致"
            );
        }
    }

    #[test]
    fn adapter_for_finds_each_source() {
        for kind in PackageSourceKind::ALL {
            assert!(adapter_for(kind).is_some(), "{kind:?} 找不到适配器");
        }
    }
}
