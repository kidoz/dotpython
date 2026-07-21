//! Generic-object conformance harness for the pinned Anyver native module.
//! Invoked with the path to the qualified Anyver native entry.

#![allow(non_snake_case)]

use core::ffi::{c_char, c_int};
use core::mem::transmute;
use core::ptr;
use dotpython_harness::*;

type InitFn = unsafe extern "C" fn() -> *mut PyObject;

const DP_OBJECT_LIST: c_int = 6;
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

fn release(object: *mut PyObject) {
    unsafe { dp_abi3_object_release(object) };
}

fn text(value: &str) -> *mut PyObject {
    let encoded = c(value);
    let mut result = ptr::null_mut();
    check!(
        unsafe {
            dp_abi3_object_from_utf8(
                encoded.as_ptr(),
                value.len().try_into().unwrap(),
                &mut result,
            )
        } == 0,
        "{}",
        err_msg()
    );
    check!(!result.is_null());
    result
}

fn integer(value: i64) -> *mut PyObject {
    let mut result = ptr::null_mut();
    check!(
        unsafe { dp_abi3_object_from_int64(value, &mut result) } == 0,
        "{}",
        err_msg()
    );
    check!(!result.is_null());
    result
}

fn attribute(owner: *mut PyObject, name: &str) -> *mut PyObject {
    let encoded = c(name);
    let mut result = ptr::null_mut();
    check!(
        unsafe { dp_abi3_object_get_attr(owner, encoded.as_ptr(), &mut result) } == 0,
        "{}",
        err_msg()
    );
    check!(!result.is_null());
    result
}

fn try_call_named(owner: *mut PyObject, name: &str, arguments: &[*mut PyObject]) -> *mut PyObject {
    let callable = attribute(owner, name);
    let mut result = ptr::null_mut();
    let status = unsafe {
        dp_abi3_object_call(
            callable,
            arguments.as_ptr(),
            arguments.len().try_into().unwrap(),
            &mut result,
        )
    };
    release(callable);
    if status == 0 { result } else { ptr::null_mut() }
}

fn copy_text(pointer: *const c_char, length: i64) -> String {
    check!(!pointer.is_null());
    check!(length >= 0);
    let bytes = unsafe { core::slice::from_raw_parts(pointer.cast::<u8>(), length as usize) };
    String::from_utf8(bytes.to_vec()).unwrap()
}

fn as_text(object: *mut PyObject) -> String {
    let mut pointer = ptr::null();
    let mut length = 0;
    check!(
        unsafe { dp_abi3_object_as_utf8(object, &mut pointer, &mut length) } == 0,
        "{}",
        err_msg()
    );
    copy_text(pointer, length)
}

fn display(object: *mut PyObject) -> String {
    let mut pointer = ptr::null();
    let mut length = 0;
    check!(
        unsafe { dp_abi3_object_string(object, &mut pointer, &mut length) } == 0,
        "{}",
        err_msg()
    );
    copy_text(pointer, length)
}

fn invoke_comparison(
    module: *mut PyObject,
    left: &str,
    right: &str,
    ecosystem: &str,
) -> Option<i64> {
    let arguments = [text(left), text(right), text(ecosystem)];
    let returned = try_call_named(module, "compare", &arguments);
    for argument in arguments {
        release(argument);
    }
    if returned.is_null() {
        return None;
    }

    let mut result = 0;
    let status = unsafe { dp_abi3_object_as_int64(returned, &mut result) };
    release(returned);
    (status == 0).then_some(result)
}

fn create_version(module: *mut PyObject, raw: &str) -> *mut PyObject {
    let raw_value = text(raw);
    let version = try_call_named(module, "Version", &[raw_value]);
    release(raw_value);
    check!(!version.is_null(), "{}", err_msg());
    version
}

fn verify_sort(module: *mut PyObject) {
    let versions = [text("2.0"), text("1.0-alpha"), text("1.0")];
    let mut list = ptr::null_mut();
    check!(
        unsafe {
            dp_abi3_object_sequence(
                DP_OBJECT_LIST,
                versions.as_ptr(),
                versions.len().try_into().unwrap(),
                &mut list,
            )
        } == 0,
        "{}",
        err_msg()
    );
    for version in versions {
        release(version);
    }
    let ecosystem = text("generic");
    let sorted = try_call_named(module, "sort_versions", &[list, ecosystem]);
    release(ecosystem);
    release(list);
    check!(!sorted.is_null(), "{}", err_msg());

    let mut size = 0;
    check!(unsafe { dp_abi3_object_size(sorted, &mut size) } == 0);
    check!(size == 3);
    for (index, expected) in ["1.0-alpha", "1.0", "2.0"].iter().enumerate() {
        let key = integer(index as i64);
        let mut item = ptr::null_mut();
        check!(unsafe { dp_abi3_object_get_item(sorted, key, &mut item) } == 0);
        check!(as_text(item) == *expected);
        release(item);
        release(key);
    }
    release(sorted);
}

fn stress_heap_type(module: *mut PyObject) {
    section("Pinned Anyver heap types survive generic reference churn");
    let warmup = create_version(module, "1.2.3");
    release(warmup);
    clear_error();
    let baseline = unsafe { dp_abi3_active_object_count() };

    for iteration in 0..HEAP_TYPE_STRESS_ITERATIONS {
        let raw = format!("1.2.{}", iteration % 100);
        let version = create_version(module, &raw);
        let raw_attribute = attribute(version, "raw");
        check!(display(version) == raw);
        check!(as_text(raw_attribute) == raw);
        release(raw_attribute);
        release(version);

        if iteration % 128 == 0 {
            check!(unsafe { dp_abi3_active_object_count() } == baseline);
        }
    }

    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn stress_failures(module: *mut PyObject) {
    section("Pinned Anyver generic failures do not leak or poison later calls");
    clear_error();
    let baseline = unsafe { dp_abi3_active_object_count() };

    for iteration in 0..FAILURE_STRESS_ITERATIONS {
        check!(invoke_comparison(module, "1.0", "2.0", "dotpython-invalid-ecosystem").is_none());
        check!(err_type() == "ValueError", "got {}", err_type());
        if iteration % 64 == 0 {
            check!(unsafe { dp_abi3_active_object_count() } <= baseline + 1);
        }
    }

    clear_error();
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
    check!(invoke_comparison(module, "1.0", "2.0", "generic") == Some(-1));
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn main() -> std::process::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("usage: native_anyver_test <anyver-module-path>");
        return std::process::ExitCode::from(2);
    }
    section("Pinned Anyver module satisfies the generic native ABI contract");
    check!(unsafe { dp_abi3_bridge_version() } == 4);

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

    let mut names = ptr::null();
    check!(unsafe { dp_abi3_module_attribute_names(module, &mut names) } == 0);
    let names = unsafe { cstr(names) };
    check!(names.contains("\"Version\""));
    check!(names.contains("\"compare\""));
    check!(names.contains("\"sort_versions\""));

    check!(invoke_comparison(module, "1.0", "2.0", "generic") == Some(-1));
    verify_sort(module);

    let version = create_version(module, "1.2.3-rc.1");
    let raw = attribute(version, "raw");
    let major = attribute(version, "major");
    let prerelease = attribute(version, "is_prerelease");
    check!(as_text(raw) == "1.2.3-rc.1");
    let mut major_value = 0;
    check!(unsafe { dp_abi3_object_as_int64(major, &mut major_value) } == 0);
    check!(major_value == 1);
    let mut prerelease_value = 0;
    check!(unsafe { dp_abi3_object_as_bool(prerelease, &mut prerelease_value) } == 0);
    check!(prerelease_value == 1);

    section("Generic object bridge dispatches Anyver sequence slots");
    let key = integer(0);
    let mut item = ptr::null_mut();
    check!(unsafe { dp_abi3_object_get_item(version, key, &mut item) } == 0);
    let mut first_segment = 0;
    check!(unsafe { dp_abi3_object_as_int64(item, &mut first_segment) } == 0);
    check!(first_segment == 1);
    release(item);
    release(key);
    release(prerelease);
    release(major);
    release(raw);
    release(version);

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
