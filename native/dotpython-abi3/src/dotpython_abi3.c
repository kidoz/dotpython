#define DOTPYTHON_ABI3_BUILD
#include "dotpython_bridge.h"

#include <limits.h>
#include <stdlib.h>
#include <string.h>

enum dp_object_kind {
    DP_OBJECT_EXCEPTION_TYPE = 1,
    DP_OBJECT_LONG = 2,
    DP_OBJECT_MODULE = 3
};

typedef struct dp_exception_type {
    PyObject base;
    int kind;
    const char *name;
} dp_exception_type;

typedef struct dp_long {
    PyObject base;
    int kind;
    long value;
} dp_long;

typedef struct dp_module {
    PyObject base;
    int kind;
    PyModuleDef *definition;
    int has_ready;
    long ready;
} dp_module;

static dp_exception_type dp_value_error = {
    PyObject_HEAD_INIT(NULL),
    DP_OBJECT_EXCEPTION_TYPE,
    "ValueError"
};
static dp_exception_type dp_type_error = {
    PyObject_HEAD_INIT(NULL),
    DP_OBJECT_EXCEPTION_TYPE,
    "TypeError"
};
DP_ABI3_EXPORT PyObject *PyExc_ValueError = (PyObject *)&dp_value_error;

static _Thread_local PyModuleDef *dp_last_definition;
static _Thread_local PyObject *dp_error_type;
static _Thread_local char dp_error_text[512];
static int64_t dp_active_objects;

static void dp_clear_error(void) {
    dp_error_type = NULL;
    dp_error_text[0] = '\0';
}

static void dp_set_error(PyObject *type, const char *message) {
    dp_error_type = type;
    if (message == NULL) {
        dp_error_text[0] = '\0';
        return;
    }

    size_t length = strlen(message);
    if (length >= sizeof(dp_error_text)) {
        length = sizeof(dp_error_text) - 1;
    }

    memcpy(dp_error_text, message, length);
    dp_error_text[length] = '\0';
}

static int dp_kind(PyObject *value) {
    if (value == NULL) {
        return 0;
    }

    return *((int *)((unsigned char *)value + sizeof(PyObject)));
}

static void dp_release(PyObject *value) {
    int kind = dp_kind(value);
    if (kind == DP_OBJECT_LONG || kind == DP_OBJECT_MODULE) {
        free(value);
        dp_active_objects--;
    }
}

DP_ABI3_EXPORT PyObject *PyModuleDef_Init(PyModuleDef *definition) {
    if (definition == NULL || definition->m_name == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module definition is invalid");
        return NULL;
    }

    definition->m_base.ob_base.ob_refcnt = 1;
    definition->m_base.ob_base.ob_type = NULL;
    dp_last_definition = definition;
    return (PyObject *)definition;
}

DP_ABI3_EXPORT int PyModule_AddIntConstant(PyObject *module, const char *name, long value) {
    if (dp_kind(module) != DP_OBJECT_MODULE || name == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module constant target is invalid");
        return -1;
    }

    if (strcmp(name, "fixture_ready") != 0) {
        dp_set_error((PyObject *)&dp_type_error, "module constant is not allowlisted");
        return -1;
    }

    dp_module *typed_module = (dp_module *)module;
    typed_module->has_ready = 1;
    typed_module->ready = value;
    return 0;
}

DP_ABI3_EXPORT PyObject *PyLong_FromLong(long value) {
    dp_long *result = (dp_long *)calloc(1, sizeof(dp_long));
    if (result == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "native object allocation failed");
        return NULL;
    }

    result->base.ob_refcnt = 1;
    result->kind = DP_OBJECT_LONG;
    result->value = value;
    dp_active_objects++;
    return (PyObject *)result;
}

DP_ABI3_EXPORT long PyLong_AsLong(PyObject *value) {
    if (dp_kind(value) != DP_OBJECT_LONG) {
        dp_set_error((PyObject *)&dp_type_error, "expected a native whole number");
        return -1;
    }

    return ((dp_long *)value)->value;
}

DP_ABI3_EXPORT void PyErr_SetString(PyObject *exception, const char *message) {
    dp_set_error(exception, message);
}

DP_ABI3_EXPORT PyObject *PyErr_Occurred(void) {
    return dp_error_type;
}

DP_ABI3_EXPORT int dp_abi3_bridge_version(void) {
    return DP_ABI3_BRIDGE_VERSION;
}

DP_ABI3_EXPORT int dp_abi3_module_initialize(
    PyObject *initialization_result,
    PyObject **module,
    int *multi_phase
) {
    if (module == NULL || multi_phase == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module initialization outputs are invalid");
        return -1;
    }

    *module = NULL;
    *multi_phase = 0;
    if (initialization_result == NULL || (PyModuleDef *)initialization_result != dp_last_definition) {
        if (dp_error_type == NULL) {
            dp_set_error((PyObject *)&dp_type_error, "module initializer returned an invalid definition");
        }
        return -1;
    }

    dp_module *created = (dp_module *)calloc(1, sizeof(dp_module));
    if (created == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module allocation failed");
        return -1;
    }

    created->base.ob_refcnt = 1;
    created->kind = DP_OBJECT_MODULE;
    created->definition = dp_last_definition;
    dp_active_objects++;
    dp_clear_error();

    if (created->definition->m_slots != NULL) {
        for (PyModuleDef_Slot *slot = created->definition->m_slots; slot->slot != 0; slot++) {
            if (slot->slot != Py_mod_exec || slot->value == NULL) {
                dp_set_error((PyObject *)&dp_type_error, "module slot is not supported");
                dp_release((PyObject *)created);
                return -1;
            }

            int (*execute)(PyObject *) = (int (*)(PyObject *))slot->value;
            if (execute((PyObject *)created) != 0) {
                if (dp_error_type == NULL) {
                    dp_set_error((PyObject *)&dp_type_error, "module execution slot failed");
                }
                dp_release((PyObject *)created);
                return -1;
            }
        }
    }

    *module = (PyObject *)created;
    *multi_phase = 1;
    return 0;
}

DP_ABI3_EXPORT int dp_abi3_module_get_int(
    PyObject *module,
    const char *name,
    int64_t *value
) {
    if (dp_kind(module) != DP_OBJECT_MODULE || name == NULL || value == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module query is invalid");
        return -1;
    }

    dp_module *typed_module = (dp_module *)module;
    if (strcmp(name, "fixture_ready") != 0 || !typed_module->has_ready) {
        dp_set_error((PyObject *)&dp_type_error, "module constant was not initialized");
        return -1;
    }

    *value = typed_module->ready;
    return 0;
}

DP_ABI3_EXPORT int dp_abi3_module_call_long(
    PyObject *module,
    const char *method,
    int has_argument,
    int64_t argument,
    int64_t *result
) {
    if (dp_kind(module) != DP_OBJECT_MODULE || method == NULL || result == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module call is invalid");
        return -1;
    }

    dp_module *typed_module = (dp_module *)module;
    PyMethodDef *definition = typed_module->definition->m_methods;
    while (definition != NULL && definition->ml_name != NULL) {
        if (strcmp(definition->ml_name, method) == 0) {
            break;
        }
        definition++;
    }

    if (definition == NULL || definition->ml_name == NULL) {
        dp_set_error((PyObject *)&dp_type_error, "module method is not allowlisted");
        return -1;
    }

    PyObject *argument_object = NULL;
    if (definition->ml_flags == METH_O) {
        if (!has_argument) {
            dp_set_error((PyObject *)&dp_type_error, "module method requires one argument");
            return -1;
        }
        if (argument < LONG_MIN || argument > LONG_MAX) {
            dp_set_error((PyObject *)&dp_type_error, "whole number is outside the Stable-ABI long range");
            return -1;
        }
        argument_object = PyLong_FromLong((long)argument);
        if (argument_object == NULL) {
            return -1;
        }
    } else if (definition->ml_flags != METH_NOARGS || has_argument) {
        dp_set_error((PyObject *)&dp_type_error, "module method arguments are invalid");
        return -1;
    }

    dp_clear_error();
    PyObject *return_value = definition->ml_meth(module, argument_object);
    dp_release(argument_object);
    if (return_value == NULL) {
        if (dp_error_type == NULL) {
            dp_set_error((PyObject *)&dp_type_error, "module method returned NULL without an error");
        }
        return -1;
    }

    if (dp_kind(return_value) != DP_OBJECT_LONG) {
        dp_release(return_value);
        dp_set_error((PyObject *)&dp_type_error, "module method returned an unsupported object");
        return -1;
    }

    *result = ((dp_long *)return_value)->value;
    dp_release(return_value);
    return 0;
}

DP_ABI3_EXPORT void dp_abi3_module_destroy(PyObject *module) {
    if (dp_kind(module) != DP_OBJECT_MODULE) {
        return;
    }

    dp_module *typed_module = (dp_module *)module;
    if (typed_module->definition->m_free != NULL) {
        typed_module->definition->m_free(module);
    }
    dp_release(module);
}

DP_ABI3_EXPORT const char *dp_abi3_error_type(void) {
    if (dp_error_type == NULL || dp_kind(dp_error_type) != DP_OBJECT_EXCEPTION_TYPE) {
        return "RuntimeError";
    }

    return ((dp_exception_type *)dp_error_type)->name;
}

DP_ABI3_EXPORT const char *dp_abi3_error_message(void) {
    return dp_error_text;
}

DP_ABI3_EXPORT int64_t dp_abi3_active_object_count(void) {
    return dp_active_objects;
}
