//! 进程调用抽象。
//!
//! 架构约定（对应项目分层要求）：**真正的进程创建由 C++ 桥接层负责**
//! （`posix_spawn` / `CreateProcess`），Rust 只描述"要跑什么"。
//!
//! 但 Rust 核心层不能因此变得不可测，所以这里定义 `ProcessRunner` trait：
//! - `NativeProcessRunner`：纯 Rust（`std::process`）实现，给 `cargo test` 和开发期用；
//! - `HostProcessRunner`（在 umango-ffi 中）：把请求转发给 C++ 宿主。
//!
//! 两者行为一致，因此适配器的逻辑可以在没有 GUI、没有 C++ 的情况下完整单测。

use std::collections::HashMap;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock, RwLock};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::error::{CoreError, Result};
use crate::model::Platform;

/// 一次进程调用的请求描述。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessSpec {
    pub program: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default)]
    pub cwd: Option<String>,
    /// 追加/覆盖的环境变量
    #[serde(default)]
    pub env: Vec<(String, String)>,
    #[serde(default = "default_timeout")]
    pub timeout_ms: u64,
    /// 写入子进程 stdin 的内容
    #[serde(default)]
    pub stdin: Option<String>,
}

fn default_timeout() -> u64 {
    30_000
}

impl ProcessSpec {
    pub fn new(program: impl Into<String>) -> Self {
        ProcessSpec {
            program: program.into(),
            args: Vec::new(),
            cwd: None,
            env: Vec::new(),
            timeout_ms: default_timeout(),
            stdin: None,
        }
    }

    pub fn arg(mut self, a: impl Into<String>) -> Self {
        self.args.push(a.into());
        self
    }

    pub fn args<I, S>(mut self, args: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        self.args.extend(args.into_iter().map(Into::into));
        self
    }

    pub fn cwd(mut self, dir: impl Into<String>) -> Self {
        self.cwd = Some(dir.into());
        self
    }

    pub fn timeout_ms(mut self, ms: u64) -> Self {
        self.timeout_ms = ms;
        self
    }

    /// 供错误信息与日志使用的可读命令行。
    pub fn display(&self) -> String {
        let mut s = self.program.clone();
        for a in &self.args {
            s.push(' ');
            if a.contains(' ') {
                s.push('"');
                s.push_str(a);
                s.push('"');
            } else {
                s.push_str(a);
            }
        }
        s
    }
}

/// 进程调用结果。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProcessOutput {
    pub status: i32,
    pub stdout: String,
    pub stderr: String,
    pub timed_out: bool,
    pub duration_ms: u64,
}

impl ProcessOutput {
    pub fn success(&self) -> bool {
        self.status == 0 && !self.timed_out
    }

    /// 把非零退出码转成 CoreError，附带上下文。
    pub fn require_success(&self, context: &str) -> Result<&ProcessOutput> {
        if self.timed_out {
            return Err(CoreError::Timeout {
                context: context.to_string(),
                timeout_ms: 0,
            });
        }
        if self.status != 0 {
            return Err(CoreError::CommandFailed {
                context: context.to_string(),
                code: Some(self.status),
                stderr: self.stderr.trim().to_string(),
                stdout: self.stdout.trim().to_string(),
            });
        }
        Ok(self)
    }
}

/// 进程执行器。
pub trait ProcessRunner: Send + Sync {
    fn run(&self, spec: &ProcessSpec) -> Result<ProcessOutput>;

    /// 在 PATH 中查找可执行文件，返回绝对路径。
    fn which(&self, program: &str) -> Option<String>;

    /// 宿主平台。测试里可以伪造，用来验证平台门控逻辑。
    fn platform(&self) -> Platform {
        Platform::host()
    }

    /// 执行器的自我描述，用于诊断"到底是谁在跑进程"。
    ///
    /// 这个值会经 JSON API 暴露给 UI：如果显示 `cpp-bridge`，
    /// 说明 C++ 底层桥接层确实在链路上，而不是悄悄退化成了纯 Rust 实现。
    fn describe(&self) -> &'static str {
        "native"
    }
}

// ---------------------------------------------------------------------------
// 全局执行器
// ---------------------------------------------------------------------------

static GLOBAL_RUNNER: OnceLock<RwLock<Arc<dyn ProcessRunner>>> = OnceLock::new();

fn slot() -> &'static RwLock<Arc<dyn ProcessRunner>> {
    GLOBAL_RUNNER.get_or_init(|| RwLock::new(Arc::new(NativeProcessRunner::new())))
}

/// 安装自定义执行器（C++ 宿主在初始化时调用）。
pub fn install_runner(runner: Arc<dyn ProcessRunner>) {
    *slot().write().expect("runner lock poisoned") = runner;
}

/// 取当前执行器。
pub fn runner() -> Arc<dyn ProcessRunner> {
    slot().read().expect("runner lock poisoned").clone()
}

/// 便捷函数：用当前执行器跑一条命令。
pub fn run(spec: &ProcessSpec) -> Result<ProcessOutput> {
    runner().run(spec)
}

// ---------------------------------------------------------------------------
// 纯 Rust 实现
// ---------------------------------------------------------------------------

/// 用 `std::process` 实现的执行器。
///
/// 这是**开发/测试**路径；发布构建下会被 C++ 宿主执行器替换。
pub struct NativeProcessRunner {
    platform: Platform,
}

impl NativeProcessRunner {
    pub fn new() -> Self {
        NativeProcessRunner {
            platform: Platform::host(),
        }
    }

    /// 测试用：伪装成别的平台，验证平台门控。
    pub fn with_platform(platform: Platform) -> Self {
        NativeProcessRunner { platform }
    }

    fn spawn(&self, spec: &ProcessSpec) -> Result<ProcessOutput> {
        use std::process::{Command, Stdio};

        let started = Instant::now();
        let mut cmd = Command::new(&spec.program);
        cmd.args(&spec.args);
        cmd.stdout(Stdio::piped());
        cmd.stderr(Stdio::piped());
        cmd.stdin(if spec.stdin.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        });

        if let Some(cwd) = &spec.cwd {
            cmd.current_dir(cwd);
        }
        for (k, v) in &spec.env {
            cmd.env(k, v);
        }
        // 减少 CLI 的交互式行为与本地化输出差异
        cmd.env("NO_COLOR", "1");
        cmd.env("CI", "1");

        #[cfg(unix)]
        {
            use std::os::unix::process::CommandExt;
            // 自成进程组，便于超时时整组清理
            unsafe {
                cmd.pre_exec(|| {
                    libc_setsid();
                    Ok(())
                });
            }
        }

        let mut child = cmd.spawn().map_err(|e| {
            if e.kind() == std::io::ErrorKind::NotFound {
                CoreError::cli_missing(
                    &spec.program,
                    Some("请先安装该命令行工具，或在设置中指定其路径"),
                )
            } else {
                CoreError::Io(e)
            }
        })?;

        if let Some(input) = &spec.stdin {
            if let Some(mut stdin) = child.stdin.take() {
                use std::io::Write;
                let _ = stdin.write_all(input.as_bytes());
                // 关闭 stdin，避免子进程等待 EOF
            }
        }

        let stdout_pipe = child.stdout.take();
        let stderr_pipe = child.stderr.take();

        let out_handle = std::thread::spawn(move || read_all(stdout_pipe));
        let err_handle = std::thread::spawn(move || read_all(stderr_pipe));

        let deadline = started + Duration::from_millis(spec.timeout_ms.max(1));
        let mut timed_out = false;
        let status = loop {
            match child.try_wait() {
                Ok(Some(status)) => break status,
                Ok(None) => {
                    if Instant::now() >= deadline {
                        timed_out = true;
                        let _ = child.kill();
                        let _ = child.wait();
                        break std::process::ExitStatus::default();
                    }
                    std::thread::sleep(Duration::from_millis(5));
                }
                Err(e) => return Err(CoreError::Io(e)),
            }
        };

        let stdout = out_handle.join().unwrap_or_default();
        let stderr = err_handle.join().unwrap_or_default();

        Ok(ProcessOutput {
            status: status.code().unwrap_or(-1),
            stdout,
            stderr,
            timed_out,
            duration_ms: started.elapsed().as_millis() as u64,
        })
    }
}

impl Default for NativeProcessRunner {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(unix)]
fn libc_setsid() {
    // 直接调用 libc 的 setsid()，避免引入 libc crate 依赖：
    // 只需要在新进程组里运行，超时时可整组 kill。
    extern "C" {
        fn setsid() -> i32;
    }
    unsafe {
        setsid();
    }
}

fn read_all<R: Read>(pipe: Option<R>) -> String {
    let Some(mut pipe) = pipe else {
        return String::new();
    };
    let mut buf = Vec::new();
    let _ = pipe.read_to_end(&mut buf);
    String::from_utf8_lossy(&buf).into_owned()
}

impl ProcessRunner for NativeProcessRunner {
    fn run(&self, spec: &ProcessSpec) -> Result<ProcessOutput> {
        self.spawn(spec)
    }

    fn which(&self, program: &str) -> Option<String> {
        which_in_path(program)
    }

    fn platform(&self) -> Platform {
        self.platform
    }
}

/// 在 PATH 中查找可执行文件（跨平台，不依赖外部 crate）。
pub fn which_in_path(program: &str) -> Option<String> {
    // 已经是路径形式：直接判定
    if program.contains('/') || program.contains('\\') {
        let p = Path::new(program);
        return if is_executable(p) {
            Some(p.to_string_lossy().into_owned())
        } else {
            None
        };
    }

    let path_var = std::env::var_os("PATH")?;
    let exts: Vec<String> = if cfg!(windows) {
        std::env::var("PATHEXT")
            .unwrap_or_else(|_| ".COM;.EXE;.BAT;.CMD".to_string())
            .split(';')
            .map(|s| s.to_ascii_lowercase())
            .collect()
    } else {
        vec![String::new()]
    };

    for dir in std::env::split_paths(&path_var) {
        for ext in &exts {
            let candidate: PathBuf = if ext.is_empty() {
                dir.join(program)
            } else {
                dir.join(format!("{program}{ext}"))
            };
            if is_executable(&candidate) {
                return Some(candidate.to_string_lossy().into_owned());
            }
        }
    }
    None
}

fn is_executable(path: &Path) -> bool {
    if !path.is_file() {
        return false;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        match std::fs::metadata(path) {
            Ok(md) => md.permissions().mode() & 0o111 != 0,
            Err(_) => false,
        }
    }
    #[cfg(not(unix))]
    {
        true
    }
}

/// 把 `ProcessOutput` 的 stdout 解析成 JSON，容忍 CLI 在 JSON 前后打印的杂项信息
/// （npm 警告、pnpm 的进度提示、scoop 的横幅都会污染 stdout）。
pub fn parse_json_lossy<T: for<'de> Deserialize<'de>>(
    raw: &str,
    context: &str,
) -> Result<T> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Err(CoreError::parse(context, "输出为空"));
    }
    if let Ok(v) = serde_json::from_str::<T>(trimmed) {
        return Ok(v);
    }
    // 从第一个 '{' 或 '[' 开始截取，逐个尝试到最后一个 '}' / ']'
    let start = trimmed
        .find(['{', '['])
        .ok_or_else(|| CoreError::parse(context, "输出中找不到 JSON 起始字符"))?;
    let slice = &trimmed[start..];
    let end = slice
        .rfind(['}', ']'])
        .ok_or_else(|| CoreError::parse(context, "输出中找不到 JSON 结束字符"))?;
    let candidate = &slice[..=end];
    serde_json::from_str::<T>(candidate).map_err(|e| {
        CoreError::parse(
            context,
            format!("JSON 解析失败: {e}；原始输出片段: {}", preview(candidate, 300)),
        )
    })
}

pub fn preview(s: &str, max: usize) -> String {
    let s = s.trim();
    if s.chars().count() <= max {
        return s.to_string();
    }
    let head: String = s.chars().take(max).collect();
    format!("{head}…")
}

/// 供适配器使用的环境快照（HOME、镜像配置等）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct EnvSnapshot {
    pub vars: HashMap<String, String>,
}

impl EnvSnapshot {
    pub fn capture() -> Self {
        let mut vars = HashMap::new();
        for key in [
            "HOME",
            "USERPROFILE",
            "PATH",
            "APPDATA",
            "LOCALAPPDATA",
            "NPM_CONFIG_REGISTRY",
            "PNPM_HOME",
            "SCOOP",
            "HOMEBREW_PREFIX",
        ] {
            if let Ok(v) = std::env::var(key) {
                vars.insert(key.to_string(), v);
            }
        }
        EnvSnapshot { vars }
    }

    pub fn get(&self, key: &str) -> Option<&str> {
        self.vars.get(key).map(|s| s.as_str())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runs_a_real_command() {
        let runner = NativeProcessRunner::new();
        let out = runner
            .run(&ProcessSpec::new("/bin/echo").arg("hello"))
            .expect("echo should run");
        assert!(out.success());
        assert_eq!(out.stdout.trim(), "hello");
    }

    #[test]
    fn missing_binary_maps_to_cli_missing() {
        let runner = NativeProcessRunner::new();
        let err = runner
            .run(&ProcessSpec::new("definitely-not-a-real-binary-umango"))
            .expect_err("should fail");
        assert_eq!(err.code(), "cli_missing");
        assert_eq!(err.advice(), crate::error::RecoveryAdvice::InstallCli);
    }

    #[test]
    fn non_zero_exit_reports_stderr() {
        let runner = NativeProcessRunner::new();
        let out = runner
            .run(&ProcessSpec::new("/bin/sh").args(["-c", "echo oops >&2; exit 3"]))
            .expect("sh should run");
        assert!(!out.success());
        let err = out.require_success("测试命令").expect_err("should error");
        assert_eq!(err.code(), "command_failed");
        assert!(err.to_string().contains("oops"));
    }

    #[test]
    fn timeout_is_enforced_and_retryable() {
        let runner = NativeProcessRunner::new();
        let out = runner
            .run(&ProcessSpec::new("/bin/sh").args(["-c", "sleep 5"]).timeout_ms(150))
            .expect("sh should run");
        assert!(out.timed_out);
        let err = out.require_success("慢命令").expect_err("should timeout");
        assert_eq!(err.code(), "timeout");
        assert!(err.is_retryable());
    }

    #[test]
    fn which_finds_real_binaries_and_rejects_fakes() {
        let runner = NativeProcessRunner::new();
        assert!(runner.which("sh").is_some());
        assert!(runner.which("umango-nonexistent-binary").is_none());
    }

    #[test]
    fn parses_json_despite_cli_noise() {
        // npm/pnpm 常在 JSON 前后打印警告
        let noisy = "npm warn config something\n{\"name\":\"react\"}\n";
        let v: serde_json::Value = parse_json_lossy(noisy, "npm view").expect("should parse");
        assert_eq!(v["name"], "react");

        let arr = "Progress: 100%\n[1,2,3]\ndone\n";
        let v: Vec<u32> = parse_json_lossy(arr, "pnpm search").expect("should parse");
        assert_eq!(v, vec![1, 2, 3]);

        assert!(parse_json_lossy::<serde_json::Value>("完全不是 JSON", "x").is_err());
        assert!(parse_json_lossy::<serde_json::Value>("", "x").is_err());
    }

    #[test]
    fn stdin_is_forwarded() {
        let runner = NativeProcessRunner::new();
        let mut spec = ProcessSpec::new("/bin/cat");
        spec.stdin = Some("piped-data".to_string());
        let out = runner.run(&spec).expect("cat should run");
        assert_eq!(out.stdout, "piped-data");
    }
}
