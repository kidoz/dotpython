// The argument-count diagnostics follow CPython 3.14.7, where each builtin method is written
// with its own argument parser and so reports its own sentence:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// How a method reports a call with the wrong number of arguments. The shape is a property of
/// the method rather than of the runtime, so it is declared here per method and owner.
/// </summary>
internal static class PythonMethodWording
{
    private enum Style
    {
        /// <summary>`list.append() takes exactly one argument (0 given)`</summary>
        ExactOne,

        /// <summary>`list.copy() takes no arguments (2 given)`</summary>
        NoArguments,

        /// <summary>`sort() takes at most 2 arguments (3 given)`</summary>
        AtMostParenthesized,

        /// <summary>`replace() takes at least 2 positional arguments (1 given)`</summary>
        AtLeastPositional,

        /// <summary>
        /// `find expected at least 1 argument, got 0` and `get expected at most 2
        /// arguments, got 3` — the same method, worded by which bound was passed.
        /// </summary>
        Range,

        /// <summary>`insert expected 2 arguments, got 0`</summary>
        Expected,
    }

    /// <summary>
    /// The sentence for a bound call of <paramref name="method"/> on
    /// <paramref name="owner"/> that was given <paramref name="count"/> arguments.
    /// </summary>
    internal static string Arity(string owner, string method, int count)
    {
        var (style, minimum, maximum) = Declarations.TryGetValue((owner, method), out var rule)
            ? rule
            : (Style.Range, 0, int.MaxValue);
        return style switch
        {
            Style.ExactOne => $"{owner}.{method}() takes exactly one argument ({count} given)",
            Style.NoArguments => $"{owner}.{method}() takes no arguments ({count} given)",
            Style.AtMostParenthesized => $"{method}() takes at most {maximum} "
                + $"argument{Plural(maximum)} ({count} given)",
            Style.AtLeastPositional => $"{method}() takes at least {minimum} positional "
                + $"argument{Plural(minimum)} ({count} given)",
            Style.Range when count < minimum =>
                $"{method} expected at least {minimum} argument{Plural(minimum)}, got {count}",
            Style.Range =>
                $"{method} expected at most {maximum} argument{Plural(maximum)}, got {count}",
            _ => $"{method} expected {maximum} arguments, got {count}",
        };
    }

    /// <summary>
    /// The rule a declaration states, or null when the method reports a call in a way this
    /// table does not describe — the caller then keeps its own default sentence.
    /// </summary>
    internal static (int Minimum, int Maximum)? Bounds(string owner, string method) =>
        Declarations.TryGetValue((owner, method), out var rule)
            ? (rule.Minimum, rule.Maximum)
            : null;

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static readonly Dictionary<
        (string Owner, string Method),
        (Style Style, int Minimum, int Maximum)
    > Declarations = new()
    {
        [("list", "append")] = (Style.ExactOne, 1, 1),
        [("list", "extend")] = (Style.ExactOne, 1, 1),
        [("list", "remove")] = (Style.ExactOne, 1, 1),
        [("list", "count")] = (Style.ExactOne, 1, 1),
        [("list", "copy")] = (Style.NoArguments, 0, 0),
        [("list", "reverse")] = (Style.NoArguments, 0, 0),
        [("list", "clear")] = (Style.NoArguments, 0, 0),
        [("list", "index")] = (Style.Range, 1, 3),
        [("list", "insert")] = (Style.Expected, 2, 2),
        [("list", "pop")] = (Style.Range, 0, 1),
        [("list", "sort")] = (Style.AtMostParenthesized, 0, 2),

        [("tuple", "count")] = (Style.ExactOne, 1, 1),
        [("tuple", "index")] = (Style.Range, 1, 3),

        [("str", "upper")] = (Style.NoArguments, 0, 0),
        [("str", "lower")] = (Style.NoArguments, 0, 0),
        [("str", "title")] = (Style.NoArguments, 0, 0),
        [("str", "capitalize")] = (Style.NoArguments, 0, 0),
        [("str", "swapcase")] = (Style.NoArguments, 0, 0),
        [("str", "casefold")] = (Style.NoArguments, 0, 0),
        [("str", "join")] = (Style.ExactOne, 1, 1),
        [("str", "split")] = (Style.AtMostParenthesized, 0, 2),
        [("str", "rsplit")] = (Style.AtMostParenthesized, 0, 2),
        [("str", "splitlines")] = (Style.AtMostParenthesized, 0, 1),
        [("str", "strip")] = (Style.Range, 0, 1),
        [("str", "lstrip")] = (Style.Range, 0, 1),
        [("str", "rstrip")] = (Style.Range, 0, 1),
        [("str", "replace")] = (Style.AtLeastPositional, 2, 3),
        [("str", "find")] = (Style.Range, 1, 3),
        [("str", "rfind")] = (Style.Range, 1, 3),
        [("str", "index")] = (Style.Range, 1, 3),
        [("str", "rindex")] = (Style.Range, 1, 3),
        [("str", "count")] = (Style.Range, 1, 3),
        [("str", "startswith")] = (Style.Range, 1, 3),
        [("str", "endswith")] = (Style.Range, 1, 3),
        [("str", "center")] = (Style.Range, 1, 2),
        [("str", "ljust")] = (Style.Range, 1, 2),
        [("str", "rjust")] = (Style.Range, 1, 2),
        [("str", "zfill")] = (Style.ExactOne, 1, 1),
        [("str", "expandtabs")] = (Style.Range, 0, 1),
        [("str", "partition")] = (Style.ExactOne, 1, 1),
        [("str", "rpartition")] = (Style.ExactOne, 1, 1),
        [("str", "removeprefix")] = (Style.ExactOne, 1, 1),
        [("str", "removesuffix")] = (Style.ExactOne, 1, 1),
        [("str", "encode")] = (Style.AtMostParenthesized, 0, 2),
        [("str", "format")] = (Style.AtLeastPositional, 0, int.MaxValue),
        [("str", "translate")] = (Style.ExactOne, 1, 1),

        [("bytes", "upper")] = (Style.NoArguments, 0, 0),
        [("bytes", "lower")] = (Style.NoArguments, 0, 0),
        [("bytes", "title")] = (Style.NoArguments, 0, 0),
        [("bytes", "capitalize")] = (Style.NoArguments, 0, 0),
        [("bytes", "swapcase")] = (Style.NoArguments, 0, 0),
        [("bytes", "split")] = (Style.AtMostParenthesized, 0, 2),
        [("bytes", "rsplit")] = (Style.AtMostParenthesized, 0, 2),
        [("bytes", "splitlines")] = (Style.AtMostParenthesized, 0, 1),
        [("bytes", "strip")] = (Style.Range, 0, 1),
        [("bytes", "lstrip")] = (Style.Range, 0, 1),
        [("bytes", "rstrip")] = (Style.Range, 0, 1),
        [("bytes", "find")] = (Style.Range, 1, 3),
        [("bytes", "rfind")] = (Style.Range, 1, 3),
        [("bytes", "index")] = (Style.Range, 1, 3),
        [("bytes", "rindex")] = (Style.Range, 1, 3),
        [("bytes", "count")] = (Style.Range, 1, 3),
        [("bytes", "startswith")] = (Style.Range, 1, 3),
        [("bytes", "endswith")] = (Style.Range, 1, 3),
        [("bytes", "replace")] = (Style.AtLeastPositional, 2, 3),
        [("bytes", "join")] = (Style.ExactOne, 1, 1),
        [("bytes", "center")] = (Style.Range, 1, 2),
        [("bytes", "ljust")] = (Style.Range, 1, 2),
        [("bytes", "rjust")] = (Style.Range, 1, 2),
        [("bytes", "zfill")] = (Style.ExactOne, 1, 1),
        [("bytes", "expandtabs")] = (Style.Range, 0, 1),
        [("bytes", "partition")] = (Style.ExactOne, 1, 1),
        [("bytes", "rpartition")] = (Style.ExactOne, 1, 1),
        [("bytes", "removeprefix")] = (Style.ExactOne, 1, 1),
        [("bytes", "removesuffix")] = (Style.ExactOne, 1, 1),
        [("bytes", "translate")] = (Style.ExactOne, 1, 1),
        [("bytes", "decode")] = (Style.AtMostParenthesized, 0, 2),

        [("bytearray", "append")] = (Style.ExactOne, 1, 1),
        [("bytearray", "extend")] = (Style.ExactOne, 1, 1),
        [("bytearray", "insert")] = (Style.Expected, 2, 2),
        [("bytearray", "remove")] = (Style.ExactOne, 1, 1),
        [("bytearray", "count")] = (Style.ExactOne, 1, 1),
        [("bytearray", "pop")] = (Style.Range, 0, 1),
        [("bytearray", "copy")] = (Style.NoArguments, 0, 0),
        [("bytearray", "reverse")] = (Style.NoArguments, 0, 0),
        [("bytearray", "clear")] = (Style.NoArguments, 0, 0),

        [("dict", "get")] = (Style.Range, 1, 2),
        [("dict", "pop")] = (Style.Range, 1, 2),
        [("dict", "setdefault")] = (Style.Range, 1, 2),
        [("dict", "update")] = (Style.Range, 0, 1),
        [("dict", "keys")] = (Style.NoArguments, 0, 0),
        [("dict", "values")] = (Style.NoArguments, 0, 0),
        [("dict", "items")] = (Style.NoArguments, 0, 0),
        [("dict", "copy")] = (Style.NoArguments, 0, 0),
        [("dict", "clear")] = (Style.NoArguments, 0, 0),
        [("dict", "popitem")] = (Style.NoArguments, 0, 0),

        [("set", "add")] = (Style.ExactOne, 1, 1),
        [("set", "remove")] = (Style.ExactOne, 1, 1),
        [("set", "discard")] = (Style.ExactOne, 1, 1),
        [("set", "issubset")] = (Style.ExactOne, 1, 1),
        [("set", "issuperset")] = (Style.ExactOne, 1, 1),
        [("set", "isdisjoint")] = (Style.ExactOne, 1, 1),
        [("set", "pop")] = (Style.NoArguments, 0, 0),
        [("set", "union")] = (Style.Range, 0, int.MaxValue),
        [("set", "difference")] = (Style.Range, 0, int.MaxValue),

        [("frozenset", "issubset")] = (Style.ExactOne, 1, 1),
        [("frozenset", "issuperset")] = (Style.ExactOne, 1, 1),
        [("frozenset", "isdisjoint")] = (Style.ExactOne, 1, 1),
    };
}
