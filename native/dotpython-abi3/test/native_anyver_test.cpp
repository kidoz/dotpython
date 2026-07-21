#include "dotpython_bridge.h"

#include <catch2/catch_session.hpp>
#include <catch2/catch_test_macros.hpp>

#include <dlfcn.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <exception>
#include <memory>
#include <string_view>

namespace {

using ModuleInitFunction = PyObject *(*)();

const char *anyver_module_path;

struct PyObjectDeleter {
    void operator()(PyObject *object) const {
        _Py_DecRef(object);
    }
};

using OwnedPyObject = std::unique_ptr<PyObject, PyObjectDeleter>;

class PinnedDynamicLibrary {
  public:
    explicit PinnedDynamicLibrary(const char *path) {
        static_cast<void>(dlerror());
        handle_ = dlopen(path, RTLD_NOW | RTLD_LOCAL);
        const char *error = dlerror();
        INFO((error == nullptr ? "" : error));
        REQUIRE(error == nullptr);
        REQUIRE(handle_ != nullptr);
    }

    PinnedDynamicLibrary(const PinnedDynamicLibrary &) = delete;
    PinnedDynamicLibrary &operator=(const PinnedDynamicLibrary &) = delete;

    template <typename Function> [[nodiscard]] Function symbol(const char *name) const {
        static_cast<void>(dlerror());
        void *address = dlsym(handle_, name);
        const char *error = dlerror();
        INFO((error == nullptr ? "" : error));
        REQUIRE(error == nullptr);
        REQUIRE(address != nullptr);
        return reinterpret_cast<Function>(address);
    }

    // PyO3 keeps heap-type and intern caches alive until worker process exit.
    ~PinnedDynamicLibrary() = default;

  private:
    void *handle_ = nullptr;
};

class ModuleHandle {
  public:
    ModuleHandle() = default;
    ModuleHandle(const ModuleHandle &) = delete;
    ModuleHandle &operator=(const ModuleHandle &) = delete;

    ~ModuleHandle() {
        reset();
    }

    [[nodiscard]] PyObject **address() {
        return &module_;
    }
    [[nodiscard]] PyObject *get() const {
        return module_;
    }

    void reset(PyObject *module = nullptr) {
        if (module_ != nullptr) {
            dp_abi3_module_destroy(module_);
        }
        module_ = module;
    }

  private:
    PyObject *module_ = nullptr;
};

[[nodiscard]] OwnedPyObject unicode(std::string_view text) {
    OwnedPyObject value(
        PyUnicode_FromStringAndSize(text.data(), static_cast<Py_ssize_t>(text.size()))
    );
    INFO(dp_abi3_error_message());
    REQUIRE(value != nullptr);
    return value;
}

[[nodiscard]] OwnedPyObject call(PyObject *owner, std::string_view name, PyObject *args) {
    OwnedPyObject attribute_name = unicode(name);
    OwnedPyObject callable(PyObject_GetAttr(owner, attribute_name.get()));
    INFO(dp_abi3_error_message());
    REQUIRE(callable != nullptr);
    OwnedPyObject result(PyObject_Call(callable.get(), args, nullptr));
    INFO(dp_abi3_error_message());
    REQUIRE(result != nullptr);
    return result;
}

void tuple_set_text(PyObject *tuple, Py_ssize_t index, std::string_view text) {
    OwnedPyObject value = unicode(text);
    REQUIRE(PyTuple_SetItem(tuple, index, value.release()) == 0);
}

} // namespace

TEST_CASE("Pinned Anyver module satisfies the native ABI contract", "[anyver][abi3]") {
    REQUIRE(dp_abi3_bridge_version() == DP_ABI3_BRIDGE_VERSION);

    const PinnedDynamicLibrary library(anyver_module_path);
    const auto initialize = library.symbol<ModuleInitFunction>("PyInit__anyver");

    ModuleHandle module;
    int multi_phase = 0;
    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_module_initialize(initialize(), module.address(), &multi_phase) == 0);
    REQUIRE(multi_phase == 1);
    const int64_t initialized_object_count = dp_abi3_active_object_count();

    int64_t bridge_comparison = 0;
    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_anyver_compare(module.get(), "1.0", "2.0", "generic", &bridge_comparison) == 0);
    REQUIRE(bridge_comparison == -1);

    const char *bridge_versions[] = {"2.0", "1.0-alpha", "1.0"};
    const char *bridge_json = nullptr;
    INFO(dp_abi3_error_message());
    REQUIRE(
        dp_abi3_anyver_sort_versions(module.get(), bridge_versions, 3, "generic", &bridge_json) == 0
    );
    REQUIRE(std::strcmp(bridge_json, "[\"1.0-alpha\",\"1.0\",\"2.0\"]") == 0);

    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_anyver_version_to_json(module.get(), "1.2.3", "auto", &bridge_json) == 0);
    REQUIRE(std::strstr(bridge_json, "\"raw\":\"1.2.3\"") != nullptr);
    REQUIRE(std::strstr(bridge_json, "\"major\":1") != nullptr);

    OwnedPyObject compare_args(PyTuple_New(2));
    REQUIRE(compare_args != nullptr);
    tuple_set_text(compare_args.get(), 0, "1.0");
    tuple_set_text(compare_args.get(), 1, "2.0");
    OwnedPyObject comparison = call(module.get(), "compare", compare_args.get());
    REQUIRE(PyLong_AsLong(comparison.get()) == -1);
    REQUIRE(PyErr_Occurred() == nullptr);
    comparison.reset();
    compare_args.reset();

    OwnedPyObject versions(PyList_New(0));
    REQUIRE(versions != nullptr);
    for (const std::string_view version_text : {"2.0", "1.0-alpha", "1.0"}) {
        OwnedPyObject text = unicode(version_text);
        INFO(dp_abi3_error_message());
        REQUIRE(PyList_Append(versions.get(), text.get()) == 0);
    }
    OwnedPyObject sort_args(PyTuple_New(1));
    REQUIRE(sort_args != nullptr);
    REQUIRE(PyTuple_SetItem(sort_args.get(), 0, versions.release()) == 0);
    OwnedPyObject sorted = call(module.get(), "sort_versions", sort_args.get());
    REQUIRE(PyList_Size(sorted.get()) == 3);
    const char *expected[] = {"1.0-alpha", "1.0", "2.0"};
    for (Py_ssize_t index = 0; index < 3; index++) {
        const char *actual = PyUnicode_AsUTF8AndSize(PyList_GetItem(sorted.get(), index), nullptr);
        REQUIRE(actual != nullptr);
        REQUIRE(std::strcmp(actual, expected[index]) == 0);
    }
    sorted.reset();
    sort_args.reset();

    OwnedPyObject version_args(PyTuple_New(1));
    REQUIRE(version_args != nullptr);
    tuple_set_text(version_args.get(), 0, "1.2.3");
    OwnedPyObject version = call(module.get(), "Version", version_args.get());
    version_args.reset();
    OwnedPyObject empty(PyTuple_New(0));
    REQUIRE(empty != nullptr);
    OwnedPyObject dictionary = call(version.get(), "to_dict", empty.get());
    empty.reset();

    OwnedPyObject raw_key = unicode("raw");
    PyObject *raw = PyDict_GetItemWithError(dictionary.get(), raw_key.get());
    REQUIRE(raw != nullptr);
    const char *raw_text = PyUnicode_AsUTF8AndSize(raw, nullptr);
    REQUIRE(raw_text != nullptr);
    REQUIRE(std::strcmp(raw_text, "1.2.3") == 0);

    OwnedPyObject major_key = unicode("major");
    PyObject *major = PyDict_GetItemWithError(dictionary.get(), major_key.get());
    REQUIRE(major != nullptr);
    REQUIRE(PyLong_AsLong(major) == 1);

    major_key.reset();
    raw_key.reset();
    dictionary.reset();
    version.reset();

    module.reset();
    REQUIRE(dp_abi3_active_object_count() <= initialized_object_count);
}

int main(int argc, char **argv) noexcept {
    try {
        if (argc < 2) {
            std::fputs("Anyver native module path is required\n", stderr);
            return 2;
        }

        anyver_module_path = argv[1];
        argv[1] = argv[0];
        return Catch::Session().run(argc - 1, argv + 1);
    } catch (const std::exception &error) {
        std::fputs(error.what(), stderr);
        std::fputc('\n', stderr);
        return 2;
    } catch (...) {
        std::fputs("unknown native Anyver test failure\n", stderr);
        return 2;
    }
}
