//! 传输无关的 JSON API。
//!
//! Flutter 只能通过 C ABI 传字符串，所以这里定义**唯一的调用约定**：
//!
//! ```text
//! 请求： umango_call("packages.versions", "{\"source\":\"npm\",\"name\":\"react\"}")
//! 响应： {"ok":true,"data":[...]}
//!       {"ok":false,"error":{"code":"cli_missing","message":"...","advice":"install_cli","retryable":false}}
//! ```
//!
//! 约定：**任何情况下都返回合法 JSON 信封，永不 panic、永不返回裸字符串**。
//! 前端只需判断 `ok` 字段，用 `error.code` 做兜底分支。

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::error::{CoreError, RecoveryAdvice, Result};
use crate::model::*;
use crate::registry::AdapterRegistry;
use crate::process;
use crate::{APP_NAME, VERSION};

/// 成功的响应信封。
pub fn ok(data: impl Serialize) -> Value {
    json!({ "ok": true, "data": data })
}

/// 失败的响应信封。
pub fn err(error: &CoreError) -> Value {
    json!({
        "ok": false,
        "error": {
            "code": error.code(),
            "message": error.to_string(),
            "advice": error.advice(),
            "retryable": error.is_retryable(),
        }
    })
}

/// 把 `Result` 折叠成信封。
pub fn finish<T: Serialize>(result: Result<T>) -> Value {
    match result {
        Ok(v) => ok(v),
        Err(e) => err(&e),
    }
}

/// 已实现的 API 方法名（用于 `core.methods` 自省与前端联调）。
pub const METHODS: [&str; 10] = [
    "core.info",
    "core.methods",
    "platform.capabilities",
    "sources.list",
    "sources.refresh",
    "packages.search",
    "packages.versions",
    "packages.dependencies",
    "packages.downloads",
    "packages.resolve_install",
];

/// 统一入口：按方法名分发。
pub fn dispatch(registry: &AdapterRegistry, method: &str, args: &Value) -> Value {
    match method {
        "core.info" => ok(core_info()),
        "core.methods" => ok(METHODS.to_vec()),
        "platform.capabilities" => ok(platform_capabilities(registry)),
        "sources.list" => ok(registry.source_views()),
        "sources.refresh" => refresh(registry, args),
        "packages.search" => finish(search(registry, args)),
        "packages.versions" => finish(versions(registry, args)),
        "packages.dependencies" => finish(dependencies(registry, args)),
        "packages.downloads" => downloads(registry, args),
        "packages.resolve_install" => finish(resolve_install(registry, args)),
        other => err(&CoreError::Other(format!(
            "未知方法 `{other}`；可用方法：{}",
            METHODS.join(", ")
        ))),
    }
}

/// 核心层与宿主信息。
pub fn core_info() -> Value {
    let platform = process::runner().platform();
    json!({
        "app": APP_NAME,
        "core_version": VERSION,
        "platform": platform.as_str(),
        "platform_display": platform.display_name(),
        "arch": std::env::consts::ARCH,
        "availability_ttl_secs": crate::AVAILABILITY_TTL_SECS,
        // "cpp-bridge" 表示进程调用确实经过 C++ 桥接层
        "process_runner": process::runner().describe(),
    })
}

fn platform_capabilities(registry: &AdapterRegistry) -> Value {
    let platform = process::runner().platform();
    json!({
        "platform": platform.as_str(),
        "sources": registry.source_views(),
    })
}

fn refresh(registry: &AdapterRegistry, args: &Value) -> Value {
    let only = args
        .get("source")
        .and_then(Value::as_str)
        .and_then(PackageSourceKind::parse);
    ok(registry.refresh(only))
}

// --- 参数提取小工具：把缺参/类型错误变成结构化错误，而不是 panic ---------

fn need_str(args: &Value, key: &str) -> Result<String> {
    args.get(key)
        .and_then(Value::as_str)
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .ok_or_else(|| CoreError::Other(format!("缺少参数 `{key}`（应为非空字符串）")))
}

fn opt_str(args: &Value, key: &str) -> Option<String> {
    args.get(key)
        .and_then(Value::as_str)
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
}

fn need_source(args: &Value) -> Result<PackageSourceKind> {
    let raw = need_str(args, "source")?;
    PackageSourceKind::parse(&raw)
        .ok_or_else(|| CoreError::UnknownSource(raw))
}

fn search(registry: &AdapterRegistry, args: &Value) -> Result<Vec<PackageMeta>> {
    let source = need_source(args)?;
    let query = need_str(args, "query")?;
    let limit = args
        .get("limit")
        .and_then(Value::as_u64)
        .map(|n| n as usize)
        .unwrap_or(30);
    registry.search(source, &query, limit)
}

fn versions(registry: &AdapterRegistry, args: &Value) -> Result<Vec<PackageVersion>> {
    let source = need_source(args)?;
    let name = need_str(args, "name")?;
    registry.versions(source, &name)
}

fn dependencies(registry: &AdapterRegistry, args: &Value) -> Result<DependencyTree> {
    let source = need_source(args)?;
    let name = need_str(args, "name")?;
    let max_depth = args
        .get("max_depth")
        .and_then(Value::as_u64)
        .map(|n| n as u32)
        .unwrap_or(3);
    let query = DependencyQuery {
        source,
        name,
        version: opt_str(args, "version"),
        max_depth,
        include_optional_kinds: args
            .get("include_optional_kinds")
            .and_then(Value::as_bool)
            .unwrap_or(false),
    };
    registry.dependency_tree(&query)
}

/// 下载量永不失败：拿不到就明确说明原因。
fn downloads(registry: &AdapterRegistry, args: &Value) -> Value {
    let result = (|| -> Result<DownloadStats> {
        let source = need_source(args)?;
        let name = need_str(args, "name")?;
        let repo = opt_str(args, "repository");
        Ok(registry.downloads(source, &name, repo.as_deref()))
    })();
    match result {
        Ok(stats) => ok(stats),
        // 参数错误仍然报错；查询失败已在 registry 内部兜底
        Err(e) => err(&e),
    }
}

/// 把"安装某个包"解析成一条待确认的命令（Phase 3 执行，Phase 1 只做计划与展示）。
fn resolve_install(registry: &AdapterRegistry, args: &Value) -> Result<Value> {
    let source = need_source(args)?;
    let name = need_str(args, "name")?;
    let version = opt_str(args, "version");
    let scope = args
        .get("scope")
        .and_then(Value::as_str)
        .and_then(|s| match s {
            "global" => Some(InstallScope::Global),
            "project_local" => Some(InstallScope::ProjectLocal),
            "user_system" => Some(InstallScope::UserSystem),
            "machine_system" => Some(InstallScope::MachineSystem),
            _ => None,
        })
        .unwrap_or_else(|| source.default_scope());

    // 校验包源当前可用（不可用就报带建议的错误）
    let adapter = registry.adapter(source)?;
    let _ = adapter;

    let target = match &version {
        Some(v) => format!("{name}@{v}"),
        None => name.clone(),
    };
    let (program, mut cmd_args, explanation) = match (source, scope) {
        (PackageSourceKind::Npm, InstallScope::Global) => (
            "npm",
            vec!["install".into(), "-g".into(), target.clone()],
            format!("使用 npm 全局安装 {target}"),
        ),
        (PackageSourceKind::Npm, _) => (
            "npm",
            vec!["install".into(), target.clone()],
            format!("在当前项目安装 {target}"),
        ),
        (PackageSourceKind::Pnpm, InstallScope::Global) => (
            "pnpm",
            vec!["add".into(), "-g".into(), target.clone()],
            format!("使用 pnpm 全局安装 {target}"),
        ),
        (PackageSourceKind::Pnpm, _) => (
            "pnpm",
            vec!["add".into(), target.clone()],
            format!("在当前项目安装 {target}"),
        ),
        (PackageSourceKind::Scoop, _) => (
            "scoop",
            vec!["install".into(), name.clone()],
            format!("使用 Scoop 安装 {}", name),
        ),
        (PackageSourceKind::Winget, _) => (
            "winget",
            vec![
                "install".into(),
                "--id".into(),
                name.clone(),
                "--accept-package-agreements".into(),
                "--accept-source-agreements".into(),
            ],
            format!("使用 WinGet 安装 {}", name),
        ),
        (PackageSourceKind::Homebrew, _) => (
            "brew",
            vec!["install".into(), name.clone()],
            format!("使用 Homebrew 安装 {}", name),
        ),
        (PackageSourceKind::Git, _) => (
            "git",
            vec!["clone".into(), name.clone()],
            format!("克隆仓库 {name}"),
        ),
    };

    // git 的 name 就是 URL，不要追加 @version
    if source == PackageSourceKind::Git {
        cmd_args = vec!["clone".into(), name.clone()];
    }

    let planned = crate::adapter::PlannedCommand {
        program: program.to_string(),
        args: cmd_args,
        scope,
        explanation,
    };
    Ok(json!({
        "plan": planned,
        "requires_confirmation": true,
        "note": "阶段1 只生成安装计划，不执行；执行能力在阶段3 接入。",
    }))
}

/// 前端可能传对象或 JSON 字符串，统一成 `Value`。
#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum RawArgs {
    Json(Value),
    #[allow(dead_code)]
    Text(String),
}

/// 从 C ABI 传进来的字符串解析参数。空字符串等价于 `{}`。
pub fn parse_args(raw: Option<&str>) -> Value {
    match raw.map(str::trim) {
        None | Some("") => json!({}),
        Some(s) => serde_json::from_str::<RawArgs>(s)
            .map(|r| match r {
                RawArgs::Json(v) => v,
                RawArgs::Text(t) => serde_json::from_str::<Value>(&t).unwrap_or(json!({})),
            })
            .unwrap_or_else(|_| json!({})),
    }
}

/// 把响应信封序列化成字符串（C ABI 出口）。
pub fn dispatch_to_string(registry: &AdapterRegistry, method: &str, raw_args: Option<&str>) -> String {
    let args = parse_args(raw_args);
    dispatch(registry, method, &args).to_string()
}

/// 让 `RecoveryAdvice` 能被前端直接消费（snake_case 字符串）。
pub fn advice_str(advice: RecoveryAdvice) -> &'static str {
    match advice {
        RecoveryAdvice::Retry => "retry",
        RecoveryAdvice::InstallCli => "install_cli",
        RecoveryAdvice::SwitchSource => "switch_source",
        RecoveryAdvice::ShowCached => "show_cached",
        RecoveryAdvice::CheckNetwork => "check_network",
        RecoveryAdvice::HideEntry => "hide_entry",
        RecoveryAdvice::Manual => "manual",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::adapter::test_support::FakeAdapter;

    fn registry_with_fake() -> AdapterRegistry {
        let adapter = FakeAdapter::new()
            .with_pkg("react", &["18.2.0", "18.3.0"], &[("loose-envify", "^1.1.0")])
            .with_pkg("loose-envify", &["1.4.0"], &[]);
        AdapterRegistry::new(vec![Box::new(adapter)])
    }

    #[test]
    fn core_info_reports_platform() {
        let registry = AdapterRegistry::default();
        let response = dispatch(&registry, "core.info", &json!({}));
        assert_eq!(response["ok"], true);
        assert_eq!(response["data"]["app"], "UMango");
        assert!(response["data"]["platform"].is_string());
        assert_eq!(response["data"]["availability_ttl_secs"], 86_400);
    }

    #[test]
    fn unknown_method_is_a_structured_error_not_a_panic() {
        let registry = AdapterRegistry::default();
        let response = dispatch(&registry, "does.not.exist", &json!({}));
        assert_eq!(response["ok"], false);
        assert!(response["error"]["message"]
            .as_str()
            .unwrap()
            .contains("未知方法"));
    }

    #[test]
    fn missing_arguments_are_reported_cleanly() {
        let registry = registry_with_fake();
        let response = dispatch(&registry, "packages.versions", &json!({}));
        assert_eq!(response["ok"], false);
        assert!(response["error"]["message"].as_str().unwrap().contains("source"));

        let response = dispatch(&registry, "packages.versions", &json!({"source":"npm"}));
        assert_eq!(response["ok"], false);
        assert!(response["error"]["message"].as_str().unwrap().contains("name"));

        let response = dispatch(&registry, "packages.versions", &json!({"source":"nope","name":"x"}));
        assert_eq!(response["error"]["code"], "unknown_source");
    }

    #[test]
    fn error_envelope_carries_actionable_fields() {
        let registry = AdapterRegistry::default();
        // 空注册表 -> unknown_source
        let response = dispatch(&registry, "packages.versions", &json!({"source":"npm","name":"x"}));
        assert_eq!(response["ok"], false);
        assert_eq!(response["error"]["code"], "unknown_source");
        assert!(response["error"]["advice"].is_string());
        assert!(response["error"]["retryable"].is_boolean());
    }

    #[test]
    fn versions_and_dependency_tree_round_trip_through_json() {
        let registry = registry_with_fake();
        let response = dispatch(
            &registry,
            "packages.versions",
            &json!({"source":"npm","name":"react"}),
        );
        assert_eq!(response["ok"], true);
        let versions = response["data"].as_array().unwrap();
        assert_eq!(versions.len(), 2);
        assert_eq!(versions[0]["channel"], "stable");

        let response = dispatch(
            &registry,
            "packages.dependencies",
            &json!({"source":"npm","name":"react","max_depth":2}),
        );
        assert_eq!(response["ok"], true);
        assert_eq!(response["data"]["root"]["id"]["name"], "react");
        assert_eq!(response["data"]["root"]["children"][0]["id"]["name"], "loose-envify");
        assert!(response["data"]["summary"]["total"].as_u64().unwrap() >= 2);
    }

    #[test]
    fn resolve_install_builds_the_right_command_per_source() {
        let registry = registry_with_fake();
        let response = dispatch(
            &registry,
            "packages.resolve_install",
            &json!({"source":"npm","name":"react","version":"18.3.0"}),
        );
        assert_eq!(response["ok"], true);
        assert_eq!(response["data"]["plan"]["program"], "npm");
        assert_eq!(response["data"]["plan"]["args"][0], "install");
        let args = response["data"]["plan"]["args"].as_array().unwrap();
        assert!(args.contains(&json!("react@18.3.0")));
        assert!(args.contains(&json!("-g")));

        let response = dispatch(
            &registry,
            "packages.resolve_install",
            &json!({"source":"npm","name":"react","scope":"project_local"}),
        );
        let args = response["data"]["plan"]["args"].as_array().unwrap();
        assert!(!args.contains(&json!("-g")));
    }

    #[test]
    fn parse_args_tolerates_junk_from_the_ffi_boundary() {
        assert_eq!(parse_args(None), json!({}));
        assert_eq!(parse_args(Some("   ")), json!({}));
        assert_eq!(parse_args(Some(r#"{"a":1}"#)), json!({"a":1}));
        // 非法 JSON 不应 panic，退化成空对象
        assert_eq!(parse_args(Some("}{ 坏掉的 json")), json!({}));
    }

    #[test]
    fn dispatch_to_string_always_yields_valid_json() {
        let registry = registry_with_fake();
        for method in METHODS {
            let s = dispatch_to_string(&registry, method, Some("{}"));
            assert!(
                serde_json::from_str::<Value>(&s).is_ok(),
                "{method} 的返回值不是合法 JSON: {s}"
            );
        }
    }
}
