//! 时间工具。统一用 ISO-8601 / UTC 字符串跨 FFI，避免时区和序列化歧义。

use chrono::{DateTime, SecondsFormat, Utc};

/// 当前时间，RFC3339（毫秒精度，`Z` 结尾）。
pub fn now_iso8601() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

/// 当前 Unix 时间戳（秒）。用于 TTL 计算，避免反复解析时间字符串。
pub fn now_epoch() -> i64 {
    Utc::now().timestamp()
}

/// 解析 ISO-8601 字符串。CLI 输出的时间格式五花八门，这里做宽容解析。
pub fn parse_iso8601(s: &str) -> Option<DateTime<Utc>> {
    let trimmed = s.trim();
    if trimmed.is_empty() {
        return None;
    }
    if let Ok(dt) = DateTime::parse_from_rfc3339(trimmed) {
        return Some(dt.with_timezone(&Utc));
    }
    // npm 的 time 字段有时是 2024-01-02T03:04:05.678Z（已覆盖）、
    // winget/scoop 可能给 2024-01-02 或 2024-01-02 03:04:05
    for fmt in ["%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d"] {
        if let Ok(naive) = chrono::NaiveDateTime::parse_from_str(trimmed, fmt) {
            return Some(naive.and_utc());
        }
        if let Ok(date) = chrono::NaiveDate::parse_from_str(trimmed, fmt) {
            return Some(date.and_hms_opt(0, 0, 0)?.and_utc());
        }
    }
    None
}

/// 距今多少秒（用于 TTL 判断）。解析失败返回 None。
pub fn age_secs(iso: &str) -> Option<i64> {
    let then = parse_iso8601(iso)?;
    Some((Utc::now() - then).num_seconds())
}

/// 人类可读的相对时间，UI 上显示"3 天前发布"。
pub fn humanize_age(iso: &str) -> String {
    let Some(secs) = age_secs(iso) else {
        return "未知".to_string();
    };
    match secs {
        s if s < 0 => "刚刚".to_string(),
        s if s < 60 => format!("{s} 秒前"),
        s if s < 3600 => format!("{} 分钟前", s / 60),
        s if s < 86_400 => format!("{} 小时前", s / 3600),
        s if s < 2_592_000 => format!("{} 天前", s / 86_400),
        s if s < 31_536_000 => format!("{} 个月前", s / 2_592_000),
        s => format!("{} 年前", s / 31_536_000),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_and_parses_round_trip() {
        let now = now_iso8601();
        assert!(now.ends_with('Z'), "应使用 UTC 标识: {now}");
        let parsed = parse_iso8601(&now).expect("应能解析自己生成的时间");
        let age = (Utc::now() - parsed).num_seconds().abs();
        assert!(age < 5, "刚生成的时间戳年龄应接近 0，实际 {age}s");
    }

    #[test]
    fn tolerates_cli_date_formats() {
        assert!(parse_iso8601("2024-01-02T03:04:05.678Z").is_some());
        assert!(parse_iso8601("2024-01-02T03:04:05").is_some());
        assert!(parse_iso8601("2024-01-02 03:04:05").is_some());
        assert!(parse_iso8601("2024-01-02").is_some());
        assert!(parse_iso8601("").is_none());
        assert!(parse_iso8601("不是时间").is_none());
    }

    #[test]
    fn humanizes_ages() {
        assert_eq!(humanize_age("这不是时间"), "未知");
        let two_days_ago = (Utc::now() - chrono::Duration::days(2)).to_rfc3339();
        assert_eq!(humanize_age(&two_days_ago), "2 天前");
    }
}
