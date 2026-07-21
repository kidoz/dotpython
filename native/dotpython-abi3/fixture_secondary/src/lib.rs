//! Second independent Stable-ABI conformance module used to prove catalog isolation.

#![allow(non_snake_case)]
#![allow(non_upper_case_globals)]
#![allow(static_mut_refs)]

use core::ffi::{c_char, c_int, c_long, c_void};
use core::ptr;

#[repr(C)]
pub struct PyObject {
    ob_refcnt: isize,
    ob_type: *mut c_void,
}

type PyCFunction = Option<unsafe extern "C" fn(*mut PyObject, *mut PyObject) -> *mut PyObject>;

#[repr(C)]
struct PyMethodDef {
    ml_name: *const c_char,
    ml_meth: PyCFunction,
    ml_flags: c_int,
    ml_doc: *const c_char,
}

#[repr(C)]
struct PyModuleDefBase {
    ob_base: PyObject,
    m_init: Option<unsafe extern "C" fn() -> *mut PyObject>,
    m_index: isize,
    m_copy: *mut PyObject,
}

#[repr(C)]
struct PyModuleDefSlot {
    slot: c_int,
    value: *mut c_void,
}

#[repr(C)]
struct PyModuleDef {
    m_base: PyModuleDefBase,
    m_name: *const c_char,
    m_doc: *const c_char,
    m_size: isize,
    m_methods: *mut PyMethodDef,
    m_slots: *mut PyModuleDefSlot,
    m_traverse: *mut c_void,
    m_clear: *mut c_void,
    m_free: Option<unsafe extern "C" fn(*mut c_void)>,
}

const METH_NOARGS: c_int = 0x0004;
const METH_O: c_int = 0x0008;
const PY_MOD_EXEC: c_int = 2;

unsafe extern "C" {
    fn PyModuleDef_Init(definition: *mut PyModuleDef) -> *mut PyObject;
    fn PyModule_AddIntConstant(module: *mut PyObject, name: *const c_char, value: c_long) -> c_int;
    fn PyLong_AsLong(value: *mut PyObject) -> c_long;
    fn PyLong_FromLong(value: c_long) -> *mut PyObject;
    fn PyErr_Occurred() -> *mut PyObject;
    fn PyErr_SetString(exception: *mut PyObject, message: *const c_char);
    static mut PyExc_ValueError: *mut PyObject;
}

static mut CLEANUP_COUNT: c_int = 0;

unsafe extern "C" fn fixture_double(
    _self: *mut PyObject,
    argument: *mut PyObject,
) -> *mut PyObject {
    let value = unsafe { PyLong_AsLong(argument) };
    if !unsafe { PyErr_Occurred() }.is_null() {
        return ptr::null_mut();
    }
    unsafe { PyLong_FromLong(value * 2) }
}

unsafe extern "C" fn fixture_fail(_self: *mut PyObject, _ignored: *mut PyObject) -> *mut PyObject {
    unsafe { PyErr_SetString(PyExc_ValueError, c"secondary fixture failure".as_ptr()) };
    ptr::null_mut()
}

unsafe extern "C" fn fixture_execute(module: *mut PyObject) -> c_int {
    unsafe { PyModule_AddIntConstant(module, c"secondary_fixture_ready".as_ptr(), 1) }
}

unsafe extern "C" fn fixture_free(_module: *mut c_void) {
    unsafe { CLEANUP_COUNT += 1 };
}

static mut METHODS: [PyMethodDef; 3] = [
    PyMethodDef {
        ml_name: c"double".as_ptr(),
        ml_meth: Some(fixture_double),
        ml_flags: METH_O,
        ml_doc: c"Double a whole number.".as_ptr(),
    },
    PyMethodDef {
        ml_name: c"fail".as_ptr(),
        ml_meth: Some(fixture_fail),
        ml_flags: METH_NOARGS,
        ml_doc: c"Raise a deliberate secondary fixture error.".as_ptr(),
    },
    PyMethodDef {
        ml_name: ptr::null(),
        ml_meth: None,
        ml_flags: 0,
        ml_doc: ptr::null(),
    },
];

static mut SLOTS: [PyModuleDefSlot; 2] = [
    PyModuleDefSlot {
        slot: PY_MOD_EXEC,
        value: ptr::null_mut(),
    },
    PyModuleDefSlot {
        slot: 0,
        value: ptr::null_mut(),
    },
];

static mut DEFINITION: PyModuleDef = PyModuleDef {
    m_base: PyModuleDefBase {
        ob_base: PyObject {
            ob_refcnt: 1,
            ob_type: ptr::null_mut(),
        },
        m_init: None,
        m_index: 0,
        m_copy: ptr::null_mut(),
    },
    m_name: c"dotpython_fixture_secondary".as_ptr(),
    m_doc: c"Second DotPython Stable-ABI fixture.".as_ptr(),
    m_size: 0,
    m_methods: ptr::null_mut(),
    m_slots: ptr::null_mut(),
    m_traverse: ptr::null_mut(),
    m_clear: ptr::null_mut(),
    m_free: Some(fixture_free),
};

#[unsafe(no_mangle)]
pub extern "C" fn PyInit_dotpython_fixture_secondary() -> *mut PyObject {
    unsafe {
        let execute: unsafe extern "C" fn(*mut PyObject) -> c_int = fixture_execute;
        SLOTS[0].value = execute as *mut c_void;
        DEFINITION.m_methods = &raw mut METHODS as *mut PyMethodDef;
        DEFINITION.m_slots = &raw mut SLOTS as *mut PyModuleDefSlot;
        PyModuleDef_Init(&raw mut DEFINITION)
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn dotpython_fixture_secondary_cleanup_count() -> c_int {
    unsafe { CLEANUP_COUNT }
}
