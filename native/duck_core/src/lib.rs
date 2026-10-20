//! duck_core - gestione in memoria di Duck Printer Manager.
//!
//! Interfaccia C ABI minima: un handle opaco + una sola funzione di dispatch JSON
//! (`duck_call`) + `duck_free_string`. Ogni stringa restituita appartiene a Rust e va
//! liberata con `duck_free_string`.

pub mod api;
pub mod hosts;
pub mod logbuf;
pub mod mac;
pub mod secrets;
pub mod tasks;

use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Mutex;

pub const ABI_VERSION: u32 = 1;

pub struct Core {
    pub(crate) inner: Mutex<tasks::TaskStore>,
    pub(crate) secrets: Mutex<secrets::SecretStore>,
}

impl Core {
    pub fn new() -> Self {
        Core { inner: Mutex::new(tasks::TaskStore::new()), secrets: Mutex::new(secrets::SecretStore::default()) }
    }
}

impl Default for Core {
    fn default() -> Self {
        Self::new()
    }
}

#[no_mangle]
pub extern "C" fn duck_abi_version() -> u32 {
    ABI_VERSION
}

#[no_mangle]
pub extern "C" fn duck_core_create() -> *mut Core {
    Box::into_raw(Box::new(Core::new()))
}

/// # Safety
/// `core` deve provenire da `duck_core_create` e non essere gia' stato distrutto.
#[no_mangle]
pub unsafe extern "C" fn duck_core_destroy(core: *mut Core) {
    if !core.is_null() {
        drop(Box::from_raw(core));
    }
}

/// # Safety
/// `s` deve essere una stringa restituita da `duck_call` (o null).
#[no_mangle]
pub unsafe extern "C" fn duck_free_string(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

fn to_c(s: String) -> *mut c_char {
    // il JSON non contiene NUL: serde_json li serializza come \u0000
    CString::new(s).unwrap_or_else(|_| CString::new("{\"ok\":false,\"error\":\"nul\"}").unwrap()).into_raw()
}

/// # Safety
/// `core` puo' essere null per i metodi senza stato; `method` e `args` sono stringhe C UTF-8
/// terminate da NUL (`args` puo' essere null = `{}`).
#[no_mangle]
pub unsafe extern "C" fn duck_call(core: *mut Core, method: *const c_char, args: *const c_char) -> *mut c_char {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if method.is_null() {
            return api::err("metodo nullo");
        }
        let method = match CStr::from_ptr(method).to_str() {
            Ok(m) => m,
            Err(_) => return api::err("metodo non UTF-8"),
        };
        let args_str = if args.is_null() { "{}" } else { CStr::from_ptr(args).to_str().unwrap_or("{}") };
        let core_ref = if core.is_null() { None } else { Some(&*core) };
        api::call(core_ref, method, args_str)
    }));
    to_c(result.unwrap_or_else(|_| api::err("panic interno nel core")))
}
