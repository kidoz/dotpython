#ifndef DOTPYTHON_BRIDGE_H
#define DOTPYTHON_BRIDGE_H

#include "dotpython_abi3.h"

#ifdef __cplusplus
extern "C" {
#endif

#define DP_ABI3_BRIDGE_VERSION 2

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
DP_ABI3_EXPORT void dp_abi3_module_destroy(PyObject *module);
DP_ABI3_EXPORT const char *dp_abi3_error_type(void);
DP_ABI3_EXPORT const char *dp_abi3_error_message(void);
DP_ABI3_EXPORT int64_t dp_abi3_active_object_count(void);

#ifdef __cplusplus
}
#endif

#endif
