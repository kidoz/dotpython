#define DOTPYTHON_ABI3_BUILD
#include "dotpython_bridge.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <charconv>
#include <limits.h>
#include <limits>
#include <new>
#include <string.h>
#include <string_view>
#include <type_traits>

enum dp_object_kind {
    DP_OBJECT_INVALID = 0,
    DP_OBJECT_BYTES = 1,
    DP_OBJECT_CMETHOD = 2,
    DP_OBJECT_DICT = 3,
    DP_OBJECT_INSTANCE = 4,
    DP_OBJECT_ITERATOR = 5,
    DP_OBJECT_LIST = 6,
    DP_OBJECT_LONG = 7,
    DP_OBJECT_MODULE = 8,
    DP_OBJECT_TUPLE = 9,
    DP_OBJECT_TYPE = 10,
    DP_OBJECT_UNICODE = 11
};

typedef struct dp_sequence {
    Py_ssize_t size;
    Py_ssize_t capacity;
    PyObject **items;
} dp_sequence;

typedef struct dp_dictionary {
    Py_ssize_t size;
    Py_ssize_t capacity;
    PyObject **keys;
    PyObject **values;
} dp_dictionary;

typedef struct dp_cmethod {
    PyMethodDef *definition;
    PyObject *self;
    PyObject *module;
    PyTypeObject *class_type;
} dp_cmethod;

typedef struct dp_module {
    PyModuleDef *definition;
    PyObject *attributes;
    char *name;
} dp_module;

typedef struct dp_iterator {
    PyObject *source;
    Py_ssize_t index;
} dp_iterator;

typedef struct dp_text {
    char *data;
    Py_ssize_t size;
} dp_text;

typedef struct dp_number {
    unsigned long long value;
    int is_unsigned;
} dp_number;

typedef struct dp_metadata {
    PyObject *object;
    enum dp_object_kind kind;
    PyObject *attributes;
    union {
        dp_sequence sequence;
        dp_dictionary dictionary;
        dp_cmethod method;
        dp_module module;
        dp_iterator iterator;
        dp_text text;
        dp_number number;
    } value;
    struct dp_metadata *next;
} dp_metadata;

struct _typeobject {
    PyObject base;
    char *name;
    int basicsize;
    int itemsize;
    unsigned long flags;
    PyType_Slot *slots;
    PyMethodDef *methods;
    PyGetSetDef *getsets;
    PyTypeObject *base_type;
    int dynamic;
};

static void dp_generic_free(void *object);
static PyObject *dp_generic_alloc(PyTypeObject *type, Py_ssize_t item_count);
static PyObject *dp_generic_new(PyTypeObject *type, PyObject *args, PyObject *kwargs);
static void dp_release(PyObject *object);
static PyObject *dp_new_unicode(const char *text, Py_ssize_t size);
static void dp_set_error_text(PyObject *type, const char *message);
static PyObject *dp_get_attribute_cstr(PyObject *object, const char *name);

/* Keep Stable-ABI initializer spelling independent of the installed clang-format version. */
// clang-format off
#define DP_STATIC_TYPE(name_literal)                                                               \
    {{DP_ABI3_IMMORTAL_REFCNT, NULL}, (char *)(name_literal), (int)sizeof(PyObject), 0, 0, NULL,   \
     NULL, NULL, NULL, 0}
// clang-format on

DP_ABI3_EXPORT PyTypeObject PyBaseObject_Type = DP_STATIC_TYPE("object");
DP_ABI3_EXPORT PyTypeObject PyDict_Type = DP_STATIC_TYPE("dict");
DP_ABI3_EXPORT PyTypeObject PyList_Type = DP_STATIC_TYPE("list");
DP_ABI3_EXPORT PyTypeObject PyModule_Type = DP_STATIC_TYPE("module");
DP_ABI3_EXPORT PyTypeObject PyTuple_Type = DP_STATIC_TYPE("tuple");
DP_ABI3_EXPORT PyTypeObject PyType_Type = DP_STATIC_TYPE("type");
DP_ABI3_EXPORT PyTypeObject PyUnicode_Type = DP_STATIC_TYPE("str");

static PyTypeObject dp_bytes_type = DP_STATIC_TYPE("bytes");
static PyTypeObject dp_cmethod_type = DP_STATIC_TYPE("builtin_function_or_method");
static PyTypeObject dp_iterator_type = DP_STATIC_TYPE("iterator");
static PyTypeObject dp_long_type = DP_STATIC_TYPE("int");
static PyTypeObject dp_bool_type = DP_STATIC_TYPE("bool");

DP_ABI3_EXPORT PyObject _Py_FalseStruct = {DP_ABI3_IMMORTAL_REFCNT, &dp_bool_type};
DP_ABI3_EXPORT PyObject _Py_NoneStruct = {DP_ABI3_IMMORTAL_REFCNT, &PyBaseObject_Type};
DP_ABI3_EXPORT PyObject _Py_NotImplementedStruct = {DP_ABI3_IMMORTAL_REFCNT, &PyBaseObject_Type};
DP_ABI3_EXPORT PyObject _Py_TrueStruct = {DP_ABI3_IMMORTAL_REFCNT, &dp_bool_type};

static PyTypeObject dp_base_exception_type = DP_STATIC_TYPE("BaseException");
static PyTypeObject dp_attribute_error_type = DP_STATIC_TYPE("AttributeError");
static PyTypeObject dp_index_error_type = DP_STATIC_TYPE("IndexError");
static PyTypeObject dp_runtime_error_type = DP_STATIC_TYPE("RuntimeError");
static PyTypeObject dp_system_error_type = DP_STATIC_TYPE("SystemError");
static PyTypeObject dp_type_error_type = DP_STATIC_TYPE("TypeError");
static PyTypeObject dp_value_error_type = DP_STATIC_TYPE("ValueError");

DP_ABI3_EXPORT PyObject *PyExc_AttributeError = (PyObject *)&dp_attribute_error_type;
DP_ABI3_EXPORT PyObject *PyExc_BaseException = (PyObject *)&dp_base_exception_type;
DP_ABI3_EXPORT PyObject *PyExc_IndexError = (PyObject *)&dp_index_error_type;
DP_ABI3_EXPORT PyObject *PyExc_RuntimeError = (PyObject *)&dp_runtime_error_type;
DP_ABI3_EXPORT PyObject *PyExc_SystemError = (PyObject *)&dp_system_error_type;
DP_ABI3_EXPORT PyObject *PyExc_TypeError = (PyObject *)&dp_type_error_type;
DP_ABI3_EXPORT PyObject *PyExc_ValueError = (PyObject *)&dp_value_error_type;

static thread_local PyModuleDef *dp_last_definition;
static thread_local PyObject *dp_error_type;
static thread_local PyObject *dp_error_value;
static thread_local PyObject *dp_error_traceback;
static thread_local char dp_error_text[1024];
static thread_local char dp_result_text[16384];
static thread_local char dp_current_thread_token;
static thread_local bool dp_setting_error;
static char dp_thread_state_token;
static dp_metadata *dp_objects;
static int64_t dp_active_objects;
static int dp_initialized;
static std::atomic<const void *> dp_owner_thread_token{NULL};
static constexpr size_t dp_max_allocation_size = size_t{64} * 1024U * 1024U;

class dp_owned_ref final {
  public:
    explicit dp_owned_ref(PyObject *object = NULL) noexcept : object_(object) {
    }

    dp_owned_ref(const dp_owned_ref &) = delete;
    dp_owned_ref &operator=(const dp_owned_ref &) = delete;

    dp_owned_ref(dp_owned_ref &&other) noexcept : object_(other.release()) {
    }

    dp_owned_ref &operator=(dp_owned_ref &&other) noexcept {
        if (this != &other) {
            reset(other.release());
        }
        return *this;
    }

    ~dp_owned_ref() noexcept {
        dp_release(object_);
    }

    [[nodiscard]] PyObject *get() const noexcept {
        return object_;
    }

    [[nodiscard]] explicit operator bool() const noexcept {
        return object_ != NULL;
    }

    [[nodiscard]] PyObject *release() noexcept {
        PyObject *object = object_;
        object_ = NULL;
        return object;
    }

    void reset(PyObject *object = NULL) noexcept {
        PyObject *previous = object_;
        object_ = object;
        dp_release(previous);
    }

  private:
    PyObject *object_;
};

template <typename To, typename From> [[nodiscard]] static To dp_pointer_cast(From value) noexcept {
    static_assert(std::is_pointer_v<To>);
    static_assert(std::is_pointer_v<From>);
    static_assert(sizeof(To) == sizeof(From));
    if (value == nullptr) {
        return nullptr;
    }
    // ABI3 and the supported POSIX loaders require equal-sized object/function pointer interop.
    return std::bit_cast<To>(value); // NOLINT(bugprone-bitwise-pointer-cast)
}

[[nodiscard]] static bool
dp_checked_add(Py_ssize_t left, Py_ssize_t right, Py_ssize_t *result) noexcept {
    if (result == NULL || left < 0 || right < 0 ||
        right > std::numeric_limits<Py_ssize_t>::max() - left) {
        return false;
    }
    *result = left + right;
    return true;
}

[[nodiscard]] static bool dp_allocation_fits(Py_ssize_t count, size_t item_size) noexcept {
    return count >= 0 &&
           (item_size == 0U || (size_t)count <= std::numeric_limits<size_t>::max() / item_size);
}

[[nodiscard]] static bool
dp_checked_growth(Py_ssize_t current, Py_ssize_t initial, Py_ssize_t *result) noexcept {
    if (result == NULL || current < 0 || initial <= 0) {
        return false;
    }
    if (current == 0) {
        *result = initial;
        return true;
    }
    return dp_checked_add(current, current, result);
}

template <typename T> [[nodiscard]] static T *dp_new_array(size_t count) noexcept {
    if (count == 0U || count > std::numeric_limits<size_t>::max() / sizeof(T) ||
        count * sizeof(T) > dp_max_allocation_size) {
        return NULL;
    }
    return new (std::nothrow) T[count]{};
}

[[nodiscard]] static void *dp_new_zeroed_storage(size_t size) noexcept {
    if (size > dp_max_allocation_size) {
        return NULL;
    }
    void *storage = ::operator new(size, std::nothrow);
    if (storage != NULL) {
        std::fill_n(static_cast<unsigned char *>(storage), size, (unsigned char)0);
    }
    return storage;
}

template <typename T>
[[nodiscard]] static bool dp_copy_items(
    T *destination,
    size_t destination_count,
    const T *source,
    size_t source_count
) noexcept {
    if (source_count > destination_count ||
        (source_count > 0U && (destination == nullptr || source == nullptr))) {
        return false;
    }
    if (source_count > 0U) {
        std::copy_n(source, source_count, destination);
    }
    return true;
}

static size_t
dp_copy_truncated_text(char *destination, size_t capacity, const char *source) noexcept {
    if (destination == nullptr || capacity == 0U) {
        return 0U;
    }
    const std::string_view text = source == nullptr ? std::string_view{} : source;
    const size_t length = std::min(text.size(), capacity - 1U);
    if (!dp_copy_items(destination, capacity, source, length)) {
        destination[0] = '\0';
        return 0U;
    }
    destination[length] = '\0';
    return length;
}

/*
 * Ownership is claimed by the first calling thread and never released. Thread identity is the
 * address of a thread_local token, which is unique only among live threads, so this check assumes
 * the owner thread lives for the whole process — as the worker scheduler thread does. If an owner
 * thread could exit, a later thread reusing its TLS address would silently pass this check.
 */
[[nodiscard]] static bool dp_require_owner_thread() noexcept {
    const void *current = &dp_current_thread_token;
    const void *expected = NULL;
    if (dp_owner_thread_token.compare_exchange_strong(
            expected,
            current,
            std::memory_order_acq_rel,
            std::memory_order_acquire
        ) ||
        expected == current) {
        return true;
    }

    /* The foreign thread must not touch registry-owned error objects. */
    dp_error_type = PyExc_RuntimeError;
    dp_error_value = NULL;
    dp_error_traceback = NULL;
    (void)dp_copy_truncated_text(
        dp_error_text,
        sizeof(dp_error_text),
        "native ABI access must execute on its owner thread"
    );
    return false;
}

static int
dp_append_text(char *buffer, size_t capacity, size_t *offset, std::string_view text) noexcept {
    if (buffer == nullptr || offset == nullptr || *offset >= capacity ||
        text.size() >= capacity - *offset) {
        return -1;
    }
    if (!dp_copy_items(buffer + *offset, capacity - *offset, text.data(), text.size())) {
        return -1;
    }
    *offset += text.size();
    buffer[*offset] = '\0';
    return 0;
}

template <typename T>
static int dp_append_integer(char *buffer, size_t capacity, size_t *offset, T value) noexcept {
    std::array<char, std::numeric_limits<T>::digits10 + 3> text{};
    const auto conversion = std::to_chars(text.data(), text.data() + text.size(), value);
    if (conversion.ec != std::errc{}) {
        return -1;
    }
    return dp_append_text(
        buffer,
        capacity,
        offset,
        std::string_view(text.data(), (size_t)(conversion.ptr - text.data()))
    );
}

static char *dp_copy_text(const char *text, Py_ssize_t size) {
    if (text == NULL || size < 0) {
        return NULL;
    }

    const size_t text_size = (size_t)size;
    if (text_size == std::numeric_limits<size_t>::max()) {
        return NULL;
    }
    char *copy = dp_new_array<char>(text_size + 1U);
    if (copy == NULL) {
        return NULL;
    }

    if (!dp_copy_items(copy, text_size + 1U, text, text_size)) {
        delete[] copy;
        return NULL;
    }
    copy[text_size] = '\0';
    return copy;
}

static void dp_initialize_type(PyTypeObject *type, PyTypeObject *base) {
    type->base.ob_type = &PyType_Type;
    type->base_type = base;
}

static void dp_initialize(void) {
    if (dp_initialized) {
        return;
    }

    dp_initialize_type(&PyType_Type, &PyBaseObject_Type);
    dp_initialize_type(&PyBaseObject_Type, NULL);
    dp_initialize_type(&PyDict_Type, &PyBaseObject_Type);
    dp_initialize_type(&PyList_Type, &PyBaseObject_Type);
    dp_initialize_type(&PyModule_Type, &PyBaseObject_Type);
    dp_initialize_type(&PyTuple_Type, &PyBaseObject_Type);
    dp_initialize_type(&PyUnicode_Type, &PyBaseObject_Type);
    dp_initialize_type(&dp_bytes_type, &PyBaseObject_Type);
    dp_initialize_type(&dp_cmethod_type, &PyBaseObject_Type);
    dp_initialize_type(&dp_iterator_type, &PyBaseObject_Type);
    dp_initialize_type(&dp_long_type, &PyBaseObject_Type);
    dp_initialize_type(&dp_bool_type, &dp_long_type);
    dp_initialize_type(&dp_base_exception_type, &PyBaseObject_Type);
    dp_initialize_type(&dp_attribute_error_type, &dp_base_exception_type);
    dp_initialize_type(&dp_index_error_type, &dp_base_exception_type);
    dp_initialize_type(&dp_runtime_error_type, &dp_base_exception_type);
    dp_initialize_type(&dp_system_error_type, &dp_base_exception_type);
    dp_initialize_type(&dp_type_error_type, &dp_base_exception_type);
    dp_initialize_type(&dp_value_error_type, &dp_base_exception_type);
    PyType_Type.flags = 1UL << 31;
    PyDict_Type.flags = 1UL << 29;
    PyList_Type.flags = 1UL << 25;
    PyTuple_Type.flags = 1UL << 26;
    PyUnicode_Type.flags = 1UL << 28;
    dp_bytes_type.flags = 1UL << 27;
    dp_long_type.flags = 1UL << 24;
    dp_bool_type.flags = 1UL << 24;
    dp_base_exception_type.flags = 1UL << 30;
    dp_attribute_error_type.flags = 1UL << 30;
    dp_index_error_type.flags = 1UL << 30;
    dp_runtime_error_type.flags = 1UL << 30;
    dp_system_error_type.flags = 1UL << 30;
    dp_type_error_type.flags = 1UL << 30;
    dp_value_error_type.flags = 1UL << 30;
    dp_initialized = 1;
}

static dp_metadata *dp_find(PyObject *object) {
    if (!dp_require_owner_thread()) {
        return NULL;
    }
    for (dp_metadata *entry = dp_objects; entry != NULL; entry = entry->next) {
        if (entry->object == object) {
            return entry;
        }
    }
    return NULL;
}

static dp_metadata *dp_add(PyObject *object, enum dp_object_kind kind) {
    if (dp_active_objects == std::numeric_limits<int64_t>::max()) {
        return NULL;
    }
    dp_metadata *entry = new (std::nothrow) dp_metadata{};
    if (entry == NULL) {
        return NULL;
    }
    entry->object = object;
    entry->kind = kind;
    entry->next = dp_objects;
    dp_objects = entry;
    dp_active_objects++;
    return entry;
}

static dp_metadata *dp_detach(PyObject *object) {
    dp_metadata **link = &dp_objects;
    while (*link != NULL) {
        if ((*link)->object == object) {
            dp_metadata *entry = *link;
            *link = entry->next;
            entry->next = NULL;
            dp_active_objects--;
            return entry;
        }
        link = &(*link)->next;
    }
    return NULL;
}

static PyObject *dp_allocate(enum dp_object_kind kind, PyTypeObject *type, size_t size) {
    if (!dp_require_owner_thread()) {
        return NULL;
    }
    dp_initialize();
    if (size < sizeof(PyObject)) {
        size = sizeof(PyObject);
    }
    PyObject *object = static_cast<PyObject *>(dp_new_zeroed_storage(size));
    if (object == NULL) {
        dp_set_error_text(PyExc_RuntimeError, "native object allocation failed");
        return NULL;
    }
    object->ob_refcnt = 1;
    object->ob_type = type;
    if (dp_add(object, kind) == NULL) {
        ::operator delete(object);
        dp_set_error_text(PyExc_RuntimeError, "native object metadata allocation failed");
        return NULL;
    }
    return object;
}

static int dp_is_static(PyObject *object) {
    return object == NULL || object == &_Py_NoneStruct || object == &_Py_TrueStruct ||
           object == &_Py_FalseStruct || object == &_Py_NotImplementedStruct ||
           object == (PyObject *)&PyType_Type || object == (PyObject *)&PyBaseObject_Type ||
           object == (PyObject *)&PyDict_Type || object == (PyObject *)&PyList_Type ||
           object == (PyObject *)&PyModule_Type || object == (PyObject *)&PyTuple_Type ||
           object == (PyObject *)&PyUnicode_Type || object == (PyObject *)&dp_bytes_type ||
           object == (PyObject *)&dp_cmethod_type || object == (PyObject *)&dp_iterator_type ||
           object == (PyObject *)&dp_long_type || object == (PyObject *)&dp_bool_type ||
           object == PyExc_BaseException || object == PyExc_AttributeError ||
           object == PyExc_IndexError || object == PyExc_RuntimeError ||
           object == PyExc_SystemError || object == PyExc_TypeError || object == PyExc_ValueError;
}

static void dp_clear_error(void) {
    PyObject *type = dp_error_type;
    PyObject *value = dp_error_value;
    PyObject *traceback = dp_error_traceback;
    dp_error_type = NULL;
    dp_error_value = NULL;
    dp_error_traceback = NULL;
    dp_error_text[0] = '\0';
    dp_release(type);
    dp_release(value);
    dp_release(traceback);
}

static void dp_set_error_text(PyObject *type, const char *message) {
    const bool reentrant = dp_setting_error;
    dp_setting_error = true;
    dp_clear_error();
    dp_error_type = Py_NewRef(type == NULL ? PyExc_RuntimeError : type);
    const size_t length = dp_copy_truncated_text(dp_error_text, sizeof(dp_error_text), message);
    if (!reentrant) {
        dp_error_value = dp_new_unicode(dp_error_text, (Py_ssize_t)length);
    }
    dp_setting_error = reentrant;
}

static void *dp_slot(PyTypeObject *type, int slot) {
    if (type == NULL) {
        return NULL;
    }
    if (slot == Py_tp_alloc) {
        return dp_pointer_cast<void *>(&dp_generic_alloc);
    }
    if (slot == Py_tp_free) {
        return dp_pointer_cast<void *>(&dp_generic_free);
    }
    if (type->slots != NULL) {
        for (PyType_Slot *entry = type->slots; entry->slot != 0; entry++) {
            if (entry->slot == slot) {
                return entry->pfunc;
            }
        }
    }
    if (slot == Py_tp_new && type == &PyBaseObject_Type) {
        return dp_pointer_cast<void *>(&dp_generic_new);
    }
    return type->base_type == NULL ? NULL : dp_slot(type->base_type, slot);
}

static void dp_release_sequence(dp_sequence *sequence) {
    if (sequence->items != NULL) {
        for (Py_ssize_t index = 0; index < sequence->size; index++) {
            dp_release(sequence->items[index]);
        }
        delete[] sequence->items;
    }
}

static void dp_release_dictionary(dp_dictionary *dictionary) {
    for (Py_ssize_t index = 0; index < dictionary->size; index++) {
        dp_release(dictionary->keys[index]);
        dp_release(dictionary->values[index]);
    }
    delete[] dictionary->keys;
    delete[] dictionary->values;
}

static void dp_destroy_metadata(dp_metadata *entry) {
    if (entry == NULL) {
        return;
    }
    switch (entry->kind) {
    case DP_OBJECT_INVALID:
        break;
    case DP_OBJECT_BYTES:
    case DP_OBJECT_UNICODE:
        delete[] entry->value.text.data;
        break;
    case DP_OBJECT_CMETHOD:
        dp_release(entry->value.method.self);
        dp_release(entry->value.method.module);
        dp_release((PyObject *)entry->value.method.class_type);
        break;
    case DP_OBJECT_DICT:
        dp_release_dictionary(&entry->value.dictionary);
        break;
    case DP_OBJECT_ITERATOR:
        dp_release(entry->value.iterator.source);
        break;
    case DP_OBJECT_LIST:
    case DP_OBJECT_TUPLE:
        dp_release_sequence(&entry->value.sequence);
        break;
    case DP_OBJECT_MODULE:
        if (entry->value.module.definition != NULL &&
            entry->value.module.definition->m_free != NULL) {
            entry->value.module.definition->m_free(entry->object);
        }
        dp_release(entry->value.module.attributes);
        delete[] entry->value.module.name;
        break;
    case DP_OBJECT_TYPE: {
        PyTypeObject *type = (PyTypeObject *)entry->object;
        dp_release((PyObject *)type->base_type);
        if (type->dynamic) {
            delete[] type->name;
            delete[] type->slots;
        }
        break;
    }
    case DP_OBJECT_INSTANCE:
        dp_release((PyObject *)entry->object->ob_type);
        break;
    case DP_OBJECT_LONG:
        break;
    }
    dp_release(entry->attributes);
    ::operator delete(entry->object);
    delete entry;
}

static void dp_release(PyObject *object) {
    if (object == NULL || dp_is_static(object)) {
        return;
    }
    if (!dp_require_owner_thread()) {
        return;
    }
    if (object->ob_refcnt == std::numeric_limits<Py_ssize_t>::max()) {
        return;
    }
    if (object->ob_refcnt <= 0) {
        return;
    }
    object->ob_refcnt--;
    if (object->ob_refcnt != 0) {
        return;
    }

    dp_metadata *entry = dp_find(object);
    if (entry != NULL && entry->kind == DP_OBJECT_INSTANCE) {
        destructor dealloc = dp_pointer_cast<destructor>(dp_slot(object->ob_type, Py_tp_dealloc));
        if (dealloc != NULL) {
            dealloc(object);
            return;
        }
    }
    dp_destroy_metadata(dp_detach(object));
}

DP_ABI3_EXPORT void _Py_IncRef(PyObject *object) {
    if (object != NULL && !dp_is_static(object) && dp_require_owner_thread()) {
        if (object->ob_refcnt != std::numeric_limits<Py_ssize_t>::max()) {
            object->ob_refcnt++;
        }
    }
}

DP_ABI3_EXPORT void _Py_DecRef(PyObject *object) {
    dp_release(object);
}

DP_ABI3_EXPORT PyObject *Py_NewRef(PyObject *object) {
    _Py_IncRef(object);
    return object;
}

static PyObject *dp_new_unicode(const char *text, Py_ssize_t size) {
    if (text == NULL || size < 0) {
        dp_set_error_text(PyExc_TypeError, "Unicode input is invalid");
        return NULL;
    }
    PyObject *object = dp_allocate(DP_OBJECT_UNICODE, &PyUnicode_Type, sizeof(PyObject));
    if (object == NULL) {
        return NULL;
    }
    dp_metadata *entry = dp_find(object);
    entry->value.text.data = dp_copy_text(text, size);
    entry->value.text.size = size;
    if (entry->value.text.data == NULL) {
        dp_release(object);
        dp_set_error_text(PyExc_RuntimeError, "Unicode allocation failed");
        return NULL;
    }
    return object;
}

DP_ABI3_EXPORT PyObject *PyUnicode_FromStringAndSize(const char *text, Py_ssize_t size) {
    return dp_new_unicode(text, size);
}

DP_ABI3_EXPORT const char *PyUnicode_AsUTF8AndSize(PyObject *unicode, Py_ssize_t *size) {
    dp_metadata *entry = dp_find(unicode);
    if (entry == NULL || entry->kind != DP_OBJECT_UNICODE) {
        dp_set_error_text(PyExc_TypeError, "expected str");
        return NULL;
    }
    if (size != NULL) {
        *size = entry->value.text.size;
    }
    return entry->value.text.data;
}

DP_ABI3_EXPORT PyObject *
PyUnicode_AsEncodedString(PyObject *unicode, const char *encoding, const char *errors) {
    (void)errors;
    if (encoding != NULL && strcmp(encoding, "utf-8") != 0 && strcmp(encoding, "utf8") != 0) {
        dp_set_error_text(PyExc_ValueError, "only UTF-8 encoding is supported");
        return NULL;
    }
    Py_ssize_t size = 0;
    const char *text = PyUnicode_AsUTF8AndSize(unicode, &size);
    if (text == NULL) {
        return NULL;
    }
    PyObject *bytes = dp_allocate(DP_OBJECT_BYTES, &dp_bytes_type, sizeof(PyObject));
    if (bytes == NULL) {
        return NULL;
    }
    dp_metadata *entry = dp_find(bytes);
    entry->value.text.data = dp_copy_text(text, size);
    entry->value.text.size = size;
    if (entry->value.text.data == NULL) {
        dp_release(bytes);
        dp_set_error_text(PyExc_RuntimeError, "bytes allocation failed");
        return NULL;
    }
    return bytes;
}

DP_ABI3_EXPORT void PyUnicode_InternInPlace(PyObject **unicode) {
    if (unicode == NULL || PyUnicode_AsUTF8AndSize(*unicode, NULL) == NULL) {
        return;
    }
}

DP_ABI3_EXPORT char *PyBytes_AsString(PyObject *value) {
    dp_metadata *entry = dp_find(value);
    if (entry == NULL || entry->kind != DP_OBJECT_BYTES) {
        dp_set_error_text(PyExc_TypeError, "expected bytes");
        return NULL;
    }
    return entry->value.text.data;
}

DP_ABI3_EXPORT Py_ssize_t PyBytes_Size(PyObject *value) {
    dp_metadata *entry = dp_find(value);
    if (entry == NULL || entry->kind != DP_OBJECT_BYTES) {
        dp_set_error_text(PyExc_TypeError, "expected bytes");
        return -1;
    }
    return entry->value.text.size;
}

static PyObject *dp_new_number(unsigned long long value, int is_unsigned) {
    PyObject *object = dp_allocate(DP_OBJECT_LONG, &dp_long_type, sizeof(PyObject));
    if (object != NULL) {
        dp_metadata *entry = dp_find(object);
        entry->value.number.value = value;
        entry->value.number.is_unsigned = is_unsigned;
    }
    return object;
}

DP_ABI3_EXPORT PyObject *PyLong_FromLong(long value) {
    return dp_new_number((unsigned long long)(long long)value, 0);
}

DP_ABI3_EXPORT PyObject *PyLong_FromSsize_t(Py_ssize_t value) {
    return dp_new_number((unsigned long long)value, 0);
}

DP_ABI3_EXPORT PyObject *PyLong_FromUnsignedLongLong(unsigned long long value) {
    return dp_new_number(value, 1);
}

DP_ABI3_EXPORT long PyLong_AsLong(PyObject *value) {
    if (value == &_Py_TrueStruct) {
        return 1;
    }
    if (value == &_Py_FalseStruct) {
        return 0;
    }
    dp_metadata *entry = dp_find(value);
    if (entry == NULL || entry->kind != DP_OBJECT_LONG) {
        dp_set_error_text(PyExc_TypeError, "expected int");
        return -1;
    }
    if (entry->value.number.is_unsigned &&
        entry->value.number.value > (unsigned long long)LONG_MAX) {
        dp_set_error_text(PyExc_ValueError, "integer does not fit in C long");
        return -1;
    }
    return (long)(long long)entry->value.number.value;
}

static PyObject *dp_new_sequence(enum dp_object_kind kind, PyTypeObject *type, Py_ssize_t size) {
    if (size < 0) {
        dp_set_error_text(PyExc_ValueError, "sequence size cannot be negative");
        return NULL;
    }
    PyObject *object = dp_allocate(kind, type, sizeof(PyObject));
    if (object == NULL) {
        return NULL;
    }
    dp_metadata *entry = dp_find(object);
    entry->value.sequence.size = size;
    entry->value.sequence.capacity = size;
    if (size > 0) {
        if (!dp_allocation_fits(size, sizeof(PyObject *))) {
            dp_release(object);
            dp_set_error_text(PyExc_RuntimeError, "sequence allocation exceeds the bridge limit");
            return NULL;
        }
        entry->value.sequence.items = dp_new_array<PyObject *>((size_t)size);
        if (entry->value.sequence.items == NULL) {
            dp_release(object);
            dp_set_error_text(PyExc_RuntimeError, "sequence allocation failed");
            return NULL;
        }
    }
    return object;
}

DP_ABI3_EXPORT PyObject *PyList_New(Py_ssize_t size) {
    return dp_new_sequence(DP_OBJECT_LIST, &PyList_Type, size);
}

DP_ABI3_EXPORT PyObject *PyTuple_New(Py_ssize_t size) {
    return dp_new_sequence(DP_OBJECT_TUPLE, &PyTuple_Type, size);
}

static dp_sequence *dp_require_sequence(PyObject *object, enum dp_object_kind kind) {
    dp_metadata *entry = dp_find(object);
    if (entry == NULL || entry->kind != kind) {
        dp_set_error_text(
            PyExc_TypeError,
            kind == DP_OBJECT_LIST ? "expected list" : "expected tuple"
        );
        return NULL;
    }
    return &entry->value.sequence;
}

static PyObject *dp_sequence_get(PyObject *object, enum dp_object_kind kind, Py_ssize_t index) {
    dp_sequence *sequence = dp_require_sequence(object, kind);
    if (sequence == NULL) {
        return NULL;
    }
    if (index < 0 || index >= sequence->size) {
        dp_set_error_text(PyExc_IndexError, "sequence index out of range");
        return NULL;
    }
    return sequence->items[index];
}

static int
dp_sequence_set(PyObject *object, enum dp_object_kind kind, Py_ssize_t index, PyObject *value) {
    dp_sequence *sequence = dp_require_sequence(object, kind);
    if (sequence == NULL) {
        dp_release(value);
        return -1;
    }
    if (index < 0 || index >= sequence->size) {
        dp_release(value);
        dp_set_error_text(PyExc_IndexError, "sequence index out of range");
        return -1;
    }
    dp_release(sequence->items[index]);
    sequence->items[index] = value;
    return 0;
}

DP_ABI3_EXPORT Py_ssize_t PyList_Size(PyObject *list) {
    dp_sequence *sequence = dp_require_sequence(list, DP_OBJECT_LIST);
    return sequence == NULL ? -1 : sequence->size;
}

DP_ABI3_EXPORT PyObject *PyList_GetItem(PyObject *list, Py_ssize_t index) {
    return dp_sequence_get(list, DP_OBJECT_LIST, index);
}

DP_ABI3_EXPORT int PyList_SetItem(PyObject *list, Py_ssize_t index, PyObject *value) {
    return dp_sequence_set(list, DP_OBJECT_LIST, index, value);
}

DP_ABI3_EXPORT int PyList_Append(PyObject *list, PyObject *value) {
    dp_sequence *sequence = dp_require_sequence(list, DP_OBJECT_LIST);
    if (sequence == NULL) {
        return -1;
    }
    if (sequence->size == sequence->capacity) {
        Py_ssize_t capacity = 0;
        if (!dp_checked_growth(sequence->capacity, 4, &capacity) ||
            !dp_allocation_fits(capacity, sizeof(PyObject *))) {
            dp_set_error_text(PyExc_RuntimeError, "list growth exceeds the bridge limit");
            return -1;
        }
        PyObject **items = dp_new_array<PyObject *>((size_t)capacity);
        if (items == NULL) {
            dp_set_error_text(PyExc_RuntimeError, "list growth failed");
            return -1;
        }
        if (!dp_copy_items(items, (size_t)capacity, sequence->items, (size_t)sequence->size)) {
            delete[] items;
            dp_set_error_text(PyExc_RuntimeError, "list growth copy failed");
            return -1;
        }
        delete[] sequence->items;
        sequence->items = items;
        sequence->capacity = capacity;
    }
    sequence->items[sequence->size++] = Py_NewRef(value);
    return 0;
}

DP_ABI3_EXPORT Py_ssize_t PyTuple_Size(PyObject *tuple) {
    dp_sequence *sequence = dp_require_sequence(tuple, DP_OBJECT_TUPLE);
    return sequence == NULL ? -1 : sequence->size;
}

DP_ABI3_EXPORT PyObject *PyTuple_GetItem(PyObject *tuple, Py_ssize_t index) {
    return dp_sequence_get(tuple, DP_OBJECT_TUPLE, index);
}

DP_ABI3_EXPORT int PyTuple_SetItem(PyObject *tuple, Py_ssize_t index, PyObject *value) {
    return dp_sequence_set(tuple, DP_OBJECT_TUPLE, index, value);
}

static int dp_equal(PyObject *left, PyObject *right) {
    if (left == right) {
        return 1;
    }
    dp_metadata *left_meta = dp_find(left);
    dp_metadata *right_meta = dp_find(right);
    if (left_meta == NULL || right_meta == NULL || left_meta->kind != right_meta->kind) {
        return 0;
    }
    if (left_meta->kind == DP_OBJECT_UNICODE) {
        return left_meta->value.text.size == right_meta->value.text.size &&
               memcmp(
                   left_meta->value.text.data,
                   right_meta->value.text.data,
                   (size_t)left_meta->value.text.size
               ) == 0;
    }
    if (left_meta->kind == DP_OBJECT_LONG) {
        const dp_number left_number = left_meta->value.number;
        const dp_number right_number = right_meta->value.number;
        if (left_number.value != right_number.value) {
            return 0;
        }
        /* Equal bits still differ in value when exactly one side is a negative signed number. */
        const bool left_negative = !left_number.is_unsigned && (long long)left_number.value < 0;
        const bool right_negative = !right_number.is_unsigned && (long long)right_number.value < 0;
        return left_negative == right_negative;
    }
    return 0;
}

DP_ABI3_EXPORT PyObject *PyDict_New(void) {
    return dp_allocate(DP_OBJECT_DICT, &PyDict_Type, sizeof(PyObject));
}

static dp_dictionary *dp_require_dictionary(PyObject *dictionary) {
    dp_metadata *entry = dp_find(dictionary);
    if (entry == NULL || entry->kind != DP_OBJECT_DICT) {
        dp_set_error_text(PyExc_TypeError, "expected dict");
        return NULL;
    }
    return &entry->value.dictionary;
}

static Py_ssize_t dp_dictionary_index(dp_dictionary *dictionary, PyObject *key) {
    for (Py_ssize_t index = 0; index < dictionary->size; index++) {
        if (dp_equal(dictionary->keys[index], key)) {
            return index;
        }
    }
    return -1;
}

DP_ABI3_EXPORT int PyDict_SetItem(PyObject *dictionary, PyObject *key, PyObject *value) {
    dp_dictionary *items = dp_require_dictionary(dictionary);
    if (items == NULL || key == NULL || value == NULL) {
        return -1;
    }
    Py_ssize_t index = dp_dictionary_index(items, key);
    if (index >= 0) {
        PyObject *replacement = Py_NewRef(value);
        dp_release(items->values[index]);
        items->values[index] = replacement;
        return 0;
    }
    if (items->size == items->capacity) {
        Py_ssize_t capacity = 0;
        if (!dp_checked_growth(items->capacity, 8, &capacity) ||
            !dp_allocation_fits(capacity, sizeof(PyObject *))) {
            dp_set_error_text(PyExc_RuntimeError, "dictionary growth exceeds the bridge limit");
            return -1;
        }
        PyObject **keys = dp_new_array<PyObject *>((size_t)capacity);
        PyObject **values = dp_new_array<PyObject *>((size_t)capacity);
        if (keys == NULL || values == NULL ||
            !dp_copy_items(keys, (size_t)capacity, items->keys, (size_t)items->size) ||
            !dp_copy_items(values, (size_t)capacity, items->values, (size_t)items->size)) {
            delete[] keys;
            delete[] values;
            dp_set_error_text(PyExc_RuntimeError, "dictionary growth failed");
            return -1;
        }
        delete[] items->keys;
        delete[] items->values;
        items->keys = keys;
        items->values = values;
        items->capacity = capacity;
    }
    items->keys[items->size] = Py_NewRef(key);
    items->values[items->size] = Py_NewRef(value);
    items->size++;
    return 0;
}

DP_ABI3_EXPORT PyObject *PyDict_GetItemWithError(PyObject *dictionary, PyObject *key) {
    dp_dictionary *items = dp_require_dictionary(dictionary);
    if (items == NULL) {
        return NULL;
    }
    Py_ssize_t index = dp_dictionary_index(items, key);
    return index < 0 ? NULL : items->values[index];
}

DP_ABI3_EXPORT Py_ssize_t PyDict_Size(PyObject *dictionary) {
    dp_dictionary *items = dp_require_dictionary(dictionary);
    return items == NULL ? -1 : items->size;
}

DP_ABI3_EXPORT int
PyDict_Next(PyObject *dictionary, Py_ssize_t *position, PyObject **key, PyObject **value) {
    dp_dictionary *items = dp_require_dictionary(dictionary);
    if (items == NULL || position == NULL || *position < 0 || *position >= items->size) {
        return 0;
    }
    Py_ssize_t index = *position;
    *position = index + 1;
    if (key != NULL) {
        *key = items->keys[index];
    }
    if (value != NULL) {
        *value = items->values[index];
    }
    return 1;
}

static int dp_dictionary_delete(PyObject *dictionary, PyObject *key) {
    dp_dictionary *items = dp_require_dictionary(dictionary);
    if (items == NULL) {
        return -1;
    }
    Py_ssize_t index = dp_dictionary_index(items, key);
    if (index < 0) {
        dp_set_error_text(PyExc_IndexError, "dictionary key was not found");
        return -1;
    }
    dp_release(items->keys[index]);
    dp_release(items->values[index]);
    for (Py_ssize_t current = index + 1; current < items->size; current++) {
        items->keys[current - 1] = items->keys[current];
        items->values[current - 1] = items->values[current];
    }
    items->size--;
    return 0;
}

static PyObject *dp_attributes(PyObject *object, int create) {
    dp_metadata *entry = dp_find(object);
    if (entry == NULL) {
        return NULL;
    }
    if (entry->kind == DP_OBJECT_MODULE) {
        return entry->value.module.attributes;
    }
    if (entry->attributes == NULL && create) {
        entry->attributes = PyDict_New();
    }
    return entry->attributes;
}

static PyObject *dp_get_dict_cstr(PyObject *dictionary, const char *name) {
    PyObject *key = dp_new_unicode(name, (Py_ssize_t)strlen(name));
    if (key == NULL) {
        return NULL;
    }
    PyObject *result = PyDict_GetItemWithError(dictionary, key);
    dp_release(key);
    return result;
}

static int dp_set_dict_cstr(PyObject *dictionary, const char *name, PyObject *value) {
    PyObject *key = dp_new_unicode(name, (Py_ssize_t)strlen(name));
    if (key == NULL) {
        return -1;
    }
    int result = value == NULL ? dp_dictionary_delete(dictionary, key)
                               : PyDict_SetItem(dictionary, key, value);
    dp_release(key);
    return result;
}

DP_ABI3_EXPORT PyObject *
PyCMethod_New(PyMethodDef *definition, PyObject *self, PyObject *module, PyTypeObject *class_type) {
    if (definition == NULL || definition->ml_name == NULL || definition->ml_meth == NULL) {
        dp_set_error_text(PyExc_TypeError, "method definition is invalid");
        return NULL;
    }
    PyObject *object = dp_allocate(DP_OBJECT_CMETHOD, &dp_cmethod_type, sizeof(PyObject));
    if (object == NULL) {
        return NULL;
    }
    dp_cmethod *method = &dp_find(object)->value.method;
    method->definition = definition;
    method->self = Py_NewRef(self);
    method->module = Py_NewRef(module);
    method->class_type = (PyTypeObject *)Py_NewRef((PyObject *)class_type);
    return object;
}

static PyObject *dp_call_method(dp_cmethod *method, PyObject *args, PyObject *kwargs) {
    dp_sequence *positional = dp_require_sequence(args, DP_OBJECT_TUPLE);
    if (positional == NULL) {
        return NULL;
    }
    int flags = method->definition->ml_flags;
    int convention = flags & (METH_VARARGS | METH_KEYWORDS | METH_NOARGS | METH_O | METH_FASTCALL);
    dp_dictionary *keywords = kwargs == NULL ? NULL : dp_require_dictionary(kwargs);
    if (kwargs != NULL && keywords == NULL) {
        return NULL;
    }
    Py_ssize_t keyword_count = keywords == NULL ? 0 : keywords->size;
    if (keyword_count > 0 && (flags & METH_KEYWORDS) == 0) {
        dp_set_error_text(PyExc_TypeError, "method does not accept keyword arguments");
        return NULL;
    }
    if ((flags & METH_FASTCALL) != 0) {
        Py_ssize_t total = 0;
        if (!dp_checked_add(positional->size, keyword_count, &total) ||
            !dp_allocation_fits(total, sizeof(PyObject *))) {
            dp_set_error_text(PyExc_RuntimeError, "call argument count exceeds the bridge limit");
            return NULL;
        }
        PyObject **values = total == 0 ? NULL : dp_new_array<PyObject *>((size_t)total);
        if (total > 0 && values == NULL) {
            dp_set_error_text(PyExc_RuntimeError, "call argument allocation failed");
            return NULL;
        }
        for (Py_ssize_t index = 0; index < positional->size; index++) {
            values[index] = positional->items[index];
        }
        dp_owned_ref keyword_names;
        if (keyword_count > 0) {
            keyword_names.reset(PyTuple_New(keyword_count));
            if (!keyword_names) {
                delete[] values;
                return NULL;
            }
            for (Py_ssize_t index = 0; index < keyword_count; index++) {
                if (PyTuple_SetItem(keyword_names.get(), index, Py_NewRef(keywords->keys[index])) !=
                    0) {
                    delete[] values;
                    return NULL;
                }
                values[positional->size + index] = keywords->values[index];
            }
        }
        PyObject *result;
        if ((flags & METH_METHOD) != 0) {
            result = dp_pointer_cast<PyCMethod>(method->definition->ml_meth)(
                method->self,
                method->class_type,
                values,
                positional->size,
                keyword_names.get()
            );
        } else if ((flags & METH_KEYWORDS) != 0) {
            result = dp_pointer_cast<PyCFunctionFastWithKeywords>(method->definition->ml_meth)(
                method->self,
                values,
                positional->size,
                keyword_names.get()
            );
        } else {
            result = dp_pointer_cast<PyCFunctionFast>(method->definition->ml_meth)(
                method->self,
                values,
                positional->size
            );
        }
        delete[] values;
        return result;
    }
    if (convention == (METH_VARARGS | METH_KEYWORDS)) {
        return dp_pointer_cast<PyCFunctionWithKeywords>(method->definition
                                                            ->ml_meth)(method->self, args, kwargs);
    }
    if (convention == METH_VARARGS) {
        return method->definition->ml_meth(method->self, args);
    }
    if (convention == METH_NOARGS && positional->size == 0 && keyword_count == 0) {
        return method->definition->ml_meth(method->self, NULL);
    }
    if (convention == METH_O && positional->size == 1 && keyword_count == 0) {
        return method->definition->ml_meth(method->self, positional->items[0]);
    }
    dp_set_error_text(PyExc_TypeError, "method arguments do not match its calling convention");
    return NULL;
}

static PyMethodDef *dp_find_method(PyTypeObject *type, const char *name) {
    for (PyTypeObject *current = type; current != NULL; current = current->base_type) {
        if (current->methods != NULL) {
            for (PyMethodDef *method = current->methods; method->ml_name != NULL; method++) {
                if (strcmp(method->ml_name, name) == 0) {
                    return method;
                }
            }
        }
    }
    return NULL;
}

static PyGetSetDef *dp_find_getset(PyTypeObject *type, const char *name) {
    for (PyTypeObject *current = type; current != NULL; current = current->base_type) {
        if (current->getsets != NULL) {
            for (PyGetSetDef *item = current->getsets; item->name != NULL; item++) {
                if (strcmp(item->name, name) == 0) {
                    return item;
                }
            }
        }
    }
    return NULL;
}

static PyObject *dp_bind_method(PyObject *object, PyTypeObject *type, PyMethodDef *method) {
    PyObject *self = object;
    PyTypeObject *class_type = NULL;
    if ((method->ml_flags & METH_STATIC) != 0) {
        self = NULL;
    } else if ((method->ml_flags & METH_CLASS) != 0) {
        self = (PyObject *)type;
    }
    if ((method->ml_flags & METH_METHOD) != 0) {
        class_type = type;
    }
    return PyCMethod_New(method, self, NULL, class_type);
}

DP_ABI3_EXPORT PyObject *PyType_FromSpec(PyType_Spec *specification) {
    dp_initialize();
    if (specification == NULL || specification->name == NULL ||
        specification->basicsize < (int)sizeof(PyObject)) {
        dp_set_error_text(PyExc_TypeError, "type specification is invalid");
        return NULL;
    }
    PyTypeObject *type =
        (PyTypeObject *)dp_allocate(DP_OBJECT_TYPE, &PyType_Type, sizeof(PyTypeObject));
    if (type == NULL) {
        return NULL;
    }
    type->name = dp_copy_text(specification->name, (Py_ssize_t)strlen(specification->name));
    if (type->name == NULL) {
        dp_release((PyObject *)type);
        dp_set_error_text(PyExc_RuntimeError, "type name allocation failed");
        return NULL;
    }
    /* Mark dynamic before further allocations so every failure path frees name and slots. */
    type->dynamic = 1;
    type->basicsize = specification->basicsize;
    type->itemsize = specification->itemsize;
    type->flags = specification->flags | (1UL << 9) | (1UL << 12);
    size_t slot_count = 0;
    while (specification->slots != NULL && specification->slots[slot_count].slot != 0) {
        if (slot_count == std::numeric_limits<size_t>::max() - 1U) {
            dp_release((PyObject *)type);
            dp_set_error_text(PyExc_RuntimeError, "type slot count exceeds the bridge limit");
            return NULL;
        }
        slot_count++;
    }
    if (slot_count + 1U > std::numeric_limits<size_t>::max() / sizeof(PyType_Slot)) {
        dp_release((PyObject *)type);
        dp_set_error_text(PyExc_RuntimeError, "type slot allocation exceeds the bridge limit");
        return NULL;
    }
    type->slots = dp_new_array<PyType_Slot>(slot_count + 1U);
    if (type->slots == NULL) {
        dp_release((PyObject *)type);
        dp_set_error_text(PyExc_RuntimeError, "type slot allocation failed");
        return NULL;
    }
    if (!dp_copy_items(type->slots, slot_count + 1U, specification->slots, slot_count)) {
        dp_release((PyObject *)type);
        dp_set_error_text(PyExc_RuntimeError, "type slot copy failed");
        return NULL;
    }
    type->base_type = &PyBaseObject_Type;
    for (PyType_Slot *slot = type->slots; slot->slot != 0; slot++) {
        if (slot->slot == Py_tp_methods) {
            type->methods = (PyMethodDef *)slot->pfunc;
        } else if (slot->slot == Py_tp_getset) {
            type->getsets = (PyGetSetDef *)slot->pfunc;
        } else if (slot->slot == Py_tp_base && slot->pfunc != NULL) {
            type->base_type = (PyTypeObject *)slot->pfunc;
        }
    }
    Py_NewRef((PyObject *)type->base_type);
    return (PyObject *)type;
}

static PyObject *dp_generic_alloc(PyTypeObject *type, Py_ssize_t item_count) {
    if (type == NULL || item_count != 0) {
        dp_set_error_text(PyExc_TypeError, "variable-sized heap types are unsupported");
        return NULL;
    }
    size_t size =
        type->basicsize < (int)sizeof(PyObject) ? sizeof(PyObject) : (size_t)type->basicsize;
    PyObject *object = dp_allocate(DP_OBJECT_INSTANCE, type, size);
    if (object != NULL) {
        Py_NewRef((PyObject *)type);
    }
    return object;
}

static PyObject *dp_generic_new(PyTypeObject *type, PyObject *args, PyObject *kwargs) {
    (void)args;
    (void)kwargs;
    return dp_generic_alloc(type, 0);
}

static void dp_generic_free(void *object) {
    if (object == NULL) {
        return;
    }
    dp_destroy_metadata(dp_detach((PyObject *)object));
}

DP_ABI3_EXPORT void *PyType_GetSlot(PyTypeObject *type, int slot) {
    return dp_slot(type, slot);
}

DP_ABI3_EXPORT unsigned long PyType_GetFlags(PyTypeObject *type) {
    return type == NULL ? 0UL : type->flags;
}

static const char *dp_short_type_name(PyTypeObject *type) {
    const char *name = type == NULL || type->name == NULL ? "object" : type->name;
    const char *dot = strrchr(name, '.');
    return dot == NULL ? name : dot + 1;
}

DP_ABI3_EXPORT PyObject *PyType_GetName(PyTypeObject *type) {
    const char *name = dp_short_type_name(type);
    return dp_new_unicode(name, (Py_ssize_t)strlen(name));
}

DP_ABI3_EXPORT PyObject *PyType_GetQualName(PyTypeObject *type) {
    return PyType_GetName(type);
}

DP_ABI3_EXPORT int PyType_IsSubtype(PyTypeObject *type, PyTypeObject *base) {
    for (PyTypeObject *current = type; current != NULL; current = current->base_type) {
        if (current == base) {
            return 1;
        }
    }
    return 0;
}

static PyObject *dp_get_attribute_cstr(PyObject *object, const char *name) {
    if (object == NULL || name == NULL) {
        dp_set_error_text(PyExc_AttributeError, "attribute target is invalid");
        return NULL;
    }
    if (strcmp(name, "__class__") == 0) {
        return Py_NewRef((PyObject *)object->ob_type);
    }
    dp_metadata *object_metadata = dp_find(object);
    if (object_metadata != NULL && object_metadata->kind == DP_OBJECT_CMETHOD) {
        if (strcmp(name, "__name__") == 0 || strcmp(name, "__qualname__") == 0) {
            const char *method_name = object_metadata->value.method.definition->ml_name;
            return dp_new_unicode(method_name, (Py_ssize_t)strlen(method_name));
        }
        if (strcmp(name, "__doc__") == 0) {
            const char *doc = object_metadata->value.method.definition->ml_doc;
            return doc == NULL ? Py_NewRef(&_Py_NoneStruct)
                               : dp_new_unicode(doc, (Py_ssize_t)strlen(doc));
        }
    }
    if (object->ob_type == &PyType_Type) {
        PyTypeObject *type = (PyTypeObject *)object;
        if (strcmp(name, "__name__") == 0 || strcmp(name, "__qualname__") == 0) {
            return PyType_GetName(type);
        }
        PyObject *attributes = dp_attributes(object, 0);
        PyObject *stored = attributes == NULL ? NULL : dp_get_dict_cstr(attributes, name);
        if (stored != NULL) {
            return Py_NewRef(stored);
        }
        PyMethodDef *method = dp_find_method(type, name);
        if (method != NULL && (method->ml_flags & (METH_CLASS | METH_STATIC)) != 0) {
            return dp_bind_method(object, type, method);
        }
    } else {
        PyGetSetDef *getset = dp_find_getset(object->ob_type, name);
        if (getset != NULL && getset->get != NULL) {
            return getset->get(object, getset->closure);
        }
        PyObject *attributes = dp_attributes(object, 0);
        PyObject *stored = attributes == NULL ? NULL : dp_get_dict_cstr(attributes, name);
        if (stored != NULL) {
            return Py_NewRef(stored);
        }
        PyMethodDef *method = dp_find_method(object->ob_type, name);
        if (method != NULL) {
            return dp_bind_method(object, object->ob_type, method);
        }
    }
    dp_set_error_text(PyExc_AttributeError, name);
    return NULL;
}

DP_ABI3_EXPORT PyObject *PyObject_GetAttr(PyObject *object, PyObject *name) {
    const char *text = PyUnicode_AsUTF8AndSize(name, NULL);
    return text == NULL ? NULL : dp_get_attribute_cstr(object, text);
}

DP_ABI3_EXPORT int PyObject_SetAttrString(PyObject *object, const char *name, PyObject *value) {
    if (object == NULL || name == NULL) {
        dp_set_error_text(PyExc_AttributeError, "attribute target is invalid");
        return -1;
    }
    PyGetSetDef *getset = dp_find_getset(object->ob_type, name);
    if (getset != NULL && getset->set != NULL) {
        return getset->set(object, value, getset->closure);
    }
    PyObject *attributes = dp_attributes(object, 1);
    if (attributes == NULL) {
        dp_set_error_text(PyExc_AttributeError, "object does not support attributes");
        return -1;
    }
    return dp_set_dict_cstr(attributes, name, value);
}

DP_ABI3_EXPORT int PyObject_SetAttr(PyObject *object, PyObject *name, PyObject *value) {
    const char *text = PyUnicode_AsUTF8AndSize(name, NULL);
    return text == NULL ? -1 : PyObject_SetAttrString(object, text, value);
}

DP_ABI3_EXPORT PyObject *PyObject_GenericGetDict(PyObject *object, void *context) {
    (void)context;
    PyObject *attributes = dp_attributes(object, 1);
    return Py_NewRef(attributes);
}

DP_ABI3_EXPORT int PyObject_GenericSetDict(PyObject *object, PyObject *value, void *context) {
    (void)context;
    dp_metadata *entry = dp_find(object);
    if (entry == NULL || (value != NULL && value->ob_type != &PyDict_Type)) {
        dp_set_error_text(PyExc_TypeError, "__dict__ must be a dict");
        return -1;
    }
    PyObject *replacement = Py_NewRef(value);
    dp_release(entry->attributes);
    entry->attributes = replacement;
    return 0;
}

DP_ABI3_EXPORT PyObject *PyObject_Call(PyObject *callable, PyObject *args, PyObject *kwargs) {
    if (callable == NULL) {
        dp_set_error_text(PyExc_TypeError, "callable is NULL");
        return NULL;
    }
    if (args == NULL) {
        args = PyTuple_New(0);
        if (args == NULL) {
            return NULL;
        }
    } else {
        Py_NewRef(args);
    }
    PyObject *result = NULL;
    dp_metadata *entry = dp_find(callable);
    if (entry != NULL && entry->kind == DP_OBJECT_CMETHOD) {
        result = dp_call_method(&entry->value.method, args, kwargs);
    } else if (callable->ob_type == &PyType_Type) {
        newfunc create = dp_pointer_cast<newfunc>(dp_slot((PyTypeObject *)callable, Py_tp_new));
        if (create == NULL) {
            dp_set_error_text(PyExc_TypeError, "type is not constructible");
        } else {
            result = create((PyTypeObject *)callable, args, kwargs);
        }
    } else {
        void *call = dp_slot(callable->ob_type, Py_tp_call);
        if (call == NULL) {
            dp_set_error_text(PyExc_TypeError, "object is not callable");
        } else {
            using callfunc = PyObject *(*)(PyObject *, PyObject *, PyObject *);
            result = dp_pointer_cast<callfunc>(call)(callable, args, kwargs);
        }
    }
    dp_release(args);
    return result;
}

DP_ABI3_EXPORT PyObject *PyObject_CallNoArgs(PyObject *callable) {
    PyObject *args = PyTuple_New(0);
    if (args == NULL) {
        return NULL;
    }
    PyObject *result = PyObject_Call(callable, args, NULL);
    dp_release(args);
    return result;
}

DP_ABI3_EXPORT Py_ssize_t PyObject_Size(PyObject *object) {
    dp_metadata *entry = dp_find(object);
    if (entry != NULL) {
        if (entry->kind == DP_OBJECT_LIST || entry->kind == DP_OBJECT_TUPLE) {
            return entry->value.sequence.size;
        }
        if (entry->kind == DP_OBJECT_DICT) {
            return entry->value.dictionary.size;
        }
        if (entry->kind == DP_OBJECT_UNICODE || entry->kind == DP_OBJECT_BYTES) {
            return entry->value.text.size;
        }
    }
    lenfunc length =
        dp_pointer_cast<lenfunc>(dp_slot(object == NULL ? NULL : object->ob_type, Py_mp_length));
    if (length == NULL) {
        length = dp_pointer_cast<lenfunc>(
            dp_slot(object == NULL ? NULL : object->ob_type, Py_sq_length)
        );
    }
    if (length == NULL) {
        dp_set_error_text(PyExc_TypeError, "object has no length");
        return -1;
    }
    return length(object);
}

DP_ABI3_EXPORT int PySequence_Check(PyObject *object) {
    if (object == NULL) {
        return 0;
    }
    dp_metadata *entry = dp_find(object);
    return (entry != NULL && (entry->kind == DP_OBJECT_LIST || entry->kind == DP_OBJECT_TUPLE)) ||
           dp_slot(object->ob_type, Py_sq_item) != NULL;
}

DP_ABI3_EXPORT PyObject *PyObject_GetItem(PyObject *object, PyObject *key) {
    if (object == NULL || key == NULL) {
        dp_set_error_text(PyExc_TypeError, "item lookup is invalid");
        return NULL;
    }
    dp_metadata *entry = dp_find(object);
    if (entry != NULL && entry->kind == DP_OBJECT_DICT) {
        PyObject *value = PyDict_GetItemWithError(object, key);
        if (value == NULL && PyErr_Occurred() == NULL) {
            dp_set_error_text(PyExc_IndexError, "dictionary key was not found");
        }
        return Py_NewRef(value);
    }
    long index = PyLong_AsLong(key);
    if (index == -1 && PyErr_Occurred() != NULL) {
        binaryfunc subscript =
            dp_pointer_cast<binaryfunc>(dp_slot(object->ob_type, Py_mp_subscript));
        if (subscript != NULL) {
            dp_clear_error();
            return subscript(object, key);
        }
        return NULL;
    }
    if (entry != NULL && entry->kind == DP_OBJECT_LIST) {
        return Py_NewRef(dp_sequence_get(object, DP_OBJECT_LIST, (Py_ssize_t)index));
    }
    if (entry != NULL && entry->kind == DP_OBJECT_TUPLE) {
        return Py_NewRef(dp_sequence_get(object, DP_OBJECT_TUPLE, (Py_ssize_t)index));
    }
    ssizeargfunc item = dp_pointer_cast<ssizeargfunc>(dp_slot(object->ob_type, Py_sq_item));
    if (item != NULL) {
        return item(object, (Py_ssize_t)index);
    }
    binaryfunc subscript = dp_pointer_cast<binaryfunc>(dp_slot(object->ob_type, Py_mp_subscript));
    if (subscript != NULL) {
        return subscript(object, key);
    }
    dp_set_error_text(PyExc_TypeError, "object does not support item lookup");
    return NULL;
}

DP_ABI3_EXPORT int PyObject_SetItem(PyObject *object, PyObject *key, PyObject *value) {
    dp_metadata *entry = dp_find(object);
    if (entry != NULL && entry->kind == DP_OBJECT_DICT) {
        return PyDict_SetItem(object, key, value);
    }
    objobjargproc assign = dp_pointer_cast<objobjargproc>(
        dp_slot(object == NULL ? NULL : object->ob_type, Py_mp_ass_subscript)
    );
    if (assign == NULL) {
        dp_set_error_text(PyExc_TypeError, "object does not support item assignment");
        return -1;
    }
    return assign(object, key, value);
}

DP_ABI3_EXPORT int PyObject_DelItem(PyObject *object, PyObject *key) {
    dp_metadata *entry = dp_find(object);
    if (entry != NULL && entry->kind == DP_OBJECT_DICT) {
        return dp_dictionary_delete(object, key);
    }
    objobjargproc assign = dp_pointer_cast<objobjargproc>(
        dp_slot(object == NULL ? NULL : object->ob_type, Py_mp_ass_subscript)
    );
    if (assign == NULL) {
        dp_set_error_text(PyExc_TypeError, "object does not support item deletion");
        return -1;
    }
    return assign(object, key, NULL);
}

DP_ABI3_EXPORT PyObject *PyObject_GetIter(PyObject *object) {
    if (object == NULL) {
        dp_set_error_text(PyExc_TypeError, "cannot iterate NULL");
        return NULL;
    }
    getiterfunc get_iterator = dp_pointer_cast<getiterfunc>(dp_slot(object->ob_type, Py_tp_iter));
    if (get_iterator != NULL) {
        return get_iterator(object);
    }
    dp_metadata *entry = dp_find(object);
    if ((entry == NULL || (entry->kind != DP_OBJECT_LIST && entry->kind != DP_OBJECT_TUPLE)) &&
        dp_slot(object->ob_type, Py_sq_item) == NULL) {
        dp_set_error_text(PyExc_TypeError, "object is not iterable");
        return NULL;
    }
    PyObject *iterator = dp_allocate(DP_OBJECT_ITERATOR, &dp_iterator_type, sizeof(PyObject));
    if (iterator != NULL) {
        dp_iterator *state = &dp_find(iterator)->value.iterator;
        state->source = Py_NewRef(object);
        state->index = 0;
    }
    return iterator;
}

DP_ABI3_EXPORT PyObject *PyIter_Next(PyObject *iterator) {
    dp_metadata *entry = dp_find(iterator);
    if (entry != NULL && entry->kind == DP_OBJECT_ITERATOR) {
        Py_ssize_t size = PyObject_Size(entry->value.iterator.source);
        if (size < 0) {
            return NULL;
        }
        if (entry->value.iterator.index >= size) {
            return NULL;
        }
        PyObject *index = PyLong_FromSsize_t(entry->value.iterator.index++);
        if (index == NULL) {
            return NULL;
        }
        PyObject *result = PyObject_GetItem(entry->value.iterator.source, index);
        dp_release(index);
        return result;
    }
    iternextfunc next = dp_pointer_cast<iternextfunc>(
        dp_slot(iterator == NULL ? NULL : iterator->ob_type, Py_tp_iternext)
    );
    if (next == NULL) {
        dp_set_error_text(PyExc_TypeError, "object is not an iterator");
        return NULL;
    }
    return next(iterator);
}

template <typename T> static PyObject *dp_new_integer_text(T value) noexcept {
    std::array<char, std::numeric_limits<T>::digits10 + 3> buffer{};
    const auto conversion = std::to_chars(buffer.data(), buffer.data() + buffer.size(), value);
    if (conversion.ec != std::errc{}) {
        dp_set_error_text(PyExc_RuntimeError, "integer formatting failed");
        return NULL;
    }
    return dp_new_unicode(buffer.data(), (Py_ssize_t)(conversion.ptr - buffer.data()));
}

static PyObject *dp_new_type_description(PyTypeObject *type) noexcept {
    constexpr std::string_view prefix = "<";
    constexpr std::string_view suffix = " object>";
    const std::string_view name = dp_short_type_name(type);
    const size_t fixed_size = prefix.size() + suffix.size();
    if (name.size() > std::numeric_limits<size_t>::max() - fixed_size - 1U ||
        name.size() + fixed_size > (size_t)std::numeric_limits<Py_ssize_t>::max()) {
        dp_set_error_text(PyExc_ValueError, "type name exceeds the bridge limit");
        return NULL;
    }
    const size_t length = name.size() + fixed_size;
    char *buffer = dp_new_array<char>(length + 1U);
    if (buffer == NULL) {
        dp_set_error_text(PyExc_RuntimeError, "type description allocation failed");
        return NULL;
    }
    size_t offset = 0U;
    if (dp_append_text(buffer, length + 1U, &offset, prefix) != 0 ||
        dp_append_text(buffer, length + 1U, &offset, name) != 0 ||
        dp_append_text(buffer, length + 1U, &offset, suffix) != 0) {
        delete[] buffer;
        dp_set_error_text(PyExc_RuntimeError, "type description formatting failed");
        return NULL;
    }
    PyObject *result = dp_new_unicode(buffer, (Py_ssize_t)length);
    delete[] buffer;
    return result;
}

DP_ABI3_EXPORT PyObject *PyObject_Str(PyObject *object) {
    if (object == NULL) {
        return dp_new_unicode("<NULL>", 6);
    }
    if (object == &_Py_NoneStruct) {
        return dp_new_unicode("None", 4);
    }
    if (object == &_Py_TrueStruct || object == &_Py_FalseStruct) {
        const char *text = object == &_Py_TrueStruct ? "True" : "False";
        return dp_new_unicode(text, (Py_ssize_t)strlen(text));
    }
    dp_metadata *entry = dp_find(object);
    if (entry != NULL && entry->kind == DP_OBJECT_UNICODE) {
        return Py_NewRef(object);
    }
    if (entry != NULL && entry->kind == DP_OBJECT_LONG) {
        return entry->value.number.is_unsigned
                   ? dp_new_integer_text(entry->value.number.value)
                   : dp_new_integer_text((long long)entry->value.number.value);
    }
    reprfunc string = dp_pointer_cast<reprfunc>(dp_slot(object->ob_type, Py_tp_str));
    if (string != NULL) {
        return string(object);
    }
    return dp_new_type_description(object->ob_type);
}

DP_ABI3_EXPORT PyObject *PyObject_Repr(PyObject *object) {
    if (object != NULL) {
        reprfunc representation = dp_pointer_cast<reprfunc>(dp_slot(object->ob_type, Py_tp_repr));
        if (representation != NULL) {
            return representation(object);
        }
        dp_metadata *entry = dp_find(object);
        if (entry != NULL && entry->kind == DP_OBJECT_UNICODE) {
            if (entry->value.text.size < 0) {
                dp_set_error_text(PyExc_RuntimeError, "representation size is invalid");
                return NULL;
            }
            size_t size = (size_t)entry->value.text.size;
            if (size > std::numeric_limits<size_t>::max() - 3U) {
                dp_set_error_text(PyExc_RuntimeError, "representation exceeds the bridge limit");
                return NULL;
            }
            char *buffer = dp_new_array<char>(size + 3U);
            if (buffer == NULL) {
                dp_set_error_text(PyExc_RuntimeError, "representation allocation failed");
                return NULL;
            }
            buffer[0] = '\'';
            if (!dp_copy_items(buffer + 1, size + 2U, entry->value.text.data, size)) {
                delete[] buffer;
                dp_set_error_text(PyExc_RuntimeError, "representation copy failed");
                return NULL;
            }
            buffer[size + 1U] = '\'';
            buffer[size + 2U] = '\0';
            PyObject *result = dp_new_unicode(buffer, (Py_ssize_t)(size + 2U));
            delete[] buffer;
            return result;
        }
    }
    return PyObject_Str(object);
}

DP_ABI3_EXPORT void PyObject_GC_UnTrack(void *object) {
    (void)object;
}

DP_ABI3_EXPORT PyObject *PyModuleDef_Init(PyModuleDef *definition) {
    if (!dp_require_owner_thread()) {
        return NULL;
    }
    dp_initialize();
    if (definition == NULL || definition->m_name == NULL) {
        dp_set_error_text(PyExc_TypeError, "module definition is invalid");
        return NULL;
    }
    definition->m_base.ob_base.ob_refcnt = DP_ABI3_IMMORTAL_REFCNT;
    definition->m_base.ob_base.ob_type = &PyType_Type;
    dp_last_definition = definition;
    return (PyObject *)definition;
}

static PyObject *dp_create_module(PyModuleDef *definition, const char *synthetic_name = NULL) {
    const char *name = definition == NULL ? synthetic_name : definition->m_name;
    if (name == NULL) {
        dp_set_error_text(PyExc_TypeError, "module name is invalid");
        return NULL;
    }
    PyObject *object = dp_allocate(DP_OBJECT_MODULE, &PyModule_Type, sizeof(PyObject));
    if (object == NULL) {
        return NULL;
    }
    dp_module *module = &dp_find(object)->value.module;
    module->definition = definition;
    module->attributes = PyDict_New();
    module->name = dp_copy_text(name, (Py_ssize_t)strlen(name));
    if (module->attributes == NULL || module->name == NULL) {
        dp_release(object);
        return NULL;
    }
    PyObject *module_name = dp_new_unicode(module->name, (Py_ssize_t)strlen(module->name));
    if (module_name == NULL || PyObject_SetAttrString(object, "__name__", module_name) != 0) {
        dp_release(module_name);
        dp_release(object);
        return NULL;
    }
    dp_release(module_name);
    if (definition != NULL && definition->m_methods != NULL) {
        for (PyMethodDef *method = definition->m_methods; method->ml_name != NULL; method++) {
            PyObject *callable = PyCMethod_New(method, object, NULL, NULL);
            if (callable == NULL ||
                PyObject_SetAttrString(object, method->ml_name, callable) != 0) {
                dp_release(callable);
                dp_release(object);
                return NULL;
            }
            dp_release(callable);
        }
    }
    return object;
}

DP_ABI3_EXPORT PyObject *PyModule_GetNameObject(PyObject *module) {
    dp_metadata *entry = dp_find(module);
    if (entry == NULL || entry->kind != DP_OBJECT_MODULE) {
        dp_set_error_text(PyExc_TypeError, "expected module");
        return NULL;
    }
    return dp_new_unicode(entry->value.module.name, (Py_ssize_t)strlen(entry->value.module.name));
}

DP_ABI3_EXPORT int PyModule_AddIntConstant(PyObject *module, const char *name, long value) {
    PyObject *number = PyLong_FromLong(value);
    if (number == NULL) {
        return -1;
    }
    int result = PyObject_SetAttrString(module, name, number);
    dp_release(number);
    return result;
}

DP_ABI3_EXPORT void PyErr_SetString(PyObject *exception, const char *message) {
    dp_set_error_text(exception, message);
}

DP_ABI3_EXPORT void PyErr_SetObject(PyObject *exception, PyObject *value) {
    dp_clear_error();
    dp_error_type = Py_NewRef(exception);
    dp_error_value = Py_NewRef(value);
    PyObject *text = PyObject_Str(value);
    if (text != NULL) {
        const char *message = PyUnicode_AsUTF8AndSize(text, NULL);
        if (message != NULL) {
            (void)dp_copy_truncated_text(dp_error_text, sizeof(dp_error_text), message);
        }
        dp_release(text);
    }
}

DP_ABI3_EXPORT PyObject *PyErr_Occurred(void) {
    return dp_error_type;
}

DP_ABI3_EXPORT void PyErr_Fetch(PyObject **type, PyObject **value, PyObject **traceback) {
    if (type != NULL) {
        *type = dp_error_type;
    } else {
        dp_release(dp_error_type);
    }
    if (value != NULL) {
        *value = dp_error_value;
    } else {
        dp_release(dp_error_value);
    }
    if (traceback != NULL) {
        *traceback = dp_error_traceback;
    } else {
        dp_release(dp_error_traceback);
    }
    dp_error_type = NULL;
    dp_error_value = NULL;
    dp_error_traceback = NULL;
    dp_error_text[0] = '\0';
}

DP_ABI3_EXPORT void PyErr_Restore(PyObject *type, PyObject *value, PyObject *traceback) {
    dp_clear_error();
    dp_error_type = type;
    dp_error_value = value;
    dp_error_traceback = traceback;
    if (value != NULL) {
        PyObject *text = PyObject_Str(value);
        if (text != NULL) {
            const char *message = PyUnicode_AsUTF8AndSize(text, NULL);
            if (message != NULL) {
                (void)dp_copy_truncated_text(dp_error_text, sizeof(dp_error_text), message);
            }
            dp_release(text);
        }
    }
}

DP_ABI3_EXPORT void
PyErr_NormalizeException(PyObject **type, PyObject **value, PyObject **traceback) {
    (void)traceback;
    if (type != NULL && *type != NULL && value != NULL && *value == NULL) {
        *value = dp_new_unicode("", 0);
    }
}

DP_ABI3_EXPORT int PyErr_GivenExceptionMatches(PyObject *given, PyObject *expected) {
    if (given == expected) {
        return 1;
    }
    if (given != NULL && expected != NULL && given->ob_type == &PyType_Type &&
        expected->ob_type == &PyType_Type) {
        return PyType_IsSubtype((PyTypeObject *)given, (PyTypeObject *)expected);
    }
    return 0;
}

DP_ABI3_EXPORT PyObject *
PyErr_NewExceptionWithDoc(const char *name, const char *doc, PyObject *base, PyObject *dictionary) {
    (void)doc;
    (void)dictionary;
    PyType_Spec specification = {name, (int)sizeof(PyObject), 0, 0, NULL};
    PyObject *type = PyType_FromSpec(&specification);
    if (type != NULL) {
        PyTypeObject *typed = (PyTypeObject *)type;
        dp_release((PyObject *)typed->base_type);
        typed->base_type = (PyTypeObject *)Py_NewRef(base == NULL ? PyExc_BaseException : base);
        typed->flags |= 1UL << 30;
    }
    return type;
}

DP_ABI3_EXPORT void PyErr_PrintEx(int set_system_last_vars) {
    (void)set_system_last_vars;
    dp_clear_error();
}

DP_ABI3_EXPORT void PyErr_WriteUnraisable(PyObject *value) {
    (void)value;
    dp_clear_error();
}

DP_ABI3_EXPORT int PyException_SetCause(PyObject *exception, PyObject *cause) {
    (void)exception;
    dp_release(cause);
    return 0;
}

DP_ABI3_EXPORT int PyException_SetTraceback(PyObject *exception, PyObject *traceback) {
    (void)exception;
    (void)traceback;
    return 0;
}

DP_ABI3_EXPORT int PyTraceBack_Print(PyObject *traceback, PyObject *file) {
    (void)traceback;
    (void)file;
    return 0;
}

DP_ABI3_EXPORT PyObject *PyImport_Import(PyObject *name) {
    const char *module_name = PyUnicode_AsUTF8AndSize(name, NULL);
    if (module_name == NULL) {
        return NULL;
    }
    return dp_create_module(NULL, module_name);
}

DP_ABI3_EXPORT PyGILState_STATE PyGILState_Ensure(void) {
    return 0;
}

DP_ABI3_EXPORT void PyGILState_Release(PyGILState_STATE state) {
    (void)state;
}

DP_ABI3_EXPORT PyThreadState *PyEval_SaveThread(void) {
    return (PyThreadState *)&dp_thread_state_token;
}

DP_ABI3_EXPORT void PyEval_RestoreThread(PyThreadState *thread_state) {
    (void)thread_state;
}

DP_ABI3_EXPORT int Py_IsInitialized(void) {
    return 1;
}

DP_ABI3_EXPORT int dp_abi3_bridge_version(void) {
    return DP_ABI3_BRIDGE_VERSION;
}

DP_ABI3_EXPORT int
dp_abi3_module_initialize(PyObject *initialization_result, PyObject **module, int *multi_phase) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (module == NULL || multi_phase == NULL) {
        dp_set_error_text(PyExc_TypeError, "module initialization outputs are invalid");
        return -1;
    }
    *module = NULL;
    *multi_phase = 0;
    if (initialization_result == NULL ||
        (PyModuleDef *)initialization_result != dp_last_definition) {
        if (dp_error_type == NULL) {
            dp_set_error_text(PyExc_TypeError, "module initializer returned an invalid definition");
        }
        return -1;
    }
    PyObject *created = dp_create_module(dp_last_definition);
    if (created == NULL) {
        return -1;
    }
    dp_clear_error();
    if (dp_last_definition->m_slots != NULL) {
        for (PyModuleDef_Slot *slot = dp_last_definition->m_slots; slot->slot != 0; slot++) {
            if (slot->slot == Py_mod_exec) {
                using module_execute = int (*)(PyObject *);
                module_execute execute = dp_pointer_cast<module_execute>(slot->value);
                if (execute == NULL || execute(created) != 0) {
                    if (dp_error_type == NULL) {
                        dp_set_error_text(PyExc_RuntimeError, "module execution slot failed");
                    }
                    dp_release(created);
                    return -1;
                }
            } else if (slot->slot != Py_mod_multiple_interpreters && slot->slot != Py_mod_gil) {
                dp_set_error_text(PyExc_TypeError, "module slot is unsupported");
                dp_release(created);
                return -1;
            }
        }
    }
    *module = created;
    *multi_phase = 1;
    return 0;
}

DP_ABI3_EXPORT int dp_abi3_module_get_int(PyObject *module, const char *name, int64_t *value) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (value == NULL) {
        dp_set_error_text(PyExc_TypeError, "module query output is invalid");
        return -1;
    }
    PyObject *attribute = dp_get_attribute_cstr(module, name);
    if (attribute == NULL) {
        return -1;
    }
    long converted = PyLong_AsLong(attribute);
    dp_release(attribute);
    if (converted == -1 && PyErr_Occurred() != NULL) {
        return -1;
    }
    *value = converted;
    return 0;
}

DP_ABI3_EXPORT int dp_abi3_module_call_long(
    PyObject *module,
    const char *method,
    int has_argument,
    int64_t argument,
    int64_t *result
) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (result == NULL) {
        dp_set_error_text(PyExc_TypeError, "module call output is invalid");
        return -1;
    }
    dp_owned_ref callable(dp_get_attribute_cstr(module, method));
    if (!callable) {
        return -1;
    }
    dp_owned_ref args(PyTuple_New(has_argument ? 1 : 0));
    if (!args) {
        return -1;
    }
    if (has_argument) {
        if (argument < (int64_t)LONG_MIN || argument > (int64_t)LONG_MAX) {
            dp_set_error_text(PyExc_ValueError, "module argument does not fit in C long");
            return -1;
        }
        dp_owned_ref number(PyLong_FromLong((long)argument));
        if (!number || PyTuple_SetItem(args.get(), 0, number.release()) != 0) {
            return -1;
        }
    }
    dp_clear_error();
    dp_owned_ref returned(PyObject_Call(callable.get(), args.get(), NULL));
    if (!returned) {
        if (dp_error_type == NULL) {
            dp_set_error_text(PyExc_RuntimeError, "module method returned NULL without an error");
        }
        return -1;
    }
    long converted = PyLong_AsLong(returned.get());
    if (converted == -1 && PyErr_Occurred() != NULL) {
        return -1;
    }
    *result = converted;
    return 0;
}

static PyObject *dp_call_named(PyObject *owner, const char *name, PyObject *args) {
    PyObject *callable = dp_get_attribute_cstr(owner, name);
    if (callable == NULL) {
        return NULL;
    }
    PyObject *result = PyObject_Call(callable, args, NULL);
    dp_release(callable);
    return result;
}

static int dp_tuple_set_text(PyObject *tuple, Py_ssize_t index, const char *text) {
    dp_owned_ref value(dp_new_unicode(text, (Py_ssize_t)strlen(text)));
    if (!value) {
        return -1;
    }
    /* PyTuple_SetItem steals the new reference, including on failure. */
    return PyTuple_SetItem(tuple, index, value.release());
}

DP_ABI3_EXPORT int dp_abi3_anyver_compare(
    PyObject *module,
    const char *left,
    const char *right,
    const char *ecosystem,
    int64_t *result
) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (left == NULL || right == NULL || ecosystem == NULL || result == NULL) {
        dp_set_error_text(PyExc_TypeError, "Anyver comparison inputs are invalid");
        return -1;
    }
    PyObject *args = PyTuple_New(3);
    if (args == NULL || dp_tuple_set_text(args, 0, left) != 0 ||
        dp_tuple_set_text(args, 1, right) != 0 || dp_tuple_set_text(args, 2, ecosystem) != 0) {
        dp_release(args);
        return -1;
    }
    dp_clear_error();
    PyObject *returned = dp_call_named(module, "compare", args);
    dp_release(args);
    if (returned == NULL) {
        return -1;
    }
    long converted = PyLong_AsLong(returned);
    dp_release(returned);
    if (converted == -1 && PyErr_Occurred() != NULL) {
        return -1;
    }
    *result = converted;
    return 0;
}

static int dp_append_json_string(char *buffer, size_t capacity, size_t *offset, const char *text) {
    if (*offset >= capacity || capacity - *offset < 3U) {
        return -1;
    }
    buffer[(*offset)++] = '"';
    for (const unsigned char *current = (const unsigned char *)text; *current != 0; current++) {
        const char *escape = NULL;
        if (*current == '"') {
            escape = "\\\"";
        } else if (*current == '\\') {
            escape = "\\\\";
        } else if (*current == '\n') {
            escape = "\\n";
        } else if (*current == '\r') {
            escape = "\\r";
        } else if (*current == '\t') {
            escape = "\\t";
        }
        if (escape != NULL) {
            if (dp_append_text(buffer, capacity, offset, escape) != 0) {
                return -1;
            }
        } else {
            if (capacity - *offset <= 1U) {
                return -1;
            }
            buffer[(*offset)++] = (char)*current;
        }
    }
    if (capacity - *offset <= 1U) {
        return -1;
    }
    buffer[(*offset)++] = '"';
    buffer[*offset] = '\0';
    return 0;
}

DP_ABI3_EXPORT int dp_abi3_anyver_sort_versions(
    PyObject *module,
    const char *const *versions,
    int64_t version_count,
    const char *ecosystem,
    const char **result_json
) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (versions == NULL || version_count < 0 || version_count > 4096 || ecosystem == NULL ||
        result_json == NULL) {
        dp_set_error_text(PyExc_TypeError, "Anyver sort inputs are invalid");
        return -1;
    }
    dp_owned_ref list(PyList_New(0));
    if (!list) {
        return -1;
    }
    for (int64_t index = 0; index < version_count; index++) {
        if (versions[index] == NULL) {
            dp_set_error_text(PyExc_TypeError, "Anyver sort contains a NULL version");
            return -1;
        }
        dp_owned_ref value(dp_new_unicode(versions[index], (Py_ssize_t)strlen(versions[index])));
        if (!value || PyList_Append(list.get(), value.get()) != 0) {
            return -1;
        }
    }
    dp_owned_ref args(PyTuple_New(2));
    if (!args || PyTuple_SetItem(args.get(), 0, list.release()) != 0 ||
        dp_tuple_set_text(args.get(), 1, ecosystem) != 0) {
        return -1;
    }
    dp_clear_error();
    dp_owned_ref sorted(dp_call_named(module, "sort_versions", args.get()));
    if (!sorted) {
        return -1;
    }
    Py_ssize_t count = PyList_Size(sorted.get());
    if (count < 0) {
        return -1;
    }
    size_t offset = 0;
    dp_result_text[offset++] = '[';
    dp_result_text[offset] = '\0';
    for (Py_ssize_t index = 0; index < count; index++) {
        const char *text = PyUnicode_AsUTF8AndSize(PyList_GetItem(sorted.get(), index), NULL);
        if (text == NULL || (index > 0 && offset + 1U >= sizeof(dp_result_text))) {
            return -1;
        }
        if (index > 0) {
            dp_result_text[offset++] = ',';
        }
        if (dp_append_json_string(dp_result_text, sizeof(dp_result_text), &offset, text) != 0) {
            dp_set_error_text(PyExc_ValueError, "Anyver sort result exceeds the bridge limit");
            return -1;
        }
    }
    if (offset + 2U > sizeof(dp_result_text)) {
        dp_set_error_text(PyExc_ValueError, "Anyver sort result exceeds the bridge limit");
        return -1;
    }
    dp_result_text[offset++] = ']';
    dp_result_text[offset] = '\0';
    *result_json = dp_result_text;
    return 0;
}

static PyObject *dp_dict_value(PyObject *dictionary, const char *name) {
    PyObject *key = dp_new_unicode(name, (Py_ssize_t)strlen(name));
    if (key == NULL) {
        return NULL;
    }
    PyObject *value = PyDict_GetItemWithError(dictionary, key);
    dp_release(key);
    return value;
}

DP_ABI3_EXPORT int dp_abi3_anyver_version_to_json(
    PyObject *module,
    const char *version,
    const char *ecosystem,
    const char **result_json
) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    if (version == NULL || ecosystem == NULL || result_json == NULL) {
        dp_set_error_text(PyExc_TypeError, "Anyver Version inputs are invalid");
        return -1;
    }
    PyObject *type = dp_get_attribute_cstr(module, "Version");
    PyObject *args = PyTuple_New(2);
    if (type == NULL || args == NULL || dp_tuple_set_text(args, 0, version) != 0 ||
        dp_tuple_set_text(args, 1, ecosystem) != 0) {
        dp_release(type);
        dp_release(args);
        return -1;
    }
    dp_clear_error();
    PyObject *instance = PyObject_Call(type, args, NULL);
    dp_release(type);
    dp_release(args);
    if (instance == NULL) {
        return -1;
    }
    PyObject *empty = PyTuple_New(0);
    PyObject *dictionary = empty == NULL ? NULL : dp_call_named(instance, "to_dict", empty);
    dp_release(empty);
    dp_release(instance);
    if (dictionary == NULL) {
        return -1;
    }
    PyObject *raw = dp_dict_value(dictionary, "raw");
    PyObject *eco = dp_dict_value(dictionary, "ecosystem");
    PyObject *build = dp_dict_value(dictionary, "build");
    PyObject *epoch = dp_dict_value(dictionary, "epoch");
    PyObject *major = dp_dict_value(dictionary, "major");
    PyObject *minor = dp_dict_value(dictionary, "minor");
    PyObject *patch = dp_dict_value(dictionary, "patch");
    PyObject *prerelease = dp_dict_value(dictionary, "is_prerelease");
    PyObject *postrelease = dp_dict_value(dictionary, "is_postrelease");
    const char *raw_text = PyUnicode_AsUTF8AndSize(raw, NULL);
    const char *eco_text = PyUnicode_AsUTF8AndSize(eco, NULL);
    const char *build_text = PyUnicode_AsUTF8AndSize(build, NULL);
    if (raw_text == NULL || eco_text == NULL || build_text == NULL || epoch == NULL ||
        major == NULL || minor == NULL || patch == NULL || prerelease == NULL ||
        postrelease == NULL) {
        dp_release(dictionary);
        return -1;
    }
    long epoch_value = PyLong_AsLong(epoch);
    long major_value = PyLong_AsLong(major);
    long minor_value = PyLong_AsLong(minor);
    long patch_value = PyLong_AsLong(patch);
    if (PyErr_Occurred() != NULL) {
        dp_release(dictionary);
        return -1;
    }
    size_t offset = 0;
    if (dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, "{\"raw\":") != 0 ||
        dp_append_json_string(dp_result_text, sizeof(dp_result_text), &offset, raw_text) != 0) {
        dp_release(dictionary);
        dp_set_error_text(PyExc_ValueError, "Anyver Version result exceeds the bridge limit");
        return -1;
    }
    if (dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"ecosystem\":") != 0 ||
        dp_append_json_string(dp_result_text, sizeof(dp_result_text), &offset, eco_text) != 0 ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"epoch\":") != 0 ||
        dp_append_integer(dp_result_text, sizeof(dp_result_text), &offset, epoch_value) != 0 ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"major\":") != 0 ||
        dp_append_integer(dp_result_text, sizeof(dp_result_text), &offset, major_value) != 0 ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"minor\":") != 0 ||
        dp_append_integer(dp_result_text, sizeof(dp_result_text), &offset, minor_value) != 0 ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"patch\":") != 0 ||
        dp_append_integer(dp_result_text, sizeof(dp_result_text), &offset, patch_value) != 0 ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"build\":") != 0) {
        dp_release(dictionary);
        dp_set_error_text(PyExc_ValueError, "Anyver Version result exceeds the bridge limit");
        return -1;
    }
    if (dp_append_json_string(dp_result_text, sizeof(dp_result_text), &offset, build_text) != 0) {
        dp_release(dictionary);
        dp_set_error_text(PyExc_ValueError, "Anyver Version result exceeds the bridge limit");
        return -1;
    }
    const int append_result =
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"is_prerelease\":") ||
        dp_append_text(
            dp_result_text,
            sizeof(dp_result_text),
            &offset,
            prerelease == &_Py_TrueStruct ? "true" : "false"
        ) ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, ",\"is_postrelease\":") ||
        dp_append_text(
            dp_result_text,
            sizeof(dp_result_text),
            &offset,
            postrelease == &_Py_TrueStruct ? "true" : "false"
        ) ||
        dp_append_text(dp_result_text, sizeof(dp_result_text), &offset, "}");
    dp_release(dictionary);
    if (append_result != 0) {
        dp_set_error_text(PyExc_ValueError, "Anyver Version result exceeds the bridge limit");
        return -1;
    }
    *result_json = dp_result_text;
    return 0;
}

DP_ABI3_EXPORT void dp_abi3_module_destroy(PyObject *module) {
    if (!dp_require_owner_thread()) {
        return;
    }
    dp_clear_error();
    dp_metadata *entry = dp_find(module);
    if (entry != NULL && entry->kind == DP_OBJECT_MODULE) {
        dp_metadata *attributes = dp_find(entry->value.module.attributes);
        if (attributes != NULL && attributes->kind == DP_OBJECT_DICT) {
            dp_dictionary *dictionary = &attributes->value.dictionary;
            for (Py_ssize_t index = 0; index < dictionary->size; index++) {
                dp_metadata *value = dp_find(dictionary->values[index]);
                if (value != NULL && value->kind == DP_OBJECT_CMETHOD &&
                    value->value.method.self == module) {
                    value->value.method.self = NULL;
                    dp_release(module);
                }
            }
        }
    }
    dp_release(module);
    dp_clear_error();
}

DP_ABI3_EXPORT const char *dp_abi3_error_type(void) {
    if (dp_error_type == NULL || dp_error_type->ob_type != &PyType_Type) {
        return "RuntimeError";
    }
    return dp_short_type_name((PyTypeObject *)dp_error_type);
}

DP_ABI3_EXPORT const char *dp_abi3_error_message(void) {
    return dp_error_text;
}

DP_ABI3_EXPORT int64_t dp_abi3_active_object_count(void) {
    if (!dp_require_owner_thread()) {
        return -1;
    }
    return dp_active_objects;
}
