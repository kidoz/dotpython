//! Rust port of `native_anyver_test.cpp` — pinned Anyver module native-ABI conformance.
//! Invoked with the path to the qualified Anyver native module.

#![allow(non_snake_case)]

use core::ffi::{c_char, c_int};
use core::mem::transmute;
use core::ptr;
use dotpython_harness::*;

type InitFn = unsafe extern "C" fn() -> *mut PyObject;

const FAILURE_STRESS_ITERATIONS: usize = 512;
const HEAP_TYPE_STRESS_ITERATIONS: usize = 2_000;
const REINITIALIZATION_STRESS_ITERATIONS: usize = 64;

fn clear_error() {
    unsafe { dp_abi3_module_destroy(ptr::null_mut()) };
}

fn err_type() -> String {
    unsafe { cstr(dp_abi3_error_type()) }
}

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

fn create_version(module: *mut PyObject, raw: &str) -> *mut PyObject {
    let args = unsafe { PyTuple_New(1) };
    tuple_set_text(args, 0, raw);
    let version = call(module, "Version", args);
    unsafe { _Py_DecRef(args) };
    version
}

fn stress_heap_type(module: *mut PyObject) {
    section("Pinned Anyver heap types survive reference churn");
    let warmup = create_version(module, "1.2.3");
    unsafe { _Py_DecRef(warmup) };
    clear_error();
    let baseline = unsafe { dp_abi3_active_object_count() };

    for iteration in 0..HEAP_TYPE_STRESS_ITERATIONS {
        let raw = format!("1.2.{}", iteration % 100);
        let version = create_version(module, &raw);
        let display = unsafe { PyObject_Str(version) };
        let representation = unsafe { PyObject_Repr(version) };
        check!(unsafe { as_text(display) } == raw);
        check!(unsafe { as_text(representation) }.starts_with("Version('"));
        unsafe { _Py_DecRef(representation) };
        unsafe { _Py_DecRef(display) };
        unsafe { _Py_DecRef(version) };

        if iteration % 128 == 0 {
            check!(unsafe { dp_abi3_active_object_count() } == baseline);
        }
    }

    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn stress_failures(module: *mut PyObject) {
    section("Pinned Anyver failures do not leak or poison later calls");
    clear_error();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let mut result = 0;

    for iteration in 0..FAILURE_STRESS_ITERATIONS {
        check!(
            unsafe {
                dp_abi3_anyver_compare(
                    module,
                    c("1.0").as_ptr(),
                    c("2.0").as_ptr(),
                    c("dotpython-invalid-ecosystem").as_ptr(),
                    &mut result,
                )
            } == -1
        );
        check!(err_type() == "ValueError", "got {}", err_type());
        if iteration % 64 == 0 {
            check!(unsafe { dp_abi3_active_object_count() } <= baseline + 1);
        }
    }

    clear_error();
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
    check!(
        unsafe {
            dp_abi3_anyver_compare(
                module,
                c("1.0").as_ptr(),
                c("2.0").as_ptr(),
                c("generic").as_ptr(),
                &mut result,
            )
        } == 0,
        "{}",
        err_msg()
    );
    check!(result == -1);
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn main() -> std::process::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("usage: native_anyver_test <anyver-module-path>");
        return std::process::ExitCode::from(2);
    }
    section("Pinned Anyver module satisfies the native ABI contract");
    check!(unsafe { dp_abi3_bridge_version() } == 3);

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

    section("Generic object bridge dispatches Anyver sequence slots");
    let version = create_version(module, "1.2.3-rc.1");
    let mut key = ptr::null_mut();
    check!(unsafe { dp_abi3_object_from_int64(0, &mut key) } == 0);
    let mut item = ptr::null_mut();
    check!(unsafe { dp_abi3_object_get_item(version, key, &mut item) } == 0);
    let mut first_segment = 0;
    check!(unsafe { dp_abi3_object_as_int64(item, &mut first_segment) } == 0);
    check!(first_segment == 1);
    unsafe {
        dp_abi3_object_release(item);
        dp_abi3_object_release(key);
        _Py_DecRef(version);
    }

    stress_heap_type(module);
    stress_failures(module);

    unsafe { dp_abi3_module_destroy(module) };
    let retained_cache_baseline = unsafe { dp_abi3_active_object_count() };

    section("Pinned Anyver modules repeatedly initialize and release cleanly");
    for _ in 0..REINITIALIZATION_STRESS_ITERATIONS {
        module = ptr::null_mut();
        multi_phase = 0;
        check!(
            unsafe { dp_abi3_module_initialize(init(), &mut module, &mut multi_phase) } == 0,
            "{}",
            err_msg()
        );
        check!(!module.is_null());
        check!(multi_phase == 1);
        unsafe { dp_abi3_module_destroy(module) };
        check!(unsafe { dp_abi3_active_object_count() } == retained_cache_baseline);
    }

    std::process::ExitCode::from(finish() as u8)
}
