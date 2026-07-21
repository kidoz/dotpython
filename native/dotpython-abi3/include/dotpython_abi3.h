#ifndef DOTPYTHON_ABI3_H
#define DOTPYTHON_ABI3_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#if defined(DOTPYTHON_ABI3_BUILD)
#define DP_ABI3_EXPORT __declspec(dllexport)
#else
#define DP_ABI3_EXPORT __declspec(dllimport)
#endif
#else
#define DP_ABI3_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef intptr_t Py_ssize_t;
typedef intptr_t Py_hash_t;
typedef struct _typeobject PyTypeObject;
typedef struct _ts PyThreadState;
typedef int PyGILState_STATE;

typedef struct _object {
    Py_ssize_t ob_refcnt;
    PyTypeObject *ob_type;
} PyObject;

typedef PyObject *(*PyCFunction)(PyObject *, PyObject *);
typedef PyObject *(*PyCFunctionFast)(PyObject *, PyObject *const *, Py_ssize_t);
typedef PyObject *(*PyCFunctionWithKeywords)(PyObject *, PyObject *, PyObject *);
typedef PyObject
    *(*PyCFunctionFastWithKeywords)(PyObject *, PyObject *const *, Py_ssize_t, PyObject *);
typedef PyObject
    *(*PyCMethod)(PyObject *, PyTypeObject *, PyObject *const *, Py_ssize_t, PyObject *);
typedef PyObject *(*unaryfunc)(PyObject *);
typedef PyObject *(*binaryfunc)(PyObject *, PyObject *);
typedef int (*inquiry)(PyObject *);
typedef Py_ssize_t (*lenfunc)(PyObject *);
typedef PyObject *(*ssizeargfunc)(PyObject *, Py_ssize_t);
typedef int (*ssizeobjargproc)(PyObject *, Py_ssize_t, PyObject *);
typedef int (*objobjargproc)(PyObject *, PyObject *, PyObject *);
typedef void (*destructor)(PyObject *);
typedef PyObject *(*reprfunc)(PyObject *);
typedef Py_hash_t (*hashfunc)(PyObject *);
typedef PyObject *(*richcmpfunc)(PyObject *, PyObject *, int);
typedef PyObject *(*getiterfunc)(PyObject *);
typedef PyObject *(*iternextfunc)(PyObject *);
typedef PyObject *(*getattrofunc)(PyObject *, PyObject *);
typedef int (*setattrofunc)(PyObject *, PyObject *, PyObject *);
typedef PyObject *(*newfunc)(PyTypeObject *, PyObject *, PyObject *);
typedef PyObject *(*allocfunc)(PyTypeObject *, Py_ssize_t);
typedef void (*freefunc)(void *);
typedef PyObject *(*getter)(PyObject *, void *);
typedef int (*setter)(PyObject *, PyObject *, void *);

typedef struct PyMethodDef {
    const char *ml_name;
    PyCFunction ml_meth;
    int ml_flags;
    const char *ml_doc;
} PyMethodDef;

typedef struct PyGetSetDef {
    const char *name;
    getter get;
    setter set;
    const char *doc;
    void *closure;
} PyGetSetDef;

typedef struct PyType_Slot {
    int slot;
    void *pfunc;
} PyType_Slot;

typedef struct PyType_Spec {
    const char *name;
    int basicsize;
    int itemsize;
    unsigned int flags;
    PyType_Slot *slots;
} PyType_Spec;

typedef struct PyModuleDef_Base {
    PyObject ob_base;
    PyObject *(*m_init)(void);
    Py_ssize_t m_index;
    PyObject *m_copy;
} PyModuleDef_Base;

typedef struct PyModuleDef_Slot {
    int slot;
    void *value;
} PyModuleDef_Slot;

typedef struct PyModuleDef {
    PyModuleDef_Base m_base;
    const char *m_name;
    const char *m_doc;
    Py_ssize_t m_size;
    PyMethodDef *m_methods;
    PyModuleDef_Slot *m_slots;
    void *m_traverse;
    void *m_clear;
    void (*m_free)(void *);
} PyModuleDef;

/* Keep Stable-ABI initializer spelling independent of the installed clang-format version. */
// clang-format off
#define PyObject_HEAD_INIT(type) {1, (type)}
#define PyModuleDef_HEAD_INIT {PyObject_HEAD_INIT(NULL), NULL, 0, NULL}
// clang-format on

#define Py_mod_create 1
#define Py_mod_exec 2
#define Py_mod_multiple_interpreters 3
#define Py_mod_gil 4

#define METH_VARARGS 0x0001
#define METH_KEYWORDS 0x0002
#define METH_NOARGS 0x0004
#define METH_O 0x0008
#define METH_CLASS 0x0010
#define METH_STATIC 0x0020
#define METH_COEXIST 0x0040
#define METH_FASTCALL 0x0080
#define METH_METHOD 0x0200

#define Py_mp_ass_subscript 3
#define Py_mp_length 4
#define Py_mp_subscript 5
#define Py_nb_bool 9
#define Py_sq_item 44
#define Py_sq_length 45
#define Py_tp_alloc 47
#define Py_tp_base 48
#define Py_tp_call 50
#define Py_tp_dealloc 52
#define Py_tp_getattro 58
#define Py_tp_hash 59
#define Py_tp_iter 62
#define Py_tp_iternext 63
#define Py_tp_methods 64
#define Py_tp_new 65
#define Py_tp_repr 66
#define Py_tp_richcompare 67
#define Py_tp_setattro 69
#define Py_tp_str 70
#define Py_tp_members 72
#define Py_tp_getset 73
#define Py_tp_free 74

#define DP_PY_LT 0
#define DP_PY_LE 1
#define DP_PY_EQ 2
#define DP_PY_NE 3
#define DP_PY_GT 4
#define DP_PY_GE 5

#define DP_ABI3_IMMORTAL_REFCNT ((Py_ssize_t)3 << 30)

DP_ABI3_EXPORT extern PyTypeObject PyBaseObject_Type;
DP_ABI3_EXPORT extern PyTypeObject PyDict_Type;
DP_ABI3_EXPORT extern PyTypeObject PyList_Type;
DP_ABI3_EXPORT extern PyTypeObject PyModule_Type;
DP_ABI3_EXPORT extern PyTypeObject PyTuple_Type;
DP_ABI3_EXPORT extern PyTypeObject PyType_Type;
DP_ABI3_EXPORT extern PyTypeObject PyUnicode_Type;

DP_ABI3_EXPORT extern PyObject _Py_FalseStruct;
DP_ABI3_EXPORT extern PyObject _Py_NoneStruct;
DP_ABI3_EXPORT extern PyObject _Py_NotImplementedStruct;
DP_ABI3_EXPORT extern PyObject _Py_TrueStruct;

DP_ABI3_EXPORT extern PyObject *PyExc_AttributeError;
DP_ABI3_EXPORT extern PyObject *PyExc_BaseException;
DP_ABI3_EXPORT extern PyObject *PyExc_IndexError;
DP_ABI3_EXPORT extern PyObject *PyExc_RuntimeError;
DP_ABI3_EXPORT extern PyObject *PyExc_SystemError;
DP_ABI3_EXPORT extern PyObject *PyExc_TypeError;
DP_ABI3_EXPORT extern PyObject *PyExc_ValueError;

DP_ABI3_EXPORT char *PyBytes_AsString(PyObject *value);
DP_ABI3_EXPORT Py_ssize_t PyBytes_Size(PyObject *value);
DP_ABI3_EXPORT PyObject *
PyCMethod_New(PyMethodDef *definition, PyObject *self, PyObject *module, PyTypeObject *class_type);
DP_ABI3_EXPORT PyObject *PyDict_GetItemWithError(PyObject *dictionary, PyObject *key);
DP_ABI3_EXPORT PyObject *PyDict_New(void);
DP_ABI3_EXPORT int
PyDict_Next(PyObject *dictionary, Py_ssize_t *position, PyObject **key, PyObject **value);
DP_ABI3_EXPORT int PyDict_SetItem(PyObject *dictionary, PyObject *key, PyObject *value);
DP_ABI3_EXPORT Py_ssize_t PyDict_Size(PyObject *dictionary);
DP_ABI3_EXPORT void PyErr_Fetch(PyObject **type, PyObject **value, PyObject **traceback);
DP_ABI3_EXPORT int PyErr_GivenExceptionMatches(PyObject *given, PyObject *expected);
DP_ABI3_EXPORT PyObject *
PyErr_NewExceptionWithDoc(const char *name, const char *doc, PyObject *base, PyObject *dictionary);
DP_ABI3_EXPORT void
PyErr_NormalizeException(PyObject **type, PyObject **value, PyObject **traceback);
DP_ABI3_EXPORT PyObject *PyErr_Occurred(void);
DP_ABI3_EXPORT void PyErr_PrintEx(int set_system_last_vars);
DP_ABI3_EXPORT void PyErr_Restore(PyObject *type, PyObject *value, PyObject *traceback);
DP_ABI3_EXPORT void PyErr_SetObject(PyObject *exception, PyObject *value);
DP_ABI3_EXPORT void PyErr_SetString(PyObject *exception, const char *message);
DP_ABI3_EXPORT void PyErr_WriteUnraisable(PyObject *value);
DP_ABI3_EXPORT void PyEval_RestoreThread(PyThreadState *thread_state);
DP_ABI3_EXPORT PyThreadState *PyEval_SaveThread(void);
DP_ABI3_EXPORT int PyException_SetCause(PyObject *exception, PyObject *cause);
DP_ABI3_EXPORT int PyException_SetTraceback(PyObject *exception, PyObject *traceback);
DP_ABI3_EXPORT PyGILState_STATE PyGILState_Ensure(void);
DP_ABI3_EXPORT void PyGILState_Release(PyGILState_STATE state);
DP_ABI3_EXPORT PyObject *PyImport_Import(PyObject *name);
DP_ABI3_EXPORT PyObject *PyIter_Next(PyObject *iterator);
DP_ABI3_EXPORT int PyList_Append(PyObject *list, PyObject *value);
DP_ABI3_EXPORT PyObject *PyList_GetItem(PyObject *list, Py_ssize_t index);
DP_ABI3_EXPORT PyObject *PyList_New(Py_ssize_t size);
/* Steals value on success and failure, matching CPython's Stable-ABI contract. */
DP_ABI3_EXPORT int PyList_SetItem(PyObject *list, Py_ssize_t index, PyObject *value);
DP_ABI3_EXPORT Py_ssize_t PyList_Size(PyObject *list);
DP_ABI3_EXPORT long PyLong_AsLong(PyObject *value);
DP_ABI3_EXPORT PyObject *PyLong_FromLong(long value);
DP_ABI3_EXPORT PyObject *PyLong_FromSsize_t(Py_ssize_t value);
DP_ABI3_EXPORT PyObject *PyLong_FromUnsignedLongLong(unsigned long long value);
DP_ABI3_EXPORT PyObject *PyModuleDef_Init(PyModuleDef *definition);
DP_ABI3_EXPORT int PyModule_AddIntConstant(PyObject *module, const char *name, long value);
DP_ABI3_EXPORT PyObject *PyModule_GetNameObject(PyObject *module);
DP_ABI3_EXPORT PyObject *PyObject_Call(PyObject *callable, PyObject *args, PyObject *kwargs);
DP_ABI3_EXPORT PyObject *PyObject_CallNoArgs(PyObject *callable);
DP_ABI3_EXPORT int PyObject_DelItem(PyObject *object, PyObject *key);
/* The experimental subset is reference-counted only; cyclic-GC-dependent packages are unsupported.
 */
DP_ABI3_EXPORT void PyObject_GC_UnTrack(void *object);
DP_ABI3_EXPORT PyObject *PyObject_GenericGetDict(PyObject *object, void *context);
DP_ABI3_EXPORT int PyObject_GenericSetDict(PyObject *object, PyObject *value, void *context);
DP_ABI3_EXPORT PyObject *PyObject_GetAttr(PyObject *object, PyObject *name);
DP_ABI3_EXPORT PyObject *PyObject_GetItem(PyObject *object, PyObject *key);
DP_ABI3_EXPORT PyObject *PyObject_GetIter(PyObject *object);
DP_ABI3_EXPORT PyObject *PyObject_Repr(PyObject *object);
DP_ABI3_EXPORT int PyObject_SetAttr(PyObject *object, PyObject *name, PyObject *value);
DP_ABI3_EXPORT int PyObject_SetAttrString(PyObject *object, const char *name, PyObject *value);
DP_ABI3_EXPORT int PyObject_SetItem(PyObject *object, PyObject *key, PyObject *value);
DP_ABI3_EXPORT Py_ssize_t PyObject_Size(PyObject *object);
DP_ABI3_EXPORT PyObject *PyObject_Str(PyObject *object);
DP_ABI3_EXPORT int PySequence_Check(PyObject *object);
DP_ABI3_EXPORT int PyTraceBack_Print(PyObject *traceback, PyObject *file);
DP_ABI3_EXPORT PyObject *PyTuple_GetItem(PyObject *tuple, Py_ssize_t index);
DP_ABI3_EXPORT PyObject *PyTuple_New(Py_ssize_t size);
/* Steals value on success and failure, matching CPython's Stable-ABI contract. */
DP_ABI3_EXPORT int PyTuple_SetItem(PyObject *tuple, Py_ssize_t index, PyObject *value);
DP_ABI3_EXPORT Py_ssize_t PyTuple_Size(PyObject *tuple);
DP_ABI3_EXPORT PyObject *PyType_FromSpec(PyType_Spec *specification);
DP_ABI3_EXPORT unsigned long PyType_GetFlags(PyTypeObject *type);
DP_ABI3_EXPORT PyObject *PyType_GetName(PyTypeObject *type);
DP_ABI3_EXPORT PyObject *PyType_GetQualName(PyTypeObject *type);
DP_ABI3_EXPORT void *PyType_GetSlot(PyTypeObject *type, int slot);
DP_ABI3_EXPORT int PyType_IsSubtype(PyTypeObject *type, PyTypeObject *base);
DP_ABI3_EXPORT PyObject *
PyUnicode_AsEncodedString(PyObject *unicode, const char *encoding, const char *errors);
DP_ABI3_EXPORT const char *PyUnicode_AsUTF8AndSize(PyObject *unicode, Py_ssize_t *size);
DP_ABI3_EXPORT PyObject *PyUnicode_FromStringAndSize(const char *text, Py_ssize_t size);
DP_ABI3_EXPORT void PyUnicode_InternInPlace(PyObject **unicode);
DP_ABI3_EXPORT int Py_IsInitialized(void);
DP_ABI3_EXPORT PyObject *Py_NewRef(PyObject *object);
DP_ABI3_EXPORT void _Py_DecRef(PyObject *object);
DP_ABI3_EXPORT void _Py_IncRef(PyObject *object);

#define PyMODINIT_FUNC DP_ABI3_EXPORT PyObject *

#ifdef __cplusplus
}
#endif

#endif
