// The str methods follow CPython 3.14.7 Objects/unicodeobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `str` methods that are neither casing nor the older search surface, together with the
/// `is*` predicates over the pinned Unicode 16 properties.
/// </summary>
internal static class PythonTextMethods
{
    private static readonly char[] LineBreaks =
    [
        '\n',
        '\r',
        '\v',
        '\f',
        '\u001c',
        '\u001d',
        '\u001e',
        '\u0085',
        '\u2028',
        '\u2029',
    ];

    internal static Dictionary<string, PythonProtocolFunctionValue> CreateTable() =>
        new(StringComparer.Ordinal)
        {
            ["center"] = Pad("center", Centre),
            ["ljust"] = Pad("ljust", Left),
            ["rjust"] = Pad("rjust", Right),
            ["zfill"] = ZFill(),
            ["expandtabs"] = ExpandTabs(),
            ["partition"] = Partition("partition", reverse: false),
            ["rpartition"] = Partition("rpartition", reverse: true),
            ["rsplit"] = RSplit(),
            ["splitlines"] = SplitLines(),
            ["removeprefix"] = RemoveAffix("removeprefix", prefix: true),
            ["removesuffix"] = RemoveAffix("removesuffix", prefix: false),
            ["rfind"] = Search("rfind", rfind: true, raiseWhenMissing: false),
            ["rindex"] = Search("rindex", rfind: true, raiseWhenMissing: true),
            ["translate"] = Translate(),
            ["isalpha"] = Predicate("isalpha", AllCharacters(PythonTextPredicates.IsAlpha)),
            ["isalnum"] = Predicate("isalnum", AllCharacters(IsAlphanumeric)),
            ["isdecimal"] = Predicate("isdecimal", AllCharacters(PythonTextPredicates.IsDecimal)),
            ["isdigit"] = Predicate("isdigit", AllCharacters(PythonTextPredicates.IsDigit)),
            ["isnumeric"] = Predicate("isnumeric", AllCharacters(PythonTextPredicates.IsNumeric)),
            ["isspace"] = Predicate("isspace", AllCharacters(PythonTextPredicates.IsSpace)),
            ["isprintable"] = Predicate("isprintable", AllPrintable),
            ["isascii"] = Predicate("isascii", AllAscii),
            ["islower"] = Predicate("islower", text => CasePattern(text, lower: true)),
            ["isupper"] = Predicate("isupper", text => CasePattern(text, lower: false)),
            ["istitle"] = Predicate("istitle", IsTitle),
            ["isidentifier"] = Predicate("isidentifier", IsIdentifier),
        };

    // ---- predicates -------------------------------------------------------------------------

    private static bool IsAlphanumeric(int codePoint) =>
        PythonTextPredicates.IsAlpha(codePoint)
        || PythonTextPredicates.IsDecimal(codePoint)
        || PythonTextPredicates.IsDigit(codePoint)
        || PythonTextPredicates.IsNumeric(codePoint);

    private static Func<string, bool> AllCharacters(Func<int, bool> predicate) =>
        text =>
        {
            var any = false;
            foreach (var character in PythonTextTraversal.Enumerate(text))
            {
                if (!predicate(character.Value))
                    return false;
                any = true;
            }
            return any;
        };

    private static bool AllPrintable(string text)
    {
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            if (!PythonTextPredicates.IsPrintable(character.Value))
                return false;
        }
        return true;
    }

    private static bool AllAscii(string text)
    {
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            if (character.Value >= 128)
                return false;
        }
        return true;
    }

    /// <summary>
    /// `islower`/`isupper`: at least one character cased in the asked-for direction and none
    /// cased the other way, so a titlecase character answers false for both.
    /// </summary>
    private static bool CasePattern(string text, bool lower)
    {
        if (text.Length == 0)
            return false;
        var any = false;
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            var code = character.Value;
            if (lower ? PythonUnicodeCase.IsLower(code) : PythonUnicodeCase.IsUpper(code))
                any = true;
            else if (PythonUnicodeCase.IsCased(code))
                return false;
        }
        return any;
    }

    /// <summary>
    /// `istitle`: an uppercase or titlecase character may not follow another cased one, a
    /// lowercase character must follow one, and at least one character must be cased.
    /// </summary>
    private static bool IsTitle(string text)
    {
        if (text.Length == 0)
            return false;
        var any = false;
        var previousIsCased = false;
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            var code = character.Value;
            if (PythonUnicodeCase.IsUpper(code) || PythonTextPredicates.IsTitlecase(code))
            {
                if (previousIsCased)
                    return false;
                previousIsCased = true;
                any = true;
            }
            else if (PythonUnicodeCase.IsLower(code))
            {
                if (!previousIsCased)
                    return false;
                previousIsCased = true;
                any = true;
            }
            else
            {
                previousIsCased = false;
            }
        }
        return any;
    }

    /// <summary>`isidentifier`: a first character that may start one, then continuing ones.</summary>
    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0)
            return false;
        var first = true;
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            var code = character.Value;
            if (first)
            {
                // An underscore may start an identifier without being `XID_Start`.
                if (code != '_' && !PythonTextPredicates.IsXidStart(code))
                    return false;
                first = false;
                continue;
            }
            if (!PythonTextPredicates.IsXidContinue(code))
                return false;
        }
        return true;
    }

    // ---- padding and layout -----------------------------------------------------------------

    private static PythonProtocolFunctionValue Pad(
        string name,
        Func<string, int, string, string> action
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 2);
                var text = ((PythonTextValue)target!).Value;
                var width = RequireCount(name, arguments[0]);
                var fill = " ";
                if (arguments.Count > 1)
                {
                    // A non-string fill is refused before its length is considered.
                    var supplied = RequireText(
                        name,
                        arguments[1],
                        "The fill character must be a unicode character, not {1}"
                    );
                    // The test counts characters, so a surrogate pair is also refused.
                    if (PythonTextTraversal.Count(supplied) != 1)
                    {
                        throw Fault(
                            "The fill character must be exactly one character long",
                            "TypeError"
                        );
                    }
                    // Kept as text: one character may still be a surrogate pair.
                    fill = supplied;
                }
                return new PythonTextValue(action(text, width, fill));
            }
        );

    private static string Centre(string text, int width, string fill)
    {
        var length = PythonTextTraversal.Count(text);
        if (length >= width)
            return text;
        // CPython biases the left padding by `marg & width & 1` rather than always
        // giving the odd character to the right, so `'ab'.center(5)` is not symmetric.
        var extra = width - length;
        var left = extra / 2 + (extra & width & 1);
        return Repeat(fill, left) + text + Repeat(fill, extra - left);
    }

    private static string Left(string text, int width, string fill)
    {
        var length = PythonTextTraversal.Count(text);
        return length >= width ? text : text + Repeat(fill, width - length);
    }

    private static string Right(string text, int width, string fill)
    {
        var length = PythonTextTraversal.Count(text);
        return length >= width ? text : Repeat(fill, width - length) + text;
    }

    private static string Repeat(string fill, int count) =>
        count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(fill, count));

    private static PythonProtocolFunctionValue ZFill() =>
        new(
            "zfill",
            (target, arguments) =>
            {
                RequireArguments("zfill", arguments, 1, 1);
                var text = ((PythonTextValue)target!).Value;
                var width = RequireCount("zfill", arguments[0]);
                var length = PythonTextTraversal.Count(text);
                if (length >= width)
                    return new PythonTextValue(text);
                var padding = new string('0', width - length);
                // A leading sign keeps its position: `'-abc'.zfill(6)` is `'-00abc'`.
                var signed = text.Length > 0 && text[0] is '+' or '-';
                return new PythonTextValue(
                    signed ? $"{text[0]}{padding}{text[1..]}" : padding + text
                );
            }
        );

    private static PythonProtocolFunctionValue ExpandTabs() =>
        new(
            "expandtabs",
            (target, arguments) =>
            {
                RequireArguments("expandtabs", arguments, 0, 1);
                var text = ((PythonTextValue)target!).Value;
                var size =
                    arguments.Count > 0 && arguments[0] is not PythonNoneValue
                        ? RequireCount("expandtabs", arguments[0])
                        : 8;
                var builder = new StringBuilder(text.Length);
                var column = 0;
                foreach (var character in PythonTextTraversal.Enumerate(text))
                {
                    var code = character.Value;
                    if (code == '\t')
                    {
                        if (size > 0)
                        {
                            var spaces = size - column % size;
                            builder.Append(' ', spaces);
                            column += spaces;
                        }
                        continue;
                    }
                    builder.Append(character.ToString());
                    column = code is '\n' or '\r' ? 0 : column + 1;
                }
                return new PythonTextValue(builder.ToString());
            }
        );

    // ---- splitting --------------------------------------------------------------------------

    private static PythonProtocolFunctionValue Partition(string name, bool reverse) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 1);
                var text = ((PythonTextValue)target!).Value;
                var separator = RequireText(name, arguments[0], "must be str, not {1}");
                if (separator.Length == 0)
                    throw Fault("empty separator", "ValueError");
                var found = reverse
                    ? text.LastIndexOf(separator, StringComparison.Ordinal)
                    : text.IndexOf(separator, StringComparison.Ordinal);
                return found < 0
                    ? new PythonTupleValue(
                        reverse
                            ?
                            [
                                new PythonTextValue(string.Empty),
                                new PythonTextValue(string.Empty),
                                new PythonTextValue(text),
                            ]
                            :
                            [
                                new PythonTextValue(text),
                                new PythonTextValue(string.Empty),
                                new PythonTextValue(string.Empty),
                            ]
                    )
                    : new PythonTupleValue([
                        new PythonTextValue(text[..found]),
                        new PythonTextValue(separator),
                        new PythonTextValue(text[(found + separator.Length)..]),
                    ]);
            }
        );

    private static PythonProtocolFunctionValue RSplit() =>
        new(
            "rsplit",
            (target, arguments) =>
            {
                RequireArguments("rsplit", arguments, 0, 2);
                var text = ((PythonTextValue)target!).Value;
                var separator =
                    arguments.Count == 0 || arguments[0] is PythonNoneValue
                        ? null
                        : RequireText("rsplit", arguments[0], "must be str or None, not {1}");
                var limit =
                    arguments.Count > 1 && arguments[1] is not PythonNoneValue
                        ? RequireCount("rsplit", arguments[1])
                        : -1;
                if (separator is { Length: 0 })
                    throw Fault("empty separator", "ValueError");
                var pieces = separator is null
                    ? SplitOnWhitespace(text, limit)
                    : SplitOnSeparator(text, separator, limit);
                return new PythonListValue([.. pieces.Select(piece => new PythonTextValue(piece))]);
            }
        );

    private static List<string> SplitOnSeparator(string text, string separator, int limit)
    {
        var pieces = new List<string>();
        if (limit == 0)
        {
            pieces.Add(text);
            return pieces;
        }
        var cursor = text.Length;
        while (limit != 0)
        {
            var found = text.LastIndexOf(
                separator,
                Math.Max(0, cursor - 1),
                StringComparison.Ordinal
            );
            if (found < 0 || found >= cursor)
                break;
            pieces.Add(text[(found + separator.Length)..cursor]);
            cursor = found;
            if (limit > 0)
                limit--;
        }
        pieces.Add(text[..cursor]);
        pieces.Reverse();
        return pieces;
    }

    private static List<string> SplitOnWhitespace(string text, int limit)
    {
        var characters = PythonTextTraversal.Enumerate(text).ToArray();
        var pieces = new List<string>();
        var end = characters.Length;
        while (limit != 0)
        {
            while (end > 0 && PythonTextPredicates.IsSpace(characters[end - 1].Value))
                end--;
            if (end == 0)
                break;
            var start = end;
            while (start > 0 && !PythonTextPredicates.IsSpace(characters[start - 1].Value))
                start--;
            pieces.Add(Join(characters[start..end]));
            end = start;
            if (limit > 0)
                limit--;
        }
        while (end > 0 && PythonTextPredicates.IsSpace(characters[end - 1].Value))
            end--;
        if (end > 0)
            pieces.Add(Join(characters[..end]));
        pieces.Reverse();
        return pieces;
    }

    private static string Join(PythonTextTraversal.Character[] characters)
    {
        var builder = new StringBuilder(characters.Length);
        foreach (var character in characters)
            builder.Append(character.ToString());
        return builder.ToString();
    }

    private static PythonProtocolFunctionValue SplitLines() =>
        new(
            "splitlines",
            (target, arguments) =>
            {
                RequireArguments("splitlines", arguments, 0, 1);
                var text = ((PythonTextValue)target!).Value;
                var keepEnds = arguments.Count > 0 && ManagedObjectProtocols.IsTrue(arguments[0]);
                var pieces = new List<PythonValue>();
                var characters = PythonTextTraversal.Enumerate(text).ToArray();
                var start = 0;
                for (var index = 0; index < characters.Length; index++)
                {
                    var code = (char)characters[index].Value;
                    if (Array.IndexOf(LineBreaks, code) < 0)
                        continue;
                    // A carriage return takes a following line feed with it.
                    var width =
                        code == '\r'
                        && index + 1 < characters.Length
                        && (char)characters[index + 1].Value == '\n'
                            ? 2
                            : 1;
                    var piece = Join(characters[start..(keepEnds ? index + width : index)]);
                    pieces.Add(new PythonTextValue(piece));
                    index += width - 1;
                    start = index + 1;
                }
                if (start < characters.Length)
                    pieces.Add(new PythonTextValue(Join(characters[start..])));
                return new PythonListValue(pieces);
            }
        );

    private static PythonProtocolFunctionValue RemoveAffix(string name, bool prefix) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 1);
                var text = ((PythonTextValue)target!).Value;
                var affix = RequireText(name, arguments[0], "{0}() argument must be str, not {1}");
                if (affix.Length == 0)
                    return new PythonTextValue(text);
                var matches = prefix
                    ? text.StartsWith(affix, StringComparison.Ordinal)
                    : text.EndsWith(affix, StringComparison.Ordinal);
                return new PythonTextValue(
                    matches
                        ? prefix
                            ? text[affix.Length..]
                            : text[..^affix.Length]
                        : text
                );
            }
        );

    // ---- search and translation -------------------------------------------------------------

    /// <summary>
    /// `rfind`/`rindex`, positioned by character rather than by UTF-16 unit so an astral
    /// character counts once, and reporting an index in those same units.
    /// </summary>
    private static PythonProtocolFunctionValue Search(
        string name,
        bool rfind,
        bool raiseWhenMissing
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 3);
                var text = ((PythonTextValue)target!).Value;
                var needle = PythonTextTraversal
                    .Enumerate(RequireText(name, arguments[0]))
                    .ToArray();
                var characters = PythonTextTraversal.Enumerate(text).ToArray();
                var start = BoundedArgument(name, arguments, 1, characters.Length);
                var end = BoundedArgument(name, arguments, 2, characters.Length);
                // An empty needle sits at a bound rather than being searched for.
                if (needle.Length == 0)
                    return PythonWholeNumberValue.Create(
                        start > end ? -1
                        : rfind ? end
                        : start
                    );
                var found = -1;
                if (rfind)
                {
                    for (
                        var index = Math.Min(end, characters.Length) - needle.Length;
                        index >= start;
                        index--
                    )
                    {
                        if (MatchesAt(characters, needle, index))
                        {
                            found = index;
                            break;
                        }
                    }
                }
                else
                {
                    for (var index = start; index + needle.Length <= end; index++)
                    {
                        if (MatchesAt(characters, needle, index))
                        {
                            found = index;
                            break;
                        }
                    }
                }
                if (found < 0 && raiseWhenMissing)
                    throw Fault("substring not found", "ValueError");
                return PythonWholeNumberValue.Create(found);
            }
        );

    private static bool MatchesAt(
        PythonTextTraversal.Character[] haystack,
        PythonTextTraversal.Character[] needle,
        int index
    )
    {
        for (var offset = 0; offset < needle.Length; offset++)
        {
            if (haystack[index + offset].Value != needle[offset].Value)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A bound argument counted in characters. Both bounds settle a negative value against
    /// the length, but only the end is capped by it: a start past the end is left there so
    /// the search reports no match rather than sliding back onto the final character.
    /// </summary>
    private static int BoundedArgument(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int position,
        int length
    )
    {
        if (arguments.Count <= position || arguments[position] is PythonNoneValue)
            return position == 1 ? 0 : length;
        int bound;
        if (arguments[position] is PythonWholeNumberValue whole)
            bound = whole.Value > int.MaxValue ? int.MaxValue : (int)whole.Value;
        else if (arguments[position] is PythonTruthValue truth)
            bound = truth.Value ? 1 : 0;
        else
            throw Fault(
                "slice indices must be integers or None or have an __index__ method",
                "TypeError"
            );
        if (bound < 0)
            bound += length;
        return position == 1 ? Math.Max(bound, 0) : Math.Clamp(bound, 0, length);
    }

    private static PythonProtocolFunctionValue Translate() =>
        new(
            "translate",
            (target, arguments) =>
            {
                RequireArguments("translate", arguments, 1, 1);
                var text = ((PythonTextValue)target!).Value;
                var table = arguments[0];
                var builder = new StringBuilder(text.Length);
                foreach (var character in PythonTextTraversal.Enumerate(text))
                {
                    if (!TryLookup(table, character.Value, out var mapped))
                    {
                        builder.Append(character.ToString());
                        continue;
                    }
                    // A value deletes the character, names a replacement ordinal, or is text.
                    switch (mapped)
                    {
                        case PythonNoneValue:
                            continue;
                        case PythonTextValue replacement:
                            builder.Append(replacement.Value);
                            continue;
                        case PythonWholeNumberValue ordinal
                            when ordinal.Value >= 0 && ordinal.Value <= 0x10FFFF:
                            builder.Append(char.ConvertFromUtf32((int)ordinal.Value));
                            continue;
                        default:
                            throw Fault(
                                "character mapping must return integer, None or str",
                                "TypeError"
                            );
                    }
                }
                return new PythonTextValue(builder.ToString());
            }
        );

    /// <summary>A table lookup where an absent key means "leave the character alone".</summary>
    private static bool TryLookup(PythonValue table, int codePoint, out PythonValue mapped)
    {
        var key = PythonWholeNumberValue.Create(codePoint);
        if (table is PythonDictionaryValue dictionary)
        {
            if (ManagedObjectProtocols.TryFindDictionaryItem(dictionary, key, out var item))
            {
                mapped = item.Value;
                return true;
            }
            mapped = null!;
            return false;
        }
        try
        {
            mapped = ManagedObjectProtocols.GetItem(table, key, default);
            return true;
        }
        catch (PythonRaisedException exception) when (exception.Value.TypeName == "KeyError")
        {
            mapped = null!;
            return false;
        }
    }

    /// <summary>
    /// `str.maketrans`, which has three forms: a mapping copied through, two equal-length
    /// strings mapped by ordinal, and a third string whose characters are deleted.
    /// </summary>
    internal static PythonBuiltinFunctionValue CreateMakeTrans() =>
        new(
            "maketrans",
            (arguments, span) =>
            {
                if (arguments.Count is < 1 or > 3)
                {
                    throw Fault(
                        $"maketrans() takes 1 to 3 arguments ({arguments.Count} given)",
                        "TypeError"
                    );
                }
                var table = new PythonDictionaryValue([]);
                if (arguments.Count == 1)
                {
                    // The mapping form keys by ordinal, so a one-character string key is
                    // converted. The two diagnostics keep CPython's own wording, typos included.
                    if (arguments[0] is not PythonDictionaryValue mapping)
                    {
                        throw Fault(
                            "if you give only one argument to maketrans it must be a dict",
                            "TypeError"
                        );
                    }
                    foreach (var item in mapping.Items)
                    {
                        PythonValue key;
                        if (item.Key is PythonTextValue textKey)
                        {
                            var characters = PythonTextTraversal.Enumerate(textKey.Value).ToArray();
                            if (characters.Length != 1)
                            {
                                throw Fault(
                                    "string keys in translatetable must be of length 1",
                                    "ValueError"
                                );
                            }
                            key = PythonWholeNumberValue.Create(characters[0].Value);
                        }
                        else if (item.Key is PythonWholeNumberValue or PythonTruthValue)
                        {
                            key = item.Key;
                        }
                        else
                        {
                            throw Fault(
                                "keys in translate table mustbe strings or integers",
                                "TypeError"
                            );
                        }
                        ManagedObjectProtocols.SetDictionaryItem(table, key, item.Value, span);
                    }
                    return table;
                }
                var from = RequireText("maketrans", arguments[0]);
                var to = RequireText("maketrans", arguments[1]);
                if (PythonTextTraversal.Count(from) != PythonTextTraversal.Count(to))
                {
                    throw Fault(
                        "the first two maketrans arguments must have equal length",
                        "ValueError"
                    );
                }
                var source = PythonTextTraversal.Enumerate(from).ToArray();
                var target = PythonTextTraversal.Enumerate(to).ToArray();
                for (var index = 0; index < source.Length; index++)
                    ManagedObjectProtocols.SetDictionaryItem(
                        table,
                        PythonWholeNumberValue.Create(source[index].Value),
                        PythonWholeNumberValue.Create(target[index].Value),
                        span
                    );
                if (arguments.Count == 3)
                {
                    foreach (
                        var character in PythonTextTraversal.Enumerate(
                            RequireText("maketrans", arguments[2])
                        )
                    )
                        ManagedObjectProtocols.SetDictionaryItem(
                            table,
                            PythonWholeNumberValue.Create(character.Value),
                            PythonNoneValue.Instance,
                            span
                        );
                }
                return table;
            },
            (_, names, _, span) =>
                throw Fault("maketrans() takes no keyword arguments", "TypeError")
        );

    // ---- shared plumbing --------------------------------------------------------------------

    private static PythonProtocolFunctionValue Predicate(
        string name,
        Func<string, bool> predicate
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 0, 0);
                return PythonTruthValue.FromBoolean(predicate(((PythonTextValue)target!).Value));
            }
        );

    /// <summary>
    /// A string argument, refused with the wording the calling method uses: CPython names the
    /// method and the argument position in some places and only the expected type in others.
    /// </summary>
    private static string RequireText(string name, PythonValue value, string? wording = null) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                wording is null
                    ? $"{name}() argument 1 must be str, "
                        + $"not {ManagedObjectProtocols.GetTypeName(value)}"
                    : wording
                        .Replace("{0}", name, StringComparison.Ordinal)
                        .Replace(
                            "{1}",
                            ManagedObjectProtocols.GetTypeName(value),
                            StringComparison.Ordinal
                        ),
                "TypeError"
            );

    private static int RequireCount(string name, PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => whole.Value > int.MaxValue ? int.MaxValue
            : whole.Value < int.MinValue ? int.MinValue
            : (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted "
                    + "as an integer",
                "TypeError"
            ),
        };

    private static int RequireIndex(string text, PythonValue value)
    {
        BigInteger bound;
        if (value is PythonWholeNumberValue whole)
            bound = whole.Value;
        else if (value is PythonTruthValue truth)
            bound = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(value, default, out bound))
            throw Fault("slice indices must be integers or have an __index__ method", "TypeError");
        var clamped =
            bound < int.MinValue ? int.MinValue
            : bound > int.MaxValue ? int.MaxValue
            : (int)bound;
        if (clamped < 0)
            clamped += text.Length;
        return Math.Clamp(clamped, 0, text.Length);
    }

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
            return;
        var expectation = minimum == maximum ? $"{maximum}" : $"between {minimum} and {maximum}";
        throw Fault(
            $"Method '{name}' expected {expectation} argument(s), "
                + $"but received {arguments.Count}.",
            "TypeError"
        );
    }

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, pythonType);
}
