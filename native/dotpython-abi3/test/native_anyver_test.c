#include "dotpython_bridge.h"

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef PyObject *(*module_init_function)(void);

static void require(int condition, const char *message) {
    if (!condition) {
        fputs(message == NULL ? "native Anyver check failed" : message, stderr);
        fputc('\n', stderr);
        exit(1);
    }
}

static PyObject *unicode(const char *text) {
    PyObject *value = PyUnicode_FromStringAndSize(text, (Py_ssize_t)strlen(text));
    require(value != NULL, dp_abi3_error_message());
    return value;
}

static PyObject *call(PyObject *owner, const char *name, PyObject *args) {
    PyObject *attribute_name = unicode(name);
    PyObject *callable = PyObject_GetAttr(owner, attribute_name);
    _Py_DecRef(attribute_name);
    require(callable != NULL, dp_abi3_error_message());
    PyObject *result = PyObject_Call(callable, args, NULL);
    _Py_DecRef(callable);
    require(result != NULL, dp_abi3_error_message());
    return result;
}

int main(int argc, char **argv) {
    require(argc == 2, "Anyver native module path is required");
    require(dp_abi3_bridge_version() == DP_ABI3_BRIDGE_VERSION, "bridge version mismatch");
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(library != NULL, dlerror());
    module_init_function initialize = (module_init_function)dlsym(library, "PyInit__anyver");
    require(initialize != NULL, "Anyver initializer is missing");

    PyObject *module = NULL;
    int multi_phase = 0;
    require(
        dp_abi3_module_initialize(initialize(), &module, &multi_phase) == 0,
        dp_abi3_error_message()
    );
    require(multi_phase == 1, "Anyver did not use multi-phase initialization");
    int64_t initialized_object_count = dp_abi3_active_object_count();

    int64_t bridge_comparison = 0;
    require(
        dp_abi3_anyver_compare(module, "1.0", "2.0", "generic", &bridge_comparison) == 0 &&
            bridge_comparison == -1,
        dp_abi3_error_message()
    );
    const char *bridge_versions[] = {"2.0", "1.0-alpha", "1.0"};
    const char *bridge_json = NULL;
    require(
        dp_abi3_anyver_sort_versions(module, bridge_versions, 3, "generic", &bridge_json) == 0,
        dp_abi3_error_message()
    );
    require(
        strcmp(bridge_json, "[\"1.0-alpha\",\"1.0\",\"2.0\"]") == 0,
        "Anyver bridge sort mismatch"
    );
    require(
        dp_abi3_anyver_version_to_json(module, "1.2.3", "auto", &bridge_json) == 0,
        dp_abi3_error_message()
    );
    require(
        strstr(bridge_json, "\"raw\":\"1.2.3\"") != NULL &&
            strstr(bridge_json, "\"major\":1") != NULL,
        "Anyver bridge Version mismatch"
    );

    PyObject *compare_args = PyTuple_New(2);
    require(compare_args != NULL, dp_abi3_error_message());
    require(PyTuple_SetItem(compare_args, 0, unicode("1.0")) == 0, dp_abi3_error_message());
    require(PyTuple_SetItem(compare_args, 1, unicode("2.0")) == 0, dp_abi3_error_message());
    PyObject *comparison = call(module, "compare", compare_args);
    require(PyLong_AsLong(comparison) == -1 && PyErr_Occurred() == NULL, "Anyver compare mismatch");
    _Py_DecRef(comparison);
    _Py_DecRef(compare_args);

    PyObject *versions = PyList_New(0);
    require(versions != NULL, dp_abi3_error_message());
    PyObject *text = unicode("2.0");
    require(PyList_Append(versions, text) == 0, dp_abi3_error_message());
    _Py_DecRef(text);
    text = unicode("1.0-alpha");
    require(PyList_Append(versions, text) == 0, dp_abi3_error_message());
    _Py_DecRef(text);
    text = unicode("1.0");
    require(PyList_Append(versions, text) == 0, dp_abi3_error_message());
    _Py_DecRef(text);
    PyObject *sort_args = PyTuple_New(1);
    require(sort_args != NULL, dp_abi3_error_message());
    require(PyTuple_SetItem(sort_args, 0, versions) == 0, dp_abi3_error_message());
    PyObject *sorted = call(module, "sort_versions", sort_args);
    require(PyList_Size(sorted) == 3, "Anyver sort result length mismatch");
    const char *expected[] = {"1.0-alpha", "1.0", "2.0"};
    for (Py_ssize_t index = 0; index < 3; index++) {
        const char *actual = PyUnicode_AsUTF8AndSize(PyList_GetItem(sorted, index), NULL);
        require(actual != NULL && strcmp(actual, expected[index]) == 0, "Anyver sort mismatch");
    }
    _Py_DecRef(sorted);
    _Py_DecRef(sort_args);

    PyObject *version_args = PyTuple_New(1);
    require(version_args != NULL, dp_abi3_error_message());
    require(PyTuple_SetItem(version_args, 0, unicode("1.2.3")) == 0, dp_abi3_error_message());
    PyObject *version = call(module, "Version", version_args);
    _Py_DecRef(version_args);
    PyObject *empty = PyTuple_New(0);
    require(empty != NULL, dp_abi3_error_message());
    PyObject *dictionary = call(version, "to_dict", empty);
    _Py_DecRef(empty);
    PyObject *raw_key = unicode("raw");
    PyObject *raw = PyDict_GetItemWithError(dictionary, raw_key);
    _Py_DecRef(raw_key);
    require(
        raw != NULL && strcmp(PyUnicode_AsUTF8AndSize(raw, NULL), "1.2.3") == 0,
        "Anyver Version.to_dict raw mismatch"
    );
    PyObject *major_key = unicode("major");
    PyObject *major = PyDict_GetItemWithError(dictionary, major_key);
    _Py_DecRef(major_key);
    require(major != NULL && PyLong_AsLong(major) == 1, "Anyver Version.to_dict major mismatch");
    _Py_DecRef(dictionary);
    _Py_DecRef(version);

    dp_abi3_module_destroy(module);
    require(
        dp_abi3_active_object_count() <= initialized_object_count,
        "Anyver transient native objects leaked"
    );
    /* PyO3 keeps heap-type and intern caches alive until worker process exit. */
    (void)library;
    return 0;
}
