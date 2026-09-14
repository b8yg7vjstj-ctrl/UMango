//! 版本号处理：通道判定、比较、npm 版本范围匹配、最优版本挑选。
//!
//! 策略（对应路线图风险点 4「不重复造轮子」）：
//! - **标准 semver 范围匹配**复用成熟 crate `node_semver`，不自己实现 `^ ~ >= ||` 语义；
//! - 但 `node_semver::Version` 只实现了 `Eq` 而**没有 `Ord`**，
//!   而排序版本列表是 UI 的硬需求，所以这里补一层比较器；
//! - git 仓库的 tag 经常不是合法 semver（`v1.2`、`release-2024-05`、`20240102`），
//!   所以比较器带一个宽松（loose）降级实现，而不是直接报错。

use std::cmp::Ordering;

use crate::model::ReleaseChannel;

/// 宽松版本号：能被切分成「数字段 / 文本段」的任何字符串。
///
/// 用于 npm 版本、git tag、winget 版本等异构来源的统一排序。
///
/// 相等性**必须**由 [`Ord`] 定义（而不是字段逐个比较）：原始字符串
/// `v1.2` 与 `1.2` 语义相同，若按字符串比较就会判为不等，
/// 从而破坏 `Eq`/`Ord` 一致性，排序与去重都会出错。
#[derive(Debug, Clone)]
pub struct LooseVersion {
    raw: String,
    /// 主版本段，例如 1.2.3 -> [1, 2, 3]
    main: Vec<Part>,
    /// 预发布段（`-` 之后），例如 1.0.0-rc.1 -> [rc, 1]
    pre: Vec<Part>,
    /// 是否有 build metadata（`+` 之后）。按 semver 规范不参与优先级比较。
    has_build: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Part {
    Num(u64),
    Text(String),
}

impl Part {
    fn parse(s: &str) -> Part {
        // 纯数字（且无前导 + 溢出保护）才算数字段；"01" 在 semver 里非法，
        // 但在 winget/git 里常见，这里按数字处理更符合直觉。
        if !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()) {
            if let Ok(n) = s.parse::<u64>() {
                return Part::Num(n);
            }
        }
        Part::Text(s.to_ascii_lowercase())
    }

    /// 主版本段的比较规则：**数字段高于文本段**。
    ///
    /// 理由：主版本段里出现文本意味着它不是一个真正的版本号
    /// （git tag 里的 `nightly`、`latest`、`stable` 这类"浮动标签"）。
    /// 版本列表里应该让 `2.0.0` 排在 `nightly` 前面 —— 早先误用了 semver
    /// 的预发布规则（数字低于字母），结果 `nightly` 被排到了所有真实版本之前。
    fn cmp_main(&self, other: &Part) -> Ordering {
        match (self, other) {
            (Part::Num(a), Part::Num(b)) => a.cmp(b),
            (Part::Num(_), Part::Text(_)) => Ordering::Greater,
            (Part::Text(_), Part::Num(_)) => Ordering::Less,
            (Part::Text(a), Part::Text(b)) => a.cmp(b),
        }
    }

    /// 预发布段的比较规则：遵循 semver —— **数字段低于文本段**
    /// （`1.0.0-1` < `1.0.0-alpha`）。
    fn cmp_pre(&self, other: &Part) -> Ordering {
        match (self, other) {
            (Part::Num(a), Part::Num(b)) => a.cmp(b),
            (Part::Num(_), Part::Text(_)) => Ordering::Less,
            (Part::Text(_), Part::Num(_)) => Ordering::Greater,
            (Part::Text(a), Part::Text(b)) => a.cmp(b),
        }
    }
}

fn split_parts(s: &str) -> Vec<Part> {
    s.split(['.', '-', '_'])
        .filter(|p| !p.is_empty())
        .map(Part::parse)
        .collect()
}

impl LooseVersion {
    pub fn parse(input: &str) -> LooseVersion {
        let raw = input.trim().to_string();
        // 去掉常见的前缀：v1.2.3 / V1.2.3 / release-1.2 / refs/tags/v1.2
        let mut body = raw.as_str();
        if let Some(idx) = body.rfind("refs/tags/") {
            body = &body[idx + "refs/tags/".len()..];
        }
        if let Some(idx) = body.rfind('/') {
            body = &body[idx + 1..];
        }
        let body = body.strip_prefix(['v', 'V']).unwrap_or(body);

        // 分离 build metadata
        let (body, has_build) = match body.split_once('+') {
            Some((a, _)) => (a, true),
            None => (body, false),
        };
        // 分离预发布段
        let (main_s, pre_s) = match body.split_once('-') {
            Some((a, b)) => (a, Some(b)),
            None => (body, None),
        };

        let mut main = split_parts(main_s);
        // "1.2" 与 "1.2.0" 应当相等 -> 补齐到 3 段
        while main.len() < 3 {
            main.push(Part::Num(0));
        }
        // 先算出 owned 的 pre，避免在结构体字面量里同时借用 raw 和移动 raw
        let pre = pre_s.map(split_parts).unwrap_or_default();

        LooseVersion {
            raw,
            main,
            pre,
            has_build,
        }
    }

    pub fn raw(&self) -> &str {
        &self.raw
    }

    /// 是否带 build metadata（`+` 之后的部分）。
    ///
    /// 按 semver 规范，build metadata **不参与**优先级比较，
    /// 所以 `1.0.0+build1` 与 `1.0.0+build2` 视为同一版本；
    /// 但信息本身要保留，UI 上仍可展示。
    pub fn has_build_metadata(&self) -> bool {
        self.has_build
    }

    /// 是否是标准 semver（能过 node_semver 的解析）。
    pub fn is_semver(&self) -> bool {
        node_semver::Version::parse(self.raw.trim_start_matches(['v', 'V'])).is_ok()
    }
}

impl Ord for LooseVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        // 1) 主版本段
        let len = self.main.len().max(other.main.len());
        for i in 0..len {
            let a = self.main.get(i).cloned().unwrap_or(Part::Num(0));
            let b = other.main.get(i).cloned().unwrap_or(Part::Num(0));
            match a.cmp_main(&b) {
                Ordering::Equal => continue,
                non_eq => return non_eq,
            }
        }
        // 2) 预发布段：**有**预发布段的版本低于没有的（1.0.0-rc < 1.0.0）
        match (self.pre.is_empty(), other.pre.is_empty()) {
            (true, true) => Ordering::Equal,
            (true, false) => Ordering::Greater,
            (false, true) => Ordering::Less,
            (false, false) => {
                let len = self.pre.len().max(other.pre.len());
                for i in 0..len {
                    match (self.pre.get(i), other.pre.get(i)) {
                        (Some(a), Some(b)) => match a.cmp_pre(b) {
                            Ordering::Equal => continue,
                            non_eq => return non_eq,
                        },
                        // 段数少的预发布版本更低：1.0.0-rc < 1.0.0-rc.1
                        (None, Some(_)) => return Ordering::Less,
                        (Some(_), None) => return Ordering::Greater,
                        (None, None) => break,
                    }
                }
                Ordering::Equal
            }
        }
    }
}

impl PartialOrd for LooseVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl PartialEq for LooseVersion {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for LooseVersion {}

/// 比较两个版本字符串。任何字符串都能比较，不会失败。
pub fn compare(a: &str, b: &str) -> Ordering {
    LooseVersion::parse(a).cmp(&LooseVersion::parse(b))
}

/// 版本是否"比另一个更新"。
pub fn is_newer(a: &str, b: &str) -> bool {
    compare(a, b) == Ordering::Greater
}

/// 判定版本通道。对应 PCL-CE 的「正式版 / 快照 / 测试版」区分。
pub fn classify(version: &str) -> ReleaseChannel {
    let v = version.trim();
    if v.is_empty() {
        return ReleaseChannel::Unknown;
    }
    let lower = v.to_ascii_lowercase();
    // 去掉前缀 v / refs/tags/
    let core = lower
        .rsplit('/')
        .next()
        .unwrap_or(&lower)
        .trim_start_matches('v');

    // 快照 / nightly
    if core.contains("snapshot") || core.contains("nightly") || core.contains("canary.nightly") {
        return ReleaseChannel::Snapshot;
    }

    // 显式预发布标记
    const PRE_TAGS: [&str; 10] = [
        "alpha", "beta", "rc", "pre", "prerelease", "next", "canary", "dev", "insiders", "exp",
    ];
    let has_pre_marker = PRE_TAGS.iter().any(|t| core.contains(t));
    let has_dash = core.contains('-');
    let has_build_only = core.contains('+') && !has_dash;

    // 主版本全零 + 无显式预发布标记 = nightly 快照。
    // 实测 react 有上千个 `0.0.0-<提交哈希>` 版本（哈希里带字母，
    // 光靠"预发布段含字母"会被误判成测试版），因此这条规则必须在通用
    // 预发布判定之前，但让位于 `0.0.0-experimental-*` 这类显式标记。
    if has_dash && !has_pre_marker && is_zero_main(core) {
        return ReleaseChannel::Snapshot;
    }

    if has_dash && (has_pre_marker || looks_like_prerelease(core)) {
        return ReleaseChannel::Prerelease;
    }

    // 纯日期戳（20240102 / 2024.01.02）通常是滚动快照
    if looks_like_date(core) {
        return ReleaseChannel::Snapshot;
    }

    if has_dash && !has_build_only {
        // 带 `-` 但认不出标记、主版本也不是全零：
        // 交给宽松比较去排序，通道标为未知而不是瞎猜
        return ReleaseChannel::Unknown;
    }

    if node_semver::Version::parse(core).is_ok() {
        return ReleaseChannel::Stable;
    }

    // 完全非语义化（git tag 等）
    ReleaseChannel::Unknown
}

/// 主版本是否全为零（`0.0.0-xxx` 这类 nightly 快照的特征）。
fn is_zero_main(core: &str) -> bool {
    let main = core.split(['-', '+']).next().unwrap_or(core);
    let parts: Vec<&str> = main.split(['.', '_']).filter(|p| !p.is_empty()).collect();
    !parts.is_empty() && parts.iter().all(|p| p.chars().all(|c| c == '0'))
}

/// `-` 之后的段是否像预发布标识（含字母，或纯数字但主版本已有 3 段）。
fn looks_like_prerelease(core: &str) -> bool {
    let Some((_, pre)) = core.split_once('-') else {
        return false;
    };
    let pre = pre.split('+').next().unwrap_or(pre);
    !pre.is_empty() && pre.chars().any(|c| c.is_ascii_alphabetic())
}

fn looks_like_date(core: &str) -> bool {
    let digits: String = core.chars().filter(|c| c.is_ascii_digit()).collect();
    let seps = core.chars().filter(|c| *c == '.' || *c == '-').count();
    // 20240102 或 2024.01.02 或 2024-01-02
    (digits.len() == 8 && (seps == 0 || seps == 2))
        || (digits.len() == 6 && seps == 1)
        || (digits.len() == 14)
}

/// 一个版本是否满足 npm 风格的版本范围。
///
/// 标准 semver 用 `node_semver` 判定；非标准字符串（git tag）降级为
/// 「精确匹配 / 通配 / 简单比较符」的宽松判定，保证依赖树不会因为
/// 一个奇怪的 tag 名整棵树挂掉。
pub fn satisfies(version: &str, requirement: &str) -> bool {
    let req = requirement.trim();
    if req.is_empty() || req == "*" || req.eq_ignore_ascii_case("latest") {
        return true;
    }

    let clean_v = version.trim().trim_start_matches(['v', 'V']);
    // npm 的 file:/git:/http: 等非 semver 依赖声明，无法用 semver 判定
    if req.starts_with("file:")
        || req.starts_with("link:")
        || req.starts_with("git")
        || req.starts_with("http")
        || req.starts_with("npm:")
        || req.starts_with("workspace:")
    {
        return true;
    }

    if let (Ok(v), Ok(r)) = (
        node_semver::Version::parse(clean_v),
        node_semver::Range::parse(req),
    ) {
        return v.satisfies(&r);
    }

    // 降级：宽松判定
    loose_satisfies(version, req)
}

fn loose_satisfies(version: &str, req: &str) -> bool {
    // 多段 `||` 任一满足即可
    if req.contains("||") {
        return req.split("||").any(|alt| loose_satisfies(version, alt.trim()));
    }
    // 空格分隔的 `>=1 <2` 必须全部满足
    if req.split_whitespace().count() > 1 {
        return req
            .split_whitespace()
            .all(|part| loose_satisfies(version, part.trim()));
    }

    let req = req.trim();
    for (op, rest) in [
        (">=", &req[..]),
        ("<=", &req[..]),
        (">", &req[..]),
        ("<", &req[..]),
        ("=", &req[..]),
        ("^", &req[..]),
        ("~", &req[..]),
    ] {
        if let Some(rest) = rest.strip_prefix(op) {
            if rest.is_empty() {
                break;
            }
            let target = LooseVersion::parse(rest);
            let actual = LooseVersion::parse(version);
            return match op {
                ">=" => actual >= target,
                "<=" => actual <= target,
                ">" => actual > target,
                "<" => actual < target,
                "=" => actual == target,
                // 宽松模式下把 ^ / ~ 当作「同主版本 / 同主次版本」
                "^" => {
                    actual.main.first() == target.main.first() && actual >= target
                }
                "~" => {
                    actual.main.first() == target.main.first()
                        && actual.main.get(1) == target.main.get(1)
                        && actual >= target
                }
                _ => false,
            };
        }
    }

    // 通配：1.x / 1.* / 1
    if let Some(prefix) = req
        .strip_suffix(".x")
        .or_else(|| req.strip_suffix(".*"))
        .or_else(|| req.strip_suffix(".X"))
    {
        return version
            .trim_start_matches(['v', 'V'])
            .starts_with(&format!("{prefix}."));
    }

    if req.contains('*') {
        let parts: Vec<&str> = req.split('*').collect();
        return parts.iter().all(|p| version.contains(p));
    }

    // 精确匹配（忽略 v 前缀）
    LooseVersion::parse(version) == LooseVersion::parse(req)
}

/// 从候选版本中挑出满足范围的最高版本。
///
/// 默认**排除预发布版本**（与 npm 语义一致）：只有范围本身显式写了预发布标记时，
/// 才允许选中预发布版本。这个行为直接决定「版本列表默认给用户选哪个版本」。
pub fn pick_best<'a, I>(candidates: I, requirement: &str) -> Option<&'a str>
where
    I: IntoIterator<Item = &'a str>,
{
    let req_allows_pre = {
        let r = requirement.trim();
        r.contains('-')
    };
    let mut best: Option<&'a str> = None;
    for cand in candidates {
        if !satisfies(cand, requirement) {
            continue;
        }
        if !req_allows_pre {
            let ch = classify(cand);
            if matches!(ch, ReleaseChannel::Prerelease | ReleaseChannel::Snapshot) {
                continue;
            }
        }
        best = match best {
            None => Some(cand),
            Some(cur) => {
                if compare(cand, cur) == Ordering::Greater {
                    Some(cand)
                } else {
                    Some(cur)
                }
            }
        };
    }
    best
}

/// 在只考虑「正式版」的前提下挑最高版本，用于卡片上的 latest 展示。
pub fn pick_latest_stable<'a, I>(candidates: I) -> Option<&'a str>
where
    I: IntoIterator<Item = &'a str>,
{
    candidates
        .into_iter()
        .filter(|c| {
            !matches!(
                classify(c),
                ReleaseChannel::Prerelease | ReleaseChannel::Snapshot
            )
        })
        .max_by(|a, b| compare(a, b))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::ReleaseChannel::*;

    #[test]
    fn classifies_channels_like_pcl_ce() {
        assert_eq!(classify("1.2.3"), Stable);
        assert_eq!(classify("v18.2.0"), Stable);
        assert_eq!(classify("2.0.0-beta.1"), Prerelease);
        assert_eq!(classify("1.0.0-rc.2"), Prerelease);
        assert_eq!(classify("3.0.0-alpha"), Prerelease);
        assert_eq!(classify("1.0.0-SNAPSHOT"), Snapshot);
        assert_eq!(classify("2.1.0-nightly"), Snapshot);
        assert_eq!(classify("20240102"), Snapshot);
        // npm 生态的 nightly 快照形态（react 实测有上千个这样的版本号）
        assert_eq!(classify("0.0.0-375616788"), Snapshot);
        assert_eq!(classify("0.0.0-0c756fb-697f004"), Snapshot);
        assert_eq!(classify("0.0.0-experimental-019019be"), Prerelease);
        assert_eq!(classify(""), Unknown);
    }

    #[test]
    fn semver_ordering() {
        assert_eq!(compare("1.2.3", "1.2.4"), Ordering::Less);
        assert_eq!(compare("1.10.0", "1.9.0"), Ordering::Greater); // 数字比较而非字符串比较
        assert_eq!(compare("v1.2.3", "1.2.3"), Ordering::Equal); // v 前缀无影响
        assert_eq!(compare("1.2", "1.2.0"), Ordering::Equal); // 缺段补零
        // 预发布版本低于同号正式版
        assert_eq!(compare("1.0.0-rc.1", "1.0.0"), Ordering::Less);
        assert_eq!(compare("1.0.0-rc.1", "1.0.0-rc.2"), Ordering::Less);
        assert_eq!(compare("1.0.0-alpha", "1.0.0-beta"), Ordering::Less);
        // build metadata 不影响优先级
        assert_eq!(compare("1.0.0+build1", "1.0.0+build2"), Ordering::Equal);
        assert!(LooseVersion::parse("1.0.0+build1").has_build_metadata());
        assert!(!LooseVersion::parse("1.0.0").has_build_metadata());
        // 浮动标签（非版本号）必须排在真实版本之后
        assert_eq!(compare("nightly", "2.0.0-rc.1"), Ordering::Less);
        assert_eq!(compare("latest", "0.0.1"), Ordering::Less);
        // 非 semver 的 git tag 也能排序，不 panic
        assert_eq!(compare("release-2", "release-10"), Ordering::Less);
    }

    #[test]
    fn npm_range_satisfaction_uses_node_semver() {
        assert!(satisfies("1.2.3", "^1.2.0"));
        assert!(!satisfies("2.0.0", "^1.2.0"));
        assert!(satisfies("1.2.9", "~1.2.0"));
        assert!(!satisfies("1.3.0", "~1.2.0"));
        assert!(satisfies("1.5.0", ">=1.0.0 <2.0.0"));
        assert!(satisfies("3.0.0", "^1.0.0 || ^3.0.0"));
        assert!(satisfies("1.2.3", "*"));
        assert!(!satisfies("1.2.3", "^2.0.0"));
        // 非 semver 声明不应让判定崩溃
        assert!(satisfies("1.0.0", "file:../local"));
        assert!(satisfies("1.0.0", "workspace:*"));
    }

    #[test]
    fn loose_fallback_for_git_tags() {
        assert!(satisfies("release-2", "release-2"));
        assert!(satisfies("v1.2", "1.2"));
        assert!(satisfies("1.5.0", "1.x"));
    }

    #[test]
    fn pick_best_excludes_prerelease_unless_requested() {
        let all = ["1.0.0", "1.1.0", "2.0.0-beta.1", "1.2.0-rc.1"];
        // 范围未提预发布 -> 只挑正式版
        assert_eq!(pick_best(all, "^1.0.0"), Some("1.1.0"));
        // 范围显式提预发布 -> 允许选中预发布
        assert_eq!(pick_best(all, "^2.0.0-beta.1"), Some("2.0.0-beta.1"));
        // 无满足项
        assert_eq!(pick_best(all, "^9.0.0"), None);
    }

    #[test]
    fn pick_latest_stable_ignores_snapshots() {
        let all = ["1.0.0", "2.0.0-beta.1", "20240102", "1.4.0"];
        assert_eq!(pick_latest_stable(all), Some("1.4.0"));
    }
}
