//! # umango-ffi — C ABI 导出层
//!
//! 这是 Rust 侧与 C++ 桥接层之间的**唯一接口**。设计目标：
//!
//! 1. **接口面极小**：只导出少量函数 + 1 个回调结构体，避免 ABI 频繁变动；
//! 2. **永不让 panic 穿过 FFI**：所有导出函数都包在 `catch_unwind` 里，
//!    Rust 侧 panic 会变成结构化错误信封，而不是让 C++/Dart 进程崩溃；
//! 3. **字符串所有权清晰**：Rust 分配、Rust 释放（`umango_free_string`），
//!    C++ 只做转发，Dart 经 C++ 释放，绝不出现跨语言 free 的悬垂指针。
//!
//! ## 调用方向
//!
//! ```text
//! 业务调用（向下）：  Dart  ->  C++ umb_bridge_call()  ->  umango_call()  ->  核心层
//! 进程执行（向上）：  核心层 ->  UmangoHostVTable.run_process  ->  C++ posix_spawn
//! ```
//!
//! 注意第二条方向：**进程创建是 C++ 向上提供的回调**，这正是项目分层里
//! "C++ 负责系统调用 / 进程调用"的落地方式 —— Rust 业务层不直接 fork/exec。
//! 当 Dart 端看到 `process_runner == "cpp-bridge"` 时，说明这条回路真的接通了。

use std::ffi::{c_char, c_int, c_void, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Mutex, OnceLock};

use umango_core::error::{CoreError, Result};
use umango_core::model::Platform;
use umango_core::process::{ProcessOutput, ProcessRunner, ProcessSpec};
use umango_core::registry::AdapterRegistry;

pub mod wire;

/// ABI 版本号。C++ 侧启动时会校验，避免 Rust 与 C++ 二进制不匹配时静默出错。
pub const UMANGO_ABI_VERSION: u32 = 1;

/// C++ 宿主提供的回调表。
///
/// 约定：所有 `*mut *mut c_char` 输出参数都由宿主分配，
/// 并统一通过 `free_buffer` 释放（由 Rust 调用），保证"谁分配谁释放"。
#[repr(C)]
pub struct UmangoHostVTable {
    pub abi_version: u32,
    /// 宿主名称，仅用于日志与诊断。
    pub host_name: *const c_char,
    /// 执行一个进程。
    ///
    /// `spec`/`spec_len` 是 [`crate::wire`] 定义的**长度前缀二进制分帧**
    /// （不是 JSON —— 因为 C++ 侧解析 JSON 需要手写解析器，而参数里可能含引号、
    /// 换行、NUL，容易写出静默错误；分帧格式无转义、可严格做越界检查）。
    ///
    /// 成功时把 [`ProcessOutput`] 的 JSON 写入 `out_json`。
    /// 返回 0 成功；[`HOST_NOT_IMPLEMENTED`] 表示本平台未实现；
    /// 其它非 0 为错误（错误信息写入 `out_err`）。
    pub run_process: Option<
        unsafe extern "C" fn(
            spec: *const u8,
            spec_len: usize,
            out_json: *mut *mut c_char,
            out_err: *mut *mut c_char,
        ) -> c_int,
    >,
    /// 在 PATH 中查找可执行文件。找到返回 0 并写入 `out_path`，否则返回非 0。
    pub which:
        Option<unsafe extern "C" fn(program: *const c_char, out_path: *mut *mut c_char) -> c_int>,
    /// 读取文件。返回 0 成功，数据写入 `out_data`（长度写入 `out_len`）。
    pub read_file: Option<
        unsafe extern "C" fn(
            path: *const c_char,
            out_data: *mut *mut u8,
            out_len: *mut usize,
        ) -> c_int,
    >,
    /// 写入文件。返回 0 成功。
    pub write_file: Option<
        unsafe extern "C" fn(
            path: *const c_char,
            data: *const u8,
            len: usize,
            out_err: *mut *mut c_char,
        ) -> c_int,
    >,
    /// 平台信息（OS 名称、版本、架构）的 JSON。
    pub platform_info: Option<unsafe extern "C" fn(out_json: *mut *mut c_char) -> c_int>,
    /// 释放上面任意回调返回的缓冲区。
    pub free_buffer: Option<unsafe extern "C" fn(ptr: *mut c_void)>,
    /// 释放 read_file 返回的数据块。
    pub free_bytes: Option<unsafe extern "C" fn(ptr: *mut u8, len: usize)>,
}

/// 已安装的宿主回调（进程创建从这里取）。
static HOST: OnceLock<HostCallbacks> = OnceLock::new();

#[derive(Clone, Copy)]
struct HostCallbacks {
    run_process:
        unsafe extern "C" fn(*const u8, usize, *mut *mut c_char, *mut *mut c_char) -> c_int,
    which: Option<unsafe extern "C" fn(*const c_char, *mut *mut c_char) -> c_int>,
    free_buffer: Option<unsafe extern "C" fn(*mut c_void)>,
}

// SAFETY: 宿主回调是普通 C 函数指针；C++ 侧对并发调用有互斥保护
// （见 native/src/umango_bridge.cpp 的串行化说明），跨线程调用安全。
unsafe impl Send for HostCallbacks {}
unsafe impl Sync for HostCallbacks {}

/// 全局注册表。首次调用 `umango_call` 时惰性构建。
static REGISTRY: OnceLock<AdapterRegistry> = OnceLock::new();

fn registry() -> &'static AdapterRegistry {
    REGISTRY.get_or_init(|| {
        let adapters = umango_adapters::builtin_adapters();
        let kinds: Vec<&str> = adapters.iter().map(|a| a.kind().as_str()).collect();
        eprintln!(
            "[umango-core] 已注册 {} 个包源适配器: {}",
            adapters.len(),
            kinds.join(", ")
        );
        AdapterRegistry::new(adapters)
    })
}

// ---------------------------------------------------------------------------
// 由宿主回调驱动的进程执行器
// ---------------------------------------------------------------------------

/// 把进程调用转交给 C++ 的执行器。
///
/// 这是发布路径上的默认执行器：C++ 用 `posix_spawn` / `CreateProcess` 真正创建进程，
/// Rust 只负责描述命令与解析结果。
pub struct HostProcessRunner {
    callbacks: HostCallbacks,
}

impl ProcessRunner for HostProcessRunner {
    fn run(&self, spec: &ProcessSpec) -> Result<ProcessOutput> {
        // 长度前缀二进制分帧（见 wire 模块），避免在 C++ 侧手写 JSON 解析器
        let payload = crate::wire::encode_process_spec(spec);

        let mut out_json: *mut c_char = std::ptr::null_mut();
        let mut out_err: *mut c_char = std::ptr::null_mut();

        // SAFETY: payload 在本函数内保持有效；宿主按 ABI 约定写回输出指针。
        let rc = unsafe {
            (self.callbacks.run_process)(
                payload.as_ptr(),
                payload.len(),
                &mut out_json,
                &mut out_err,
            )
        };

        // 宿主明确声明"本平台未实现"时退回内置执行器，
        // 保证桥接层可以分平台逐步实现而不牺牲可用性。
        if rc == HOST_NOT_IMPLEMENTED {
            let _ = take_c_string(out_err, self.callbacks.free_buffer);
            return umango_core::process::NativeProcessRunner::new().run(spec);
        }

        let error_text = take_c_string(out_err, self.callbacks.free_buffer);
        if rc != 0 {
            let detail = error_text.unwrap_or_else(|| format!("宿主返回错误码 {rc}"));
            // 与纯 Rust 路径保持一致的错误分类：找不到可执行文件 -> cli_missing
            if detail.contains("ENOENT") || detail.contains("not found") {
                return Err(CoreError::cli_missing(&spec.program, None));
            }
            return Err(CoreError::other(format!(
                "宿主执行 `{}` 失败：{detail}",
                spec.display()
            )));
        }

        let raw = take_c_string(out_json, self.callbacks.free_buffer)
            .ok_or_else(|| CoreError::other("宿主未返回进程执行结果"))?;
        let output: ProcessOutput = serde_json::from_str(&raw).map_err(|e| {
            CoreError::other(format!("宿主返回的进程结果不是合法 JSON（{e}）：{raw}"))
        })?;
        Ok(output)
    }

    fn which(&self, program: &str) -> Option<String> {
        let which = self.callbacks.which?;
        let c_prog = CString::new(program).ok()?;
        let mut out_path: *mut c_char = std::ptr::null_mut();
        // SAFETY: 同上，指针有效且由宿主写回。
        let rc = unsafe { which(c_prog.as_ptr(), &mut out_path) };
        if rc != 0 {
            return None;
        }
        take_c_string(out_path, self.callbacks.free_buffer).filter(|s| !s.is_empty())
    }

    fn describe(&self) -> &'static str {
        "cpp-bridge"
    }
}

/// 读取宿主返回的 C 字符串并立刻释放它。
fn take_c_string(
    ptr: *mut c_char,
    free_buffer: Option<unsafe extern "C" fn(*mut c_void)>,
) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    // SAFETY: 宿主保证非空指针指向合法的 NUL 结尾字符串。
    let owned = unsafe { CStr::from_ptr(ptr) }
        .to_string_lossy()
        .into_owned();
    if let Some(free) = free_buffer {
        // SAFETY: 指针由宿主分配，交给宿主的 free_buffer 释放。
        unsafe { free(ptr as *mut c_void) };
    }
    Some(owned)
}

// ---------------------------------------------------------------------------
// 导出的 C ABI
// ---------------------------------------------------------------------------

/// ABI 版本。C++ 侧应先校验它，再调用其它函数。
#[no_mangle]
pub extern "C" fn umango_abi_version() -> u32 {
    UMANGO_ABI_VERSION
}

/// 宿主回调返回这个值表示"本平台未实现"，Rust 侧据此退回内置执行器。
pub const HOST_NOT_IMPLEMENTED: c_int = -1001;

/// 核心层版本字符串（静态生命周期，调用方**不要**释放）。
#[no_mangle]
pub extern "C" fn umango_core_version() -> *const c_char {
    static VERSION: OnceLock<CString> = OnceLock::new();
    VERSION
        .get_or_init(|| CString::new(umango_core::VERSION).unwrap_or_default())
        .as_ptr()
}

/// 初始化：安装 C++ 宿主回调表。
///
/// 返回 0 成功；-1 宿主表为空；-2 ABI 版本不匹配。
///
/// # Safety
/// `host` 必须指向一个在程序生命周期内保持有效的 [`UmangoHostVTable`]。
#[no_mangle]
pub unsafe extern "C" fn umango_init(host: *const UmangoHostVTable) -> c_int {
    catch_unwind(AssertUnwindSafe(|| {
        if host.is_null() {
            eprintln!("[umango-core] umango_init 收到空宿主表");
            return -1;
        }
        let host = &*host;
        if host.abi_version != UMANGO_ABI_VERSION {
            eprintln!(
                "[umango-core] ABI 版本不匹配：宿主 {} vs 核心 {}",
                host.abi_version, UMANGO_ABI_VERSION
            );
            return -2;
        }
        let Some(run_process) = host.run_process else {
            eprintln!("[umango-core] 宿主表缺少 run_process 回调，保持纯 Rust 执行器");
            return 0;
        };

        let host_name = if host.host_name.is_null() {
            "unknown".to_string()
        } else {
            CStr::from_ptr(host.host_name).to_string_lossy().into_owned()
        };

        let callbacks = HostCallbacks {
            run_process,
            which: host.which,
            free_buffer: host.free_buffer,
        };
        let _ = HOST.set(callbacks);
        umango_core::process::install_runner(Arc::new(HostProcessRunner { callbacks }));
        eprintln!("[umango-core] 已挂载 C++ 宿主进程执行器（宿主：{host_name}）");
        0
    }))
    .unwrap_or(-99)
}

/// 切回纯 Rust 执行器（诊断用：便于对比两条路径行为是否一致）。
#[no_mangle]
pub extern "C" fn umango_use_native_runner() {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        umango_core::process::install_runner(Arc::new(
            umango_core::process::NativeProcessRunner::new(),
        ));
        eprintln!("[umango-core] 已切回纯 Rust 进程执行器");
    }));
}

/// 统一业务调用入口。
///
/// # Safety
/// `method` 必须是 NUL 结尾的合法 C 字符串；`args` 可以为 NULL。
/// 返回的字符串必须用 [`umango_free_string`] 释放。
#[no_mangle]
pub unsafe extern "C" fn umango_call(method: *const c_char, args: *const c_char) -> *mut c_char {
    let response = catch_unwind(AssertUnwindSafe(|| {
        if method.is_null() {
            return error_envelope("缺少方法名");
        }
        let method = match CStr::from_ptr(method).to_str() {
            Ok(m) => m.to_string(),
            Err(_) => return error_envelope("方法名不是合法 UTF-8"),
        };
        let args = if args.is_null() {
            None
        } else {
            match CStr::from_ptr(args).to_str() {
                Ok(s) => Some(s.to_string()),
                Err(_) => return error_envelope("参数不是合法 UTF-8"),
            }
        };
        umango_core::api::dispatch_to_string(registry(), &method, args.as_deref())
    }))
    // panic 也必须变成合法 JSON 信封，绝不让 unwind 穿越 FFI 边界
    .unwrap_or_else(|_| error_envelope("核心层发生内部 panic（已捕获，未影响宿主进程）"));

    CString::new(response)
        .unwrap_or_else(|_| {
            CString::new(r#"{"ok":false,"error":{"code":"internal_error"}}"#).unwrap()
        })
        .into_raw()
}

fn error_envelope(message: &str) -> String {
    serde_json::json!({
        "ok": false,
        "error": {
            "code": "ffi_error",
            "message": message,
            "advice": "manual",
            "retryable": false,
        }
    })
    .to_string()
}

/// 释放 [`umango_call`] 返回的字符串。
///
/// # Safety
/// `ptr` 必须来自 `umango_call`，且同一指针只能释放一次。
#[no_mangle]
pub unsafe extern "C" fn umango_free_string(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    drop(CString::from_raw(ptr));
}

/// 宿主平台标识（`windows` / `macos` / `linux`），静态生命周期。
#[no_mangle]
pub extern "C" fn umango_host_platform() -> *const c_char {
    static NAME: OnceLock<CString> = OnceLock::new();
    NAME.get_or_init(|| CString::new(Platform::host().as_str()).unwrap_or_default())
        .as_ptr()
}

/// 当前进程执行器的名字（`cpp-bridge` 或 `native`），静态生命周期。
#[no_mangle]
pub extern "C" fn umango_process_runner_name() -> *const c_char {
    static NAME: Mutex<Option<CString>> = Mutex::new(None);
    let mut guard = NAME.lock().unwrap_or_else(|e| e.into_inner());
    let locked = guard.get_or_insert_with(|| {
        CString::new(umango_core::process::runner().describe()).unwrap_or_default()
    });
    locked.as_ptr()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_is_exposed() {
        assert_eq!(umango_abi_version(), UMANGO_ABI_VERSION);
    }

    #[test]
    fn core_version_and_platform_are_valid_c_strings() {
        // SAFETY: 返回值指向静态 CString
        let version = unsafe { CStr::from_ptr(umango_core_version()) }
            .to_str()
            .unwrap()
            .to_string();
        assert!(!version.is_empty());
        let platform = unsafe { CStr::from_ptr(umango_host_platform()) }
            .to_str()
            .unwrap()
            .to_string();
        assert!(["windows", "macos", "linux"].contains(&platform.as_str()));
    }

    #[test]
    fn call_returns_valid_json_and_can_be_freed() {
        let method = CString::new("core.info").unwrap();
        let ptr = unsafe { umango_call(method.as_ptr(), std::ptr::null()) };
        assert!(!ptr.is_null());
        let json_text = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap().to_string();
        let value: serde_json::Value = serde_json::from_str(&json_text).expect("必须是合法 JSON");
        assert_eq!(value["ok"], true);
        assert_eq!(value["data"]["app"], "UMango");
        // 释放，不应崩溃或泄漏
        unsafe { umango_free_string(ptr) };
    }

    #[test]
    fn sources_list_works_through_the_ffi() {
        let method = CString::new("sources.list").unwrap();
        let ptr = unsafe { umango_call(method.as_ptr(), std::ptr::null()) };
        let json_text = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap().to_string();
        unsafe { umango_free_string(ptr) };

        let value: serde_json::Value = serde_json::from_str(&json_text).unwrap();
        assert_eq!(value["ok"], true);
        let sources = value["data"].as_array().expect("data 应为数组");
        assert_eq!(sources.len(), 6, "应返回全部 6 个包源");
        for source in sources {
            assert!(source["kind"].is_string());
            assert!(source["supported_here"].is_boolean());
            assert!(source["availability"]["state"].is_string());
            assert!(source["capabilities"]["search"].is_boolean());
        }
    }

    #[test]
    fn null_method_is_reported_not_crashing() {
        let ptr = unsafe { umango_call(std::ptr::null(), std::ptr::null()) };
        assert!(!ptr.is_null());
        let json_text = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap().to_string();
        unsafe { umango_free_string(ptr) };
        let value: serde_json::Value = serde_json::from_str(&json_text).unwrap();
        assert_eq!(value["ok"], false);
        assert_eq!(value["error"]["code"], "ffi_error");
    }

    #[test]
    fn freeing_null_is_a_noop() {
        unsafe { umango_free_string(std::ptr::null_mut()) };
    }

    #[test]
    fn init_rejects_null_and_wrong_abi() {
        // SAFETY: 空指针被显式处理
        assert_eq!(unsafe { umango_init(std::ptr::null()) }, -1);

        let bad = UmangoHostVTable {
            abi_version: 999,
            host_name: std::ptr::null(),
            run_process: None,
            which: None,
            read_file: None,
            write_file: None,
            platform_info: None,
            free_buffer: None,
            free_bytes: None,
        };
        let rc = unsafe { umango_init(&bad) };
        assert!(rc < 0, "ABI 不匹配必须被拒绝，实际返回 {rc}");
    }

    #[test]
    fn unknown_method_yields_structured_error() {
        let method = CString::new("nope.nope").unwrap();
        let ptr = unsafe { umango_call(method.as_ptr(), std::ptr::null()) };
        let json_text = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap().to_string();
        unsafe { umango_free_string(ptr) };
        let value: serde_json::Value = serde_json::from_str(&json_text).unwrap();
        assert_eq!(value["ok"], false);
        assert!(value["error"]["message"]
            .as_str()
            .unwrap()
            .contains("未知方法"));
    }
}
