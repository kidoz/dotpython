#include "dotpython_bridge.h"

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef PyObject *(*module_init_function)(void);
typedef int (*cleanup_count_function)(void);

static void require(int condition, const char *message) {
    if (!condition) {
        fprintf(stderr, "%s\n", message);
        exit(1);
    }
}

int main(int argc, char **argv) {
    require(argc == 2, "fixture path is required");
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(library != NULL, dlerror());

    module_init_function initialize = (module_init_function)dlsym(
        library,
        "PyInit_dotpython_fixture"
    );
    cleanup_count_function cleanup_count = (cleanup_count_function)dlsym(
        library,
        "dotpython_fixture_cleanup_count"
    );
    require(initialize != NULL, "fixture initializer is missing");
    require(cleanup_count != NULL, "fixture cleanup probe is missing");
    require(dp_abi3_bridge_version() == DP_ABI3_BRIDGE_VERSION, "bridge version mismatch");

    PyObject *module = NULL;
    int multi_phase = 0;
    require(
        dp_abi3_module_initialize(initialize(), &module, &multi_phase) == 0,
        dp_abi3_error_message()
    );
    require(multi_phase == 1, "fixture did not use multi-phase initialization");

    int64_t value = 0;
    require(
        dp_abi3_module_get_int(module, "fixture_ready", &value) == 0 && value == 1,
        "fixture execution slot did not run"
    );
    require(
        dp_abi3_module_call_long(module, "increment", 1, 41, &value) == 0 && value == 42,
        "fixture scalar call failed"
    );
    require(
        dp_abi3_module_call_long(module, "fail", 0, 0, &value) == -1,
        "fixture failure call unexpectedly succeeded"
    );
    require(strcmp(dp_abi3_error_type(), "ValueError") == 0, "fixture error type mismatch");
    require(
        strcmp(dp_abi3_error_message(), "fixture failure") == 0,
        "fixture error message mismatch"
    );

    dp_abi3_module_destroy(module);
    require(cleanup_count() == 1, "fixture cleanup did not run exactly once");
    require(dp_abi3_active_object_count() == 0, "native objects leaked");
    require(dlclose(library) == 0, "fixture library did not close");
    return 0;
}
