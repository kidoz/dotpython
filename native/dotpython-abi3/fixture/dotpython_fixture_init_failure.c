#include "dotpython_abi3.h"

PyMODINIT_FUNC PyInit_dotpython_fixture(void) {
    PyErr_SetString(PyExc_ValueError, "fixture initialization failure");
    return NULL;
}

DP_ABI3_EXPORT int dotpython_fixture_cleanup_count(void) {
    return 0;
}
