using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `json` module against the pinned CPython 3.14 reference: encoder output,
/// decoder results, and the JSONDecodeError diagnostics. The reference must exit 0
/// with empty stderr, so every probe wraps its own failures.
/// </summary>
public sealed class JsonCompatibilityTests
{
    [Theory]
    [InlineData(
        """
            import json

            print(json.dumps({"a": [1, 2], "b": {"c": None}}))
            print(json.dumps([True, False, None, 1.5, "x"]), json.dumps((1, 2)))
            print(json.dumps([]), json.dumps({}), json.dumps(""))
            print(json.dumps(2 ** 200), json.dumps(-0.0), json.dumps(1e100), json.dumps(1e-7))
            print(json.dumps('a"b\\c'), json.dumps("a\nb\tc\rd\be\ff\vg"))
            print(json.dumps("\x00\x1f\x7f"), json.dumps("café"), json.dumps("😀"), json.dumps("\ud800"))
            """
    )]
    [InlineData(
        """
            import json

            print(json.dumps("café😀\x7f", ensure_ascii=False))
            print(json.dumps({"café": "ü\n"}, ensure_ascii=False))
            print(json.dumps([float("nan"), float("inf"), float("-inf")]))
            print(json.dumps({1: "a", 1.5: "b", True: "c", None: "d"}))
            print(json.dumps({float("nan"): 1}), json.dumps({2: "b", 1: "a"}, sort_keys=True))
            """
    )]
    [InlineData(
        """
            import json

            data = {"b": [1, 2], "a": {"x": None, "y": True}}
            print(repr(json.dumps(data)))
            print(repr(json.dumps(data, indent=2)))
            print(repr(json.dumps(data, indent=0)))
            print(repr(json.dumps(data, indent="\t")))
            print(repr(json.dumps({"b": 1, "a": [1]}, indent=2, sort_keys=True)))
            print(repr(json.dumps(data, sort_keys=True, separators=(",", ":"))))
            print(repr(json.dumps([[1], [2]], indent=1)))
            print(repr(json.dumps({"a": 1}, indent=2, separators=(",", " = "))))
            print(repr(json.dumps({"a": [], "b": {}}, indent=2)))
            print(repr(json.dumps(data, indent=3, ensure_ascii=False, sort_keys=True)))
            """
    )]
    [InlineData(
        """
            import json


            class Custom:
                pass


            def circular():
                value = [1]
                value.append(value)
                return value


            for value in (object(), {1, 2}, b"ab", Custom(), len):
                try:
                    json.dumps(value)
                except TypeError as error:
                    print("TypeError:", error)
            for call in (
                lambda: json.dumps(circular()),
                lambda: json.dumps(float("nan"), allow_nan=False),
                lambda: json.dumps({"a": [float("inf")]}, allow_nan=False),
                lambda: json.dumps({(1, 2): "a"}),
                lambda: json.dumps({1: "a", "b": 2}, sort_keys=True),
                lambda: json.dumps(),
                lambda: json.dumps(1, 2),
                lambda: json.dumps([1], separators=(",",)),
                lambda: json.dumps([1], separators=(1, 2)),
                lambda: json.dumps([1], indent=1.5),
            ):
                try:
                    call()
                except (TypeError, ValueError) as error:
                    print(type(error).__name__, error)
            print(json.dumps(Custom(), default=lambda value: {"replaced": True}))
            print(json.dumps({(1, 2): "a", "b": 1}, skipkeys=True))
            print(json.dumps(obj={"a": 1}))
            """
    )]
    public Task DumpsMatchesTheReference(string source) =>
        CompatibilityOracle.AssertMatchesAsync(source);

    [Theory]
    [InlineData(
        """
            import json

            print(json.loads(' {"a": [1, 2.5, true, null], "b": "x"} '))
            print(json.loads("1"), json.loads("-0"), json.loads("1.5"), json.loads("1e-3"))
            print(json.loads("123456789012345678901234567890"), json.loads("-0.0"))
            print(json.loads("NaN"), json.loads("Infinity"), json.loads("-Infinity"))
            print(repr(json.loads(r'"a\nb\t\"\\\/\bf\rcA"')))
            print(repr(json.loads(r'"\ud83d\ude00"')), repr(json.loads(r'"\ud83d"')), repr(json.loads(r'"\udc00"')))
            print(json.loads('{"a": 1, "a": 2}'), json.loads('{"b": 1, "a": 2, "b": 3}'))
            print(json.loads(b'{"a": 1}'), json.loads(b'\xef\xbb\xbf[1]'))
            print(json.loads("{}"), json.loads("[]"), json.loads(" [ ] "), type(json.loads("1")).__name__)
            """
    )]
    [InlineData(
        """
            import json

            print(json.loads("[" * 40 + "]" * 40) == json.loads("[" * 40 + "]" * 40))
            print(json.loads(json.dumps({"a": [1, {"b": None}], "c": "d"})))
            for value in (0, 1, -1, 255, 2 ** 70, 1.5, -2.25, 1e-4, 1e20, 0.1, 3.141592653589793):
                text = json.dumps(value)
                print(text == repr(value), json.loads(text) == value)
            """
    )]
    public Task LoadsMatchesTheReference(string source) =>
        CompatibilityOracle.AssertMatchesAsync(source);

    [Fact]
    public async Task DecoderDiagnosticsMatchTheReference()
    {
        // Every malformed document reports CPython's message with the same
        // index, line and column, and the same class attributes.
        var cases = new[]
        {
            "{\"a\" 1}",
            "",
            "  \t\n\r ",
            "1 2",
            "[1,]",
            "[1, ]",
            "[1,\n2,\n]",
            "{\"a\":1,}",
            "{\"a\":1,\n}",
            "{1: 2}",
            "{,}",
            "[1 2]",
            "{\"a\": 1 \"b\": 2}",
            "[1,,2]",
            "[,1]",
            "[,]",
            "nope",
            "tru",
            "nul",
            "\"unterminated",
            "\"",
            "\"a\nb\"",
            "\"a\tb\"",
            "\"a\u0001b\"",
            "\"abc\\",
            "\"\\q\"",
            "\"\\u12\"",
            "\"\\uZZZZ\"",
            "\"\\u\"",
            "{",
            "[",
            "]",
            "}",
            "{\"a\"",
            "{\"a\":",
            "{\"a\": }",
            "{\"a\"::1}",
            "{\"a\":1,",
            "[1",
            "[1,",
            "[1, 2",
            "{\"a\"}",
            "{\"a\": tru}",
            "{\"a\": [1, 2, }]}",
            "{\"a\": {1: 2}}",
            "﻿{}",
            "{\n \"a\": tru\n}",
            "01",
            "+1",
            ".5",
            "-",
            "1.",
            "1e",
            "1e+",
            "\"a\x7fb\"",
            "TRUE",
            "Na",
            "Infinit",
            "[1] x",
            "'a'",
            "1 2 3",
        };
        var source = new System.Text.StringBuilder();
        source.AppendLine("import json");
        source.AppendLine("cases = [");
        foreach (var item in cases)
        {
            source.Append("    \"").Append(Escape(item)).AppendLine("\",");
        }
        source.AppendLine("]");
        source.AppendLine("for text in cases:");
        source.AppendLine("    try:");
        source.AppendLine("        json.loads(text)");
        source.AppendLine("        print('ok', repr(text))");
        source.AppendLine("    except json.JSONDecodeError as error:");
        source.AppendLine(
            "        print(error.msg, error.pos, error.lineno, error.colno, '|', error)"
        );

        await CompatibilityOracle.AssertMatchesAsync(source.ToString());
    }

    /// <summary>A Python string literal body that spells every character out, so the
    /// generated source cannot be broken by quoting or control characters.</summary>
    private static string Escape(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            builder.Append(
                character is >= ' ' and <= '~' and not ('"' or '\\')
                    ? character.ToString()
                    : $"\\u{(int)character:x4}"
            );
        }

        return builder.ToString();
    }

    [Fact]
    public Task JsonDecodeErrorMatchesTheReference() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
                import json

                error = json.JSONDecodeError("msg", "a\nbc", 3)
                print(repr(error), error.args)
                print(error.msg, repr(error.doc), error.pos, error.lineno, error.colno)
                error.msg = "changed"
                error.pos = 99
                print(str(error), error.msg, error.pos, error.lineno, error.colno)
                print(issubclass(json.JSONDecodeError, ValueError), issubclass(json.JSONDecodeError, Exception))
                print([base.__name__ for base in json.JSONDecodeError.__mro__])
                print([base.__name__ for base in json.JSONDecodeError.__bases__])
                print(json.JSONDecodeError.__module__, json.JSONDecodeError.__name__)
                print(json.JSONDecodeError(msg="m", doc="d", pos=0).args)
                for call in (
                    lambda: json.JSONDecodeError("m", "d"),
                    lambda: json.JSONDecodeError("m", "d", 0, 1),
                    lambda: json.JSONDecodeError("m", "d", 0, x=1),
                    lambda: json.JSONDecodeError("m", "d", 0, pos=1),
                ):
                    try:
                        call()
                    except TypeError as error:
                        print("TypeError:", error)
                for source in ("[", "{}x", "\"a\nb\""):
                    try:
                        json.loads(source)
                    except ValueError as error:
                        print(type(error).__name__, str(error), error.msg, error.pos)
                print(json.loads('"a\nb"', strict=False))
                print(json.loads(s="[1]"), json.loads("[1]", strict=True))
            """
        );

    [Fact]
    public async Task MutatedAndRoundTrippedDocumentsMatchTheReference()
    {
        // Truncations and single-character substitutions of valid documents:
        // every diagnostic must carry CPython's message, index, line and column.
        var source = """
            import json

            documents = [
                '{"a": [1, 2, 3], "b": {"c": null, "d": [true, false]}}',
                '[1, 2.5, "x", null, true, false, {"k": "v"}]',
                '{"unicode": "caf\\u00e9", "esc": "a\\nb\\tc"}',
                '{"nested": {"deep": {"deeper": [1, [2, [3, [4]]]]}}}',
                '[[], {}, [{}], {"a": []}]',
                '-0.5e+3',
            ]
            replacements = ['"', "}", "]", ",", ":", "{", "[", "x", " ", "\\n", "\\\\", "1", "e", "-", "n"]
            cases = []
            for document in documents:
                for cut in range(len(document) + 1):
                    cases.append(document[:cut])
                for position in range(len(document)):
                    for replacement in replacements:
                        cases.append(document[:position] + replacement + document[position + 1:])
            for text in cases:
                try:
                    json.loads(text)
                except json.JSONDecodeError as error:
                    print(error.msg, error.pos, error.lineno, error.colno)
                except Exception as error:
                    print(type(error).__name__, str(error))
            print("cases:", len(cases))
            """;

        await CompatibilityOracle.AssertMatchesAsync(source);
    }

    [Fact]
    public async Task EncoderSweepMatchesTheReference()
    {
        var source = """
            import json

            values = [
                0, 1, -1, 255, 256, -1000000000000000000000, 2 ** 64,
                0.0, 1.5, -2.25, 1e-4, 1e-3, 100.0, 1e20, 1.5e-9, 0.1,
                "", "a", "abcdefghij", "é☃ȳ", "\u0000\u001f\u007f", "😀", "\ud800",
                "tab\tnl\ncr\rslash\\quote\"", "</script>",
                True, False, None, [], {},
                [1, 2, 3], [1, [2, [3, [4, [5]]]]], {"a": 1}, {"a": {"b": {"c": {"d": []}}}},
                {"x": [1, {"y": [None, True, False]}]}, [{"a": []}, [], {}, {"": ""}],
                [1, "two", 3.0, None, True], {"a" * 30: "b" * 30},
                [[{"k%d" % i: i} for i in range(5)] for _ in range(3)],
            ]
            options = [
                {},
                {"indent": 2},
                {"indent": 0},
                {"indent": "\t"},
                {"indent": 4, "sort_keys": True},
                {"sort_keys": True},
                {"separators": (",", ":")},
                {"ensure_ascii": False},
                {"ensure_ascii": False, "indent": 3},
                {"indent": 1, "separators": (", ", " : "), "ensure_ascii": False},
            ]
            for value in values:
                for option in options:
                    text = json.dumps(value, **option)
                    print(repr(text))
                    print(repr(json.loads(text)) == repr(value))
            print("done")
            """;

        await CompatibilityOracle.AssertMatchesAsync(source);
    }

    [Fact]
    public Task MissingAndUnsupportedParametersMatchTheReference() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import json

            for call in (
                lambda: json.loads(),
                lambda: json.loads("1", 2),
                lambda: json.loads("1", foo=2),
                lambda: json.dumps(),
                lambda: json.dumps(1, 2),
                lambda: json.dumps(1, foo=2),
                lambda: json.loads(1),
            ):
                try:
                    call()
                except TypeError as error:
                    print("TypeError:", error)
            print(json.loads("1", cls=None, object_hook=None, parse_float=None, parse_int=None))
            """
        );
}
