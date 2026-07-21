//! Rust port of `native_fixture_test.cpp` — bridge/fixture lifecycle, ownership, and failure tests.

#![allow(non_snake_case)]

use core::ffi::{c_char, c_int};
use core::mem::transmute;
use core::ptr;
use dotpython_harness::*;

type InitFn = unsafe extern "C" fn() -> *mut PyObject;
type CleanupFn = unsafe extern "C" fn() -> c_int;

fn clear() {
    unsafe { dp_abi3_module_destroy(ptr::null_mut()) };
}

fn err_type() -> String {
    unsafe { cstr(dp_abi3_error_type()) }
}
fn err_msg() -> String {
    unsafe { cstr(dp_abi3_error_message()) }
}

unsafe extern "C" fn fastcall_probe(
    _self: *mut PyObject,
    _args: *const *mut PyObject,
    _n: Py_ssize_t,
) -> *mut PyObject {
    unsafe { PyLong_FromLong(7) }
}

fn lifecycle(success: &str, failure: &str) {
    section("Stable-ABI fixture lifecycle and failure handling");
    let lib = Library::open(success);
    let init: InitFn = unsafe { transmute(lib.symbol("PyInit_dotpython_fixture")) };
    let cleanup: CleanupFn = unsafe { transmute(lib.symbol("dotpython_fixture_cleanup_count")) };

    check!(unsafe { dp_abi3_bridge_version() } == 3);

    let mut module: *mut PyObject = ptr::null_mut();
    let mut multi_phase: c_int = 0;
    check!(
        unsafe { dp_abi3_module_initialize(init(), &mut module, &mut multi_phase) } == 0,
        "{}",
        err_msg()
    );
    check!(multi_phase == 1);

    let mut value: i64 = 0;
    check!(
        unsafe { dp_abi3_module_get_int(module, c("fixture_ready").as_ptr(), &mut value) } == 0,
        "{}",
        err_msg()
    );
    check!(value == 1);

    check!(
        unsafe { dp_abi3_module_call_long(module, c("increment").as_ptr(), 1, 41, &mut value) }
            == 0,
        "{}",
        err_msg()
    );
    check!(value == 42, "got {value}");

    section("Generic object bridge owns values and invokes module exports");
    let mut names = ptr::null();
    check!(unsafe { dp_abi3_module_attribute_names(module, &mut names) } == 0);
    check!(unsafe { cstr(names) }.contains("\"increment\""));

    let mut increment = ptr::null_mut();
    check!(
        unsafe { dp_abi3_object_get_attr(module, c("increment").as_ptr(), &mut increment) } == 0
    );
    let mut kind = 0;
    check!(unsafe { dp_abi3_object_kind_of(increment, &mut kind) } == 0);
    check!(kind == DP_ABI3_OBJECT_CALLABLE);

    let mut argument = ptr::null_mut();
    check!(unsafe { dp_abi3_object_from_int64(41, &mut argument) } == 0);
    let arguments = [argument];
    let mut returned = ptr::null_mut();
    check!(unsafe { dp_abi3_object_call(increment, arguments.as_ptr(), 1, &mut returned) } == 0);
    check!(unsafe { dp_abi3_object_kind_of(returned, &mut kind) } == 0);
    check!(kind == DP_ABI3_OBJECT_INT);
    check!(unsafe { dp_abi3_object_as_int64(returned, &mut value) } == 0);
    check!(value == 42);
    let mut text = ptr::null();
    let mut text_length = 0;
    check!(unsafe { dp_abi3_object_string(returned, &mut text, &mut text_length) } == 0);
    check!(text_length == 2);
    check!(unsafe { cstr(text) } == "42");

    let mut first = ptr::null_mut();
    let mut second = ptr::null_mut();
    check!(unsafe { dp_abi3_object_from_utf8(c("alpha").as_ptr(), 5, &mut first) } == 0);
    check!(unsafe { dp_abi3_object_from_utf8(c("beta").as_ptr(), 4, &mut second) } == 0);
    let items = [first, second];
    let mut list = ptr::null_mut();
    check!(
        unsafe { dp_abi3_object_sequence(DP_ABI3_OBJECT_LIST, items.as_ptr(), 2, &mut list) } == 0
    );
    check!(unsafe { dp_abi3_object_size(list, &mut value) } == 0);
    check!(value == 2);
    let mut index = ptr::null_mut();
    check!(unsafe { dp_abi3_object_from_int64(1, &mut index) } == 0);
    let mut item = ptr::null_mut();
    check!(unsafe { dp_abi3_object_get_item(list, index, &mut item) } == 0);
    check!(unsafe { dp_abi3_object_as_utf8(item, &mut text, &mut text_length) } == 0);
    check!(text_length == 4);
    check!(unsafe { cstr(text) } == "beta");
    for object in [
        item, index, list, second, first, returned, argument, increment,
    ] {
        unsafe { dp_abi3_object_release(object) };
    }

    check!(unsafe { dp_abi3_module_call_long(module, c("fail").as_ptr(), 0, 0, &mut value) } == -1);
    check!(err_type() == "ValueError", "got {}", err_type());
    check!(err_msg() == "fixture failure", "got {}", err_msg());

    unsafe { dp_abi3_module_destroy(module) };
    check!(unsafe { cleanup() } == 1);
    check!(unsafe { dp_abi3_active_object_count() } == 0);
    check!(lib.close() == 0);

    let fail_lib = Library::open(failure);
    let fail_init: InitFn = unsafe { transmute(fail_lib.symbol("PyInit_dotpython_fixture")) };
    let fail_cleanup: CleanupFn =
        unsafe { transmute(fail_lib.symbol("dotpython_fixture_cleanup_count")) };

    module = ptr::null_mut();
    multi_phase = 0;
    check!(unsafe { dp_abi3_module_initialize(fail_init(), &mut module, &mut multi_phase) } == -1);
    check!(module.is_null());
    check!(multi_phase == 0);
    check!(err_type() == "ValueError", "got {}", err_type());
    check!(
        err_msg() == "fixture initialization failure",
        "got {}",
        err_msg()
    );
    check!(unsafe { fail_cleanup() } == 0);
    check!(unsafe { dp_abi3_active_object_count() } == 1);

    clear();
    check!(unsafe { dp_abi3_active_object_count() } == 0);
    check!(fail_lib.close() == 0);
}

fn null_destroy_clears_error() {
    section("Null module destruction clears only bridge error state");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    unsafe { PyErr_SetString(PyExc_ValueError, c("discarded error").as_ptr()) };
    check!(unsafe { PyErr_Occurred() } == unsafe { PyExc_ValueError });
    check!(err_msg() == "discarded error");
    check!(unsafe { dp_abi3_active_object_count() } == baseline + 1);
    clear();
    check!(unsafe { PyErr_Occurred() }.is_null());
    check!(err_msg().is_empty());
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn borrowed_alias_dict() {
    section("Borrowed aliases survive dictionary replacement");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let dict = unsafe { PyDict_New() };
    let key = unicode("key");
    let value = unicode("value");
    check!(!dict.is_null() && !key.is_null() && !value.is_null());
    check!(unsafe { PyDict_SetItem(dict, key, value) } == 0);
    unsafe { _Py_DecRef(value) };
    let borrowed = unsafe { PyDict_GetItemWithError(dict, key) };
    check!(!borrowed.is_null());
    check!(unsafe { PyDict_SetItem(dict, key, borrowed) } == 0);
    check!(unsafe { as_text(borrowed) } == "value");
    unsafe { _Py_DecRef(key) };
    unsafe { _Py_DecRef(dict) };
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn borrowed_alias_generic_dict() {
    section("Borrowed aliases survive generic dictionary replacement");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let owner = unsafe { PyList_New(0) };
    let dict = unsafe { PyDict_New() };
    check!(unsafe { PyObject_GenericSetDict(owner, dict, ptr::null_mut()) } == 0);
    let borrowed = dict;
    unsafe { _Py_DecRef(dict) };
    check!(unsafe { PyObject_GenericSetDict(owner, borrowed, ptr::null_mut()) } == 0);
    let observed = unsafe { PyObject_GenericGetDict(owner, ptr::null_mut()) };
    check!(observed == borrowed);
    unsafe { _Py_DecRef(observed) };
    unsafe { _Py_DecRef(owner) };
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn synthetic_import() {
    section("Synthetic imports own all retained metadata");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let name = unicode("synthetic");
    let module = unsafe { PyImport_Import(name) };
    check!(!module.is_null(), "{}", err_msg());
    let imported = unsafe { PyModule_GetNameObject(module) };
    check!(unsafe { as_text(imported) } == "synthetic");
    unsafe { _Py_DecRef(imported) };
    unsafe { _Py_DecRef(module) };
    unsafe { _Py_DecRef(name) };
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn allocation_limit() {
    section("Allocation-limit errors do not recursively allocate");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let value = unsafe { PyUnicode_FromStringAndSize(c("x").as_ptr(), Py_ssize_t::MAX) };
    check!(value.is_null());
    check!(err_type() == "RuntimeError", "got {}", err_type());
    check!(
        err_msg() == "Unicode allocation failed",
        "got {}",
        err_msg()
    );
    clear();
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn tuple_setter_consumes_on_failure() {
    section("Tuple setters consume new references on failure");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let tuple = unsafe { PyTuple_New(1) };
    let value = unicode("value");
    check!(unsafe { PyTuple_SetItem(tuple, 1, value) } == -1);
    clear();
    check!(unsafe { dp_abi3_active_object_count() } == baseline + 1);
    unsafe { _Py_DecRef(tuple) };
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn deep_nested_release() {
    section("Deeply nested containers release without overflowing the stack");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let mut nested = unsafe { PyList_New(0) };
    for _ in 0..200_000 {
        let outer = unsafe { PyList_New(1) };
        check!(unsafe { PyList_SetItem(outer, 0, nested) } == 0);
        nested = outer;
    }
    unsafe { _Py_DecRef(nested) };
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn integer_key_signedness() {
    section("Integer dictionary keys distinguish signedness");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let dict = unsafe { PyDict_New() };
    let negative = unsafe { PyLong_FromLong(-1) };
    let wrapped = unsafe { PyLong_FromUnsignedLongLong(u64::MAX) };
    let small_signed = unsafe { PyLong_FromLong(5) };
    let small_unsigned = unsafe { PyLong_FromUnsignedLongLong(5) };
    let value = unicode("negative");
    check!(unsafe { PyDict_SetItem(dict, negative, value) } == 0);
    check!(unsafe { PyDict_GetItemWithError(dict, wrapped) }.is_null());
    check!(unsafe { PyDict_GetItemWithError(dict, negative) } == value);
    check!(unsafe { PyDict_SetItem(dict, small_signed, value) } == 0);
    check!(unsafe { PyDict_GetItemWithError(dict, small_unsigned) } == value);
    for o in [value, small_unsigned, small_signed, wrapped, negative, dict] {
        unsafe { _Py_DecRef(o) };
    }
    clear();
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn kwargs_rejected() {
    section("Methods without METH_KEYWORDS reject keyword arguments");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    // Keep the name alive for the duration of the calls.
    let name_c = c("probe");
    let mut def = PyMethodDef {
        ml_name: name_c.as_ptr(),
        ml_meth: Some(unsafe {
            transmute::<
                *const (),
                unsafe extern "C" fn(*mut PyObject, *mut PyObject) -> *mut PyObject,
            >(fastcall_probe as *const ())
        }),
        ml_flags: METH_FASTCALL,
        ml_doc: ptr::null(),
    };
    let method =
        unsafe { PyCMethod_New(&mut def, ptr::null_mut(), ptr::null_mut(), ptr::null_mut()) };
    check!(!method.is_null(), "{}", err_msg());
    let args = unsafe { PyTuple_New(0) };
    let result = unsafe { PyObject_Call(method, args, ptr::null_mut()) };
    check!(!result.is_null(), "{}", err_msg());
    check!(unsafe { PyLong_AsLong(result) } == 7);
    unsafe { _Py_DecRef(result) };

    let kwargs = unsafe { PyDict_New() };
    let key = unicode("flag");
    let value = unicode("on");
    check!(unsafe { PyDict_SetItem(kwargs, key, value) } == 0);
    let result = unsafe { PyObject_Call(method, args, kwargs) };
    check!(result.is_null());
    check!(err_type() == "TypeError", "got {}", err_type());
    check!(
        err_msg() == "method does not accept keyword arguments",
        "got {}",
        err_msg()
    );
    for o in [value, key, kwargs, args, method] {
        unsafe { _Py_DecRef(o) };
    }
    clear();
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn owner_thread_confinement() {
    section("Public Stable-ABI calls are confined to the bridge owner thread");
    clear();
    let baseline = unsafe { dp_abi3_active_object_count() };
    let handle = std::thread::spawn(|| {
        let count = unsafe { dp_abi3_active_object_count() };
        let initialized = unsafe { Py_IsInitialized() };
        let type_flags = unsafe { PyType_GetFlags(&raw mut PyType_Type) };
        let mut object = ptr::null_mut();
        let generic_status = unsafe { dp_abi3_object_from_none(&mut object) };
        let reported = unsafe { cstr(dp_abi3_error_message()) }
            == "native ABI access must execute on its owner thread";
        (
            count,
            initialized,
            type_flags,
            generic_status,
            object.is_null(),
            reported,
        )
    });
    let (count, initialized, type_flags, generic_status, object_is_null, reported) =
        handle.join().unwrap();
    check!(count == -1);
    check!(initialized == 0);
    check!(type_flags == 0);
    check!(generic_status == -1);
    check!(object_is_null);
    check!(reported);
    check!(unsafe { dp_abi3_active_object_count() } == baseline);
}

fn text_bounds() {
    section("Bridge text operations are bounded");
    clear();
    let signed_number = unsafe { PyLong_FromLong(-42) };
    check!(as_text_str(signed_number) == "-42");
    unsafe { _Py_DecRef(signed_number) };
    let unsigned_number = unsafe { PyLong_FromUnsignedLongLong(u64::MAX) };
    check!(as_text_str(unsigned_number) == "18446744073709551615");
    unsafe { _Py_DecRef(unsigned_number) };

    let value = unicode("checked copy");
    let repr = unsafe { PyObject_Repr(value) };
    check!(unsafe { as_text(repr) } == "'checked copy'", "{}", unsafe {
        as_text(repr)
    });
    unsafe { _Py_DecRef(repr) };
    unsafe { _Py_DecRef(value) };

    let long_message = "x".repeat(2047);
    unsafe { PyErr_SetString(PyExc_ValueError, c(&long_message).as_ptr()) };
    check!(err_msg().len() == 1023, "got {}", err_msg().len());
    clear();
    check!(unsafe { dp_abi3_active_object_count() } == 0);
}

fn as_text_str(object: *mut PyObject) -> String {
    let s = unsafe { PyObject_Str(object) };
    let out = unsafe { as_text(s) };
    unsafe { _Py_DecRef(s) };
    out
}

fn main() -> std::process::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: native_fixture_test <success-fixture> <failure-fixture>");
        return std::process::ExitCode::from(2);
    }
    lifecycle(&args[1], &args[2]);
    null_destroy_clears_error();
    borrowed_alias_dict();
    borrowed_alias_generic_dict();
    synthetic_import();
    allocation_limit();
    tuple_setter_consumes_on_failure();
    deep_nested_release();
    integer_key_signedness();
    kwargs_rejected();
    owner_thread_confinement();
    text_bounds();
    std::process::ExitCode::from(finish() as u8)
}

// silence unused import of c_char when only used via casts
const _: *const c_char = ptr::null();
