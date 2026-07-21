#include "dotpython_bridge.h"

#include <catch2/catch_session.hpp>
#include <catch2/catch_test_macros.hpp>

#include <dlfcn.h>

#include <algorithm>
#include <array>
#include <bit>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <thread>

namespace {

using ModuleInitFunction = PyObject *(*)();
using CleanupCountFunction = int (*)();

const char *success_fixture_path;
const char *failure_fixture_path;

class DynamicLibrary {
  public:
    explicit DynamicLibrary(const char *path) {
        static_cast<void>(dlerror());
        handle_ = dlopen(path, RTLD_NOW | RTLD_LOCAL);
        const char *error = dlerror();
        INFO((error == nullptr ? "" : error));
        REQUIRE(error == nullptr);
        REQUIRE(handle_ != nullptr);
    }

    DynamicLibrary(const DynamicLibrary &) = delete;
    DynamicLibrary &operator=(const DynamicLibrary &) = delete;

    ~DynamicLibrary() {
        if (handle_ != nullptr) {
            static_cast<void>(dlclose(handle_));
        }
    }

    template <typename Function> [[nodiscard]] Function symbol(const char *name) const {
        static_cast<void>(dlerror());
        void *address = dlsym(handle_, name);
        const char *error = dlerror();
        INFO((error == nullptr ? "" : error));
        REQUIRE(error == nullptr);
        REQUIRE(address != nullptr);
        return reinterpret_cast<Function>(address);
    }

    int close() {
        const int result = dlclose(handle_);
        handle_ = nullptr;
        return result;
    }

  private:
    void *handle_ = nullptr;
};

class ErrorStateGuard {
  public:
    ErrorStateGuard() = default;
    ErrorStateGuard(const ErrorStateGuard &) = delete;
    ErrorStateGuard &operator=(const ErrorStateGuard &) = delete;

    ~ErrorStateGuard() {
        dp_abi3_module_destroy(nullptr);
    }
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

struct PyObjectDeleter {
    void operator()(PyObject *object) const {
        _Py_DecRef(object);
    }
};

using OwnedPyObject = std::unique_ptr<PyObject, PyObjectDeleter>;

void require_object_text(PyObject *object, const char *expected) {
    OwnedPyObject text(PyObject_Str(object));
    INFO(dp_abi3_error_message());
    REQUIRE(text != nullptr);
    const char *actual = PyUnicode_AsUTF8AndSize(text.get(), nullptr);
    REQUIRE(actual != nullptr);
    REQUIRE(std::strcmp(actual, expected) == 0);
}

PyObject *fastcall_probe(PyObject *self, PyObject *const *args, Py_ssize_t count) {
    static_cast<void>(self);
    static_cast<void>(args);
    static_cast<void>(count);
    return PyLong_FromLong(7);
}

} // namespace

TEST_CASE("Stable-ABI fixture lifecycle and failure handling", "[fixture][lifecycle][failure]") {
    const ErrorStateGuard error_state;
    DynamicLibrary library(success_fixture_path);
    const auto initialize = library.symbol<ModuleInitFunction>("PyInit_dotpython_fixture");
    const auto cleanup_count =
        library.symbol<CleanupCountFunction>("dotpython_fixture_cleanup_count");

    REQUIRE(dp_abi3_bridge_version() == DP_ABI3_BRIDGE_VERSION);

    ModuleHandle module;
    int multi_phase = 0;
    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_module_initialize(initialize(), module.address(), &multi_phase) == 0);
    REQUIRE(multi_phase == 1);

    int64_t value = 0;
    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_module_get_int(module.get(), "fixture_ready", &value) == 0);
    REQUIRE(value == 1);

    INFO(dp_abi3_error_message());
    REQUIRE(dp_abi3_module_call_long(module.get(), "increment", 1, 41, &value) == 0);
    REQUIRE(value == 42);

    REQUIRE(dp_abi3_module_call_long(module.get(), "fail", 0, 0, &value) == -1);
    REQUIRE(std::strcmp(dp_abi3_error_type(), "ValueError") == 0);
    REQUIRE(std::strcmp(dp_abi3_error_message(), "fixture failure") == 0);

    module.reset();
    REQUIRE(cleanup_count() == 1);
    REQUIRE(dp_abi3_active_object_count() == 0);
    REQUIRE(library.close() == 0);

    DynamicLibrary failure_library(failure_fixture_path);
    const auto failure_initialize =
        failure_library.symbol<ModuleInitFunction>("PyInit_dotpython_fixture");
    const auto failure_cleanup_count =
        failure_library.symbol<CleanupCountFunction>("dotpython_fixture_cleanup_count");

    multi_phase = 0;
    REQUIRE(dp_abi3_module_initialize(failure_initialize(), module.address(), &multi_phase) == -1);
    REQUIRE(module.get() == nullptr);
    REQUIRE(multi_phase == 0);
    REQUIRE(std::strcmp(dp_abi3_error_type(), "ValueError") == 0);
    REQUIRE(std::strcmp(dp_abi3_error_message(), "fixture initialization failure") == 0);
    REQUIRE(failure_cleanup_count() == 0);
    REQUIRE(dp_abi3_active_object_count() == 1);

    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == 0);
    REQUIRE(failure_library.close() == 0);
}

TEST_CASE("Borrowed aliases survive dictionary replacement", "[bridge][ownership]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject dictionary(PyDict_New());
    OwnedPyObject key(PyUnicode_FromStringAndSize("key", 3));
    OwnedPyObject value(PyUnicode_FromStringAndSize("value", 5));
    REQUIRE(dictionary != nullptr);
    REQUIRE(key != nullptr);
    REQUIRE(value != nullptr);
    REQUIRE(PyDict_SetItem(dictionary.get(), key.get(), value.get()) == 0);

    value.reset();
    PyObject *borrowed = PyDict_GetItemWithError(dictionary.get(), key.get());
    REQUIRE(borrowed != nullptr);
    REQUIRE(PyDict_SetItem(dictionary.get(), key.get(), borrowed) == 0);
    require_object_text(borrowed, "value");

    key.reset();
    dictionary.reset();
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Borrowed aliases survive generic dictionary replacement", "[bridge][ownership]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject owner(PyList_New(0));
    OwnedPyObject dictionary(PyDict_New());
    REQUIRE(owner != nullptr);
    REQUIRE(dictionary != nullptr);
    REQUIRE(PyObject_GenericSetDict(owner.get(), dictionary.get(), nullptr) == 0);

    PyObject *borrowed = dictionary.get();
    dictionary.reset();
    REQUIRE(PyObject_GenericSetDict(owner.get(), borrowed, nullptr) == 0);
    OwnedPyObject observed(PyObject_GenericGetDict(owner.get(), nullptr));
    REQUIRE(observed.get() == borrowed);

    observed.reset();
    owner.reset();
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Synthetic imports own all retained metadata", "[bridge][import][ownership]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject name(PyUnicode_FromStringAndSize("synthetic", 9));
    REQUIRE(name != nullptr);
    OwnedPyObject module(PyImport_Import(name.get()));
    INFO(dp_abi3_error_message());
    REQUIRE(module != nullptr);
    OwnedPyObject imported_name(PyModule_GetNameObject(module.get()));
    require_object_text(imported_name.get(), "synthetic");

    imported_name.reset();
    module.reset();
    name.reset();
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Allocation-limit errors do not recursively allocate", "[bridge][allocation][failure]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject value(PyUnicode_FromStringAndSize("x", std::numeric_limits<Py_ssize_t>::max()));
    REQUIRE(value == nullptr);
    REQUIRE(std::strcmp(dp_abi3_error_type(), "RuntimeError") == 0);
    REQUIRE(std::strcmp(dp_abi3_error_message(), "Unicode allocation failed") == 0);

    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Tuple setters consume new references on failure", "[bridge][ownership][failure]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject tuple(PyTuple_New(1));
    OwnedPyObject value(PyUnicode_FromStringAndSize("value", 5));
    REQUIRE(tuple != nullptr);
    REQUIRE(value != nullptr);
    REQUIRE(PyTuple_SetItem(tuple.get(), 1, value.release()) == -1);
    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == baseline + 1);
    tuple.reset();
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Methods without METH_KEYWORDS reject keyword arguments", "[bridge][call][failure]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    static PyMethodDef definition =
        // PyMethodDef stores every calling convention as PyCFunction; ml_flags selects the cast.
        // NOLINTNEXTLINE(bugprone-bitwise-pointer-cast)
        {"probe", std::bit_cast<PyCFunction>(&fastcall_probe), METH_FASTCALL, nullptr};
    OwnedPyObject method(PyCMethod_New(&definition, nullptr, nullptr, nullptr));
    INFO(dp_abi3_error_message());
    REQUIRE(method != nullptr);

    OwnedPyObject args(PyTuple_New(0));
    REQUIRE(args != nullptr);
    OwnedPyObject result(PyObject_Call(method.get(), args.get(), nullptr));
    INFO(dp_abi3_error_message());
    REQUIRE(result != nullptr);
    REQUIRE(PyLong_AsLong(result.get()) == 7);
    result.reset();

    OwnedPyObject kwargs(PyDict_New());
    OwnedPyObject key(PyUnicode_FromStringAndSize("flag", 4));
    OwnedPyObject value(PyUnicode_FromStringAndSize("on", 2));
    REQUIRE(kwargs != nullptr);
    REQUIRE(key != nullptr);
    REQUIRE(value != nullptr);
    REQUIRE(PyDict_SetItem(kwargs.get(), key.get(), value.get()) == 0);
    result.reset(PyObject_Call(method.get(), args.get(), kwargs.get()));
    REQUIRE(result == nullptr);
    REQUIRE(std::strcmp(dp_abi3_error_type(), "TypeError") == 0);
    REQUIRE(std::strcmp(dp_abi3_error_message(), "method does not accept keyword arguments") == 0);

    value.reset();
    key.reset();
    kwargs.reset();
    args.reset();
    method.reset();
    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Integer dictionary keys distinguish signedness", "[bridge][dict]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();

    OwnedPyObject dictionary(PyDict_New());
    OwnedPyObject negative(PyLong_FromLong(-1));
    OwnedPyObject wrapped(PyLong_FromUnsignedLongLong(ULLONG_MAX));
    OwnedPyObject small_signed(PyLong_FromLong(5));
    OwnedPyObject small_unsigned(PyLong_FromUnsignedLongLong(5));
    OwnedPyObject value(PyUnicode_FromStringAndSize("negative", 8));
    REQUIRE(dictionary != nullptr);
    REQUIRE(negative != nullptr);
    REQUIRE(wrapped != nullptr);
    REQUIRE(small_signed != nullptr);
    REQUIRE(small_unsigned != nullptr);
    REQUIRE(value != nullptr);

    REQUIRE(PyDict_SetItem(dictionary.get(), negative.get(), value.get()) == 0);
    REQUIRE(PyDict_GetItemWithError(dictionary.get(), wrapped.get()) == nullptr);
    REQUIRE(PyDict_GetItemWithError(dictionary.get(), negative.get()) == value.get());

    REQUIRE(PyDict_SetItem(dictionary.get(), small_signed.get(), value.get()) == 0);
    REQUIRE(PyDict_GetItemWithError(dictionary.get(), small_unsigned.get()) == value.get());

    value.reset();
    small_unsigned.reset();
    small_signed.reset();
    wrapped.reset();
    negative.reset();
    dictionary.reset();
    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Stateful ABI access is confined to its owner thread", "[bridge][threading]") {
    const ErrorStateGuard error_state;
    const int64_t baseline = dp_abi3_active_object_count();
    int64_t foreign_count = 0;
    bool error_was_reported = false;

    std::thread foreign([&foreign_count, &error_was_reported] {
        foreign_count = dp_abi3_active_object_count();
        error_was_reported = std::strcmp(
                                 dp_abi3_error_message(),
                                 "native ABI access must execute on its owner thread"
                             ) == 0;
    });
    foreign.join();

    REQUIRE(foreign_count == -1);
    REQUIRE(error_was_reported);
    REQUIRE(dp_abi3_active_object_count() == baseline);
}

TEST_CASE("Bridge text operations are bounded", "[bridge][text]") {
    const ErrorStateGuard error_state;

    OwnedPyObject signed_number(PyLong_FromLong(-42));
    REQUIRE(signed_number != nullptr);
    require_object_text(signed_number.get(), "-42");
    signed_number.reset();

    OwnedPyObject unsigned_number(PyLong_FromUnsignedLongLong(ULLONG_MAX));
    REQUIRE(unsigned_number != nullptr);
    require_object_text(unsigned_number.get(), "18446744073709551615");
    unsigned_number.reset();

    OwnedPyObject value(PyUnicode_FromStringAndSize("checked copy", 12));
    REQUIRE(value != nullptr);
    OwnedPyObject representation(PyObject_Repr(value.get()));
    INFO(dp_abi3_error_message());
    REQUIRE(representation != nullptr);
    const char *actual = PyUnicode_AsUTF8AndSize(representation.get(), nullptr);
    REQUIRE(actual != nullptr);
    REQUIRE(std::strcmp(actual, "'checked copy'") == 0);
    representation.reset();
    value.reset();

    std::array<char, 2048> long_message{};
    std::fill(long_message.begin(), long_message.end() - 1, 'x');
    PyErr_SetString(PyExc_ValueError, long_message.data());
    REQUIRE(std::strlen(dp_abi3_error_message()) == 1023U);

    dp_abi3_module_destroy(nullptr);
    REQUIRE(dp_abi3_active_object_count() == 0);
}

int main(int argc, char **argv) noexcept {
    try {
        if (argc < 3) {
            std::fputs("success and failure fixture paths are required\n", stderr);
            return 2;
        }

        success_fixture_path = argv[1];
        failure_fixture_path = argv[2];
        argv[2] = argv[0];
        return Catch::Session().run(argc - 2, argv + 2);
    } catch (const std::exception &error) {
        std::fputs(error.what(), stderr);
        std::fputc('\n', stderr);
        return 2;
    } catch (...) {
        std::fputs("unknown native fixture test failure\n", stderr);
        return 2;
    }
}
