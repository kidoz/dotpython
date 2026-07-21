#ifndef DOTPYTHON_BRIDGE_H
#define DOTPYTHON_BRIDGE_H

#include "dotpython_abi3.h"

#ifdef __cplusplus
extern "C" {
#endif

#define DP_ABI3_BRIDGE_VERSION 1

DP_ABI3_EXPORT int dp_abi3_bridge_version(void);
DP_ABI3_EXPORT int dp_abi3_module_initialize(
    PyObject *initialization_result,
    PyObject **module,
    int *multi_phase
);
DP_ABI3_EXPORT int dp_abi3_module_get_int(
    PyObject *module,
    const char *name,
    int64_t *value
);
DP_ABI3_EXPORT int dp_abi3_module_call_long(
    PyObject *module,
    const char *method,
    int has_argument,
    int64_t argument,
    int64_t *result
);
DP_ABI3_EXPORT void dp_abi3_module_destroy(PyObject *module);
DP_ABI3_EXPORT const char *dp_abi3_error_type(void);
DP_ABI3_EXPORT const char *dp_abi3_error_message(void);
DP_ABI3_EXPORT int64_t dp_abi3_active_object_count(void);

#ifdef __cplusplus
}
#endif

#endif
