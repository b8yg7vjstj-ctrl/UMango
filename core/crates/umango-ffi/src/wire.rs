//! Rust ↔ C++ 之间的进程请求线格式（wire format）。
//!
//! # 为什么要自己定义一种格式，而不是直接传 JSON？
//!
//! 因为**解析方向**决定了风险：JSON 的写入（C++ → Rust，即 `ProcessOutput`）
//! 很简单，一个转义器就够；但 JSON 的**解析**（Rust → C++，即 `ProcessSpec`）
//! 意味着要在 C++ 里手写一个 JSON 解析器，而参数里可能包含引号、反斜杠、
//! 换行、Unicode 控制字符 —— 这正是最容易写出静默 bug 的地方。
//!
//! 所以请求方向改用**长度前缀二进制分帧**：
//! - 没有任何转义，任何字节序列都能安全传输（包括 NUL、换行、任意二进制）；
//! - 每个字段都带长度，C++ 侧可以做严格的越界检查，不存在"解析歧义"；
//! - 带 magic 头 `UMS1` 与版本号，格式演进时能立刻发现不匹配。
//!
//! 所有整数为**小端序**，两端都是显式读写，不依赖平台字节序。
//!
//! # 布局
//!
//! ```text
//! magic            : 4 字节  "UMS1"
//! version          : u32    = 1
//! program          : u32 len + bytes
//! arg_count        : u32
//!   args[i]        : u32 len + bytes
//! cwd_present      : u8     (0/1)
//!   cwd            : u32 len + bytes    (仅当 present=1)
//! timeout_ms       : u64
//! env_count        : u32
//!   env[i].key     : u32 len + bytes
//!   env[i].value   : u32 len + bytes
//! stdin_present    : u8
//!   stdin          : u32 len + bytes    (仅当 present=1)
//! ```

use umango_core::process::ProcessSpec;

/// 线格式 magic，用于快速判别数据是否走错通道。
pub const WIRE_MAGIC: &[u8; 4] = b"UMS1";
/// 线格式版本。
pub const WIRE_VERSION: u32 = 1;

fn push_u8(buf: &mut Vec<u8>, v: u8) {
    buf.push(v);
}

fn push_u32(buf: &mut Vec<u8>, v: u32) {
    buf.extend_from_slice(&v.to_le_bytes());
}

fn push_u64(buf: &mut Vec<u8>, v: u64) {
    buf.extend_from_slice(&v.to_le_bytes());
}

fn push_bytes(buf: &mut Vec<u8>, bytes: &[u8]) {
    push_u32(buf, bytes.len() as u32);
    buf.extend_from_slice(bytes);
}

fn push_str(buf: &mut Vec<u8>, s: &str) {
    push_bytes(buf, s.as_bytes());
}

/// 把 `ProcessSpec` 编码成线格式。
pub fn encode_process_spec(spec: &ProcessSpec) -> Vec<u8> {
    let mut buf = Vec::with_capacity(64 + spec.args.len() * 16);
    buf.extend_from_slice(WIRE_MAGIC);
    push_u32(&mut buf, WIRE_VERSION);

    push_str(&mut buf, &spec.program);

    push_u32(&mut buf, spec.args.len() as u32);
    for arg in &spec.args {
        push_str(&mut buf, arg);
    }

    match &spec.cwd {
        Some(cwd) => {
            push_u8(&mut buf, 1);
            push_str(&mut buf, cwd);
        }
        None => push_u8(&mut buf, 0),
    }

    push_u64(&mut buf, spec.timeout_ms);

    push_u32(&mut buf, spec.env.len() as u32);
    for (k, v) in &spec.env {
        push_str(&mut buf, k);
        push_str(&mut buf, v);
    }

    match &spec.stdin {
        Some(data) => {
            push_u8(&mut buf, 1);
            push_bytes(&mut buf, data.as_bytes());
        }
        None => push_u8(&mut buf, 0),
    }

    buf
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 一个极端的 spec：包含引号、反斜杠、换行、制表符、Unicode 与 NUL。
    /// 这个用例就是"为什么不用 JSON"的证明 —— 二进制分帧下这些字符无需任何转义。
    #[test]
    fn encodes_hostile_strings_without_escaping() {
        let mut spec = ProcessSpec::new("npm");
        spec.args = vec![
            "view".into(),
            "包名/with\"quotes\\and\\backslash".into(),
            "line1\nline2\ttabbed".into(),
            "emoji-🦀-中文".into(),
            "embedded\0nul".into(),
        ];
        spec.cwd = Some("/tmp/带 空格/目录".into());
        spec.env = vec![("UMANGO_TEST".into(), "值=with=equals".into())];
        spec.timeout_ms = 12345;
        spec.stdin = Some("stdin\ncontent\0binary".into());

        let bytes = encode_process_spec(&spec);

        // magic + version 头
        assert_eq!(&bytes[0..4], WIRE_MAGIC);
        assert_eq!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), WIRE_VERSION);
        // 载荷必须原样出现（无转义）
        let haystack = String::from_utf8_lossy(&bytes);
        assert!(haystack.contains("line1\nline2\ttabbed"));
        assert!(haystack.contains("emoji-🦀-中文"));
        assert!(bytes.contains(&0u8), "NUL 字节应原样保留");
    }

    #[test]
    fn encodes_minimal_spec() {
        let spec = ProcessSpec::new("true");
        let bytes = encode_process_spec(&spec);
        assert_eq!(&bytes[0..4], WIRE_MAGIC);
        // magic(4) + version(4) + program_len(4) + "true"(4) + arg_count(4)
        //  + cwd_present(1) + timeout(8) + env_count(4) + stdin_present(1)
        let expected = 4 + 4 + 4 + 4 + 4 + 1 + 8 + 4 + 1;
        assert_eq!(bytes.len(), expected, "最小 spec 的长度应为固定布局");
    }

    #[test]
    fn layout_is_stable_and_documented() {
        // 逐字段校验布局，防止有人无意中改动字段顺序而破坏 ABI
        let spec = ProcessSpec::new("prog").arg("a");
        let bytes = encode_process_spec(&spec);
        let mut off = 0;

        assert_eq!(&bytes[off..off + 4], b"UMS1");
        off += 4;
        assert_eq!(u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap()), 1);
        off += 4;

        let prog_len = u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap()) as usize;
        off += 4;
        assert_eq!(&bytes[off..off + prog_len], b"prog");
        off += prog_len;

        let arg_count = u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap());
        off += 4;
        assert_eq!(arg_count, 1);
        let arg_len = u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap()) as usize;
        off += 4;
        assert_eq!(&bytes[off..off + arg_len], b"a");
        off += arg_len;

        assert_eq!(bytes[off], 0, "无 cwd");
        off += 1;
        assert_eq!(
            u64::from_le_bytes(bytes[off..off + 8].try_into().unwrap()),
            30_000,
            "默认超时"
        );
        off += 8;
        assert_eq!(u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap()), 0);
        off += 4;
        assert_eq!(bytes[off], 0, "无 stdin");
        off += 1;
        assert_eq!(off, bytes.len(), "不应有多余字节");
    }

    #[test]
    fn empty_program_is_encodable() {
        // 空 program 应由 C++ 侧拒绝，编码本身不应 panic
        let spec = ProcessSpec::new("");
        let bytes = encode_process_spec(&spec);
        assert_eq!(&bytes[0..4], WIRE_MAGIC);
    }
}
