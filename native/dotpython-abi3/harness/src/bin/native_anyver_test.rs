//! Rust port of `native_anyver_test.cpp` — pinned Anyver module native-ABI conformance.
//! Invoked with the path to the qualified Anyver native module.

#![allow(non_snake_case)]

use core::ffi::{c_char, c_int};
use core::mem::transmute;
use core::ptr;
use dotpython_harness::*;

type InitFn = unsafe extern "C" fn() -> *mut PyObject;

fn err_msg() -> String {
    unsafe { cstr(dp_abi3_error_message()) }
}

fn call(owner: *mut PyObject, name: &str, args: *mut PyObject) -> *mut PyObject {
    let attr = unicode(name);
    let callable = unsafe { PyObject_GetAttr(owner, attr) };
    unsafe { _Py_DecRef(attr) };
    check!(!callable.is_null(), "{}", err_msg());
    let result = unsafe { PyObject_Call(callable, args, ptr::null_mut()) };
    unsafe { _Py_DecRef(callable) };
    check!(!result.is_null(), "{}", err_msg());
    result
}

fn tuple_set_text(tuple: *mut PyObject, index: Py_ssize_t, text: &str) {
    let value = unicode(text);
    check!(unsafe { PyTuple_SetItem(tuple, index, value) } == 0);
}

fn main() -> std::process::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("usage: native_anyver_test <anyver-module-path>");
        return std::process::ExitCode::from(2);
    }
    section("Pinned Anyver module satisfies the native ABI contract");
    check!(unsafe { dp_abi3_bridge_version() } == 2);

    let lib = Library::open(&args[1]);
    let init: InitFn = unsafe { transmute(lib.symbol("PyInit__anyver")) };

    let mut module: *mut PyObject = ptr::null_mut();
    let mut multi_phase: c_int = 0;
    check!(
        unsafe { dp_abi3_module_initialize(init(), &mut module, &mut multi_phase) } == 0,
        "{}",
        err_msg()
    );
    check!(multi_phase == 1);
    let initialized = unsafe { dp_abi3_active_object_count() };

    let mut cmp: i64 = 0;
    check!(
        unsafe {
            dp_abi3_anyver_compare(
                module,
                c("1.0").as_ptr(),
                c("2.0").as_ptr(),
                c("generic").as_ptr(),
                &mut cmp,
            )
        } == 0,
        "{}",
        err_msg()
    );
    check!(cmp == -1);

    let versions = [c("2.0"), c("1.0-alpha"), c("1.0")];
    let raw: Vec<*const c_char> = versions.iter().map(|v| v.as_ptr()).collect();
    let mut json: *const c_char = ptr::null();
    check!(
        unsafe {
            dp_abi3_anyver_sort_versions(module, raw.as_ptr(), 3, c("generic").as_ptr(), &mut json)
        } == 0,
        "{}",
        err_msg()
    );
    check!(
        unsafe { cstr(json) } == "[\"1.0-alpha\",\"1.0\",\"2.0\"]",
        "got {}",
        unsafe { cstr(json) }
    );

    check!(
        unsafe {
            dp_abi3_anyver_version_to_json(
                module,
                c("1.2.3").as_ptr(),
                c("auto").as_ptr(),
                &mut json,
            )
        } == 0,
        "{}",
        err_msg()
    );
    let text = unsafe { cstr(json) };
    check!(text.contains("\"raw\":\"1.2.3\""), "got {text}");
    check!(text.contains("\"major\":1"), "got {text}");

    // Direct Python-level call path.
    let compare_args = unsafe { PyTuple_New(2) };
    tuple_set_text(compare_args, 0, "1.0");
    tuple_set_text(compare_args, 1, "2.0");
    let comparison = call(module, "compare", compare_args);
    check!(unsafe { PyLong_AsLong(comparison) } == -1);
    check!(unsafe { PyErr_Occurred() }.is_null());
    unsafe { _Py_DecRef(comparison) };
    unsafe { _Py_DecRef(compare_args) };

    unsafe { dp_abi3_module_destroy(module) };
    check!(unsafe { dp_abi3_active_object_count() } <= initialized);

    std::process::ExitCode::from(finish() as u8)
}
