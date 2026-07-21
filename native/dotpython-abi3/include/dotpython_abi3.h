#ifndef DOTPYTHON_ABI3_H
#define DOTPYTHON_ABI3_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
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

typedef struct _typeobject PyTypeObject;

typedef struct _object {
    Py_ssize_t ob_refcnt;
    PyTypeObject *ob_type;
} PyObject;

typedef PyObject *(*PyCFunction)(PyObject *, PyObject *);

typedef struct PyMethodDef {
    const char *ml_name;
    PyCFunction ml_meth;
    int ml_flags;
    const char *ml_doc;
} PyMethodDef;

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

#define PyObject_HEAD_INIT(type) { 1, (type) }
#define PyModuleDef_HEAD_INIT { PyObject_HEAD_INIT(NULL), NULL, 0, NULL }
#define Py_mod_exec 2
#define METH_NOARGS 0x0004
#define METH_O 0x0008

DP_ABI3_EXPORT PyObject *PyModuleDef_Init(PyModuleDef *definition);
DP_ABI3_EXPORT int PyModule_AddIntConstant(PyObject *module, const char *name, long value);
DP_ABI3_EXPORT PyObject *PyLong_FromLong(long value);
DP_ABI3_EXPORT long PyLong_AsLong(PyObject *value);
DP_ABI3_EXPORT void PyErr_SetString(PyObject *exception, const char *message);
DP_ABI3_EXPORT PyObject *PyErr_Occurred(void);
DP_ABI3_EXPORT extern PyObject *PyExc_ValueError;

#define PyMODINIT_FUNC DP_ABI3_EXPORT PyObject *

#ifdef __cplusplus
}
#endif

#endif
