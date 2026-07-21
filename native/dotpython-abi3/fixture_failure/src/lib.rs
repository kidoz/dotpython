//! DotPython Stable-ABI initialization-failure fixture (Rust port of
//! `dotpython_fixture_init_failure.c`). Its `PyInit` records a Python error and returns NULL.

#![allow(non_snake_case)]
#![allow(non_upper_case_globals)]

use core::ffi::{c_char, c_int, c_void};
use core::ptr;

#[repr(C)]
pub struct PyObject {
    ob_refcnt: isize,
    ob_type: *mut c_void,
}

unsafe extern "C" {
    fn PyErr_SetString(exception: *mut PyObject, message: *const c_char);
    static mut PyExc_ValueError: *mut PyObject;
}

#[unsafe(no_mangle)]
pub extern "C" fn PyInit_dotpython_fixture() -> *mut PyObject {
    unsafe { PyErr_SetString(PyExc_ValueError, c"fixture initialization failure".as_ptr()) };
    ptr::null_mut()
}

#[unsafe(no_mangle)]
pub extern "C" fn dotpython_fixture_cleanup_count() -> c_int {
    0
}
