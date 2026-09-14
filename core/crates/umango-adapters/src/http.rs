//! HTTP 取数工具。
//!
//! 设计选择：**不引入 HTTP/TLS 依赖库，改为调用系统 `curl`**。
//!
//! 理由：
//! 1. 项目架构里"进程调用"本来就归 C++ 桥接层管，走 `ProcessRunner` 是同一套机制；
//! 2. `curl` 在 macOS / Linux / Windows 10+ 都是系统自带，省掉一整套 TLS 依赖树，
//!    也让离线构建更容易（本机 github.com 不可达，依赖越少越好）；
//! 3. 结果可缓存，能顺带保护 GitHub API 的未认证限流（60 次/小时）。

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::Value;
use umango_core::error::{CoreError, Result};
use umango_core::process::{self, ProcessSpec};

/// 缓存容量上限，超过就整体清空（简单策略，避免无界增长）。
const CACHE_CAPACITY: usize = 512;

fn cache() -> &'static Mutex<HashMap<String, (i64, Value)>> {
    static CACHE: OnceLock<Mutex<HashMap<String, (i64, Value)>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

fn now_epoch() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// 带缓存的 GET JSON。
pub fn get_json_cached(url: &str, ttl_secs: i64, timeout_ms: u64) -> Result<Value> {
    {
        let cache = cache().lock().expect("http cache poisoned");
        if let Some((fetched_at, value)) = cache.get(url) {
            if now_epoch() - fetched_at < ttl_secs {
                return Ok(value.clone());
            }
        }
    }

    let value = get_json(url, timeout_ms)?;

    let mut cache = cache().lock().expect("http cache poisoned");
    if cache.len() >= CACHE_CAPACITY {
        cache.clear();
    }
    cache.insert(url.to_string(), (now_epoch(), value.clone()));
    Ok(value)
}

/// GET 一个 JSON 文档。
///
/// 关键细节（都是真机踩出来的）：
/// - 必须跟随重定向：`api.github.com/repos/facebook/react` 会返回 **301**，
///   不跟随时拿到的是 `{"message":"Moved Permanently"}` 而不是数据；
/// - 必须显式拿 HTTP 状态码：curl 默认在 4xx/5xx 时仍以退出码 0 返回，
///   只把错误体写到 stdout，不检查状态码就会把错误体当成数据解析。
pub fn get_json(url: &str, timeout_ms: u64) -> Result<Value> {
    let body = get(url, timeout_ms)?;
    process::parse_json_lossy::<Value>(&body, &format!("HTTP GET {url}"))
}

/// GET 文本内容（返回 body 字符串，已校验状态码）。
pub fn get(url: &str, timeout_ms: u64) -> Result<String> {
    let runner = process::runner();
    if runner.which("curl").is_none() {
        return Err(CoreError::cli_missing(
            "curl",
            Some("UMango 依赖系统 curl 访问包源 API；Windows 10+ 已内置，老系统请手动安装"),
        ));
    }

    let timeout_secs = (timeout_ms / 1000).max(1);
    // 用临时文件接 body：避免把 JSON 和状态码混在同一个 stdout 里再切分
    let tmp = std::env::temp_dir().join(format!(
        "umango-http-{}-{:x}.body",
        std::process::id(),
        fnv1a(url)
    ));
    let tmp_str = tmp.to_string_lossy().into_owned();

    let mut spec = ProcessSpec::new("curl")
        .args([
            "-sS".to_string(),
            "-L".to_string(), // 跟随重定向
            "--max-time".to_string(),
            timeout_secs.to_string(),
            "--retry".to_string(),
            "1".to_string(),
            "-o".to_string(),
            tmp_str.clone(),
            "-w".to_string(),
            "%{http_code}".to_string(),
            "-H".to_string(),
            "Accept: application/json".to_string(),
            "-H".to_string(),
            format!("User-Agent: UMango/{}", umango_core::VERSION),
        ])
        .timeout_ms(timeout_ms + 2_000);

    // 可选：GitHub Token 可把未认证的 60 次/小时提升到 5000 次/小时
    if let Ok(token) = std::env::var("UMANGO_GITHUB_TOKEN") {
        if !token.trim().is_empty() && url.contains("api.github.com") {
            spec = spec.args([
                "-H".to_string(),
                format!("Authorization: Bearer {}", token.trim()),
            ]);
        }
    }

    spec = spec.arg(url);

    let out = runner.run(&spec);

    // 无论成败都要清理临时文件
    let read_body = || std::fs::read_to_string(&tmp).unwrap_or_default();

    let out = match out {
        Ok(o) => o,
        Err(e) => {
            let _ = std::fs::remove_file(&tmp);
            return Err(match e {
                CoreError::CliMissing { cli, hint } => CoreError::CliMissing { cli, hint },
                other => CoreError::Network {
                    reason: format!("请求 {url} 失败：{other}"),
                    url: Some(url.to_string()),
                },
            });
        }
    };

    if out.timed_out {
        let _ = std::fs::remove_file(&tmp);
        return Err(CoreError::Network {
            reason: format!("请求超时（>{timeout_secs}s）"),
            url: Some(url.to_string()),
        });
    }

    let status_text = out.stdout.trim().to_string();
    let body = read_body();
    let _ = std::fs::remove_file(&tmp);

    if !out.success() {
        return Err(CoreError::Network {
            reason: format!(
                "curl 退出码 {}：{}",
                out.status,
                process::preview(out.stderr.trim(), 200)
            ),
            url: Some(url.to_string()),
        });
    }

    let status: u16 = status_text.parse().unwrap_or(0);
    match status {
        200..=299 => Ok(body),
        403 => Err(CoreError::Network {
            reason: "被拒绝（403），可能是 API 限流或需要令牌；可设置环境变量 UMANGO_GITHUB_TOKEN"
                .to_string(),
            url: Some(url.to_string()),
        }),
        404 => Err(CoreError::Network {
            reason: "资源不存在（404）".to_string(),
            url: Some(url.to_string()),
        }),
        429 => Err(CoreError::Network {
            reason: "请求过于频繁（429），请稍后重试".to_string(),
            url: Some(url.to_string()),
        }),
        other => Err(CoreError::Network {
            reason: format!("HTTP {other}"),
            url: Some(url.to_string()),
        }),
    }
}

fn fnv1a(s: &str) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for b in s.as_bytes() {
        hash ^= *b as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

// ---------------------------------------------------------------------------
// GitHub Stars（下载量兜底链路）
// ---------------------------------------------------------------------------

/// 从 npm 的 `repository` 字段解析出 `owner/repo`。
///
/// 这个字段的形态非常多，实测见过的都有：
/// - `"git+https://github.com/facebook/react.git"`
/// - `{ "type": "git", "url": "git://github.com/foo/bar.git" }`
/// - `"github:foo/bar"`
/// - `"foo/bar"`（npm 简写）
/// - `"https://gitlab.com/foo/bar"`（非 GitHub，无法取 stars）
pub fn parse_repo_slug(repository: &Value) -> Option<String> {
    if repository.is_null() {
        return None;
    }
    let raw = match repository {
        Value::String(s) => s.clone(),
        Value::Object(map) => match map.get("url") {
            Some(Value::String(s)) => s.clone(),
            _ => return None,
        },
        _ => return None,
    };
    slug_from_repo_string(&raw)
}

/// 单独暴露出来便于单测。
pub fn slug_from_repo_string(raw: &str) -> Option<String> {
    let mut s = raw.trim().to_string();
    if s.is_empty() {
        return None;
    }
    // github:owner/repo
    if let Some(rest) = s.strip_prefix("github:") {
        return normalize_slug(rest);
    }
    for prefix in ["git+", "git://", "https://", "http://", "ssh://", "git@"] {
        if let Some(rest) = s.strip_prefix(prefix) {
            s = rest.to_string();
            break;
        }
    }
    // git@github.com:owner/repo.git
    if let Some(rest) = s.strip_prefix("github.com:") {
        return normalize_slug(rest);
    }
    // 处理 host 路径：github.com/owner/repo
    if let Some(idx) = s.find("github.com/") {
        let rest = &s[idx + "github.com/".len()..];
        return normalize_slug(rest);
    }
    // 已经是 owner/repo 简写
    if !s.contains('/') || s.contains(' ') {
        return None;
    }
    if s.matches('/').count() == 1 {
        return normalize_slug(&s);
    }
    None
}

fn normalize_slug(s: &str) -> Option<String> {
    let s = s.trim().trim_end_matches('/');
    let s = s.strip_suffix(".git").unwrap_or(s);
    let mut parts = s.splitn(2, '/');
    let owner = parts.next()?.trim();
    let repo = parts.next()?.trim().trim_end_matches('/');
    if owner.is_empty() || repo.is_empty() || repo.contains('/') {
        return None;
    }
    Some(format!("{owner}/{repo}"))
}

/// 查询 GitHub 仓库的 Star 数。
///
/// 只在"官方下载量拿不到"时作为兜底调用（对应需求：下载量优先，GitHub Stars 兜底）。
pub fn github_stars(slug: &str) -> Result<u64> {
    let slug = slug.trim().trim_matches('/');
    if slug.split('/').count() != 2 {
        return Err(CoreError::network(format!("非法的仓库标识 `{slug}`")));
    }
    let url = format!("https://api.github.com/repos/{slug}");
    // Stars 变化慢，缓存 6 小时；也能保护未认证限流
    let json = get_json_cached(&url, 6 * 3600, 15_000)?;
    json.get("stargazers_count")
        .and_then(Value::as_u64)
        .ok_or_else(|| CoreError::network(format!("GitHub 未返回 `{slug}` 的 stars 字段")))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_all_repository_shapes() {
        let cases = [
            ("git+https://github.com/facebook/react.git", Some("facebook/react")),
            ("https://github.com/facebook/react", Some("facebook/react")),
            ("git://github.com/foo/bar.git", Some("foo/bar")),
            ("github:foo/bar", Some("foo/bar")),
            ("git@github.com:foo/bar.git", Some("foo/bar")),
            ("foo/bar", Some("foo/bar")),
            ("https://gitlab.com/foo/bar", None),
            ("", None),
            ("just-a-word", None),
        ];
        for (input, expected) in cases {
            assert_eq!(
                slug_from_repo_string(input).as_deref(),
                expected,
                "输入 {input:?} 解析结果不符"
            );
        }
    }

    #[test]
    fn parses_repository_object_form() {
        let obj = serde_json::json!({"type": "git", "url": "git+https://github.com/vuejs/core.git"});
        assert_eq!(parse_repo_slug(&obj).as_deref(), Some("vuejs/core"));
        assert_eq!(parse_repo_slug(&Value::Null), None);
        assert_eq!(
            parse_repo_slug(&serde_json::json!("https://github.com/npm/cli.git")),
            Some("npm/cli".to_string())
        );
    }

    /// 真机联网测试：验证 GitHub Stars 兜底链路真的可用。
    /// 网络不可用时跳过（不算失败），避免离线环境误报。
    #[test]
    fn github_stars_is_reachable_in_this_environment() {
        match github_stars("microsoft/winget-cli") {
            Ok(stars) => assert!(stars > 1000, "winget-cli 的 star 数异常: {stars}"),
            Err(e) => eprintln!("跳过：当前网络无法访问 api.github.com（{e}）"),
        }
    }

    #[test]
    fn invalid_slug_is_rejected() {
        assert!(github_stars("not-a-slug").is_err());
    }
}
