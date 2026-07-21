#ifndef DOTPYTHON_BRIDGE_H
#define DOTPYTHON_BRIDGE_H

#include "dotpython_abi3.h"

#ifdef __cplusplus
extern "C" {
#endif

#define DP_ABI3_BRIDGE_VERSION 3

typedef enum dp_abi3_object_kind {
    DP_ABI3_OBJECT_INVALID = 0,
    DP_ABI3_OBJECT_NONE = 1,
    DP_ABI3_OBJECT_BOOL = 2,
    DP_ABI3_OBJECT_INT = 3,
    DP_ABI3_OBJECT_TEXT = 4,
    DP_ABI3_OBJECT_BYTES = 5,
    DP_ABI3_OBJECT_LIST = 6,
    DP_ABI3_OBJECT_TUPLE = 7,
    DP_ABI3_OBJECT_DICT = 8,
    DP_ABI3_OBJECT_MODULE = 9,
    DP_ABI3_OBJECT_CALLABLE = 10,
    DP_ABI3_OBJECT_TYPE = 11,
    DP_ABI3_OBJECT_INSTANCE = 12
} dp_abi3_object_kind;

/*
 * Stateful calls, native callbacks, and object release must stay on the thread that first
 * activates the bridge. Returned text points to thread-local bridge storage and remains valid
 * only until the next bridge/C-API call on that thread. PyObject results follow CPython's
 * documented new/borrowed/stolen reference conventions.
 */
DP_ABI3_EXPORT int dp_abi3_bridge_version(void);
DP_ABI3_EXPORT int
dp_abi3_module_initialize(PyObject *initialization_result, PyObject **module, int *multi_phase);
DP_ABI3_EXPORT int dp_abi3_module_get_int(PyObject *module, const char *name, int64_t *value);
DP_ABI3_EXPORT int dp_abi3_module_call_long(
    PyObject *module,
    const char *method,
    int has_argument,
    int64_t argument,
    int64_t *result
);
/*
 * Generic qualified-object surface. Every PyObject output is a new reference owned by the caller
 * and must be released with dp_abi3_object_release on the owner thread. Input object arrays contain
 * borrowed references. Text output uses temporary owner-thread storage and an explicit byte count.
 */
DP_ABI3_EXPORT int
dp_abi3_module_attribute_names(PyObject *module, const char **result_json);
DP_ABI3_EXPORT int
dp_abi3_object_get_attr(PyObject *object, const char *name, PyObject **result);
DP_ABI3_EXPORT int dp_abi3_object_call(
    PyObject *callable,
    PyObject *const *arguments,
    int64_t argument_count,
    PyObject **result
);
DP_ABI3_EXPORT int dp_abi3_object_from_utf8(
    const char *value,
    int64_t value_length,
    PyObject **result
);
DP_ABI3_EXPORT int dp_abi3_object_from_int64(int64_t value, PyObject **result);
DP_ABI3_EXPORT int dp_abi3_object_from_bool(int value, PyObject **result);
DP_ABI3_EXPORT int dp_abi3_object_from_none(PyObject **result);
DP_ABI3_EXPORT int dp_abi3_object_sequence(
    int kind,
    PyObject *const *items,
    int64_t item_count,
    PyObject **result
);
DP_ABI3_EXPORT int dp_abi3_object_kind_of(PyObject *object, int *kind);
DP_ABI3_EXPORT int dp_abi3_object_as_int64(PyObject *object, int64_t *result);
DP_ABI3_EXPORT int dp_abi3_object_as_bool(PyObject *object, int *result);
DP_ABI3_EXPORT int dp_abi3_object_as_utf8(
    PyObject *object,
    const char **result,
    int64_t *result_length
);
DP_ABI3_EXPORT int dp_abi3_object_string(
    PyObject *object,
    const char **result,
    int64_t *result_length
);
DP_ABI3_EXPORT int dp_abi3_object_size(PyObject *object, int64_t *result);
DP_ABI3_EXPORT int
dp_abi3_object_get_item(PyObject *object, PyObject *key, PyObject **result);
DP_ABI3_EXPORT void dp_abi3_object_release(PyObject *object);
/* Anyver scalar outputs are copied values. result_json uses the temporary storage described above.
 */
DP_ABI3_EXPORT int dp_abi3_anyver_compare(
    PyObject *module,
    const char *left,
    const char *right,
    const char *ecosystem,
    int64_t *result
);
DP_ABI3_EXPORT int dp_abi3_anyver_sort_versions(
    PyObject *module,
    const char *const *versions,
    int64_t version_count,
    const char *ecosystem,
    const char **result_json
);
DP_ABI3_EXPORT int dp_abi3_anyver_version_to_json(
    PyObject *module,
    const char *version,
    const char *ecosystem,
    const char **result_json
);
/*
 * Releases module when non-NULL and clears the owner thread's native error indicator. Passing
 * NULL performs only the error-state cleanup, including release of all owned error references;
 * it is the supported operation for discarding an error without a module handle.
 */
DP_ABI3_EXPORT void dp_abi3_module_destroy(PyObject *module);
DP_ABI3_EXPORT const char *dp_abi3_error_type(void);
DP_ABI3_EXPORT const char *dp_abi3_error_message(void);
DP_ABI3_EXPORT int64_t dp_abi3_active_object_count(void);

#ifdef __cplusplus
}
#endif

#endif
