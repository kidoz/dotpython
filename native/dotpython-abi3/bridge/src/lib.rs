//! DotPython experimental CPython Stable-ABI bridge — Rust port (edition 2024).
//!
//! A reference-counting-only object model with no cyclic GC. Every stateful call is confined to the
//! thread that first activates the bridge (`OWNER_THREAD`). Panics must never cross the C ABI, so
//! the crate builds with `panic = "abort"`; each exported function returns a failure indicator
//! rather than unwinding. Ownership at the boundary follows CPython's new/borrowed/stolen contract.
//!
//! The object registry is process-global and only ever touched by the owner thread (as in the C++
//! original), so raw access is sound in practice; error/result buffers are thread-local so a
//! foreign thread observes its own "wrong thread" diagnostic. Object teardown for containers is
//! iterative (a deferred worklist), so deeply nested structures release in O(1) stack.

#![allow(non_snake_case)]
#![allow(non_upper_case_globals)]
#![allow(static_mut_refs)]
// C callers already treat pointer-bearing API functions as unsafe; marking the Rust exports
// `unsafe fn` would not strengthen the C ABI and would make internal ABI-to-ABI calls noisier.
#![allow(clippy::not_unsafe_ptr_arg_deref)]
// These casts preserve the declared C widths across targets even when the local aliases happen to
// have the same Rust representation.
#![allow(clippy::unnecessary_cast)]
// Detached metadata stays boxed while it moves through the iterative destruction worklist so its
// address remains stable for the duration of nested release operations.
#![allow(clippy::vec_box)]
// This is a pervasively-unsafe FFI shim; raw-ref (`&raw mut`) of statics does not need `unsafe`,
// but keeping the blocks uniform reads better than sprinkling exceptions.
#![allow(unused_unsafe)]

mod abi;

use abi::*;
use core::ffi::{c_char, c_int, c_long, c_ulong, c_void};
use core::mem::transmute;
use core::ptr;
use core::sync::atomic::{AtomicPtr, Ordering};
use std::collections::HashMap;
use std::ffi::CString;

// ===========================================================================
// Exported static objects (data symbols).
// ===========================================================================

const fn static_type() -> PyTypeObject {
    PyTypeObject {
        base: PyObject {
            ob_refcnt: DP_ABI3_IMMORTAL_REFCNT,
            ob_type: ptr::null_mut(),
        },
        name: ptr::null_mut(),
        basicsize: size_of::<PyObject>() as c_int,
        itemsize: 0,
        flags: 0,
        slots: ptr::null_mut(),
        methods: ptr::null_mut(),
        getsets: ptr::null_mut(),
        base_type: ptr::null_mut(),
        dynamic: 0,
    }
}

const fn static_object() -> PyObject {
    PyObject {
        ob_refcnt: DP_ABI3_IMMORTAL_REFCNT,
        ob_type: ptr::null_mut(),
    }
}

macro_rules! export_type {
    ($name:ident) => {
        #[unsafe(no_mangle)]
        pub static mut $name: PyTypeObject = static_type();
    };
}
macro_rules! static_type_var {
    ($name:ident) => {
        static mut $name: PyTypeObject = static_type();
    };
}

export_type!(PyBaseObject_Type);
export_type!(PyDict_Type);
export_type!(PyList_Type);
export_type!(PyModule_Type);
export_type!(PyTuple_Type);
export_type!(PyType_Type);
export_type!(PyUnicode_Type);

static_type_var!(DP_BYTES_TYPE);
static_type_var!(DP_CMETHOD_TYPE);
static_type_var!(DP_ITERATOR_TYPE);
static_type_var!(DP_LONG_TYPE);
static_type_var!(DP_BOOL_TYPE);
static_type_var!(DP_BASE_EXCEPTION_TYPE);
static_type_var!(DP_ATTRIBUTE_ERROR_TYPE);
static_type_var!(DP_INDEX_ERROR_TYPE);
static_type_var!(DP_RUNTIME_ERROR_TYPE);
static_type_var!(DP_SYSTEM_ERROR_TYPE);
static_type_var!(DP_TYPE_ERROR_TYPE);
static_type_var!(DP_VALUE_ERROR_TYPE);

#[unsafe(no_mangle)]
pub static mut _Py_FalseStruct: PyObject = static_object();
#[unsafe(no_mangle)]
pub static mut _Py_NoneStruct: PyObject = static_object();
#[unsafe(no_mangle)]
pub static mut _Py_NotImplementedStruct: PyObject = static_object();
#[unsafe(no_mangle)]
pub static mut _Py_TrueStruct: PyObject = static_object();

#[unsafe(no_mangle)]
pub static mut PyExc_AttributeError: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_BaseException: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_IndexError: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_RuntimeError: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_SystemError: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_TypeError: *mut PyObject = ptr::null_mut();
#[unsafe(no_mangle)]
pub static mut PyExc_ValueError: *mut PyObject = ptr::null_mut();

// Convenience accessors for static addresses as `*mut PyObject`.
fn t(p: *mut PyTypeObject) -> *mut PyObject {
    p as *mut PyObject
}
fn base_object_type() -> *mut PyTypeObject {
    &raw mut PyBaseObject_Type
}
fn type_type() -> *mut PyTypeObject {
    &raw mut PyType_Type
}

// ===========================================================================
// Owner-thread enforcement.
// ===========================================================================

static OWNER_THREAD: AtomicPtr<c_void> = AtomicPtr::new(ptr::null_mut());

thread_local! {
    static THREAD_TOKEN: u8 = const { 0 };
}

fn thread_token() -> *mut c_void {
    THREAD_TOKEN.with(|slot| slot as *const u8 as *mut c_void)
}

fn require_owner_thread() -> bool {
    let current = thread_token();
    match OWNER_THREAD.compare_exchange(
        ptr::null_mut(),
        current,
        Ordering::AcqRel,
        Ordering::Acquire,
    ) {
        Ok(_) => true,
        Err(existing) if existing == current => true,
        Err(_) => {
            // Foreign thread: record RuntimeError in its own error buffer, do not touch registry.
            with_err(|e| {
                copy_truncated(
                    &mut e.text,
                    "native ABI access must execute on its owner thread",
                );
                e.etype = unsafe { t(&raw mut DP_RUNTIME_ERROR_TYPE) };
                e.evalue = ptr::null_mut();
                e.etraceback = ptr::null_mut();
            });
            false
        }
    }
}

macro_rules! require_owner {
    () => {
        if !require_owner_thread() {
            return;
        }
    };
    ($ret:expr) => {
        if !require_owner_thread() {
            return $ret;
        }
    };
}

// ===========================================================================
// Value kinds, metadata, and the process-global registry.
// ===========================================================================

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Bytes,
    CMethod,
    Dict,
    Instance,
    Iterator,
    List,
    Long,
    Module,
    Tuple,
    Type,
    Unicode,
}

enum Value {
    Empty,
    Number {
        value: u64,
        is_unsigned: bool,
    },
    Text {
        bytes: Vec<u8>,
    }, // trailing NUL included; logical size = bytes.len() - 1
    Seq {
        items: Vec<*mut PyObject>,
    },
    Dict {
        keys: Vec<*mut PyObject>,
        values: Vec<*mut PyObject>,
    },
    Method {
        def: *mut PyMethodDef,
        self_: *mut PyObject,
        module: *mut PyObject,
        class_type: *mut PyTypeObject,
    },
    Module {
        def: *mut PyModuleDef,
        attributes: *mut PyObject,
        name: CString,
    },
    Iter {
        source: *mut PyObject,
        index: isize,
    },
    TypeData {
        _name: Option<CString>,
        _slots: Vec<PyType_Slot>,
    },
}

struct Meta {
    object: *mut PyObject,
    alloc_size: usize,
    kind: Kind,
    attributes: *mut PyObject,
    value: Value,
}

struct Registry {
    map: HashMap<usize, Box<Meta>>,
    active: i64,
    initialized: bool,
    last_definition: *mut PyModuleDef,
    deferred: Vec<Box<Meta>>,
    draining: bool,
}

static mut REGISTRY: Option<Registry> = None;

#[allow(clippy::mut_from_ref)]
fn reg() -> &'static mut Registry {
    unsafe {
        let slot = &raw mut REGISTRY;
        if (*slot).is_none() {
            *slot = Some(Registry {
                map: HashMap::new(),
                active: 0,
                initialized: false,
                last_definition: ptr::null_mut(),
                deferred: Vec::new(),
                draining: false,
            });
        }
        (*slot).as_mut().unwrap()
    }
}

const MAX_ALLOCATION_SIZE: usize = 64 * 1024 * 1024;

// ===========================================================================
// Thread-local error / result buffers.
// ===========================================================================

struct ErrState {
    etype: *mut PyObject,
    evalue: *mut PyObject,
    etraceback: *mut PyObject,
    text: [u8; 1024],
    result: [u8; 16384],
    setting: bool,
}

thread_local! {
    static ERR: core::cell::UnsafeCell<ErrState> = const {
        core::cell::UnsafeCell::new(ErrState {
            etype: ptr::null_mut(),
            evalue: ptr::null_mut(),
            etraceback: ptr::null_mut(),
            text: [0; 1024],
            result: [0; 16384],
            setting: false,
        })
    };
}

fn with_err<R>(f: impl FnOnce(&mut ErrState) -> R) -> R {
    ERR.with(|cell| f(unsafe { &mut *cell.get() }))
}

fn err_ptr() -> *mut ErrState {
    ERR.with(|cell| cell.get())
}

fn copy_truncated(buffer: &mut [u8], message: &str) -> usize {
    if buffer.is_empty() {
        return 0;
    }
    let n = message.len().min(buffer.len() - 1);
    buffer[..n].copy_from_slice(&message.as_bytes()[..n]);
    buffer[n] = 0;
    n
}

// ===========================================================================
// Raw object memory.
// ===========================================================================

fn alloc_layout(size: usize) -> std::alloc::Layout {
    let size = size.max(size_of::<PyObject>());
    // 16-byte alignment covers PyObject and typical extension instance data.
    std::alloc::Layout::from_size_align(size, 16).unwrap()
}

unsafe fn raw_alloc(size: usize) -> *mut PyObject {
    unsafe { std::alloc::alloc_zeroed(alloc_layout(size)) as *mut PyObject }
}

unsafe fn raw_free(object: *mut PyObject, size: usize) {
    unsafe { std::alloc::dealloc(object as *mut u8, alloc_layout(size)) }
}

// ===========================================================================
// Static-object identity and type initialization.
// ===========================================================================

fn is_static(object: *mut PyObject) -> bool {
    if object.is_null() {
        return true;
    }
    let statics: [*mut PyObject; 24] = unsafe {
        [
            &raw mut _Py_NoneStruct,
            &raw mut _Py_TrueStruct,
            &raw mut _Py_FalseStruct,
            &raw mut _Py_NotImplementedStruct,
            t(&raw mut PyType_Type),
            t(&raw mut PyBaseObject_Type),
            t(&raw mut PyDict_Type),
            t(&raw mut PyList_Type),
            t(&raw mut PyModule_Type),
            t(&raw mut PyTuple_Type),
            t(&raw mut PyUnicode_Type),
            t(&raw mut DP_BYTES_TYPE),
            t(&raw mut DP_CMETHOD_TYPE),
            t(&raw mut DP_ITERATOR_TYPE),
            t(&raw mut DP_LONG_TYPE),
            t(&raw mut DP_BOOL_TYPE),
            PyExc_BaseException,
            PyExc_AttributeError,
            PyExc_IndexError,
            PyExc_RuntimeError,
            PyExc_SystemError,
            PyExc_TypeError,
            PyExc_ValueError,
            ptr::null_mut(),
        ]
    };
    statics.contains(&object)
}

unsafe fn init_type(type_: *mut PyTypeObject, name: &'static str, base: *mut PyTypeObject) {
    unsafe {
        (*type_).base.ob_type = type_type();
        (*type_).name = name.as_ptr() as *mut c_char; // 'static str with trailing bytes; names are NUL-terminated below
        (*type_).base_type = base;
    }
}

// Static NUL-terminated type names.
macro_rules! cstr {
    ($s:literal) => {
        concat!($s, "\0").as_ptr() as *mut c_char
    };
}

unsafe fn initialize() {
    let r = reg();
    if r.initialized {
        return;
    }
    unsafe {
        PyType_Type.base.ob_type = type_type();
        PyType_Type.name = cstr!("type");
        PyType_Type.base_type = base_object_type();

        let set = |ty: *mut PyTypeObject, nm: *mut c_char, base: *mut PyTypeObject| {
            (*ty).base.ob_type = type_type();
            (*ty).name = nm;
            (*ty).base_type = base;
        };
        set(&raw mut PyBaseObject_Type, cstr!("object"), ptr::null_mut());
        set(&raw mut PyDict_Type, cstr!("dict"), base_object_type());
        set(&raw mut PyList_Type, cstr!("list"), base_object_type());
        set(&raw mut PyModule_Type, cstr!("module"), base_object_type());
        set(&raw mut PyTuple_Type, cstr!("tuple"), base_object_type());
        set(&raw mut PyUnicode_Type, cstr!("str"), base_object_type());
        set(&raw mut DP_BYTES_TYPE, cstr!("bytes"), base_object_type());
        set(
            &raw mut DP_CMETHOD_TYPE,
            cstr!("builtin_function_or_method"),
            base_object_type(),
        );
        set(
            &raw mut DP_ITERATOR_TYPE,
            cstr!("iterator"),
            base_object_type(),
        );
        set(&raw mut DP_LONG_TYPE, cstr!("int"), base_object_type());
        set(&raw mut DP_BOOL_TYPE, cstr!("bool"), &raw mut DP_LONG_TYPE);
        set(
            &raw mut DP_BASE_EXCEPTION_TYPE,
            cstr!("BaseException"),
            base_object_type(),
        );
        set(
            &raw mut DP_ATTRIBUTE_ERROR_TYPE,
            cstr!("AttributeError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );
        set(
            &raw mut DP_INDEX_ERROR_TYPE,
            cstr!("IndexError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );
        set(
            &raw mut DP_RUNTIME_ERROR_TYPE,
            cstr!("RuntimeError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );
        set(
            &raw mut DP_SYSTEM_ERROR_TYPE,
            cstr!("SystemError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );
        set(
            &raw mut DP_TYPE_ERROR_TYPE,
            cstr!("TypeError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );
        set(
            &raw mut DP_VALUE_ERROR_TYPE,
            cstr!("ValueError"),
            &raw mut DP_BASE_EXCEPTION_TYPE,
        );

        PyType_Type.flags = 1u64 << 31;
        PyDict_Type.flags = 1u64 << 29;
        PyList_Type.flags = 1u64 << 25;
        PyTuple_Type.flags = 1u64 << 26;
        PyUnicode_Type.flags = 1u64 << 28;
        DP_BYTES_TYPE.flags = 1u64 << 27;
        DP_LONG_TYPE.flags = 1u64 << 24;
        DP_BOOL_TYPE.flags = 1u64 << 24;
        for ty in [
            &raw mut DP_BASE_EXCEPTION_TYPE,
            &raw mut DP_ATTRIBUTE_ERROR_TYPE,
            &raw mut DP_INDEX_ERROR_TYPE,
            &raw mut DP_RUNTIME_ERROR_TYPE,
            &raw mut DP_SYSTEM_ERROR_TYPE,
            &raw mut DP_TYPE_ERROR_TYPE,
            &raw mut DP_VALUE_ERROR_TYPE,
        ] {
            (*ty).flags = 1u64 << 30;
        }

        _Py_FalseStruct.ob_type = &raw mut DP_BOOL_TYPE;
        _Py_TrueStruct.ob_type = &raw mut DP_BOOL_TYPE;
        _Py_NoneStruct.ob_type = base_object_type();
        _Py_NotImplementedStruct.ob_type = base_object_type();

        PyExc_AttributeError = t(&raw mut DP_ATTRIBUTE_ERROR_TYPE);
        PyExc_BaseException = t(&raw mut DP_BASE_EXCEPTION_TYPE);
        PyExc_IndexError = t(&raw mut DP_INDEX_ERROR_TYPE);
        PyExc_RuntimeError = t(&raw mut DP_RUNTIME_ERROR_TYPE);
        PyExc_SystemError = t(&raw mut DP_SYSTEM_ERROR_TYPE);
        PyExc_TypeError = t(&raw mut DP_TYPE_ERROR_TYPE);
        PyExc_ValueError = t(&raw mut DP_VALUE_ERROR_TYPE);
    }
    let _ = init_type; // retained for symmetry with the C++ helper
    reg().initialized = true;
}

// ===========================================================================
// Registry operations.
// ===========================================================================

fn key(object: *mut PyObject) -> usize {
    object as usize
}

fn find(object: *mut PyObject) -> Option<&'static mut Meta> {
    if !require_owner_thread() {
        return None;
    }
    reg()
        .map
        .get_mut(&key(object))
        .map(|b| unsafe { &mut *(b.as_mut() as *mut Meta) })
}

fn add(
    object: *mut PyObject,
    kind: Kind,
    alloc_size: usize,
    value: Value,
) -> Option<&'static mut Meta> {
    let r = reg();
    if r.active == i64::MAX {
        return None;
    }
    let meta = Box::new(Meta {
        object,
        alloc_size,
        kind,
        attributes: ptr::null_mut(),
        value,
    });
    r.map.insert(key(object), meta);
    r.active += 1;
    r.map
        .get_mut(&key(object))
        .map(|b| unsafe { &mut *(b.as_mut() as *mut Meta) })
}

fn detach(object: *mut PyObject) -> Option<Box<Meta>> {
    let r = reg();
    match r.map.remove(&key(object)) {
        Some(meta) => {
            r.active -= 1;
            Some(meta)
        }
        None => None,
    }
}

fn allocate(kind: Kind, type_: *mut PyTypeObject, size: usize, value: Value) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    unsafe { initialize() };
    let object = unsafe { raw_alloc(size) };
    if object.is_null() {
        set_error(
            unsafe { PyExc_RuntimeError },
            "native object allocation failed",
        );
        return ptr::null_mut();
    }
    unsafe {
        (*object).ob_refcnt = 1;
        (*object).ob_type = type_;
    }
    if add(object, kind, alloc_layout(size).size(), value).is_none() {
        unsafe { raw_free(object, alloc_layout(size).size()) };
        set_error(
            unsafe { PyExc_RuntimeError },
            "native object metadata allocation failed",
        );
        return ptr::null_mut();
    }
    object
}

// ===========================================================================
// Reference counting and iterative teardown.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn _Py_IncRef(object: *mut PyObject) {
    require_owner!();
    if !object.is_null() && !is_static(object) {
        unsafe {
            if (*object).ob_refcnt != isize::MAX {
                (*object).ob_refcnt += 1;
            }
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn _Py_DecRef(object: *mut PyObject) {
    require_owner!();
    release(object);
}

#[unsafe(no_mangle)]
pub extern "C" fn Py_NewRef(object: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    _Py_IncRef(object);
    object
}

fn newref(object: *mut PyObject) -> *mut PyObject {
    _Py_IncRef(object);
    object
}

fn release(object: *mut PyObject) {
    if object.is_null() || is_static(object) {
        return;
    }
    if !require_owner_thread() {
        return;
    }
    unsafe {
        if (*object).ob_refcnt == isize::MAX || (*object).ob_refcnt <= 0 {
            return;
        }
        (*object).ob_refcnt -= 1;
        if (*object).ob_refcnt != 0 {
            return;
        }
    }

    // Instance dealloc slot recurses through the extension destructor (unchanged from C++).
    if let Some(meta) = find(object) {
        if meta.kind == Kind::Instance {
            let dealloc = unsafe { slot((*object).ob_type, PY_TP_DEALLOC) };
            if !dealloc.is_null() {
                let f: destructor = unsafe { transmute::<*mut c_void, destructor>(dealloc) };
                unsafe { f(object) };
                return;
            }
        }
    }

    let detached = match detach(object) {
        Some(meta) => meta,
        None => return,
    };
    if reg().draining {
        reg().deferred.push(detached);
        return;
    }
    reg().draining = true;
    destroy_meta(detached);
    while let Some(next) = reg().deferred.pop() {
        destroy_meta(next);
    }
    reg().draining = false;
}

fn destroy_meta(meta: Box<Meta>) {
    match &meta.value {
        Value::Seq { items } => {
            for &item in items {
                release(item);
            }
        }
        Value::Dict { keys, values } => {
            for &k in keys {
                release(k);
            }
            for &v in values {
                release(v);
            }
        }
        Value::Iter { source, .. } => release(*source),
        Value::Method {
            self_,
            module,
            class_type,
            ..
        } => {
            release(*self_);
            release(*module);
            release(*class_type as *mut PyObject);
        }
        Value::Module {
            def, attributes, ..
        } => {
            unsafe {
                if !def.is_null() {
                    if let Some(free) = (**def).m_free {
                        free(meta.object as *mut c_void);
                    }
                }
            }
            release(*attributes);
        }
        Value::Empty if meta.kind == Kind::Type => unsafe {
            release(t((*(meta.object as *mut PyTypeObject)).base_type));
        },
        Value::TypeData { .. } => unsafe {
            release(t((*(meta.object as *mut PyTypeObject)).base_type));
        },
        Value::Empty if meta.kind == Kind::Instance => unsafe {
            release(t((*meta.object).ob_type));
        },
        _ => {}
    }
    release(meta.attributes);
    let size = meta.alloc_size;
    let object = meta.object;
    drop(meta);
    unsafe { raw_free(object, size) };
}

// ===========================================================================
// Error state.
// ===========================================================================

fn clear_error() {
    let (ty, val, tb) = with_err(|e| {
        let out = (e.etype, e.evalue, e.etraceback);
        e.etype = ptr::null_mut();
        e.evalue = ptr::null_mut();
        e.etraceback = ptr::null_mut();
        e.text[0] = 0;
        out
    });
    release(ty);
    release(val);
    release(tb);
}

fn set_error(type_: *mut PyObject, message: &str) {
    let reentrant = with_err(|e| e.setting);
    with_err(|e| e.setting = true);
    clear_error();
    let type_ = if type_.is_null() {
        unsafe { PyExc_RuntimeError }
    } else {
        type_
    };
    let type_ref = newref(type_);
    let length = with_err(|e| {
        e.etype = type_ref;
        copy_truncated(&mut e.text, message)
    });
    if !reentrant {
        // Build the message value; new_unicode touches only the registry, never error state.
        let text_copy: Vec<u8> = with_err(|e| e.text[..length].to_vec());
        let value = new_unicode_bytes(&text_copy);
        with_err(|e| e.evalue = value);
    }
    with_err(|e| e.setting = reentrant);
}

// ===========================================================================
// Unicode / bytes.
// ===========================================================================

fn new_unicode_bytes(text: &[u8]) -> *mut PyObject {
    let object = allocate(
        Kind::Unicode,
        unsafe { &raw mut PyUnicode_Type },
        size_of::<PyObject>(),
        Value::Empty,
    );
    if object.is_null() {
        return ptr::null_mut();
    }
    let mut bytes = Vec::with_capacity(text.len() + 1);
    bytes.extend_from_slice(text);
    bytes.push(0);
    if let Some(meta) = find(object) {
        meta.value = Value::Text { bytes };
    }
    object
}

fn new_unicode(text: *const c_char, size: Py_ssize_t) -> *mut PyObject {
    if text.is_null() || size < 0 {
        set_error(unsafe { PyExc_TypeError }, "Unicode input is invalid");
        return ptr::null_mut();
    }
    if size as usize >= MAX_ALLOCATION_SIZE {
        set_error(unsafe { PyExc_RuntimeError }, "Unicode allocation failed");
        return ptr::null_mut();
    }
    let slice = unsafe { core::slice::from_raw_parts(text as *const u8, size as usize) };
    new_unicode_bytes(slice)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyUnicode_FromStringAndSize(
    text: *const c_char,
    size: Py_ssize_t,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_unicode(text, size)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyUnicode_AsUTF8AndSize(
    unicode: *mut PyObject,
    size: *mut Py_ssize_t,
) -> *const c_char {
    require_owner!(ptr::null());
    match find(unicode) {
        Some(meta) if meta.kind == Kind::Unicode => {
            if let Value::Text { bytes } = &meta.value {
                if !size.is_null() {
                    unsafe { *size = (bytes.len() - 1) as Py_ssize_t };
                }
                return bytes.as_ptr() as *const c_char;
            }
            ptr::null()
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected str");
            ptr::null()
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyUnicode_AsEncodedString(
    unicode: *mut PyObject,
    encoding: *const c_char,
    _errors: *const c_char,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if !encoding.is_null() {
        let enc = unsafe { cstr_to_str(encoding) };
        if enc != "utf-8" && enc != "utf8" {
            set_error(
                unsafe { PyExc_ValueError },
                "only UTF-8 encoding is supported",
            );
            return ptr::null_mut();
        }
    }
    let mut size: Py_ssize_t = 0;
    let text = PyUnicode_AsUTF8AndSize(unicode, &mut size);
    if text.is_null() {
        return ptr::null_mut();
    }
    let slice = unsafe { core::slice::from_raw_parts(text as *const u8, size as usize) };
    let bytes_obj = allocate(
        Kind::Bytes,
        unsafe { &raw mut DP_BYTES_TYPE },
        size_of::<PyObject>(),
        Value::Empty,
    );
    if bytes_obj.is_null() {
        return ptr::null_mut();
    }
    let mut data = Vec::with_capacity(slice.len() + 1);
    data.extend_from_slice(slice);
    data.push(0);
    if let Some(meta) = find(bytes_obj) {
        meta.value = Value::Text { bytes: data };
    }
    bytes_obj
}

#[unsafe(no_mangle)]
pub extern "C" fn PyUnicode_InternInPlace(unicode: *mut *mut PyObject) {
    require_owner!();
    if unicode.is_null() {
        return;
    }
    let _ = PyUnicode_AsUTF8AndSize(unsafe { *unicode }, ptr::null_mut());
}

#[unsafe(no_mangle)]
pub extern "C" fn PyBytes_AsString(value: *mut PyObject) -> *mut c_char {
    require_owner!(ptr::null_mut());
    match find(value) {
        Some(meta) if meta.kind == Kind::Bytes => {
            if let Value::Text { bytes } = &meta.value {
                return bytes.as_ptr() as *mut c_char;
            }
            ptr::null_mut()
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected bytes");
            ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyBytes_Size(value: *mut PyObject) -> Py_ssize_t {
    require_owner!(-1);
    match find(value) {
        Some(meta) if meta.kind == Kind::Bytes => {
            if let Value::Text { bytes } = &meta.value {
                return (bytes.len() - 1) as Py_ssize_t;
            }
            -1
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected bytes");
            -1
        }
    }
}

// ===========================================================================
// Long.
// ===========================================================================

fn new_number(value: u64, is_unsigned: bool) -> *mut PyObject {
    allocate(
        Kind::Long,
        unsafe { &raw mut DP_LONG_TYPE },
        size_of::<PyObject>(),
        Value::Number { value, is_unsigned },
    )
}

#[unsafe(no_mangle)]
pub extern "C" fn PyLong_FromLong(value: c_long) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_number(value as i64 as u64, false)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyLong_FromSsize_t(value: Py_ssize_t) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_number(value as i64 as u64, false)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyLong_FromUnsignedLongLong(value: u64) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_number(value, true)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyLong_AsLong(value: *mut PyObject) -> c_long {
    require_owner!(-1);
    unsafe {
        if value == &raw mut _Py_TrueStruct {
            return 1;
        }
        if value == &raw mut _Py_FalseStruct {
            return 0;
        }
    }
    match find(value) {
        Some(meta) if meta.kind == Kind::Long => {
            if let Value::Number { value, is_unsigned } = meta.value {
                if is_unsigned && value > c_long::MAX as u64 {
                    set_error(
                        unsafe { PyExc_ValueError },
                        "integer does not fit in C long",
                    );
                    return -1;
                }
                return value as i64 as c_long;
            }
            -1
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected int");
            -1
        }
    }
}

// ===========================================================================
// Helpers shared below.
// ===========================================================================

unsafe fn cstr_to_str<'a>(ptr: *const c_char) -> &'a str {
    if ptr.is_null() {
        return "";
    }
    let cstr = unsafe { core::ffi::CStr::from_ptr(ptr) };
    cstr.to_str().unwrap_or("")
}

/// Look up a type slot walking the base chain, with defaults for alloc/free/new.
unsafe fn slot(type_: *mut PyTypeObject, slot_id: c_int) -> *mut c_void {
    if type_.is_null() {
        return ptr::null_mut();
    }
    if slot_id == PY_TP_ALLOC {
        return dp_generic_alloc as *mut c_void;
    }
    if slot_id == PY_TP_FREE {
        return dp_generic_free as *mut c_void;
    }
    unsafe {
        if !(*type_).slots.is_null() {
            let mut entry = (*type_).slots;
            while (*entry).slot != 0 {
                if (*entry).slot == slot_id {
                    return (*entry).pfunc;
                }
                entry = entry.add(1);
            }
        }
        if slot_id == PY_TP_NEW && type_ == base_object_type() {
            return dp_generic_new as *mut c_void;
        }
        if (*type_).base_type.is_null() {
            ptr::null_mut()
        } else {
            slot((*type_).base_type, slot_id)
        }
    }
}

// The generic alloc/new/free used as default slots (defined with the type machinery below).
extern "C" fn dp_generic_alloc(type_: *mut PyTypeObject, item_count: Py_ssize_t) -> *mut PyObject {
    if type_.is_null() || item_count != 0 {
        set_error(
            unsafe { PyExc_TypeError },
            "variable-sized heap types are unsupported",
        );
        return ptr::null_mut();
    }
    let size = unsafe {
        if (*type_).basicsize < size_of::<PyObject>() as c_int {
            size_of::<PyObject>()
        } else {
            (*type_).basicsize as usize
        }
    };
    let object = allocate(Kind::Instance, type_, size, Value::Empty);
    if !object.is_null() {
        newref(t(type_));
    }
    object
}

extern "C" fn dp_generic_new(
    type_: *mut PyTypeObject,
    _args: *mut PyObject,
    _kwargs: *mut PyObject,
) -> *mut PyObject {
    dp_generic_alloc(type_, 0)
}

extern "C" fn dp_generic_free(object: *mut c_void) {
    if object.is_null() {
        return;
    }
    if let Some(meta) = detach(object as *mut PyObject) {
        destroy_meta(meta);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GC_UnTrack(_object: *mut c_void) {
    require_owner!();
}

#[unsafe(no_mangle)]
pub extern "C" fn PyGILState_Ensure() -> c_int {
    require_owner!(-1);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyGILState_Release(_state: c_int) {
    require_owner!();
}

static mut THREAD_STATE_TOKEN: u8 = 0;

#[unsafe(no_mangle)]
pub extern "C" fn PyEval_SaveThread() -> *mut c_void {
    require_owner!(ptr::null_mut());
    unsafe { &raw mut THREAD_STATE_TOKEN as *mut c_void }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyEval_RestoreThread(_thread_state: *mut c_void) {
    require_owner!();
}

// ===========================================================================
// Bridge status / error accessors.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_bridge_version() -> c_int {
    DP_ABI3_BRIDGE_VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn Py_IsInitialized() -> c_int {
    require_owner!(0);
    1
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_active_object_count() -> i64 {
    require_owner!(-1);
    reg().active
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_error_message() -> *const c_char {
    unsafe { (*err_ptr()).text.as_ptr() as *const c_char }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_Occurred() -> *mut PyObject {
    require_owner!(with_err(|e| e.etype));
    with_err(|e| e.etype)
}

// ===========================================================================
// Sequences (list / tuple).
// ===========================================================================

fn new_sequence(kind: Kind, type_: *mut PyTypeObject, size: Py_ssize_t) -> *mut PyObject {
    if size < 0 {
        set_error(
            unsafe { PyExc_ValueError },
            "sequence size cannot be negative",
        );
        return ptr::null_mut();
    }
    allocate(
        kind,
        type_,
        size_of::<PyObject>(),
        Value::Seq {
            items: vec![ptr::null_mut(); size as usize],
        },
    )
}

#[unsafe(no_mangle)]
pub extern "C" fn PyList_New(size: Py_ssize_t) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_sequence(Kind::List, unsafe { &raw mut PyList_Type }, size)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyTuple_New(size: Py_ssize_t) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_sequence(Kind::Tuple, unsafe { &raw mut PyTuple_Type }, size)
}

fn require_sequence(object: *mut PyObject, list: bool) -> Option<&'static mut Meta> {
    let want = if list { Kind::List } else { Kind::Tuple };
    match find(object) {
        Some(m) if m.kind == want => Some(m),
        _ => {
            set_error(
                unsafe { PyExc_TypeError },
                if list {
                    "expected list"
                } else {
                    "expected tuple"
                },
            );
            None
        }
    }
}

fn sequence_get(object: *mut PyObject, list: bool, index: Py_ssize_t) -> *mut PyObject {
    let m = match require_sequence(object, list) {
        Some(m) => m,
        None => return ptr::null_mut(),
    };
    if let Value::Seq { items } = &m.value {
        if index < 0 || index as usize >= items.len() {
            set_error(unsafe { PyExc_IndexError }, "sequence index out of range");
            return ptr::null_mut();
        }
        return items[index as usize];
    }
    ptr::null_mut()
}

fn sequence_set(
    object: *mut PyObject,
    list: bool,
    index: Py_ssize_t,
    value: *mut PyObject,
) -> c_int {
    let old = {
        let m = match require_sequence(object, list) {
            Some(m) => m,
            None => {
                release(value);
                return -1;
            }
        };
        if let Value::Seq { items } = &mut m.value {
            if index < 0 || index as usize >= items.len() {
                release(value);
                set_error(unsafe { PyExc_IndexError }, "sequence index out of range");
                return -1;
            }
            let old = items[index as usize];
            items[index as usize] = value;
            old
        } else {
            release(value);
            return -1;
        }
    };
    release(old);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyList_Size(list: *mut PyObject) -> Py_ssize_t {
    require_owner!(-1);
    match require_sequence(list, true) {
        Some(m) => {
            if let Value::Seq { items } = &m.value {
                items.len() as Py_ssize_t
            } else {
                -1
            }
        }
        None => -1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyList_GetItem(list: *mut PyObject, index: Py_ssize_t) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    sequence_get(list, true, index)
}

/// Steals `value` on success and failure, matching CPython's Stable-ABI contract.
#[unsafe(no_mangle)]
pub extern "C" fn PyList_SetItem(
    list: *mut PyObject,
    index: Py_ssize_t,
    value: *mut PyObject,
) -> c_int {
    require_owner!({
        release(value);
        -1
    });
    sequence_set(list, true, index, value)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyList_Append(list: *mut PyObject, value: *mut PyObject) -> c_int {
    require_owner!(-1);
    let referenced = newref(value);
    match require_sequence(list, true) {
        Some(m) => {
            if let Value::Seq { items } = &mut m.value {
                items.push(referenced);
                0
            } else {
                release(referenced);
                -1
            }
        }
        None => {
            release(referenced);
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyTuple_Size(tuple: *mut PyObject) -> Py_ssize_t {
    require_owner!(-1);
    match require_sequence(tuple, false) {
        Some(m) => {
            if let Value::Seq { items } = &m.value {
                items.len() as Py_ssize_t
            } else {
                -1
            }
        }
        None => -1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyTuple_GetItem(tuple: *mut PyObject, index: Py_ssize_t) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    sequence_get(tuple, false, index)
}

/// Steals `value` on success and failure, matching CPython's Stable-ABI contract.
#[unsafe(no_mangle)]
pub extern "C" fn PyTuple_SetItem(
    tuple: *mut PyObject,
    index: Py_ssize_t,
    value: *mut PyObject,
) -> c_int {
    require_owner!({
        release(value);
        -1
    });
    sequence_set(tuple, false, index, value)
}

// ===========================================================================
// Equality (used by dict keys).
// ===========================================================================

enum Snap {
    Unicode(Vec<u8>),
    Long { value: u64, is_unsigned: bool },
    Other,
}

fn snapshot(object: *mut PyObject) -> Option<Snap> {
    let m = find(object)?;
    Some(match (m.kind, &m.value) {
        (Kind::Unicode, Value::Text { bytes }) => Snap::Unicode(bytes.clone()),
        (Kind::Long, Value::Number { value, is_unsigned }) => Snap::Long {
            value: *value,
            is_unsigned: *is_unsigned,
        },
        _ => Snap::Other,
    })
}

fn equal(left: *mut PyObject, right: *mut PyObject) -> bool {
    if left == right {
        return true;
    }
    let (l, r) = match (snapshot(left), snapshot(right)) {
        (Some(l), Some(r)) => (l, r),
        _ => return false,
    };
    match (l, r) {
        (Snap::Unicode(a), Snap::Unicode(b)) => a == b,
        (
            Snap::Long {
                value: av,
                is_unsigned: au,
            },
            Snap::Long {
                value: bv,
                is_unsigned: bu,
            },
        ) => {
            if av != bv {
                return false;
            }
            // Equal bits still differ when exactly one side is a negative signed number.
            let a_neg = !au && (av as i64) < 0;
            let b_neg = !bu && (bv as i64) < 0;
            a_neg == b_neg
        }
        _ => false,
    }
}

// ===========================================================================
// Dictionary.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn PyDict_New() -> *mut PyObject {
    require_owner!(ptr::null_mut());
    allocate(
        Kind::Dict,
        unsafe { &raw mut PyDict_Type },
        size_of::<PyObject>(),
        Value::Dict {
            keys: Vec::new(),
            values: Vec::new(),
        },
    )
}

fn require_dictionary(dictionary: *mut PyObject) -> Option<&'static mut Meta> {
    match find(dictionary) {
        Some(m) if m.kind == Kind::Dict => Some(m),
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected dict");
            None
        }
    }
}

fn dictionary_index(dictionary: *mut PyObject, target: *mut PyObject) -> isize {
    let keys: Vec<*mut PyObject> = match find(dictionary) {
        Some(m) => {
            if let Value::Dict { keys, .. } = &m.value {
                keys.clone()
            } else {
                return -1;
            }
        }
        None => return -1,
    };
    for (index, &k) in keys.iter().enumerate() {
        if equal(k, target) {
            return index as isize;
        }
    }
    -1
}

#[unsafe(no_mangle)]
pub extern "C" fn PyDict_SetItem(
    dictionary: *mut PyObject,
    dkey: *mut PyObject,
    value: *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if require_dictionary(dictionary).is_none() || dkey.is_null() || value.is_null() {
        return -1;
    }
    let index = dictionary_index(dictionary, dkey);
    if index >= 0 {
        let replacement = newref(value);
        let old = {
            let m = require_dictionary(dictionary).unwrap();
            if let Value::Dict { values, .. } = &mut m.value {
                let old = values[index as usize];
                values[index as usize] = replacement;
                old
            } else {
                release(replacement);
                return -1;
            }
        };
        release(old);
        return 0;
    }
    let referenced_key = newref(dkey);
    let referenced_value = newref(value);
    let m = require_dictionary(dictionary).unwrap();
    if let Value::Dict { keys, values } = &mut m.value {
        keys.push(referenced_key);
        values.push(referenced_value);
        0
    } else {
        release(referenced_key);
        release(referenced_value);
        -1
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyDict_GetItemWithError(
    dictionary: *mut PyObject,
    dkey: *mut PyObject,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if require_dictionary(dictionary).is_none() {
        return ptr::null_mut();
    }
    let index = dictionary_index(dictionary, dkey);
    if index < 0 {
        return ptr::null_mut();
    }
    let m = require_dictionary(dictionary).unwrap();
    if let Value::Dict { values, .. } = &m.value {
        values[index as usize]
    } else {
        ptr::null_mut()
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyDict_Size(dictionary: *mut PyObject) -> Py_ssize_t {
    require_owner!(-1);
    match require_dictionary(dictionary) {
        Some(m) => {
            if let Value::Dict { keys, .. } = &m.value {
                keys.len() as Py_ssize_t
            } else {
                -1
            }
        }
        None => -1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyDict_Next(
    dictionary: *mut PyObject,
    position: *mut Py_ssize_t,
    dkey: *mut *mut PyObject,
    value: *mut *mut PyObject,
) -> c_int {
    require_owner!(0);
    if position.is_null() {
        return 0;
    }
    let pos = unsafe { *position };
    let m = match require_dictionary(dictionary) {
        Some(m) => m,
        None => return 0,
    };
    if let Value::Dict { keys, values } = &m.value {
        if pos < 0 || pos as usize >= keys.len() {
            return 0;
        }
        let index = pos as usize;
        unsafe {
            *position = pos + 1;
            if !dkey.is_null() {
                *dkey = keys[index];
            }
            if !value.is_null() {
                *value = values[index];
            }
        }
        1
    } else {
        0
    }
}

fn dictionary_delete(dictionary: *mut PyObject, dkey: *mut PyObject) -> c_int {
    if require_dictionary(dictionary).is_none() {
        return -1;
    }
    let index = dictionary_index(dictionary, dkey);
    if index < 0 {
        set_error(unsafe { PyExc_IndexError }, "dictionary key was not found");
        return -1;
    }
    let (old_key, old_value) = {
        let m = require_dictionary(dictionary).unwrap();
        if let Value::Dict { keys, values } = &mut m.value {
            let k = keys.remove(index as usize);
            let v = values.remove(index as usize);
            (k, v)
        } else {
            return -1;
        }
    };
    release(old_key);
    release(old_value);
    0
}

// ===========================================================================
// Attributes and C-method binding.
// ===========================================================================

fn new_unicode_str(text: &str) -> *mut PyObject {
    new_unicode_bytes(text.as_bytes())
}

fn attributes(object: *mut PyObject, create: bool) -> *mut PyObject {
    let (is_module, module_attr, has_attr) = match find(object) {
        Some(m) => match &m.value {
            Value::Module { attributes, .. } => (true, *attributes, false),
            _ => (false, ptr::null_mut(), !m.attributes.is_null()),
        },
        None => return ptr::null_mut(),
    };
    if is_module {
        return module_attr;
    }
    if !has_attr && create {
        let dict = PyDict_New();
        if let Some(m) = find(object) {
            m.attributes = dict;
        }
    }
    find(object)
        .map(|m| m.attributes)
        .unwrap_or(ptr::null_mut())
}

fn get_dict_cstr(dictionary: *mut PyObject, name: &str) -> *mut PyObject {
    let dkey = new_unicode_str(name);
    if dkey.is_null() {
        return ptr::null_mut();
    }
    let result = PyDict_GetItemWithError(dictionary, dkey);
    release(dkey);
    result
}

fn set_dict_cstr(dictionary: *mut PyObject, name: &str, value: *mut PyObject) -> c_int {
    let dkey = new_unicode_str(name);
    if dkey.is_null() {
        return -1;
    }
    let result = if value.is_null() {
        dictionary_delete(dictionary, dkey)
    } else {
        PyDict_SetItem(dictionary, dkey, value)
    };
    release(dkey);
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn PyCMethod_New(
    definition: *mut PyMethodDef,
    self_: *mut PyObject,
    module: *mut PyObject,
    class_type: *mut PyTypeObject,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    unsafe {
        if definition.is_null()
            || (*definition).ml_name.is_null()
            || (*definition).ml_meth.is_none()
        {
            set_error(PyExc_TypeError, "method definition is invalid");
            return ptr::null_mut();
        }
    }
    let object = allocate(
        Kind::CMethod,
        unsafe { &raw mut DP_CMETHOD_TYPE },
        size_of::<PyObject>(),
        Value::Empty,
    );
    if object.is_null() {
        return ptr::null_mut();
    }
    let s = newref(self_);
    let md = newref(module);
    let ct = newref(t(class_type)) as *mut PyTypeObject;
    if let Some(m) = find(object) {
        m.value = Value::Method {
            def: definition,
            self_: s,
            module: md,
            class_type: ct,
        };
    }
    object
}

unsafe fn find_method(type_: *mut PyTypeObject, name: &str) -> *mut PyMethodDef {
    let mut current = type_;
    unsafe {
        while !current.is_null() {
            let mut method = (*current).methods;
            if !method.is_null() {
                while !(*method).ml_name.is_null() {
                    if cstr_to_str((*method).ml_name) == name {
                        return method;
                    }
                    method = method.add(1);
                }
            }
            current = (*current).base_type;
        }
    }
    ptr::null_mut()
}

unsafe fn find_getset(type_: *mut PyTypeObject, name: &str) -> *mut PyGetSetDef {
    let mut current = type_;
    unsafe {
        while !current.is_null() {
            let mut item = (*current).getsets;
            if !item.is_null() {
                while !(*item).name.is_null() {
                    if cstr_to_str((*item).name) == name {
                        return item;
                    }
                    item = item.add(1);
                }
            }
            current = (*current).base_type;
        }
    }
    ptr::null_mut()
}

unsafe fn bind_method(
    object: *mut PyObject,
    type_: *mut PyTypeObject,
    method: *mut PyMethodDef,
) -> *mut PyObject {
    let flags = unsafe { (*method).ml_flags };
    let mut self_ = object;
    let mut class_type = ptr::null_mut();
    if flags & METH_STATIC != 0 {
        self_ = ptr::null_mut();
    } else if flags & METH_CLASS != 0 {
        self_ = t(type_);
    }
    if flags & METH_METHOD != 0 {
        class_type = type_;
    }
    PyCMethod_New(method, self_, ptr::null_mut(), class_type)
}

fn call_method(
    def: *mut PyMethodDef,
    self_: *mut PyObject,
    class_type: *mut PyTypeObject,
    args: *mut PyObject,
    kwargs: *mut PyObject,
) -> *mut PyObject {
    let positional: Vec<*mut PyObject> = match require_sequence(args, false) {
        Some(m) => {
            if let Value::Seq { items } = &m.value {
                items.clone()
            } else {
                return ptr::null_mut();
            }
        }
        None => return ptr::null_mut(),
    };
    let (kw_keys, kw_values): (Vec<*mut PyObject>, Vec<*mut PyObject>) = if kwargs.is_null() {
        (Vec::new(), Vec::new())
    } else {
        match require_dictionary(kwargs) {
            Some(m) => {
                if let Value::Dict { keys, values } = &m.value {
                    (keys.clone(), values.clone())
                } else {
                    return ptr::null_mut();
                }
            }
            None => return ptr::null_mut(),
        }
    };

    let flags = unsafe { (*def).ml_flags };
    let convention = flags & (METH_VARARGS | METH_KEYWORDS | METH_NOARGS | METH_O | METH_FASTCALL);
    let keyword_count = kw_keys.len();
    if keyword_count > 0 && flags & METH_KEYWORDS == 0 {
        set_error(
            unsafe { PyExc_TypeError },
            "method does not accept keyword arguments",
        );
        return ptr::null_mut();
    }

    let ml_meth = unsafe { (*def).ml_meth };
    if flags & METH_FASTCALL != 0 {
        let mut values: Vec<*mut PyObject> = Vec::with_capacity(positional.len() + keyword_count);
        values.extend_from_slice(&positional);
        let mut keyword_names = ptr::null_mut();
        if keyword_count > 0 {
            keyword_names = PyTuple_New(keyword_count as Py_ssize_t);
            if keyword_names.is_null() {
                return ptr::null_mut();
            }
            for index in 0..keyword_count {
                if PyTuple_SetItem(keyword_names, index as Py_ssize_t, newref(kw_keys[index])) != 0
                {
                    return ptr::null_mut();
                }
                values.push(kw_values[index]);
            }
        }
        let nargs = positional.len() as Py_ssize_t;
        let result = unsafe {
            if flags & METH_METHOD != 0 {
                let f: PyCMethod = transmute(ml_meth);
                f(self_, class_type, values.as_ptr(), nargs, keyword_names)
            } else if flags & METH_KEYWORDS != 0 {
                let f: PyCFunctionFastWithKeywords = transmute(ml_meth);
                f(self_, values.as_ptr(), nargs, keyword_names)
            } else {
                let f: PyCFunctionFast = transmute(ml_meth);
                f(self_, values.as_ptr(), nargs)
            }
        };
        release(keyword_names);
        return result;
    }
    if convention == METH_VARARGS | METH_KEYWORDS {
        let f: PyCFunctionWithKeywords = unsafe { transmute(ml_meth) };
        return unsafe { f(self_, args, kwargs) };
    }
    if convention == METH_VARARGS {
        return unsafe { (ml_meth.unwrap())(self_, args) };
    }
    if convention == METH_NOARGS && positional.is_empty() && keyword_count == 0 {
        return unsafe { (ml_meth.unwrap())(self_, ptr::null_mut()) };
    }
    if convention == METH_O && positional.len() == 1 && keyword_count == 0 {
        return unsafe { (ml_meth.unwrap())(self_, positional[0]) };
    }
    set_error(
        unsafe { PyExc_TypeError },
        "method arguments do not match its calling convention",
    );
    ptr::null_mut()
}

// ===========================================================================
// Type object: PyType_FromSpec and accessors.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn PyType_FromSpec(specification: *mut PyType_Spec) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    unsafe { initialize() };
    unsafe {
        if specification.is_null()
            || (*specification).name.is_null()
            || (*specification).basicsize < size_of::<PyObject>() as c_int
        {
            set_error(PyExc_TypeError, "type specification is invalid");
            return ptr::null_mut();
        }
    }
    let type_obj = allocate(
        Kind::Type,
        type_type(),
        size_of::<PyTypeObject>(),
        Value::Empty,
    );
    if type_obj.is_null() {
        return ptr::null_mut();
    }
    let type_ = type_obj as *mut PyTypeObject;
    let name_str = unsafe { cstr_to_str((*specification).name) }.to_owned();
    let name_c = CString::new(name_str).unwrap_or_default();

    let mut slots_vec: Vec<PyType_Slot> = Vec::new();
    unsafe {
        if !(*specification).slots.is_null() {
            let mut entry = (*specification).slots;
            while (*entry).slot != 0 {
                slots_vec.push(PyType_Slot {
                    slot: (*entry).slot,
                    pfunc: (*entry).pfunc,
                });
                entry = entry.add(1);
            }
        }
    }
    slots_vec.push(PyType_Slot {
        slot: 0,
        pfunc: ptr::null_mut(),
    });

    unsafe {
        (*type_).basicsize = (*specification).basicsize;
        (*type_).itemsize = (*specification).itemsize;
        (*type_).flags = (*specification).flags as c_ulong | (1u64 << 9) | (1u64 << 12);
        (*type_).base_type = base_object_type();
        (*type_).dynamic = 1;
    }
    if let Some(m) = find(type_obj) {
        m.value = Value::TypeData {
            _name: Some(name_c),
            _slots: slots_vec,
        };
    }
    if let Some(m) = find(type_obj) {
        if let Value::TypeData {
            _name: Some(nc),
            _slots,
        } = &m.value
        {
            unsafe {
                (*type_).name = nc.as_ptr() as *mut c_char;
                (*type_).slots = _slots.as_ptr() as *mut PyType_Slot;
            }
        }
    }
    unsafe {
        let mut entry = (*type_).slots;
        while (*entry).slot != 0 {
            match (*entry).slot {
                PY_TP_METHODS => (*type_).methods = (*entry).pfunc as *mut PyMethodDef,
                PY_TP_GETSET => (*type_).getsets = (*entry).pfunc as *mut PyGetSetDef,
                PY_TP_BASE if !(*entry).pfunc.is_null() => {
                    (*type_).base_type = (*entry).pfunc as *mut PyTypeObject
                }
                _ => {}
            }
            entry = entry.add(1);
        }
        newref(t((*type_).base_type));
    }
    type_obj
}

#[unsafe(no_mangle)]
pub extern "C" fn PyType_GetSlot(type_: *mut PyTypeObject, slot_id: c_int) -> *mut c_void {
    require_owner!(ptr::null_mut());
    unsafe { slot(type_, slot_id) }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyType_GetFlags(type_: *mut PyTypeObject) -> c_ulong {
    require_owner!(0);
    if type_.is_null() {
        0
    } else {
        unsafe { (*type_).flags }
    }
}

fn short_type_name(type_: *mut PyTypeObject) -> &'static str {
    let name = unsafe {
        if type_.is_null() || (*type_).name.is_null() {
            "object"
        } else {
            cstr_to_str((*type_).name)
        }
    };
    match name.rfind('.') {
        Some(pos) => &name[pos + 1..],
        None => name,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyType_GetName(type_: *mut PyTypeObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    new_unicode_str(short_type_name(type_))
}

#[unsafe(no_mangle)]
pub extern "C" fn PyType_GetQualName(type_: *mut PyTypeObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    PyType_GetName(type_)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyType_IsSubtype(type_: *mut PyTypeObject, base: *mut PyTypeObject) -> c_int {
    require_owner!(0);
    let mut current = type_;
    unsafe {
        while !current.is_null() {
            if current == base {
                return 1;
            }
            current = (*current).base_type;
        }
    }
    0
}

// ===========================================================================
// Attribute protocol.
// ===========================================================================

fn get_attribute_cstr(object: *mut PyObject, name: &str) -> *mut PyObject {
    if object.is_null() {
        set_error(
            unsafe { PyExc_AttributeError },
            "attribute target is invalid",
        );
        return ptr::null_mut();
    }
    if name == "__class__" {
        return newref(t(unsafe { (*object).ob_type }));
    }
    // C-method introspection.
    if let Some(m) = find(object) {
        if m.kind == Kind::CMethod {
            if let Value::Method { def, .. } = &m.value {
                let def = *def;
                if name == "__name__" || name == "__qualname__" {
                    return new_unicode_str(unsafe { cstr_to_str((*def).ml_name) });
                }
                if name == "__doc__" {
                    let doc = unsafe { (*def).ml_doc };
                    return if doc.is_null() {
                        newref(unsafe { &raw mut _Py_NoneStruct })
                    } else {
                        new_unicode_str(unsafe { cstr_to_str(doc) })
                    };
                }
            }
        }
    }
    if unsafe { (*object).ob_type } == type_type() {
        let type_ = object as *mut PyTypeObject;
        if name == "__name__" || name == "__qualname__" {
            return PyType_GetName(type_);
        }
        let attrs = attributes(object, false);
        let stored = if attrs.is_null() {
            ptr::null_mut()
        } else {
            get_dict_cstr(attrs, name)
        };
        if !stored.is_null() {
            return newref(stored);
        }
        let method = unsafe { find_method(type_, name) };
        if !method.is_null() && unsafe { (*method).ml_flags } & (METH_CLASS | METH_STATIC) != 0 {
            return unsafe { bind_method(object, type_, method) };
        }
    } else {
        let getset = unsafe { find_getset((*object).ob_type, name) };
        if !getset.is_null() {
            if let Some(get) = unsafe { (*getset).get } {
                return unsafe { get(object, (*getset).closure) };
            }
        }
        let attrs = attributes(object, false);
        let stored = if attrs.is_null() {
            ptr::null_mut()
        } else {
            get_dict_cstr(attrs, name)
        };
        if !stored.is_null() {
            return newref(stored);
        }
        let method = unsafe { find_method((*object).ob_type, name) };
        if !method.is_null() {
            return unsafe { bind_method(object, (*object).ob_type, method) };
        }
    }
    set_error(unsafe { PyExc_AttributeError }, name);
    ptr::null_mut()
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GetAttr(object: *mut PyObject, name: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let text = PyUnicode_AsUTF8AndSize(name, ptr::null_mut());
    if text.is_null() {
        ptr::null_mut()
    } else {
        get_attribute_cstr(object, unsafe { cstr_to_str(text) })
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_SetAttrString(
    object: *mut PyObject,
    name: *const c_char,
    value: *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if object.is_null() || name.is_null() {
        set_error(
            unsafe { PyExc_AttributeError },
            "attribute target is invalid",
        );
        return -1;
    }
    let name_str = unsafe { cstr_to_str(name) };
    let getset = unsafe { find_getset((*object).ob_type, name_str) };
    if !getset.is_null() {
        if let Some(set) = unsafe { (*getset).set } {
            return unsafe { set(object, value, (*getset).closure) };
        }
    }
    let attrs = attributes(object, true);
    if attrs.is_null() {
        set_error(
            unsafe { PyExc_AttributeError },
            "object does not support attributes",
        );
        return -1;
    }
    set_dict_cstr(attrs, name_str, value)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_SetAttr(
    object: *mut PyObject,
    name: *mut PyObject,
    value: *mut PyObject,
) -> c_int {
    require_owner!(-1);
    let text = PyUnicode_AsUTF8AndSize(name, ptr::null_mut());
    if text.is_null() {
        -1
    } else {
        PyObject_SetAttrString(object, text, value)
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GenericGetDict(
    object: *mut PyObject,
    _context: *mut c_void,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let attrs = attributes(object, true);
    newref(attrs)
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GenericSetDict(
    object: *mut PyObject,
    value: *mut PyObject,
    _context: *mut c_void,
) -> c_int {
    require_owner!(-1);
    let bad_type =
        !value.is_null() && unsafe { (*value).ob_type } != unsafe { &raw mut PyDict_Type };
    match find(object) {
        Some(m) if !bad_type => {
            let replacement = newref(value);
            let old = m.attributes;
            m.attributes = replacement;
            release(old);
            0
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "__dict__ must be a dict");
            -1
        }
    }
}

// ===========================================================================
// Object protocol.
// ===========================================================================

type CallFn = unsafe extern "C" fn(*mut PyObject, *mut PyObject, *mut PyObject) -> *mut PyObject;

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_Call(
    callable: *mut PyObject,
    args: *mut PyObject,
    kwargs: *mut PyObject,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if callable.is_null() {
        set_error(unsafe { PyExc_TypeError }, "callable is NULL");
        return ptr::null_mut();
    }
    let args = if args.is_null() {
        let created = PyTuple_New(0);
        if created.is_null() {
            return ptr::null_mut();
        }
        created
    } else {
        newref(args)
    };

    let cmethod = match find(callable) {
        Some(m) if m.kind == Kind::CMethod => {
            if let Value::Method {
                def,
                self_,
                class_type,
                ..
            } = &m.value
            {
                Some((*def, *self_, *class_type))
            } else {
                None
            }
        }
        _ => None,
    };

    let result = if let Some((def, self_, class_type)) = cmethod {
        call_method(def, self_, class_type, args, kwargs)
    } else if unsafe { (*callable).ob_type } == type_type() {
        let create = unsafe { slot(callable as *mut PyTypeObject, PY_TP_NEW) };
        if create.is_null() {
            set_error(unsafe { PyExc_TypeError }, "type is not constructible");
            ptr::null_mut()
        } else {
            let f: newfunc = unsafe { transmute(create) };
            unsafe { f(callable as *mut PyTypeObject, args, kwargs) }
        }
    } else {
        let call = unsafe { slot((*callable).ob_type, PY_TP_CALL) };
        if call.is_null() {
            set_error(unsafe { PyExc_TypeError }, "object is not callable");
            ptr::null_mut()
        } else {
            let f: CallFn = unsafe { transmute(call) };
            unsafe { f(callable, args, kwargs) }
        }
    };
    release(args);
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_CallNoArgs(callable: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let args = PyTuple_New(0);
    if args.is_null() {
        return ptr::null_mut();
    }
    let result = PyObject_Call(callable, args, ptr::null_mut());
    release(args);
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_Size(object: *mut PyObject) -> Py_ssize_t {
    require_owner!(-1);
    if let Some(m) = find(object) {
        match (m.kind, &m.value) {
            (Kind::List | Kind::Tuple, Value::Seq { items }) => return items.len() as Py_ssize_t,
            (Kind::Dict, Value::Dict { keys, .. }) => return keys.len() as Py_ssize_t,
            (Kind::Unicode | Kind::Bytes, Value::Text { bytes }) => {
                return (bytes.len() - 1) as Py_ssize_t;
            }
            _ => {}
        }
    }
    let ob_type = if object.is_null() {
        ptr::null_mut()
    } else {
        unsafe { (*object).ob_type }
    };
    let mut length = unsafe { slot(ob_type, PY_MP_LENGTH) };
    if length.is_null() {
        length = unsafe { slot(ob_type, PY_SQ_LENGTH) };
    }
    if length.is_null() {
        set_error(unsafe { PyExc_TypeError }, "object has no length");
        return -1;
    }
    let f: lenfunc = unsafe { transmute(length) };
    unsafe { f(object) }
}

#[unsafe(no_mangle)]
pub extern "C" fn PySequence_Check(object: *mut PyObject) -> c_int {
    require_owner!(0);
    if object.is_null() {
        return 0;
    }
    let is_seq = matches!(find(object), Some(m) if m.kind == Kind::List || m.kind == Kind::Tuple);
    if is_seq || !unsafe { slot((*object).ob_type, PY_SQ_ITEM) }.is_null() {
        1
    } else {
        0
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GetItem(object: *mut PyObject, dkey: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if object.is_null() || dkey.is_null() {
        set_error(unsafe { PyExc_TypeError }, "item lookup is invalid");
        return ptr::null_mut();
    }
    let kind = find(object).map(|m| m.kind);
    if kind == Some(Kind::Dict) {
        let value = PyDict_GetItemWithError(object, dkey);
        if value.is_null() && PyErr_Occurred().is_null() {
            set_error(unsafe { PyExc_IndexError }, "dictionary key was not found");
        }
        return newref(value);
    }
    let subscript = unsafe { slot((*object).ob_type, PY_MP_SUBSCRIPT) };
    if !subscript.is_null() {
        let function: binaryfunc = unsafe { transmute(subscript) };
        return unsafe { function(object, dkey) };
    }
    let index = PyLong_AsLong(dkey);
    if index == -1 && !PyErr_Occurred().is_null() {
        return ptr::null_mut();
    }
    if kind == Some(Kind::List) {
        return newref(sequence_get(object, true, index as Py_ssize_t));
    }
    if kind == Some(Kind::Tuple) {
        return newref(sequence_get(object, false, index as Py_ssize_t));
    }
    let item = unsafe { slot((*object).ob_type, PY_SQ_ITEM) };
    if !item.is_null() {
        let f: ssizeargfunc = unsafe { transmute(item) };
        return unsafe { f(object, index as Py_ssize_t) };
    }
    set_error(
        unsafe { PyExc_TypeError },
        "object does not support item lookup",
    );
    ptr::null_mut()
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_SetItem(
    object: *mut PyObject,
    dkey: *mut PyObject,
    value: *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if matches!(find(object), Some(m) if m.kind == Kind::Dict) {
        return PyDict_SetItem(object, dkey, value);
    }
    let ob_type = if object.is_null() {
        ptr::null_mut()
    } else {
        unsafe { (*object).ob_type }
    };
    let assign = unsafe { slot(ob_type, PY_MP_ASS_SUBSCRIPT) };
    if assign.is_null() {
        set_error(
            unsafe { PyExc_TypeError },
            "object does not support item assignment",
        );
        return -1;
    }
    let f: objobjargproc = unsafe { transmute(assign) };
    unsafe { f(object, dkey, value) }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_DelItem(object: *mut PyObject, dkey: *mut PyObject) -> c_int {
    require_owner!(-1);
    if matches!(find(object), Some(m) if m.kind == Kind::Dict) {
        return dictionary_delete(object, dkey);
    }
    let ob_type = if object.is_null() {
        ptr::null_mut()
    } else {
        unsafe { (*object).ob_type }
    };
    let assign = unsafe { slot(ob_type, PY_MP_ASS_SUBSCRIPT) };
    if assign.is_null() {
        set_error(
            unsafe { PyExc_TypeError },
            "object does not support item deletion",
        );
        return -1;
    }
    let f: objobjargproc = unsafe { transmute(assign) };
    unsafe { f(object, dkey, ptr::null_mut()) }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_GetIter(object: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if object.is_null() {
        set_error(unsafe { PyExc_TypeError }, "cannot iterate NULL");
        return ptr::null_mut();
    }
    let get_iter = unsafe { slot((*object).ob_type, PY_TP_ITER) };
    if !get_iter.is_null() {
        let f: getiterfunc = unsafe { transmute(get_iter) };
        return unsafe { f(object) };
    }
    let is_seq = matches!(find(object), Some(m) if m.kind == Kind::List || m.kind == Kind::Tuple);
    if !is_seq && unsafe { slot((*object).ob_type, PY_SQ_ITEM) }.is_null() {
        set_error(unsafe { PyExc_TypeError }, "object is not iterable");
        return ptr::null_mut();
    }
    let iterator = allocate(
        Kind::Iterator,
        unsafe { &raw mut DP_ITERATOR_TYPE },
        size_of::<PyObject>(),
        Value::Empty,
    );
    if !iterator.is_null() {
        let source = newref(object);
        if let Some(m) = find(iterator) {
            m.value = Value::Iter { source, index: 0 };
        }
    }
    iterator
}

#[unsafe(no_mangle)]
pub extern "C" fn PyIter_Next(iterator: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let state = match find(iterator) {
        Some(m) if m.kind == Kind::Iterator => {
            if let Value::Iter { source, index } = &m.value {
                Some((*source, *index))
            } else {
                None
            }
        }
        _ => None,
    };
    if let Some((source, index)) = state {
        let size = PyObject_Size(source);
        if size < 0 || index >= size {
            return ptr::null_mut();
        }
        let idx = PyLong_FromSsize_t(index);
        if idx.is_null() {
            return ptr::null_mut();
        }
        if let Some(m) = find(iterator) {
            if let Value::Iter { index, .. } = &mut m.value {
                *index += 1;
            }
        }
        let result = PyObject_GetItem(source, idx);
        release(idx);
        return result;
    }
    let ob_type = if iterator.is_null() {
        ptr::null_mut()
    } else {
        unsafe { (*iterator).ob_type }
    };
    let next = unsafe { slot(ob_type, PY_TP_ITERNEXT) };
    if next.is_null() {
        set_error(unsafe { PyExc_TypeError }, "object is not an iterator");
        return ptr::null_mut();
    }
    let f: iternextfunc = unsafe { transmute(next) };
    unsafe { f(iterator) }
}

fn new_type_description(type_: *mut PyTypeObject) -> *mut PyObject {
    let name = short_type_name(type_);
    new_unicode_str(&format!("<{name} object>"))
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_Str(object: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    unsafe {
        if object.is_null() {
            return new_unicode_str("<NULL>");
        }
        if object == &raw mut _Py_NoneStruct {
            return new_unicode_str("None");
        }
        if object == &raw mut _Py_TrueStruct {
            return new_unicode_str("True");
        }
        if object == &raw mut _Py_FalseStruct {
            return new_unicode_str("False");
        }
    }
    if let Some(m) = find(object) {
        match (m.kind, &m.value) {
            (Kind::Unicode, _) => return newref(object),
            (Kind::Long, Value::Number { value, is_unsigned }) => {
                return if *is_unsigned {
                    new_unicode_str(&format!("{value}"))
                } else {
                    new_unicode_str(&format!("{}", *value as i64))
                };
            }
            _ => {}
        }
    }
    let string = unsafe { slot((*object).ob_type, PY_TP_STR) };
    if !string.is_null() {
        let f: reprfunc = unsafe { transmute(string) };
        return unsafe { f(object) };
    }
    new_type_description(unsafe { (*object).ob_type })
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_Repr(object: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if !object.is_null() {
        let representation = unsafe { slot((*object).ob_type, PY_TP_REPR) };
        if !representation.is_null() {
            let f: reprfunc = unsafe { transmute(representation) };
            return unsafe { f(object) };
        }
        let text: Option<Vec<u8>> = match find(object) {
            Some(m) if m.kind == Kind::Unicode => {
                if let Value::Text { bytes } = &m.value {
                    Some(bytes[..bytes.len() - 1].to_vec())
                } else {
                    None
                }
            }
            _ => None,
        };
        if let Some(text) = text {
            let mut quoted = Vec::with_capacity(text.len() + 2);
            quoted.push(b'\'');
            quoted.extend_from_slice(&text);
            quoted.push(b'\'');
            return new_unicode_bytes(&quoted);
        }
    }
    PyObject_Str(object)
}

fn swapped_comparison(operation: c_int) -> c_int {
    match operation {
        PY_LT => PY_GT,
        PY_LE => PY_GE,
        PY_EQ => PY_EQ,
        PY_NE => PY_NE,
        PY_GT => PY_LT,
        PY_GE => PY_LE,
        _ => operation,
    }
}

unsafe fn call_rich_comparison(
    function: *mut c_void,
    left: *mut PyObject,
    right: *mut PyObject,
    operation: c_int,
) -> *mut PyObject {
    let compare: richcmpfunc = unsafe { transmute(function) };
    unsafe { compare(left, right, operation) }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyObject_RichCompare(
    left: *mut PyObject,
    right: *mut PyObject,
    operation: c_int,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    if left.is_null() || right.is_null() || !(PY_LT..=PY_GE).contains(&operation) {
        set_error(
            unsafe { PyExc_TypeError },
            "rich comparison inputs are invalid",
        );
        return ptr::null_mut();
    }

    let left_type = unsafe { (*left).ob_type };
    let right_type = unsafe { (*right).ob_type };
    let left_compare = unsafe { slot(left_type, PY_TP_RICHCOMPARE) };
    let right_compare = unsafe { slot(right_type, PY_TP_RICHCOMPARE) };
    let not_implemented = &raw mut _Py_NotImplementedStruct;
    let mut tried_reverse = false;

    if left_type != right_type
        && !right_compare.is_null()
        && right_compare != left_compare
        && PyType_IsSubtype(right_type, left_type) != 0
    {
        let result = unsafe {
            call_rich_comparison(right_compare, right, left, swapped_comparison(operation))
        };
        if result.is_null() || result != not_implemented {
            return result;
        }
        release(result);
        tried_reverse = true;
    }

    if !left_compare.is_null() {
        let result = unsafe { call_rich_comparison(left_compare, left, right, operation) };
        if result.is_null() || result != not_implemented {
            return result;
        }
        release(result);
    }

    if !tried_reverse && !right_compare.is_null() {
        let result = unsafe {
            call_rich_comparison(right_compare, right, left, swapped_comparison(operation))
        };
        if result.is_null() || result != not_implemented {
            return result;
        }
        release(result);
    }

    match operation {
        PY_EQ => newref(if left == right {
            &raw mut _Py_TrueStruct
        } else {
            &raw mut _Py_FalseStruct
        }),
        PY_NE => newref(if left != right {
            &raw mut _Py_TrueStruct
        } else {
            &raw mut _Py_FalseStruct
        }),
        _ => {
            set_error(
                unsafe { PyExc_TypeError },
                "objects do not support the requested ordering",
            );
            ptr::null_mut()
        }
    }
}

// ===========================================================================
// Modules.
// ===========================================================================

fn create_module(definition: *mut PyModuleDef, synthetic_name: Option<&str>) -> *mut PyObject {
    let name: String = if definition.is_null() {
        match synthetic_name {
            Some(n) => n.to_owned(),
            None => {
                set_error(unsafe { PyExc_TypeError }, "module name is invalid");
                return ptr::null_mut();
            }
        }
    } else {
        let n = unsafe { (*definition).m_name };
        if n.is_null() {
            set_error(unsafe { PyExc_TypeError }, "module name is invalid");
            return ptr::null_mut();
        }
        unsafe { cstr_to_str(n) }.to_owned()
    };

    let object = allocate(
        Kind::Module,
        unsafe { &raw mut PyModule_Type },
        size_of::<PyObject>(),
        Value::Empty,
    );
    if object.is_null() {
        return ptr::null_mut();
    }
    let attrs = PyDict_New();
    if attrs.is_null() {
        release(object);
        return ptr::null_mut();
    }
    let name_c = CString::new(name.clone()).unwrap_or_default();
    if let Some(m) = find(object) {
        m.value = Value::Module {
            def: definition,
            attributes: attrs,
            name: name_c,
        };
    }
    let module_name = new_unicode_str(&name);
    if module_name.is_null() {
        release(object);
        return ptr::null_mut();
    }
    let name_key = CString::new("__name__").unwrap();
    if PyObject_SetAttrString(object, name_key.as_ptr(), module_name) != 0 {
        release(module_name);
        release(object);
        return ptr::null_mut();
    }
    release(module_name);

    if !definition.is_null() && !unsafe { (*definition).m_methods }.is_null() {
        let mut method = unsafe { (*definition).m_methods };
        unsafe {
            while !(*method).ml_name.is_null() {
                let callable = PyCMethod_New(method, object, ptr::null_mut(), ptr::null_mut());
                if callable.is_null()
                    || PyObject_SetAttrString(object, (*method).ml_name, callable) != 0
                {
                    release(callable);
                    release(object);
                    return ptr::null_mut();
                }
                release(callable);
                method = method.add(1);
            }
        }
    }
    object
}

#[unsafe(no_mangle)]
pub extern "C" fn PyModuleDef_Init(definition: *mut PyModuleDef) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    unsafe { initialize() };
    unsafe {
        if definition.is_null() || (*definition).m_name.is_null() {
            set_error(PyExc_TypeError, "module definition is invalid");
            return ptr::null_mut();
        }
        (*definition).m_base.ob_base.ob_refcnt = DP_ABI3_IMMORTAL_REFCNT;
        (*definition).m_base.ob_base.ob_type = type_type();
    }
    reg().last_definition = definition;
    definition as *mut PyObject
}

#[unsafe(no_mangle)]
pub extern "C" fn PyModule_GetNameObject(module: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    match find(module) {
        Some(m) if m.kind == Kind::Module => {
            if let Value::Module { name, .. } = &m.value {
                let owned = name.to_string_lossy().into_owned();
                new_unicode_str(&owned)
            } else {
                ptr::null_mut()
            }
        }
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected module");
            ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyModule_AddIntConstant(
    module: *mut PyObject,
    name: *const c_char,
    value: c_long,
) -> c_int {
    require_owner!(-1);
    let number = PyLong_FromLong(value);
    if number.is_null() {
        return -1;
    }
    let result = PyObject_SetAttrString(module, name, number);
    release(number);
    result
}

// ===========================================================================
// Error API.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_SetString(exception: *mut PyObject, message: *const c_char) {
    require_owner!();
    set_error(exception, unsafe { cstr_to_str(message) });
}

fn store_error_text_from(value: *mut PyObject) {
    let text_obj = PyObject_Str(value);
    if !text_obj.is_null() {
        let message = PyUnicode_AsUTF8AndSize(text_obj, ptr::null_mut());
        if !message.is_null() {
            let s = unsafe { cstr_to_str(message) }.to_owned();
            with_err(|e| {
                copy_truncated(&mut e.text, &s);
            });
        }
        release(text_obj);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_SetObject(exception: *mut PyObject, value: *mut PyObject) {
    require_owner!();
    clear_error();
    let ty = newref(exception);
    let val = newref(value);
    with_err(|e| {
        e.etype = ty;
        e.evalue = val;
    });
    store_error_text_from(value);
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_Fetch(
    type_: *mut *mut PyObject,
    value: *mut *mut PyObject,
    traceback: *mut *mut PyObject,
) {
    require_owner!();
    let (ty, val, tb) = with_err(|e| {
        let out = (e.etype, e.evalue, e.etraceback);
        e.etype = ptr::null_mut();
        e.evalue = ptr::null_mut();
        e.etraceback = ptr::null_mut();
        e.text[0] = 0;
        out
    });
    if !type_.is_null() {
        unsafe { *type_ = ty };
    } else {
        release(ty);
    }
    if !value.is_null() {
        unsafe { *value = val };
    } else {
        release(val);
    }
    if !traceback.is_null() {
        unsafe { *traceback = tb };
    } else {
        release(tb);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_Restore(
    type_: *mut PyObject,
    value: *mut PyObject,
    traceback: *mut PyObject,
) {
    require_owner!();
    clear_error();
    with_err(|e| {
        e.etype = type_;
        e.evalue = value;
        e.etraceback = traceback;
    });
    if !value.is_null() {
        store_error_text_from(value);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_NormalizeException(
    type_: *mut *mut PyObject,
    value: *mut *mut PyObject,
    _traceback: *mut *mut PyObject,
) {
    require_owner!();
    unsafe {
        if !type_.is_null() && !(*type_).is_null() && !value.is_null() && (*value).is_null() {
            *value = new_unicode_str("");
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_GivenExceptionMatches(
    given: *mut PyObject,
    expected: *mut PyObject,
) -> c_int {
    require_owner!(0);
    if given == expected {
        return 1;
    }
    if !given.is_null()
        && !expected.is_null()
        && unsafe { (*given).ob_type } == type_type()
        && unsafe { (*expected).ob_type } == type_type()
    {
        return PyType_IsSubtype(given as *mut PyTypeObject, expected as *mut PyTypeObject);
    }
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_NewExceptionWithDoc(
    name: *const c_char,
    _doc: *const c_char,
    base: *mut PyObject,
    _dictionary: *mut PyObject,
) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let mut spec = PyType_Spec {
        name,
        basicsize: size_of::<PyObject>() as c_int,
        itemsize: 0,
        flags: 0,
        slots: ptr::null_mut(),
    };
    let type_ = PyType_FromSpec(&mut spec);
    if !type_.is_null() {
        let typed = type_ as *mut PyTypeObject;
        unsafe {
            release(t((*typed).base_type));
            let chosen = if base.is_null() {
                PyExc_BaseException
            } else {
                base
            };
            (*typed).base_type = newref(chosen) as *mut PyTypeObject;
            (*typed).flags |= 1u64 << 30;
        }
    }
    type_
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_PrintEx(_set_system_last_vars: c_int) {
    require_owner!();
    clear_error();
}

#[unsafe(no_mangle)]
pub extern "C" fn PyErr_WriteUnraisable(_value: *mut PyObject) {
    require_owner!();
    clear_error();
}

#[unsafe(no_mangle)]
pub extern "C" fn PyException_SetCause(_exception: *mut PyObject, cause: *mut PyObject) -> c_int {
    require_owner!(-1);
    release(cause);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyException_SetTraceback(
    _exception: *mut PyObject,
    _traceback: *mut PyObject,
) -> c_int {
    require_owner!(-1);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyTraceBack_Print(_traceback: *mut PyObject, _file: *mut PyObject) -> c_int {
    require_owner!(-1);
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn PyImport_Import(name: *mut PyObject) -> *mut PyObject {
    require_owner!(ptr::null_mut());
    let module_name = PyUnicode_AsUTF8AndSize(name, ptr::null_mut());
    if module_name.is_null() {
        return ptr::null_mut();
    }
    let owned = unsafe { cstr_to_str(module_name) }.to_owned();
    create_module(ptr::null_mut(), Some(&owned))
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_error_type() -> *const c_char {
    let etype = with_err(|e| e.etype);
    if etype.is_null() || unsafe { (*etype).ob_type } != type_type() {
        return c"RuntimeError".as_ptr();
    }
    short_type_name(etype as *mut PyTypeObject).as_ptr() as *const c_char
}

// ===========================================================================
// Bridge module lifecycle.
// ===========================================================================

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_module_initialize(
    initialization_result: *mut PyObject,
    module: *mut *mut PyObject,
    multi_phase: *mut c_int,
) -> c_int {
    require_owner!(-1);
    if module.is_null() || multi_phase.is_null() {
        set_error(
            unsafe { PyExc_TypeError },
            "module initialization outputs are invalid",
        );
        return -1;
    }
    unsafe {
        *module = ptr::null_mut();
        *multi_phase = 0;
    }
    let last_definition = reg().last_definition;
    if initialization_result.is_null()
        || initialization_result as *mut PyModuleDef != last_definition
    {
        if with_err(|e| e.etype).is_null() {
            set_error(
                unsafe { PyExc_TypeError },
                "module initializer returned an invalid definition",
            );
        }
        return -1;
    }
    let created = create_module(last_definition, None);
    if created.is_null() {
        return -1;
    }
    clear_error();
    unsafe {
        if !(*last_definition).m_slots.is_null() {
            let mut slot_ptr = (*last_definition).m_slots;
            while (*slot_ptr).slot != 0 {
                if (*slot_ptr).slot == PY_MOD_EXEC {
                    let value = (*slot_ptr).value;
                    if value.is_null() {
                        set_error(PyExc_RuntimeError, "module execution slot failed");
                        release(created);
                        return -1;
                    }
                    let execute: unsafe extern "C" fn(*mut PyObject) -> c_int = transmute(value);
                    if execute(created) != 0 {
                        if with_err(|e| e.etype).is_null() {
                            set_error(PyExc_RuntimeError, "module execution slot failed");
                        }
                        release(created);
                        return -1;
                    }
                } else if (*slot_ptr).slot != PY_MOD_MULTIPLE_INTERPRETERS
                    && (*slot_ptr).slot != PY_MOD_GIL
                {
                    set_error(PyExc_TypeError, "module slot is unsupported");
                    release(created);
                    return -1;
                }
                slot_ptr = slot_ptr.add(1);
            }
        }
        *module = created;
        *multi_phase = 1;
    }
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_module_destroy(module: *mut PyObject) {
    require_owner!();
    clear_error();
    // Break the module <-> bound-method self-reference cycle before releasing the module.
    let values: Vec<*mut PyObject> = match find(module) {
        Some(m) if m.kind == Kind::Module => {
            let attrs = if let Value::Module { attributes, .. } = &m.value {
                *attributes
            } else {
                ptr::null_mut()
            };
            match find(attrs) {
                Some(am) if am.kind == Kind::Dict => {
                    if let Value::Dict { values, .. } = &am.value {
                        values.clone()
                    } else {
                        Vec::new()
                    }
                }
                _ => Vec::new(),
            }
        }
        _ => Vec::new(),
    };
    for v in values {
        let is_self = matches!(
            find(v),
            Some(mm) if mm.kind == Kind::CMethod
                && matches!(&mm.value, Value::Method { self_, .. } if *self_ == module)
        );
        if is_self {
            if let Some(mm) = find(v) {
                if let Value::Method { self_, .. } = &mut mm.value {
                    *self_ = ptr::null_mut();
                }
            }
            release(module);
        }
    }
    release(module);
    clear_error();
}

fn json_escape(bytes: &[u8], out: &mut String) {
    out.push('"');
    for &b in bytes {
        match b {
            b'"' => out.push_str("\\\""),
            b'\\' => out.push_str("\\\\"),
            b'\n' => out.push_str("\\n"),
            b'\r' => out.push_str("\\r"),
            b'\t' => out.push_str("\\t"),
            _ => out.push(b as char),
        }
    }
    out.push('"');
}

fn store_result(json: &str) -> *const c_char {
    store_result_bytes(json.as_bytes())
}

fn store_result_bytes(bytes: &[u8]) -> *const c_char {
    with_err(|e| {
        if bytes.len() + 1 > e.result.len() {
            return ptr::null();
        }
        e.result[..bytes.len()].copy_from_slice(bytes);
        e.result[bytes.len()] = 0;
        e.result.as_ptr() as *const c_char
    })
}

// ===========================================================================
// Generic qualified object bridge.
// ===========================================================================

const DP_OBJECT_INVALID: c_int = 0;
const DP_OBJECT_NONE: c_int = 1;
const DP_OBJECT_BOOL: c_int = 2;
const DP_OBJECT_INT: c_int = 3;
const DP_OBJECT_TEXT: c_int = 4;
const DP_OBJECT_BYTES: c_int = 5;
const DP_OBJECT_LIST: c_int = 6;
const DP_OBJECT_TUPLE: c_int = 7;
const DP_OBJECT_DICT: c_int = 8;
const DP_OBJECT_MODULE: c_int = 9;
const DP_OBJECT_CALLABLE: c_int = 10;
const DP_OBJECT_TYPE: c_int = 11;
const DP_OBJECT_INSTANCE: c_int = 12;
const MAX_BRIDGE_ARGUMENTS: i64 = 4096;

fn initialize_object_output(result: *mut *mut PyObject) -> bool {
    if result.is_null() {
        set_error(unsafe { PyExc_TypeError }, "object output is invalid");
        false
    } else {
        unsafe { *result = ptr::null_mut() };
        true
    }
}

fn generic_kind(object: *mut PyObject) -> c_int {
    if object.is_null() {
        return DP_OBJECT_INVALID;
    }
    unsafe {
        if object == &raw mut _Py_NoneStruct {
            return DP_OBJECT_NONE;
        }
        if object == &raw mut _Py_TrueStruct || object == &raw mut _Py_FalseStruct {
            return DP_OBJECT_BOOL;
        }
    }
    match find(object).map(|meta| meta.kind) {
        Some(Kind::Long) => DP_OBJECT_INT,
        Some(Kind::Unicode) => DP_OBJECT_TEXT,
        Some(Kind::Bytes) => DP_OBJECT_BYTES,
        Some(Kind::List) => DP_OBJECT_LIST,
        Some(Kind::Tuple) => DP_OBJECT_TUPLE,
        Some(Kind::Dict) => DP_OBJECT_DICT,
        Some(Kind::Module) => DP_OBJECT_MODULE,
        Some(Kind::CMethod) => DP_OBJECT_CALLABLE,
        Some(Kind::Type) => DP_OBJECT_TYPE,
        Some(Kind::Instance | Kind::Iterator) => DP_OBJECT_INSTANCE,
        None => DP_OBJECT_INVALID,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_module_attribute_names(
    module: *mut PyObject,
    result_json: *mut *const c_char,
) -> c_int {
    require_owner!(-1);
    if result_json.is_null() {
        set_error(
            unsafe { PyExc_TypeError },
            "attribute-name output is invalid",
        );
        return -1;
    }
    unsafe { *result_json = ptr::null() };
    let attributes = match find(module) {
        Some(meta) if meta.kind == Kind::Module => match &meta.value {
            Value::Module { attributes, .. } => *attributes,
            _ => ptr::null_mut(),
        },
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected module");
            return -1;
        }
    };
    let keys = match find(attributes) {
        Some(meta) if meta.kind == Kind::Dict => match &meta.value {
            Value::Dict { keys, .. } => keys.clone(),
            _ => Vec::new(),
        },
        _ => Vec::new(),
    };
    let mut json = String::from("[");
    let mut first = true;
    for key in &keys {
        let bytes = match find(*key) {
            Some(meta) if meta.kind == Kind::Unicode => match &meta.value {
                Value::Text { bytes } => &bytes[..bytes.len().saturating_sub(1)],
                _ => continue,
            },
            _ => continue,
        };
        if !first {
            json.push(',');
        }
        json_escape(bytes, &mut json);
        first = false;
    }
    json.push(']');
    let stored = store_result(&json);
    if stored.is_null() {
        set_error(
            unsafe { PyExc_ValueError },
            "module attribute names exceed the bridge result limit",
        );
        return -1;
    }
    unsafe { *result_json = stored };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_get_attr(
    object: *mut PyObject,
    name: *const c_char,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) || object.is_null() || name.is_null() {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "attribute lookup is invalid");
        }
        return -1;
    }
    clear_error();
    let attribute = get_attribute_cstr(object, unsafe { cstr_to_str(name) });
    if attribute.is_null() {
        return -1;
    }
    unsafe { *result = attribute };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_call(
    callable: *mut PyObject,
    arguments: *const *mut PyObject,
    argument_count: i64,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result)
        || callable.is_null()
        || !(0..=MAX_BRIDGE_ARGUMENTS).contains(&argument_count)
        || (argument_count != 0 && arguments.is_null())
    {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "object call inputs are invalid");
        }
        return -1;
    }
    let args = PyTuple_New(argument_count as Py_ssize_t);
    if args.is_null() {
        return -1;
    }
    for index in 0..argument_count {
        let argument = unsafe { *arguments.add(index as usize) };
        if argument.is_null() || PyTuple_SetItem(args, index as Py_ssize_t, newref(argument)) != 0 {
            release(args);
            set_error(
                unsafe { PyExc_TypeError },
                "object call contains an invalid argument",
            );
            return -1;
        }
    }
    clear_error();
    let returned = PyObject_Call(callable, args, ptr::null_mut());
    release(args);
    if returned.is_null() {
        if with_err(|error| error.etype).is_null() {
            set_error(
                unsafe { PyExc_RuntimeError },
                "object call returned NULL without an error",
            );
        }
        return -1;
    }
    unsafe { *result = returned };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_call_kw(
    callable: *mut PyObject,
    arguments: *const *mut PyObject,
    argument_count: i64,
    keyword_names: *const *const c_char,
    keyword_values: *const *mut PyObject,
    keyword_count: i64,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result)
        || callable.is_null()
        || !(0..=MAX_BRIDGE_ARGUMENTS).contains(&argument_count)
        || (argument_count != 0 && arguments.is_null())
        || !(0..=MAX_BRIDGE_ARGUMENTS).contains(&keyword_count)
        || (keyword_count != 0 && (keyword_names.is_null() || keyword_values.is_null()))
    {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "object call inputs are invalid");
        }
        return -1;
    }
    let args = PyTuple_New(argument_count as Py_ssize_t);
    if args.is_null() {
        return -1;
    }
    for index in 0..argument_count {
        let argument = unsafe { *arguments.add(index as usize) };
        if argument.is_null() || PyTuple_SetItem(args, index as Py_ssize_t, newref(argument)) != 0 {
            release(args);
            set_error(
                unsafe { PyExc_TypeError },
                "object call contains an invalid argument",
            );
            return -1;
        }
    }
    let kwargs = PyDict_New();
    if kwargs.is_null() {
        release(args);
        return -1;
    }
    for index in 0..keyword_count {
        let name = unsafe { *keyword_names.add(index as usize) };
        let value = unsafe { *keyword_values.add(index as usize) };
        if name.is_null() || value.is_null() {
            release(kwargs);
            release(args);
            set_error(
                unsafe { PyExc_TypeError },
                "object call contains an invalid keyword argument",
            );
            return -1;
        }
        let text = unsafe { cstr_to_str(name) };
        let key =
            PyUnicode_FromStringAndSize(text.as_ptr() as *const c_char, text.len() as Py_ssize_t);
        if key.is_null() || PyDict_SetItem(kwargs, key, value) != 0 {
            release(key);
            release(kwargs);
            release(args);
            set_error(
                unsafe { PyExc_TypeError },
                "object call contains an invalid keyword argument",
            );
            return -1;
        }
        release(key);
    }
    clear_error();
    let returned = PyObject_Call(callable, args, kwargs);
    release(kwargs);
    release(args);
    if returned.is_null() {
        if with_err(|error| error.etype).is_null() {
            set_error(
                unsafe { PyExc_RuntimeError },
                "object call returned NULL without an error",
            );
        }
        return -1;
    }
    unsafe { *result = returned };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_from_utf8(
    value: *const c_char,
    value_length: i64,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result)
        || value_length < 0
        || value_length > isize::MAX as i64
        || (value_length != 0 && value.is_null())
    {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "UTF-8 object input is invalid");
        }
        return -1;
    }
    let object = new_unicode(value, value_length as Py_ssize_t);
    if object.is_null() {
        return -1;
    }
    unsafe { *result = object };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_from_int64(value: i64, result: *mut *mut PyObject) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) {
        return -1;
    }
    if value < c_long::MIN as i64 || value > c_long::MAX as i64 {
        set_error(
            unsafe { PyExc_ValueError },
            "integer does not fit in C long",
        );
        return -1;
    }
    let object = PyLong_FromLong(value as c_long);
    if object.is_null() {
        return -1;
    }
    unsafe { *result = object };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_from_bool(value: c_int, result: *mut *mut PyObject) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) {
        return -1;
    }
    let object = unsafe {
        if value == 0 {
            &raw mut _Py_FalseStruct
        } else {
            &raw mut _Py_TrueStruct
        }
    };
    unsafe { *result = newref(object) };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_from_none(result: *mut *mut PyObject) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) {
        return -1;
    }
    unsafe { *result = newref(&raw mut _Py_NoneStruct) };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_sequence(
    kind: c_int,
    items: *const *mut PyObject,
    item_count: i64,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result)
        || !matches!(kind, DP_OBJECT_LIST | DP_OBJECT_TUPLE)
        || !(0..=MAX_BRIDGE_ARGUMENTS).contains(&item_count)
        || (item_count != 0 && items.is_null())
    {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "sequence inputs are invalid");
        }
        return -1;
    }
    let sequence = if kind == DP_OBJECT_LIST {
        PyList_New(item_count as Py_ssize_t)
    } else {
        PyTuple_New(item_count as Py_ssize_t)
    };
    if sequence.is_null() {
        return -1;
    }
    for index in 0..item_count {
        let item = unsafe { *items.add(index as usize) };
        let status = if item.is_null() {
            -1
        } else if kind == DP_OBJECT_LIST {
            PyList_SetItem(sequence, index as Py_ssize_t, newref(item))
        } else {
            PyTuple_SetItem(sequence, index as Py_ssize_t, newref(item))
        };
        if status != 0 {
            release(sequence);
            set_error(
                unsafe { PyExc_TypeError },
                "sequence contains an invalid item",
            );
            return -1;
        }
    }
    unsafe { *result = sequence };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_kind_of(object: *mut PyObject, kind: *mut c_int) -> c_int {
    require_owner!(-1);
    if kind.is_null() {
        set_error(unsafe { PyExc_TypeError }, "object-kind output is invalid");
        return -1;
    }
    let value = generic_kind(object);
    unsafe { *kind = value };
    if value == DP_OBJECT_INVALID {
        set_error(unsafe { PyExc_TypeError }, "object is invalid");
        -1
    } else {
        0
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_as_int64(object: *mut PyObject, result: *mut i64) -> c_int {
    require_owner!(-1);
    if result.is_null() {
        set_error(unsafe { PyExc_TypeError }, "integer output is invalid");
        return -1;
    }
    clear_error();
    let value = PyLong_AsLong(object);
    if value == -1 && !PyErr_Occurred().is_null() {
        return -1;
    }
    unsafe { *result = value as i64 };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_as_bool(object: *mut PyObject, result: *mut c_int) -> c_int {
    require_owner!(-1);
    if result.is_null() {
        set_error(unsafe { PyExc_TypeError }, "boolean output is invalid");
        return -1;
    }
    unsafe {
        if object == &raw mut _Py_TrueStruct {
            *result = 1;
            return 0;
        }
        if object == &raw mut _Py_FalseStruct {
            *result = 0;
            return 0;
        }
    }
    set_error(unsafe { PyExc_TypeError }, "expected bool");
    -1
}

fn copy_object_text(
    object: *mut PyObject,
    result: *mut *const c_char,
    result_length: *mut i64,
) -> c_int {
    if result.is_null() || result_length.is_null() {
        set_error(unsafe { PyExc_TypeError }, "text output is invalid");
        return -1;
    }
    unsafe {
        *result = ptr::null();
        *result_length = 0;
    }
    let bytes = match find(object) {
        Some(meta) if matches!(meta.kind, Kind::Unicode | Kind::Bytes) => match &meta.value {
            Value::Text { bytes } => bytes[..bytes.len().saturating_sub(1)].to_vec(),
            _ => Vec::new(),
        },
        _ => {
            set_error(unsafe { PyExc_TypeError }, "expected text or bytes");
            return -1;
        }
    };
    let stored = store_result_bytes(&bytes);
    if stored.is_null() {
        set_error(
            unsafe { PyExc_ValueError },
            "text exceeds the bridge result limit",
        );
        return -1;
    }
    unsafe {
        *result = stored;
        *result_length = bytes.len() as i64;
    }
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_as_utf8(
    object: *mut PyObject,
    result: *mut *const c_char,
    result_length: *mut i64,
) -> c_int {
    require_owner!(-1);
    copy_object_text(object, result, result_length)
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_string(
    object: *mut PyObject,
    result: *mut *const c_char,
    result_length: *mut i64,
) -> c_int {
    require_owner!(-1);
    let text = PyObject_Str(object);
    if text.is_null() {
        return -1;
    }
    let status = copy_object_text(text, result, result_length);
    release(text);
    status
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_repr(
    object: *mut PyObject,
    result: *mut *const c_char,
    result_length: *mut i64,
) -> c_int {
    require_owner!(-1);
    let text = PyObject_Repr(object);
    if text.is_null() {
        return -1;
    }
    let status = copy_object_text(text, result, result_length);
    release(text);
    status
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_rich_compare(
    left: *mut PyObject,
    right: *mut PyObject,
    operation: c_int,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) || left.is_null() || right.is_null() {
        if !result.is_null() {
            set_error(
                unsafe { PyExc_TypeError },
                "rich comparison inputs are invalid",
            );
        }
        return -1;
    }
    clear_error();
    let compared = PyObject_RichCompare(left, right, operation);
    if compared.is_null() {
        return -1;
    }
    unsafe { *result = compared };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_size(object: *mut PyObject, result: *mut i64) -> c_int {
    require_owner!(-1);
    if result.is_null() {
        set_error(unsafe { PyExc_TypeError }, "size output is invalid");
        return -1;
    }
    clear_error();
    let size = PyObject_Size(object);
    if size < 0 {
        return -1;
    }
    unsafe { *result = size as i64 };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_get_item(
    object: *mut PyObject,
    key: *mut PyObject,
    result: *mut *mut PyObject,
) -> c_int {
    require_owner!(-1);
    if !initialize_object_output(result) || object.is_null() || key.is_null() {
        if !result.is_null() {
            set_error(unsafe { PyExc_TypeError }, "item lookup inputs are invalid");
        }
        return -1;
    }
    clear_error();
    let item = PyObject_GetItem(object, key);
    if item.is_null() {
        return -1;
    }
    unsafe { *result = item };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_abi3_object_release(object: *mut PyObject) {
    require_owner!();
    release(object);
}
