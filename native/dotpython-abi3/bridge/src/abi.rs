//! C ABI type layer mirroring `include/dotpython_abi3.h`.
//!
//! Every type here is `#[repr(C)]` and byte-compatible with the public C header, because Stable-ABI
//! fixtures and the managed P/Invoke layer share these layouts. `Py_ssize_t` is `isize`.

#![allow(non_camel_case_types)]
// Some ABI typedefs and constants exist for completeness of the C header mirror.
#![allow(dead_code)]

use core::ffi::{c_char, c_int, c_uint, c_ulong, c_void};

pub type Py_ssize_t = isize;
pub type Py_hash_t = isize;

#[repr(C)]
pub struct PyObject {
    pub ob_refcnt: Py_ssize_t,
    pub ob_type: *mut PyTypeObject,
}

/// The bridge's private `struct _typeobject`. Not part of the Stable ABI shape CPython exposes;
/// extensions only touch it through `PyType_*` accessors, so the internal layout is ours to define.
#[repr(C)]
pub struct PyTypeObject {
    pub base: PyObject,
    pub name: *mut c_char,
    pub basicsize: c_int,
    pub itemsize: c_int,
    pub flags: c_ulong,
    pub slots: *mut PyType_Slot,
    pub methods: *mut PyMethodDef,
    pub getsets: *mut PyGetSetDef,
    pub base_type: *mut PyTypeObject,
    pub dynamic: c_int,
}

// Function-pointer typedefs. All are nullable at the ABI, hence `Option<... fn ...>`.
pub type PyCFunction = Option<unsafe extern "C" fn(*mut PyObject, *mut PyObject) -> *mut PyObject>;
pub type PyCFunctionFast =
    unsafe extern "C" fn(*mut PyObject, *const *mut PyObject, Py_ssize_t) -> *mut PyObject;
pub type PyCFunctionWithKeywords =
    unsafe extern "C" fn(*mut PyObject, *mut PyObject, *mut PyObject) -> *mut PyObject;
pub type PyCFunctionFastWithKeywords = unsafe extern "C" fn(
    *mut PyObject,
    *const *mut PyObject,
    Py_ssize_t,
    *mut PyObject,
) -> *mut PyObject;
pub type PyCMethod = unsafe extern "C" fn(
    *mut PyObject,
    *mut PyTypeObject,
    *const *mut PyObject,
    Py_ssize_t,
    *mut PyObject,
) -> *mut PyObject;
pub type lenfunc = unsafe extern "C" fn(*mut PyObject) -> Py_ssize_t;
pub type ssizeargfunc = unsafe extern "C" fn(*mut PyObject, Py_ssize_t) -> *mut PyObject;
pub type binaryfunc = unsafe extern "C" fn(*mut PyObject, *mut PyObject) -> *mut PyObject;
pub type objobjargproc = unsafe extern "C" fn(*mut PyObject, *mut PyObject, *mut PyObject) -> c_int;
pub type destructor = unsafe extern "C" fn(*mut PyObject);
pub type reprfunc = unsafe extern "C" fn(*mut PyObject) -> *mut PyObject;
pub type getiterfunc = unsafe extern "C" fn(*mut PyObject) -> *mut PyObject;
pub type iternextfunc = unsafe extern "C" fn(*mut PyObject) -> *mut PyObject;
pub type newfunc =
    unsafe extern "C" fn(*mut PyTypeObject, *mut PyObject, *mut PyObject) -> *mut PyObject;
pub type allocfunc = unsafe extern "C" fn(*mut PyTypeObject, Py_ssize_t) -> *mut PyObject;
pub type getter = unsafe extern "C" fn(*mut PyObject, *mut c_void) -> *mut PyObject;
pub type setter = unsafe extern "C" fn(*mut PyObject, *mut PyObject, *mut c_void) -> c_int;

#[repr(C)]
pub struct PyMethodDef {
    pub ml_name: *const c_char,
    pub ml_meth: PyCFunction,
    pub ml_flags: c_int,
    pub ml_doc: *const c_char,
}

#[repr(C)]
pub struct PyGetSetDef {
    pub name: *const c_char,
    pub get: Option<getter>,
    pub set: Option<setter>,
    pub doc: *const c_char,
    pub closure: *mut c_void,
}

#[repr(C)]
pub struct PyType_Slot {
    pub slot: c_int,
    pub pfunc: *mut c_void,
}

#[repr(C)]
pub struct PyType_Spec {
    pub name: *const c_char,
    pub basicsize: c_int,
    pub itemsize: c_int,
    pub flags: c_uint,
    pub slots: *mut PyType_Slot,
}

#[repr(C)]
pub struct PyModuleDef_Base {
    pub ob_base: PyObject,
    pub m_init: Option<unsafe extern "C" fn() -> *mut PyObject>,
    pub m_index: Py_ssize_t,
    pub m_copy: *mut PyObject,
}

#[repr(C)]
pub struct PyModuleDef_Slot {
    pub slot: c_int,
    pub value: *mut c_void,
}

#[repr(C)]
pub struct PyModuleDef {
    pub m_base: PyModuleDef_Base,
    pub m_name: *const c_char,
    pub m_doc: *const c_char,
    pub m_size: Py_ssize_t,
    pub m_methods: *mut PyMethodDef,
    pub m_slots: *mut PyModuleDef_Slot,
    pub m_traverse: *mut c_void,
    pub m_clear: *mut c_void,
    pub m_free: Option<unsafe extern "C" fn(*mut c_void)>,
}

// Method calling conventions.
pub const METH_VARARGS: c_int = 0x0001;
pub const METH_KEYWORDS: c_int = 0x0002;
pub const METH_NOARGS: c_int = 0x0004;
pub const METH_O: c_int = 0x0008;
pub const METH_CLASS: c_int = 0x0010;
pub const METH_STATIC: c_int = 0x0020;
pub const METH_FASTCALL: c_int = 0x0080;
pub const METH_METHOD: c_int = 0x0200;

// Module def slots.
pub const PY_MOD_EXEC: c_int = 2;
pub const PY_MOD_MULTIPLE_INTERPRETERS: c_int = 3;
pub const PY_MOD_GIL: c_int = 4;

// Type slots.
pub const PY_MP_ASS_SUBSCRIPT: c_int = 3;
pub const PY_MP_LENGTH: c_int = 4;
pub const PY_MP_SUBSCRIPT: c_int = 5;
pub const PY_SQ_ITEM: c_int = 44;
pub const PY_SQ_LENGTH: c_int = 45;
pub const PY_TP_ALLOC: c_int = 47;
pub const PY_TP_BASE: c_int = 48;
pub const PY_TP_CALL: c_int = 50;
pub const PY_TP_DEALLOC: c_int = 52;
pub const PY_TP_ITER: c_int = 62;
pub const PY_TP_ITERNEXT: c_int = 63;
pub const PY_TP_METHODS: c_int = 64;
pub const PY_TP_NEW: c_int = 65;
pub const PY_TP_REPR: c_int = 66;
pub const PY_TP_STR: c_int = 70;
pub const PY_TP_GETSET: c_int = 73;
pub const PY_TP_FREE: c_int = 74;

/// Immortal refcount sentinel (`(Py_ssize_t)3 << 30`) shared with the C header.
pub const DP_ABI3_IMMORTAL_REFCNT: Py_ssize_t = 3 << 30;
pub const DP_ABI3_BRIDGE_VERSION: c_int = 4;
