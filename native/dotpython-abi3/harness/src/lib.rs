//! Shared bindings and a tiny assertion framework for the native harnesses.
//!
//! Replaces the C++ Catch2 harnesses. The bridge is linked (build.rs), so its C ABI is available
//! through the `extern` block below; fixtures are loaded with the `dl` helpers.

#![allow(non_snake_case)]
#![allow(non_upper_case_globals)]
#![allow(non_camel_case_types)]

use core::ffi::{c_char, c_int, c_long, c_ulong, c_void};
use std::sync::atomic::{AtomicUsize, Ordering};

pub type Py_ssize_t = isize;

#[repr(C)]
pub struct PyObject {
    pub ob_refcnt: isize,
    pub ob_type: *mut c_void,
}

#[repr(C)]
pub struct PyMethodDef {
    pub ml_name: *const c_char,
    pub ml_meth: Option<unsafe extern "C" fn(*mut PyObject, *mut PyObject) -> *mut PyObject>,
    pub ml_flags: c_int,
    pub ml_doc: *const c_char,
}

pub const METH_FASTCALL: c_int = 0x0080;

unsafe extern "C" {
    pub fn dp_abi3_bridge_version() -> c_int;
    pub fn dp_abi3_module_initialize(
        init: *mut PyObject,
        module: *mut *mut PyObject,
        multi_phase: *mut c_int,
    ) -> c_int;
    pub fn dp_abi3_module_get_int(
        module: *mut PyObject,
        name: *const c_char,
        value: *mut i64,
    ) -> c_int;
    pub fn dp_abi3_module_call_long(
        module: *mut PyObject,
        method: *const c_char,
        has_arg: c_int,
        arg: i64,
        result: *mut i64,
    ) -> c_int;
    pub fn dp_abi3_module_destroy(module: *mut PyObject);
    pub fn dp_abi3_error_type() -> *const c_char;
    pub fn dp_abi3_error_message() -> *const c_char;
    pub fn dp_abi3_active_object_count() -> i64;
    pub fn dp_abi3_anyver_compare(
        module: *mut PyObject,
        left: *const c_char,
        right: *const c_char,
        ecosystem: *const c_char,
        result: *mut i64,
    ) -> c_int;
    pub fn dp_abi3_anyver_sort_versions(
        module: *mut PyObject,
        versions: *const *const c_char,
        count: i64,
        ecosystem: *const c_char,
        result_json: *mut *const c_char,
    ) -> c_int;
    pub fn dp_abi3_anyver_version_to_json(
        module: *mut PyObject,
        version: *const c_char,
        ecosystem: *const c_char,
        result_json: *mut *const c_char,
    ) -> c_int;

    pub fn PyUnicode_FromStringAndSize(text: *const c_char, size: Py_ssize_t) -> *mut PyObject;
    pub fn PyUnicode_AsUTF8AndSize(unicode: *mut PyObject, size: *mut Py_ssize_t) -> *const c_char;
    pub fn PyObject_Str(object: *mut PyObject) -> *mut PyObject;
    pub fn PyObject_Repr(object: *mut PyObject) -> *mut PyObject;
    pub fn PyObject_GetAttr(object: *mut PyObject, name: *mut PyObject) -> *mut PyObject;
    pub fn PyObject_Call(
        callable: *mut PyObject,
        args: *mut PyObject,
        kwargs: *mut PyObject,
    ) -> *mut PyObject;
    pub fn PyObject_GenericGetDict(object: *mut PyObject, ctx: *mut c_void) -> *mut PyObject;
    pub fn PyObject_GenericSetDict(
        object: *mut PyObject,
        value: *mut PyObject,
        ctx: *mut c_void,
    ) -> c_int;
    pub fn PyDict_New() -> *mut PyObject;
    pub fn PyDict_SetItem(d: *mut PyObject, k: *mut PyObject, v: *mut PyObject) -> c_int;
    pub fn PyDict_GetItemWithError(d: *mut PyObject, k: *mut PyObject) -> *mut PyObject;
    pub fn PyList_New(size: Py_ssize_t) -> *mut PyObject;
    pub fn PyList_Append(list: *mut PyObject, v: *mut PyObject) -> c_int;
    pub fn PyList_Size(list: *mut PyObject) -> Py_ssize_t;
    pub fn PyList_GetItem(list: *mut PyObject, index: Py_ssize_t) -> *mut PyObject;
    pub fn PyList_SetItem(list: *mut PyObject, index: Py_ssize_t, v: *mut PyObject) -> c_int;
    pub fn PyTuple_New(size: Py_ssize_t) -> *mut PyObject;
    pub fn PyTuple_SetItem(t: *mut PyObject, index: Py_ssize_t, v: *mut PyObject) -> c_int;
    pub fn PyLong_FromLong(v: c_long) -> *mut PyObject;
    pub fn PyLong_FromUnsignedLongLong(v: u64) -> *mut PyObject;
    pub fn PyLong_AsLong(v: *mut PyObject) -> c_long;
    pub fn PyImport_Import(name: *mut PyObject) -> *mut PyObject;
    pub fn PyModule_GetNameObject(module: *mut PyObject) -> *mut PyObject;
    pub fn PyCMethod_New(
        def: *mut PyMethodDef,
        self_: *mut PyObject,
        module: *mut PyObject,
        class_type: *mut c_void,
    ) -> *mut PyObject;
    pub fn PyErr_Occurred() -> *mut PyObject;
    pub fn PyErr_SetString(exc: *mut PyObject, msg: *const c_char);
    pub fn PyType_GetFlags(type_: *mut PyObject) -> c_ulong;
    pub fn Py_IsInitialized() -> c_int;
    pub fn Py_NewRef(o: *mut PyObject) -> *mut PyObject;
    pub fn _Py_DecRef(o: *mut PyObject);

    pub static mut PyExc_ValueError: *mut PyObject;
    pub static mut PyType_Type: PyObject;
}

// dlopen helpers (libSystem / libdl).
unsafe extern "C" {
    fn dlopen(path: *const c_char, flags: c_int) -> *mut c_void;
    fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
    fn dlclose(handle: *mut c_void) -> c_int;
    fn dlerror() -> *const c_char;
}

pub const RTLD_NOW: c_int = 0x2;
pub const RTLD_GLOBAL: c_int = 0x8;

pub struct Library {
    handle: *mut c_void,
}

impl Library {
    pub fn open(path: &str) -> Library {
        let c = std::ffi::CString::new(path).unwrap();
        unsafe {
            dlerror();
            let handle = dlopen(c.as_ptr(), RTLD_NOW | RTLD_GLOBAL);
            let err = dlerror();
            if handle.is_null() || !err.is_null() {
                let msg = if err.is_null() {
                    String::new()
                } else {
                    cstr(err)
                };
                panic!("dlopen({path}) failed: {msg}");
            }
            Library { handle }
        }
    }

    pub fn symbol(&self, name: &str) -> *mut c_void {
        let c = std::ffi::CString::new(name).unwrap();
        unsafe {
            dlerror();
            let addr = dlsym(self.handle, c.as_ptr());
            let err = dlerror();
            if addr.is_null() || !err.is_null() {
                panic!("dlsym({name}) failed");
            }
            addr
        }
    }

    pub fn close(self) -> i32 {
        unsafe { dlclose(self.handle) }
    }
}

/// Copies a NUL-terminated C string into an owned Rust string.
///
/// # Safety
///
/// `p` must be null or point to a valid NUL-terminated byte sequence for the duration of the call.
pub unsafe fn cstr(p: *const c_char) -> String {
    if p.is_null() {
        return String::new();
    }
    unsafe { core::ffi::CStr::from_ptr(p) }
        .to_string_lossy()
        .into_owned()
}

pub fn c(s: &str) -> std::ffi::CString {
    std::ffi::CString::new(s).unwrap()
}

pub fn unicode(text: &str) -> *mut PyObject {
    unsafe { PyUnicode_FromStringAndSize(text.as_ptr() as *const c_char, text.len() as Py_ssize_t) }
}

/// Copies the UTF-8 contents of a bridge Unicode object.
///
/// # Safety
///
/// `object` must be null or a valid bridge-owned `PyObject` on the bridge owner thread.
pub unsafe fn as_text(object: *mut PyObject) -> String {
    unsafe {
        let p = PyUnicode_AsUTF8AndSize(object, core::ptr::null_mut());
        cstr(p)
    }
}

// --- assertion framework ---

pub static FAILURES: AtomicUsize = AtomicUsize::new(0);

#[macro_export]
macro_rules! check {
    ($cond:expr) => {{
        if !$cond {
            eprintln!("  FAIL [{}:{}]: {}", file!(), line!(), stringify!($cond));
            $crate::FAILURES.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        }
    }};
    ($cond:expr, $($arg:tt)+) => {{
        if !$cond {
            eprintln!("  FAIL [{}:{}]: {} — {}", file!(), line!(), stringify!($cond), format!($($arg)+));
            $crate::FAILURES.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        }
    }};
}

pub fn section(name: &str) {
    eprintln!("• {name}");
}

pub fn finish() -> i32 {
    let f = FAILURES.load(Ordering::Relaxed);
    if f == 0 {
        eprintln!("All checks passed.");
        0
    } else {
        eprintln!("{f} check(s) failed.");
        1
    }
}
