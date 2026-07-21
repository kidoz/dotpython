#include "dotpython_abi3.h"

static int cleanup_count;

static PyObject *fixture_increment(PyObject *self, PyObject *argument) {
    (void)self;
    long value = PyLong_AsLong(argument);
    if (PyErr_Occurred() != NULL) {
        return NULL;
    }
    return PyLong_FromLong(value + 1);
}

static PyObject *fixture_fail(PyObject *self, PyObject *ignored) {
    (void)self;
    (void)ignored;
    PyErr_SetString(PyExc_ValueError, "fixture failure");
    return NULL;
}

static int fixture_execute(PyObject *module) {
    return PyModule_AddIntConstant(module, "fixture_ready", 1);
}

static void fixture_free(void *module) {
    (void)module;
    cleanup_count++;
}

static PyMethodDef fixture_methods[] = {
    { "increment", fixture_increment, METH_O, "Increment a whole number." },
    { "fail", fixture_fail, METH_NOARGS, "Raise a deliberate fixture error." },
    { NULL, NULL, 0, NULL }
};

static PyModuleDef_Slot fixture_slots[] = {
    { Py_mod_exec, (void *)fixture_execute },
    { 0, NULL }
};

static PyModuleDef fixture_definition = {
    PyModuleDef_HEAD_INIT,
    "dotpython_fixture",
    "Minimal DotPython Stable-ABI fixture.",
    0,
    fixture_methods,
    fixture_slots,
    NULL,
    NULL,
    fixture_free
};

PyMODINIT_FUNC PyInit_dotpython_fixture(void) {
    return PyModuleDef_Init(&fixture_definition);
}

DP_ABI3_EXPORT int dotpython_fixture_cleanup_count(void) {
    return cleanup_count;
}
