// The `collections.namedtuple` factory follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.namedtuple</c> factory: a tuple subclass whose fields are read by name,
/// built one class at a time with the methods the Python source spells out.
/// </summary>
/// <remarks>
/// The class is built the way the source builds it — a namespace handed to `type()` with the
/// tuple as its base — so a named tuple is an ordinary class of the runtime: its fields are
/// `_tuplegetter` descriptors, its `__new__` binds the field names with their defaults, and
/// `_make`, `_replace`, `_asdict` and `__getnewargs__` are the functions the source creates.
/// </remarks>
internal static class PythonNamedTuple
{
    /// <summary>`collections.namedtuple`.</summary>
    internal static readonly PythonValue Function = new PythonBuiltinFunctionValue(
        "namedtuple",
        (arguments, span) => Create(arguments, [], [], span),
        (arguments, keywordNames, keywordValues, span) =>
            Create(arguments, keywordNames, keywordValues, span)
    );

    /// <summary>The result of validating the factory's arguments.</summary>
    private sealed record Definition(
        string TypeName,
        string[] Fields,
        PythonValue[] Defaults,
        PythonDictionaryValue FieldDefaults,
        string Doc,
        string ArgumentList,
        string RepresentationFormat
    );

    /// <summary>
    /// `namedtuple(typename, field_names, *, rename=False, defaults=None, module=None)`.
    /// </summary>
    private static PythonManagedTypeValue Create(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = PythonKeywordArguments.Bind(
            "namedtuple",
            ["typename", "field_names", "rename", "defaults", "module"],
            positionalOnly: 2,
            positional,
            keywordNames,
            keywordValues,
            span
        );
        if (bound[0] is null || bound[1] is null)
        {
            var missing = bound[0] is null ? "typename" : "field_names";
            var position = bound[0] is null ? 1 : 2;
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"namedtuple() missing required argument '{missing}' (pos {position})",
                span,
                "TypeError"
            );
        }
        var rename = bound[2] is { } renameValue && ManagedObjectProtocols.IsTrue(renameValue);
        var defaults = bound[3] ?? PythonNoneValue.Instance;
        var definition = Define(bound[0]!, bound[1]!, rename, defaults, span);
        var type = Build(definition, span);
        if (bound[4] is { } module)
            SetModule(type, module);
        return type;
    }

    /// <summary>`module=`: the module the class reports, which is otherwise the caller's.</summary>
    private static void SetModule(PythonValue type, PythonValue module)
    {
        if (type is not PythonManagedTypeValue managed)
            return;
        managed.Attributes["__module__"] = module;
        if (module is PythonTextValue text)
            managed.Module = text.Value;
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    /// <summary>The field names and everything derived from them, in the source's order.</summary>
    private static Definition Define(
        PythonValue typeArgument,
        PythonValue fieldsArgument,
        bool rename,
        PythonValue defaultsArgument,
        TextSpan span
    )
    {
        var typeName = Text(
            ManagedObjectProtocols.Call(PythonBuiltinTypes.Str, [typeArgument], span)
        );
        var fieldNames = FieldNames(fieldsArgument, span);

        if (rename)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < fieldNames.Count; index++)
            {
                var name = fieldNames[index];
                if (
                    !PythonIdentifier.IsIdentifier(name)
                    || PythonKeywordArguments.IsLanguageKeyword(name)
                    || name.StartsWith('_')
                    || seen.Contains(name)
                )
                {
                    fieldNames[index] = $"_{index}";
                }
                seen.Add(name);
            }
        }

        foreach (var name in new[] { typeName }.Concat(fieldNames))
        {
            if (!PythonIdentifier.IsIdentifier(name))
                throw Invalid(
                    $"Type names and field names must be valid identifiers: {Quote(name)}",
                    span
                );
            if (PythonKeywordArguments.IsLanguageKeyword(name))
                throw Invalid(
                    $"Type names and field names cannot be a keyword: {Quote(name)}",
                    span
                );
        }

        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in fieldNames)
        {
            if (name.StartsWith('_') && !rename)
                throw Invalid($"Field names cannot start with an underscore: {Quote(name)}", span);
            if (!duplicates.Add(name))
                throw Invalid($"Encountered duplicate field name: {Quote(name)}", span);
        }

        var defaults = Defaults(defaultsArgument, fieldNames.Count, span);
        var fieldDefaults = new PythonDictionaryValue([]);
        for (var index = fieldNames.Count - defaults.Length; index < fieldNames.Count; index++)
        {
            ManagedObjectProtocols.SetItem(
                fieldDefaults,
                new PythonTextValue(fieldNames[index]),
                defaults[index - (fieldNames.Count - defaults.Length)]
            );
        }

        var argumentList = string.Join(", ", fieldNames);
        if (fieldNames.Count == 1)
            argumentList += ",";
        var representation = "(" + string.Join(", ", fieldNames.Select(name => $"{name}=%r")) + ")";
        return new Definition(
            typeName,
            [.. fieldNames],
            defaults,
            fieldDefaults,
            $"{typeName}({argumentList})",
            argumentList,
            representation
        );
    }

    /// <summary>
    /// The names as the source reads them: a string is split on commas and whitespace, and
    /// every other iterable is walked into a list, each name converted with `str()`.
    /// </summary>
    private static List<string> FieldNames(PythonValue fields, TextSpan span)
    {
        if (fields is PythonTextValue text)
        {
            var normalized = text.Value.Replace(',', ' ');
            return
            [
                .. normalized
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name),
            ];
        }
        var names = new List<string>();
        var iterator = ManagedObjectProtocols.GetIterator(fields, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var name, span))
            names.Add(Text(ManagedObjectProtocols.Call(PythonBuiltinTypes.Str, [name], span)));
        return names;
    }

    /// <summary>`defaults=`: the values filling the last fields, in order.</summary>
    private static PythonValue[] Defaults(PythonValue given, int fieldCount, TextSpan span)
    {
        if (given is PythonNoneValue)
            return [];
        var values = ManagedObjectProtocols.MaterializeValues(given, span);
        if (values.Count > fieldCount)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Got more default values than field names",
                span,
                "TypeError"
            );
        return [.. values];
    }

    private static string Text(PythonValue value) =>
        value is PythonTextValue text
            ? text.Value
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object is not a string",
                default,
                "TypeError"
            );

    private static string Quote(string name) => new PythonTextValue(name).ToRepresentationString();

    private static PythonRuntimeException Invalid(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "ValueError");

    // -------------------------------------------------------------------------
    // The class
    // -------------------------------------------------------------------------

    /// <summary>`type(typename, (tuple,), namespace)`, with the namespace the source builds.</summary>
    private static PythonManagedTypeValue Build(Definition definition, TextSpan span)
    {
        var members = new PythonDictionaryValue([]);
        void Field(string name, PythonValue value) =>
            ManagedObjectProtocols.SetItem(members, new PythonTextValue(name), value);

        Field("__doc__", new PythonTextValue(definition.Doc));
        Field("__slots__", new PythonTupleValue([]));
        Field("_fields", new PythonTupleValue([.. definition.Fields.Select(FieldName)]));
        Field("_field_defaults", definition.FieldDefaults);
        Field(
            "__new__",
            new PythonStaticMethodValue(
                Method(
                    "__new__",
                    definition,
                    (receiver, arguments, definition) => New(receiver, arguments, definition),
                    keywords: (receiver, positional, names, values) =>
                        New(receiver, positional, definition, names, values),
                    doc: $"Create new instance of {definition.TypeName}({definition.ArgumentList})",
                    defaults: definition.Defaults
                )
            )
        );
        Field(
            "_make",
            new PythonClassMethodValue(
                Method(
                    "_make",
                    definition,
                    (receiver, arguments, definition) => Make(receiver, arguments, definition)
                )
            )
        );
        var replace = Method(
            "_replace",
            definition,
            (receiver, arguments, definition) => Replace(receiver, arguments, definition),
            keywords: (receiver, positional, names, values) =>
                Replace(receiver, positional, definition, names, values)
        );
        Field("_replace", replace);
        Field("__replace__", replace);
        Field(
            "__repr__",
            Method(
                "__repr__",
                definition,
                (receiver, arguments, definition) => Represent(receiver, arguments, definition)
            )
        );
        Field(
            "_asdict",
            Method(
                "_asdict",
                definition,
                (receiver, arguments, definition) => AsDictionary(receiver, arguments, definition)
            )
        );
        Field(
            "__getnewargs__",
            Method(
                "__getnewargs__",
                definition,
                (receiver, arguments, definition) => NewArguments(receiver, arguments, definition)
            )
        );
        Field("__match_args__", new PythonTupleValue([.. definition.Fields.Select(FieldName)]));
        for (var index = 0; index < definition.Fields.Length; index++)
        {
            Field(
                definition.Fields[index],
                new PythonTupleGetterValue(
                    definition.Fields[index],
                    index,
                    $"Alias for field number {index}"
                )
            );
        }

        var type = (PythonManagedTypeValue)
            UserObjectProtocols.Dispatcher!.CallType(
                PythonBuiltinTypes.Type,
                [
                    new PythonTextValue(definition.TypeName),
                    new PythonTupleValue([PythonBuiltinTypes.Tuple]),
                    members,
                ],
                [],
                [],
                span
            );
        // The class reports the module it was defined in, which is the frame that called the
        // factory — the module being executed here.
        SetModule(type, new PythonTextValue(CurrentModule()));
        return type;
    }

    /// <summary>The module the factory was called from, which is the caller's frame.</summary>
    private static string CurrentModule() =>
        UserObjectProtocols.Dispatcher!.CurrentModuleName() ?? "__main__";

    private static PythonTextValue FieldName(string name) => new(name);

    /// <summary>
    /// One of the functions the source creates for the class. They are functions written in
    /// Python, qualified by the class they were made for, and they take the receiver the way
    /// any function object does.
    /// </summary>
    private static PythonProtocolFunctionValue Method(
        string name,
        Definition definition,
        Func<PythonValue?, IReadOnlyList<PythonValue>, Definition, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        string? doc = null,
        PythonValue[]? defaults = null
    ) =>
        new(name, (receiver, arguments) => body(receiver, arguments, definition), keywords)
        {
            DeclaringType = definition.TypeName,
            IsPythonMethod = true,
            Doc = doc,
            Defaults = defaults is null or { Length: 0 }
                ? null
                : new PythonTupleValue([.. defaults]),
        };

    /// <summary>
    /// `__new__(_cls, …)`: the fields bound by name, with the defaults filling the last ones,
    /// and the tuple they make.
    /// </summary>
    private static PythonManagedObjectValue New(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition,
        IReadOnlyList<string>? keywordNames = null,
        IReadOnlyList<PythonValue>? keywordValues = null
    )
    {
        var (type, rest) = Receiver(receiver, arguments);
        var count = definition.Fields.Length;
        if (rest.Count > count)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{definition.TypeName}.__new__() takes {count + 1} positional arguments "
                    + $"but {rest.Count + 1} were given",
                default,
                "TypeError"
            );
        var values = new PythonValue?[count];
        for (var index = 0; index < rest.Count; index++)
            values[index] = rest[index];
        for (var index = 0; index < (keywordNames?.Count ?? 0); index++)
        {
            var name = keywordNames![index];
            var position = Array.IndexOf(definition.Fields, name);
            if (position < 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{definition.TypeName}.__new__() got an unexpected keyword argument "
                        + $"'{name}'",
                    default,
                    "TypeError"
                );
            if (values[position] is not null)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{definition.TypeName}.__new__() got multiple values for argument '{name}'",
                    default,
                    "TypeError"
                );
            values[position] = keywordValues![index];
        }
        var missing = new List<string>();
        for (var index = 0; index < count; index++)
        {
            if (values[index] is not null)
                continue;
            var defaultIndex = index - (count - definition.Defaults.Length);
            if (defaultIndex >= 0)
                values[index] = definition.Defaults[defaultIndex];
            else
                missing.Add(definition.Fields[index]);
        }
        if (missing.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{definition.TypeName}.__new__() missing {missing.Count} required positional "
                    + $"argument{(missing.Count == 1 ? "" : "s")}: {JoinNames(missing)}",
                default,
                "TypeError"
            );
        return new PythonManagedObjectValue(
            (PythonManagedTypeValue)(
                type as PythonManagedTypeValue
                ?? throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"'{ManagedObjectProtocols.GetTypeName(type)}' is not a named tuple class",
                    default,
                    "TypeError"
                )
            ),
            new PythonTupleValue([.. values.Select(v => v!)])
        );
    }

    /// <summary>CPython's own list wording: `'a'`, `'a' and 'b'`, `'a', 'b', and 'c'`.</summary>
    private static string JoinNames(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"'{name}'").ToArray();
        return quoted.Length switch
        {
            1 => quoted[0],
            2 => $"{quoted[0]} and {quoted[1]}",
            _ => string.Join(", ", quoted.Take(quoted.Length - 1)) + $", and {quoted[^1]}",
        };
    }

    /// <summary>`_make(cls, iterable)`: the tuple the class is built on, with its length checked.</summary>
    private static PythonManagedObjectValue Make(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition
    )
    {
        var (cls, rest) = Receiver(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"_make() takes exactly one argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var values = ManagedObjectProtocols.MaterializeValues(rest[0], default);
        if (values.Count != definition.Fields.Length)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Expected {definition.Fields.Length} arguments, got {values.Count}",
                default,
                "TypeError"
            );
        return new PythonManagedObjectValue(
            (PythonManagedTypeValue)(
                cls as PythonManagedTypeValue
                ?? throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"'{ManagedObjectProtocols.GetTypeName(cls)}' is not a named tuple class",
                    default,
                    "TypeError"
                )
            ),
            new PythonTupleValue([.. values])
        );
    }

    /// <summary>`_replace(self, /, **kwds)`: the fields with the named ones replaced.</summary>
    private static PythonManagedObjectValue Replace(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition,
        IReadOnlyList<string>? keywordNames = null,
        IReadOnlyList<PythonValue>? keywordValues = null
    )
    {
        var (self, rest) = Receiver(receiver, arguments);
        if (rest.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{definition.TypeName}._replace() takes 1 positional argument but "
                    + $"{rest.Count + 1} were given",
                default,
                "TypeError"
            );
        var remaining = new PythonDictionaryValue([]);
        for (var index = 0; index < (keywordNames?.Count ?? 0); index++)
        {
            ManagedObjectProtocols.SetItem(
                remaining,
                new PythonTextValue(keywordNames![index]),
                keywordValues![index]
            );
        }
        var values = new PythonValue[definition.Fields.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var name = new PythonTextValue(definition.Fields[index]);
            values[index] = ManagedObjectProtocols.TryFindDictionaryItem(
                remaining,
                name,
                out var replacement
            )
                ? Remove(remaining, name, replacement.Value)
                : Element(self, index);
        }
        if (remaining.Items.Count != 0)
        {
            var listed = string.Join(
                ", ",
                remaining.Items.Select(item => item.Key.ToRepresentationString())
            );
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Got unexpected field names: [{listed}]",
                default,
                "TypeError"
            );
        }
        return new PythonManagedObjectValue(
            PythonUserTypes.ClassOf(self) as PythonManagedTypeValue
                ?? throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "'_replace' requires a named tuple instance",
                    default,
                    "TypeError"
                ),
            new PythonTupleValue(values)
        );
    }

    /// <summary>`_replace`'s `kwds.pop`: the value is taken out of the dictionary as it is used.</summary>
    private static PythonValue Remove(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value
    )
    {
        if (ManagedObjectProtocols.TryFindDictionaryItem(dictionary, key, out var item))
            dictionary.RemoveItem(item);
        return value;
    }

    /// <summary>`__repr__`: `Point(x=11, y=22)`, over the fields and the tuple's items.</summary>
    private static PythonTextValue Represent(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition
    )
    {
        var (self, _) = Receiver(receiver, arguments);
        var name = PythonUserTypes.ClassOf(self) is PythonManagedTypeValue type
            ? type.Name
            : definition.TypeName;
        var values = new PythonTupleValue([
            .. Enumerable.Range(0, definition.Fields.Length).Select(index => Element(self, index)),
        ]);
        return new PythonTextValue(
            name
                + PythonTextFormatting.FormatPercent(
                    definition.RepresentationFormat,
                    values,
                    default
                )
        );
    }

    /// <summary>`_asdict`: the field names mapped to the items they name.</summary>
    private static PythonDictionaryValue AsDictionary(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition
    )
    {
        var (self, _) = Receiver(receiver, arguments);
        var dictionary = new PythonDictionaryValue([]);
        for (var index = 0; index < definition.Fields.Length; index++)
        {
            ManagedObjectProtocols.SetItem(
                dictionary,
                new PythonTextValue(definition.Fields[index]),
                Element(self, index)
            );
        }
        return dictionary;
    }

    /// <summary>`__getnewargs__`: the items as a plain tuple, which is what copy and pickle
    /// carry.</summary>
    private static PythonTupleValue NewArguments(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Definition definition
    )
    {
        var (self, _) = Receiver(receiver, arguments);
        return new PythonTupleValue([
            .. Enumerable.Range(0, definition.Fields.Length).Select(index => Element(self, index)),
        ]);
    }

    /// <summary>The item at a field's index, read off the tuple the instance carries.</summary>
    private static PythonValue Element(PythonValue instance, int index) =>
        PythonSubclassStorage.Of(instance) is PythonTupleValue tuple
        && index < tuple.Elements.Length
            ? tuple.Elements[index]
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "named tuple methods require a named tuple instance",
                default,
                "TypeError"
            );

    /// <summary>The receiver and the remaining arguments, bound or handed over first.</summary>
    private static (PythonValue Self, IReadOnlyList<PythonValue> RestArguments) Receiver(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    ) =>
        receiver is not null ? (receiver, arguments)
        : arguments.Count != 0 ? (arguments[0], [.. arguments.Skip(1)])
        : throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "unbound method call needs a receiver",
            default,
            "TypeError"
        );
}
