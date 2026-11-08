use std::ffi::{c_char, c_void, CStr};
use std::panic::catch_unwind;
use std::ptr;
use std::slice;
use crate::core::Engine;

pub const RUNA_ENGINE_ABI_VERSION: u32 = 2;

static LAST_CREATE_ERROR: parking_lot::Mutex<Option<String>> = parking_lot::Mutex::new(None);
static LAST_PANIC: parking_lot::Mutex<Option<String>> = parking_lot::Mutex::new(None);
static PANIC_HOOK_INIT: std::sync::Once = std::sync::Once::new();

pub fn init_panic_hook() {
    PANIC_HOOK_INIT.call_once(|| {
        let prev_hook = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |panic_info| {
            let msg = if let Some(s) = panic_info.payload().downcast_ref::<&str>() {
                s.to_string()
            } else if let Some(s) = panic_info.payload().downcast_ref::<String>() {
                s.clone()
            } else {
                "Unknown panic payload".to_string()
            };

            let loc = panic_info.location().map(|l| format!("{}:{}:{}", l.file(), l.line(), l.column()))
                .unwrap_or_else(|| "unknown location".to_string());
            let backtrace = std::backtrace::Backtrace::force_capture();

            let full_err = format!("Rust Engine PANIC at {}: {}\nBacktrace:\n{}", loc, msg, backtrace);
            tracing::error!("{}", full_err);

            *LAST_PANIC.lock() = Some(full_err);
            *LAST_CREATE_ERROR.lock() = Some(format!("Panic at {}: {}", loc, msg));

            prev_hook(panic_info);
        }));
    });
}

pub const RUNA_OK: i32 = 0;
pub const RUNA_ERR_GENERIC: i32 = 1;
pub const RUNA_ERR_INVALID_ARG: i32 = 2;
pub const RUNA_ERR_PANIC: i32 = 3;
pub const RUNA_ERR_BUFFER_TOO_SMALL: i32 = 4;

pub type RunaEventCallback = Option<unsafe extern "C" fn(
    user: *mut c_void,
    kind: u32,
    data: *const u8,
    len: usize,
)>;

#[repr(C)]
pub struct RunaEngine {
    inner: Engine,
}

#[no_mangle]
pub extern "C" fn runa_engine_abi_version() -> u32 {
    RUNA_ENGINE_ABI_VERSION
}

/// Creates a new RunaEngine instance.
///
/// # Safety
/// - `config_json` must point to valid UTF-8 bytes of length `len` if not null.
/// - `out` must point to a valid memory location that can receive a pointer to `RunaEngine`.
/// - `cb` and `user`, if provided, must be thread-safe and valid for the lifetime of the engine.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_create(
    config_json: *const u8,
    len: usize,
    cb: RunaEventCallback,
    user: *mut c_void,
    out: *mut *mut RunaEngine,
) -> i32 {
    init_panic_hook();
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out.is_null() {
            *LAST_CREATE_ERROR.lock() = Some("Null output pointer provided to runa_engine_create".to_string());
            return RUNA_ERR_INVALID_ARG;
        }

        let config_str = if config_json.is_null() || len == 0 {
            ""
        } else {
            // SAFETY: Checking pointer non-null and slicing within len bytes provided by caller
            let slice = unsafe { slice::from_raw_parts(config_json, len) };
            match std::str::from_utf8(slice) {
                Ok(s) => s,
                Err(_) => {
                    *LAST_CREATE_ERROR.lock() = Some("Invalid UTF-8 in config_json".to_string());
                    return RUNA_ERR_INVALID_ARG;
                }
            }
        };

        match Engine::new(config_str, cb, user) {
            Ok(engine) => {
                let boxed = Box::new(RunaEngine { inner: engine });
                // SAFETY: Writing initialized engine pointer to valid out pointer provided by caller
                unsafe {
                    *out = Box::into_raw(boxed);
                }
                *LAST_CREATE_ERROR.lock() = None;
                RUNA_OK
            }
            Err(e) => {
                eprintln!("runa_engine_create failed: {}", e);
                tracing::error!("runa_engine_create failed: {}", e);
                *LAST_CREATE_ERROR.lock() = Some(e);
                RUNA_ERR_GENERIC
            }
        }
    }));

    match result {
        Ok(code) => code,
        Err(_) => {
            if LAST_CREATE_ERROR.lock().is_none() {
                *LAST_CREATE_ERROR.lock() = Some(
                    LAST_PANIC.lock().clone().unwrap_or_else(|| "Engine creation panicked".to_string()),
                );
            }
            RUNA_ERR_PANIC
        }
    }
}

/// Returns the last error or panic message from runa_engine_create.
///
/// # Safety
/// - `buf` must point to a buffer of at least `cap` bytes if not null.
/// - `len` must point to a valid location receiving the actual message length in bytes.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_last_create_error(
    buf: *mut u8,
    cap: usize,
    len: *mut usize,
) -> i32 {
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if buf.is_null() || len.is_null() {
            return RUNA_ERR_INVALID_ARG;
        }

        let err_msg = LAST_CREATE_ERROR.lock().clone().unwrap_or_default();
        let bytes = err_msg.as_bytes();

        unsafe {
            *len = bytes.len();
        }

        if bytes.len() > cap {
            return RUNA_ERR_BUFFER_TOO_SMALL;
        }

        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len());
        }

        RUNA_OK
    }));

    match result {
        Ok(code) => code,
        Err(_) => RUNA_ERR_PANIC,
    }
}

/// Executes an asynchronous command represented by JSON bytes.
///
/// # Safety
/// - `e` must be a valid pointer to a `RunaEngine` returned by `runa_engine_create`.
/// - `json` must point to valid UTF-8 bytes of length `len`.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_command(
    e: *mut RunaEngine,
    json: *const u8,
    len: usize,
) -> i32 {
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if e.is_null() || json.is_null() || len == 0 {
            return RUNA_ERR_INVALID_ARG;
        }

        // SAFETY: e is non-null and points to a valid RunaEngine
        let engine = unsafe { &(*e).inner };
        let _guard = engine.enter_runtime();

        // SAFETY: json is non-null and len bytes are valid during this call
        let slice = unsafe { slice::from_raw_parts(json, len) };
        let json_str = match std::str::from_utf8(slice) {
            Ok(s) => s,
            Err(err) => {
                engine.set_last_error(format!("Invalid UTF-8 in command: {}", err));
                return RUNA_ERR_INVALID_ARG;
            }
        };

        match engine.execute_command(json_str) {
            Ok(()) => RUNA_OK,
            Err(err) => {
                engine.set_last_error(err);
                RUNA_ERR_GENERIC
            }
        }
    }));

    match result {
        Ok(code) => code,
        Err(_) => {
            if !e.is_null() {
                let panic_msg = LAST_PANIC.lock().clone().unwrap_or_else(|| "Command panicked".to_string());
                unsafe { (*e).inner.set_last_error(panic_msg); }
            }
            RUNA_ERR_PANIC
        }
    }
}

/// Executes a synchronous small query.
///
/// # Safety
/// - `e` must be a valid pointer to a `RunaEngine`.
/// - `json` must point to valid UTF-8 bytes of length `len`.
/// - `out_buf` must point to a buffer of capacity `out_cap`.
/// - `out_len` must point to a valid `usize` to receive the actual length written.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_query(
    e: *mut RunaEngine,
    json: *const u8,
    len: usize,
    out_buf: *mut u8,
    out_cap: usize,
    out_len: *mut usize,
) -> i32 {
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if e.is_null() || json.is_null() || len == 0 || out_buf.is_null() || out_len.is_null() {
            return RUNA_ERR_INVALID_ARG;
        }

        // SAFETY: e is valid pointer
        let engine = unsafe { &(*e).inner };
        let _guard = engine.enter_runtime();

        // SAFETY: json is non-null, len bytes are valid
        let slice = unsafe { slice::from_raw_parts(json, len) };
        let json_str = match std::str::from_utf8(slice) {
            Ok(s) => s,
            Err(err) => {
                engine.set_last_error(format!("Invalid UTF-8 in query: {}", err));
                return RUNA_ERR_INVALID_ARG;
            }
        };

        match engine.query(json_str) {
            Ok(resp) => {
                let bytes = resp.as_bytes();
                // SAFETY: out_len is non-null pointer verified above
                unsafe {
                    *out_len = bytes.len();
                }
                if bytes.len() > out_cap {
                    return RUNA_ERR_BUFFER_TOO_SMALL;
                }
                // SAFETY: copying bytes into out_buf of size out_cap >= bytes.len()
                unsafe {
                    ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, bytes.len());
                }
                RUNA_OK
            }
            Err(err) => {
                engine.set_last_error(err);
                RUNA_ERR_GENERIC
            }
        }
    }));

    match result {
        Ok(code) => code,
        Err(_) => RUNA_ERR_PANIC,
    }
}

/// Sets the native DirectX / WinUI SwapChainPanel presentation surface for a video stream.
///
/// # Safety
/// - `e` must be a valid pointer to a `RunaEngine`.
/// - `stream_id` must point to a valid null-terminated C string.
/// - `_swap_chain_panel_native` must be a valid COM pointer (IUnknown) to ISwapChainPanelNative.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_set_video_surface(
    e: *mut RunaEngine,
    stream_id: *const c_char,
    _swap_chain_panel_native: *mut c_void,
) -> i32 {
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if e.is_null() || stream_id.is_null() {
            return RUNA_ERR_INVALID_ARG;
        }

        let c_str = CStr::from_ptr(stream_id);
        let stream_id_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return RUNA_ERR_INVALID_ARG,
        };

        let engine = &(*e).inner;
        let _guard = engine.enter_runtime();
        if engine.video().set_video_surface(stream_id_str, _swap_chain_panel_native) {
            RUNA_OK
        } else {
            RUNA_ERR_GENERIC
        }
    }));

    match result {
        Ok(code) => code,
        Err(_) => RUNA_ERR_PANIC,
    }
}

/// Retrieves the last error message recorded by the engine.
///
/// # Safety
/// - `e` must be a valid pointer to a `RunaEngine`.
/// - `buf` must point to a valid byte buffer of capacity `cap`.
/// - `len` must point to a valid `usize` to receive the error message byte length.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_last_error(
    e: *mut RunaEngine,
    buf: *mut u8,
    cap: usize,
    len: *mut usize,
) -> i32 {
    let result = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if e.is_null() || buf.is_null() || len.is_null() {
            return RUNA_ERR_INVALID_ARG;
        }

        // SAFETY: e is a valid pointer
        let engine = unsafe { &(*e).inner };

        let err_msg = engine.get_last_error().or_else(|| LAST_PANIC.lock().clone()).unwrap_or_default();
        let bytes = err_msg.as_bytes();

        // SAFETY: len is non-null verified above
        unsafe {
            *len = bytes.len();
        }

        if bytes.len() > cap {
            return RUNA_ERR_BUFFER_TOO_SMALL;
        }

        // SAFETY: copying error string bytes into destination buffer
        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len());
        }

        RUNA_OK
    }));

    match result {
        Ok(code) => code,
        Err(_) => RUNA_ERR_PANIC,
    }
}

/// Destroys a RunaEngine instance and releases all associated resources.
///
/// # Safety
/// - `e` must be a valid pointer to a `RunaEngine` returned by `runa_engine_create`, or null.
#[no_mangle]
pub unsafe extern "C" fn runa_engine_destroy(e: *mut RunaEngine) {
    let _ = catch_unwind(std::panic::AssertUnwindSafe(|| {
        if !e.is_null() {
            // SAFETY: Reclaiming ownership of heap-allocated engine to safely drop it
            unsafe {
                let boxed = Box::from_raw(e);
                let _guard = boxed.inner.enter_runtime();
                boxed.inner.shutdown();
                drop(boxed);
            }
        }
    }));
}
