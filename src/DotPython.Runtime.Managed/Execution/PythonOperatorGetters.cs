// The `operator` getters follow CPython 3.14.7 Modules/_operator.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The three callable classes <c>operator</c> publishes beside its functions:
/// <c>itemgetter</c>, <c>attrgetter</c> and <c>methodcaller</c>.
/// </summary>
/// <remarks>
/// Each is a C type with its own state — the keys, the names or the call the instance was
/// built with — so each is a managed type here whose instances keep that state beside them.
/// The state is held in a side table rather than in the instance's own dictionary, which is
/// what keeps `dir()` of one empty, as CPython's `tp_members`-free types are.
/// </remarks>
internal static class PythonOperatorGetters
{
    /// <summary>The state one getter instance carries.</summary>
    private sealed class State
    {
        internal PythonValue[] Keys { get; set; } = [];

        /// <summary>The method a `methodcaller` calls, and what it calls it with.</summary>
        internal string Method { get; set; } = string.Empty;

        internal PythonValue[] Arguments { get; set; } = [];
        internal string[] KeywordNames { get; set; } = [];
        internal PythonValue[] KeywordValues { get; set; } = [];
    }

    private static readonly ConditionalWeakTable<PythonManagedObjectValue, State> States = new();

    internal static readonly PythonManagedTypeValue ItemGetterType = Create(
        "itemgetter",
        "Return a callable object that fetches the given item(s) from its operand.",
        ItemObject,
        ItemCall,
        ItemRepresentation
    );

    internal static readonly PythonManagedTypeValue AttributeGetterType = Create(
        "attrgetter",
        "Return a callable object that fetches the given attribute(s) from its operand.",
        AttributeObject,
        AttributeCall,
        AttributeRepresentation
    );

    internal static readonly PythonManagedTypeValue MethodCallerType = Create(
        "methodcaller",
        "Return a callable object that calls the given method on its operand.",
        MethodObject,
        MethodCall,
        MethodRepresentation
    );

    private static PythonManagedTypeValue Create(
        string name,
        string doc,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> initialize,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> call,
        Func<PythonValue, string> representation
    )
    {
        var type = new PythonManagedTypeValue(name)
        {
            Module = "operator",
            QualName = name,
            ReportsQualifiedName = true,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("operator");
        type.Attributes["__doc__"] = new PythonTextValue(doc + "\n");
        type.Attributes["__init__"] = new PythonProtocolFunctionValue(
            "__init__",
            (receiver, arguments) => initialize(receiver, arguments),
            (receiver, positional, keywordNames, keywordValues) =>
            {
                initialize(receiver, positional);
                if (keywordNames.Count != 0 && receiver is PythonManagedObjectValue instance)
                    MethodKeywords(instance, keywordNames, keywordValues);
                return PythonNoneValue.Instance;
            }
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (receiver, arguments) => call(receiver, arguments)
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (receiver, _) =>
                // The C type reports itself by its qualified name, `operator.itemgetter(1)`.
                new PythonTextValue($"operator.{name}({representation(Require(receiver, name))})")
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        // The state is what a copy and a pickle carry, so the reduction is the class and the
        // arguments the instance was built with.
        // A copy carries the state the instance was built with, which the reduction also
        // describes: the runtime's copier reaches `__copy__` first.
        type.Attributes["__copy__"] = new PythonProtocolFunctionValue(
            "__copy__",
            (receiver, _) =>
            {
                var instance = Require(receiver, name);
                var state = StateOf(instance);
                var copy = new PythonManagedObjectValue(
                    (PythonManagedTypeValue)
                        ManagedObjectProtocols.GetAttribute(instance, "__class__")
                );
                var copied = StateOf(copy);
                copied.Keys = [.. state.Keys];
                copied.Method = state.Method;
                copied.Arguments = [.. state.Arguments];
                copied.KeywordNames = [.. state.KeywordNames];
                copied.KeywordValues = [.. state.KeywordValues];
                return copy;
            }
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        type.Attributes["__deepcopy__"] = new PythonProtocolFunctionValue(
            "__deepcopy__",
            (receiver, _) =>
                ManagedObjectProtocols.Call(
                    ManagedObjectProtocols.GetAttribute(Require(receiver, name), "__copy__"),
                    []
                )
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        type.Attributes["__reduce__"] = new PythonProtocolFunctionValue(
            "__reduce__",
            (receiver, _) =>
            {
                var instance = Require(receiver, name);
                var state = StateOf(instance);
                return new PythonTupleValue([
                    ManagedObjectProtocols.GetAttribute(instance, "__class__"),
                    new PythonTupleValue([.. Representation(state)]),
                ]);
            }
        )
        {
            DeclaringType = name,
            IsPythonMethod = true,
        };
        return type;
    }

    /// <summary>The arguments a reduction is rebuilt from: the keywords a `methodcaller` was
    /// built with followed by its positional ones, which is the order its repr shows.</summary>
    private static List<PythonValue> Representation(State state) =>
        state.Method.Length != 0
            ? [new PythonTextValue(state.Method), .. state.Arguments]
            : [.. state.Keys];

    private static State StateOf(PythonValue instance) =>
        instance is PythonManagedObjectValue managed
            ? States.GetOrCreateValue(managed)
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(instance)}' is not a getter",
                default,
                "TypeError"
            );

    // -------------------------------------------------------------------------
    // itemgetter
    // -------------------------------------------------------------------------

    private static PythonValue ItemObject(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var instance = Require(receiver, "itemgetter");
        if (arguments.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "itemgetter expected 1 argument, got 0",
                default,
                "TypeError"
            );
        StateOf(instance).Keys = [.. arguments];
        return PythonNoneValue.Instance;
    }

    private static PythonValue ItemCall(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var instance = Require(receiver, "itemgetter");
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"itemgetter() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var keys = StateOf(instance).Keys;
        if (keys.Length == 1)
            return ManagedObjectProtocols.GetItem(arguments[0], keys[0]);
        return new PythonTupleValue([
            .. keys.Select(key => ManagedObjectProtocols.GetItem(arguments[0], key)),
        ]);
    }

    private static string ItemRepresentation(PythonValue instance) =>
        string.Join(", ", StateOf(instance).Keys.Select(key => key.ToRepresentationString()));

    // -------------------------------------------------------------------------
    // attrgetter
    // -------------------------------------------------------------------------

    private static PythonValue AttributeObject(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var instance = Require(receiver, "attrgetter");
        if (arguments.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "attrgetter expected 1 argument, got 0",
                default,
                "TypeError"
            );
        StateOf(instance).Keys = [.. arguments];
        return PythonNoneValue.Instance;
    }

    private static PythonValue AttributeCall(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var instance = Require(receiver, "attrgetter");
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"attrgetter() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var names = StateOf(instance).Keys;
        if (names.Length == 1)
            return Attribute(arguments[0], names[0]);
        return new PythonTupleValue([.. names.Select(name => Attribute(arguments[0], name))]);
    }

    /// <summary>One attribute, walking the dots a name may contain.</summary>
    private static PythonValue Attribute(PythonValue target, PythonValue name)
    {
        if (name is not PythonTextValue text)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "attribute name must be a string",
                default,
                "TypeError"
            );
        var current = target;
        foreach (var part in text.Value.Split('.'))
        {
            try
            {
                current = ManagedObjectProtocols.GetAttribute(current, part);
            }
            catch (PythonRuntimeException fault)
                when (fault.PythonExceptionTypeName == "AttributeError")
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"'{ManagedObjectProtocols.GetTypeName(current)}' object has no attribute "
                        + $"'{part}'",
                    default,
                    "AttributeError"
                );
            }
        }
        return current;
    }

    private static string AttributeRepresentation(PythonValue instance) =>
        string.Join(", ", StateOf(instance).Keys.Select(key => key.ToRepresentationString()));

    // -------------------------------------------------------------------------
    // methodcaller
    // -------------------------------------------------------------------------

    private static PythonValue MethodObject(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var instance = Require(receiver, "methodcaller");
        if (arguments.Count == 0 || arguments[0] is not PythonTextValue name)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "methodcaller needs an argument",
                default,
                "TypeError"
            );
        var state = StateOf(instance);
        state.Method = name.Value;
        state.Arguments = [.. arguments.Skip(1)];
        state.KeywordNames = [];
        state.KeywordValues = [];
        return PythonNoneValue.Instance;
    }

    private static PythonValue MethodCall(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var instance = Require(receiver, "methodcaller");
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"methodcaller() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var state = StateOf(instance);
        // The method is called through the dispatcher, which is what a call of a function
        // written in Python needs.
        var method = ManagedObjectProtocols.GetAttribute(arguments[0], state.Method);
        if (state.KeywordNames.Length == 0)
            return UserObjectProtocols.Dispatcher!.Invoke(method, state.Arguments, default);
        return UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
            method,
            state.Arguments,
            state.KeywordNames,
            state.KeywordValues,
            default
        );
    }

    /// <summary>`methodcaller` keeps the keywords its construction carried, which the
    /// positional form of the constructor does not see.</summary>
    internal static PythonValue MethodKeywords(
        PythonManagedObjectValue instance,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var state = StateOf(instance);
        state.KeywordNames = [.. keywordNames];
        state.KeywordValues = [.. keywordValues];
        return PythonNoneValue.Instance;
    }

    private static string MethodRepresentation(PythonValue instance)
    {
        var state = StateOf(instance);
        var parts = new List<string> { new PythonTextValue(state.Method).ToRepresentationString() };
        parts.AddRange(state.Arguments.Select(argument => argument.ToRepresentationString()));
        parts.AddRange(
            state.KeywordNames.Select(
                (name, index) => $"{name}={state.KeywordValues[index].ToRepresentationString()}"
            )
        );
        return string.Join(", ", parts);
    }

    private static PythonValue Require(PythonValue? receiver, string owner) =>
        receiver as PythonManagedObjectValue
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"descriptor '{owner}' requires a '{owner}' object",
            default,
            "TypeError"
        );
}
