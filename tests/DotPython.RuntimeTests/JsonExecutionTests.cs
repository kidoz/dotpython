using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class JsonExecutionTests
{
    [Fact]
    public void DumpsEncodesScalarsContainersAndEscapes()
    {
        var output = Run(
            """
            import json

            print(json.dumps({"a": [1, 2], "b": {"c": None}}))
            print(json.dumps([True, False, None, 1.5, "x"]))
            print(json.dumps((1, 2)), json.dumps([]), json.dumps({}), json.dumps(""))
            print(json.dumps('a"b\\c'))
            print(json.dumps("a\nb\tc\rd\be\ff\vg"))
            print(json.dumps("\x00\x1f\x7f"))
            print(json.dumps(2 ** 200))
            print(json.dumps(3.0), json.dumps(-0.0), json.dumps(1e100))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{\"a\": [1, 2], \"b\": {\"c\": null}}",
                "[true, false, null, 1.5, \"x\"]",
                "[1, 2] [] {} \"\"",
                "\"a\\\"b\\\\c\"",
                "\"a\\nb\\tc\\rd\\be\\ff\\u000bg\"",
                "\"\\u0000\\u001f\\u007f\"",
                "1606938044258990275541962092341162602522202993782792835301376",
                "3.0 -0.0 1e+100",
                ""
            ),
            output
        );
    }

    [Fact]
    public void DumpsEscapesNonAsciiUnlessTheAsciiFormIsDisabled()
    {
        var output = Run(
            """
            import json

            print(json.dumps("café"), json.dumps("😀"), json.dumps("\ud800"))
            print(json.dumps("café😀\x7f", ensure_ascii=False))
            print(json.dumps({"café": "ü"}, ensure_ascii=False))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "\"caf\\u00e9\" \"\\ud83d\\ude00\" \"\\ud800\"",
                "\"café😀\x7f\"",
                "{\"café\": \"ü\"}",
                ""
            ),
            output
        );
    }

    [Fact]
    public void DumpsHonoursIndentSortKeysAndSeparators()
    {
        var output = Run(
            """
            import json

            data = {"b": [1, 2], "a": {"x": None, "y": True}}
            print(json.dumps(data))
            print(repr(json.dumps(data, indent=2)))
            print(repr(json.dumps(data, indent="\t")))
            print(repr(json.dumps({"b": 1, "a": [1]}, indent=2, sort_keys=True)))
            print(repr(json.dumps(data, sort_keys=True, separators=(",", ":"))))
            print(repr(json.dumps({"a": []}, indent=2)))
            print(repr(json.dumps([[1], [2]], indent=1)))
            print(repr(json.dumps({"a": 1}, indent=2, separators=(",", " = "))))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{\"b\": [1, 2], \"a\": {\"x\": null, \"y\": true}}",
                "'{\\n  \"b\": [\\n    1,\\n    2\\n  ],\\n  \"a\": {\\n    \"x\": null,\\n    \"y\": true\\n  }\\n}'",
                "'{\\n\\t\"b\": [\\n\\t\\t1,\\n\\t\\t2\\n\\t],\\n\\t\"a\": {\\n\\t\\t\"x\": null,\\n\\t\\t\"y\": true\\n\\t}\\n}'",
                "'{\\n  \"a\": [\\n    1\\n  ],\\n  \"b\": 1\\n}'",
                "'{\"a\":{\"x\":null,\"y\":true},\"b\":[1,2]}'",
                "'{\\n  \"a\": []\\n}'",
                "'[\\n [\\n  1\\n ],\\n [\\n  2\\n ]\\n]'",
                "'{\\n  \"a\" = 1\\n}'",
                ""
            ),
            output
        );
    }

    [Fact]
    public void DumpsKeysFollowTheCpythonConversionRules()
    {
        var output = Run(
            """
            import json

            print(json.dumps({1: "a", 1.5: "b", True: "c", None: "d"}))
            print(json.dumps({float("nan"): 1, float("inf"): 2}))
            print(json.dumps({(1, 2): "a", "b": 1}, skipkeys=True))
            print(json.dumps({2: "b", 1: "a"}, sort_keys=True))
            try:
                json.dumps({(1, 2): "a"})
            except TypeError as error:
                print("TypeError:", error)
            try:
                json.dumps({1: "a", "b": 2}, sort_keys=True)
            except TypeError as error:
                print("TypeError:", error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                // True == 1 and hashes equal, so the dict keeps the first key object.
                "{\"1\": \"c\", \"1.5\": \"b\", \"null\": \"d\"}",
                "{\"NaN\": 1, \"Infinity\": 2}",
                "{\"b\": 1}",
                "{\"1\": \"a\", \"2\": \"b\"}",
                "TypeError: keys must be str, int, float, bool or None, not tuple",
                "TypeError: '<' not supported between instances of 'str' and 'int'",
                ""
            ),
            output
        );
    }

    [Fact]
    public void DumpsReportsUnserializableCircularAndOutOfRangeValues()
    {
        var output = Run(
            """
            import json

            class Custom:
                pass

            def circular():
                value = [1]
                value.append(value)
                return value

            for value in (object(), {1, 2}, b"ab", Custom()):
                try:
                    json.dumps(value)
                except TypeError as error:
                    print("TypeError:", error)
            try:
                json.dumps(circular())
            except ValueError as error:
                print("ValueError:", error)
            try:
                json.dumps([float("inf")], allow_nan=False)
            except ValueError as error:
                print("ValueError:", error)
            print(json.dumps(float("nan")), json.dumps([float("-inf")]))
            print(json.dumps(Custom(), default=lambda value: {"replaced": True}))
            try:
                json.dumps()
            except TypeError as error:
                print("TypeError:", error)
            try:
                json.dumps(1, 2)
            except TypeError as error:
                print("TypeError:", error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "TypeError: Object of type object is not JSON serializable",
                "TypeError: Object of type set is not JSON serializable",
                "TypeError: Object of type bytes is not JSON serializable",
                "TypeError: Object of type Custom is not JSON serializable",
                "ValueError: Circular reference detected",
                "ValueError: Out of range float values are not JSON compliant: inf",
                "NaN [-Infinity]",
                "{\"replaced\": true}",
                "TypeError: dumps() missing 1 required positional argument: 'obj'",
                "TypeError: dumps() takes 1 positional argument but 2 were given",
                ""
            ),
            output
        );
    }

    [Fact]
    public void LoadsParsesScalarsContainersAndEscapes()
    {
        var output = Run(
            """
            import json

            print(json.loads(' {"a": [1, 2.5, true, null], "b": "x"} '))
            print(json.loads("1"), json.loads("-0"), json.loads("1.5"), json.loads("1e-3"))
            print(json.loads("123456789012345678901234567890"))
            print(json.loads("NaN"), json.loads("Infinity"), json.loads("-Infinity"))
            print(repr(json.loads(r'"a\nb\t\"\\\/\bf\rcA"')))
            print(repr(json.loads(r'"\ud83d\ude00"')), repr(json.loads(r'"\ud83d"')))
            print(json.loads('{"a": 1, "a": 2}'))
            print(json.loads(b'{"a": 1}'), json.loads(b'\xef\xbb\xbf[1]'))
            print(json.loads("{}"), json.loads("[]"), json.loads(" {} "))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'a': [1, 2.5, True, None], 'b': 'x'}",
                "1 0 1.5 0.001",
                "123456789012345678901234567890",
                "nan inf -inf",
                "'a\\nb\\t\"\\\\/\\x08f\\rcA'",
                "'😀' '\\ud83d'",
                "{'a': 2}",
                "{'a': 1} [1]",
                "{} [] {}",
                ""
            ),
            output
        );
    }

    [Fact]
    public void LoadsReportsTheCpythonErrorPosition()
    {
        var output = Run(
            """
            import json

            for text in ('{"a" 1}', "", "1 2", "[1,]", "[1, ]", '{"a":1,}', "{1: 2}", "[1 2]",
                         "nope", '"unterminated', '"a\nb"', r'"\q"', r'"\u12"', "  \t\n\r ", "{"):
                try:
                    json.loads(text)
                    print("ok")
                except json.JSONDecodeError as error:
                    print(error.msg, error.pos, error.lineno, error.colno, "|", error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Expecting ':' delimiter 5 1 6 | Expecting ':' delimiter: line 1 column 6 (char 5)",
                "Expecting value 0 1 1 | Expecting value: line 1 column 1 (char 0)",
                "Extra data 2 1 3 | Extra data: line 1 column 3 (char 2)",
                "Illegal trailing comma before end of array 2 1 3 | Illegal trailing comma before end of array: line 1 column 3 (char 2)",
                "Illegal trailing comma before end of array 2 1 3 | Illegal trailing comma before end of array: line 1 column 3 (char 2)",
                "Illegal trailing comma before end of object 6 1 7 | Illegal trailing comma before end of object: line 1 column 7 (char 6)",
                "Expecting property name enclosed in double quotes 1 1 2 | Expecting property name enclosed in double quotes: line 1 column 2 (char 1)",
                "Expecting ',' delimiter 3 1 4 | Expecting ',' delimiter: line 1 column 4 (char 3)",
                "Expecting value 0 1 1 | Expecting value: line 1 column 1 (char 0)",
                "Unterminated string starting at 0 1 1 | Unterminated string starting at: line 1 column 1 (char 0)",
                "Invalid control character at 2 1 3 | Invalid control character at: line 1 column 3 (char 2)",
                "Invalid \\escape 1 1 2 | Invalid \\escape: line 1 column 2 (char 1)",
                "Invalid \\uXXXX escape 2 1 3 | Invalid \\uXXXX escape: line 1 column 3 (char 2)",
                "Expecting value 6 2 3 | Expecting value: line 2 column 3 (char 6)",
                "Expecting property name enclosed in double quotes 1 1 2 | Expecting property name enclosed in double quotes: line 1 column 2 (char 1)",
                ""
            ),
            output
        );
    }

    [Fact]
    public void JsonDecodeErrorIsAValueErrorWithTheCpythonAttributes()
    {
        var output = Run(
            """
            import json

            error = json.JSONDecodeError("msg", "a\nbc", 3)
            print(repr(error))
            print(error.args, error.msg, error.doc, error.pos, error.lineno, error.colno)
            print(str(error))
            error.msg = "changed"
            error.pos = 99
            print(str(error), error.msg, error.pos, error.lineno, error.colno)
            print(issubclass(json.JSONDecodeError, ValueError))
            print(issubclass(json.JSONDecodeError, Exception))
            print([base.__name__ for base in json.JSONDecodeError.__mro__])
            print(json.JSONDecodeError.__module__, json.JSONDecodeError.__name__)
            try:
                raise json.JSONDecodeError("boom", "doc", 1)
            except ValueError as error:
                print("caught ValueError:", error)
            try:
                json.JSONDecodeError("m", "d")
            except TypeError as error:
                print("TypeError:", error)
            try:
                json.loads("[")
            except json.JSONDecodeError as error:
                print(type(error).__name__, error.pos)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "JSONDecodeError('msg: line 2 column 2 (char 3)')",
                "('msg: line 2 column 2 (char 3)',) msg a\nbc 3 2 2",
                "msg: line 2 column 2 (char 3)",
                "msg: line 2 column 2 (char 3) changed 99 2 2",
                "True",
                "True",
                "['JSONDecodeError', 'ValueError', 'Exception', 'BaseException', 'object']",
                "json.decoder JSONDecodeError",
                "caught ValueError: boom: line 1 column 2 (char 1)",
                "TypeError: JSONDecodeError.__init__() missing 1 required positional argument: 'pos'",
                "JSONDecodeError 1",
                ""
            ),
            output
        );
    }

    [Fact]
    public void UnsupportedDecoderAndEncoderParametersAreRejected()
    {
        var output = Run(
            """
            import json

            for name, call in (
                ("cls", lambda: json.loads("[1]", cls=int)),
                ("object_hook", lambda: json.loads("{}", object_hook=print)),
                ("parse_float", lambda: json.loads("1.5", parse_float=str)),
                ("parse_int", lambda: json.loads("1", parse_int=str)),
                ("parse_constant", lambda: json.loads("NaN", parse_constant=str)),
                ("object_pairs_hook", lambda: json.loads("{}", object_pairs_hook=print)),
            ):
                try:
                    call()
                except TypeError as error:
                    print(name, "=>", error)
            try:
                json.dumps([1], cls=None, default=None)
                print("defaults accepted")
            except TypeError as error:
                print(error)
            print(json.loads('"a\nb"', strict=False))
            print(json.dumps("[1]"))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "cls => json.loads() does not support cls in this runtime slice.",
                "object_hook => json.loads() does not support object_hook in this runtime slice.",
                "parse_float => json.loads() does not support parse_float in this runtime slice.",
                "parse_int => json.loads() does not support parse_int in this runtime slice.",
                "parse_constant => json.loads() does not support parse_constant in this runtime slice.",
                "object_pairs_hook => json.loads() does not support object_pairs_hook in this runtime slice.",
                "defaults accepted",
                "a" + "\n" + "b",
                "\"[1]\"",
                ""
            ),
            output
        );
    }

    [Fact]
    public void NestingBeyondTheBoundIsARecursionError()
    {
        var output = Run(
            """
            import json

            print(isinstance(json.loads("[" * 900 + "]" * 900), list))
            try:
                json.loads("[" * 1500 + "]" * 1500)
            except RecursionError as error:
                print("RecursionError:", error)
            try:
                json.dumps(json.loads("[" * 1500 + "]" * 1500))
            except RecursionError as error:
                print("dumps RecursionError:", error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "True",
                "RecursionError: maximum recursion depth exceeded",
                "dumps RecursionError: maximum recursion depth exceeded",
                ""
            ),
            output
        );
    }

    [Fact]
    public void RoundTrippingComposesThroughBothDirections()
    {
        var output = Run(
            """
            import json

            text = json.dumps({"a": [1, 2, 3], "b": {"c": "d"}}, indent=2, sort_keys=True)
            print(json.loads(text))
            print(json.dumps(json.loads('{"b": 2, "a": 1}'), sort_keys=True))
            print(json.loads(json.dumps([1, 2, 3])) == [1, 2, 3])
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'a': [1, 2, 3], 'b': {'c': 'd'}}",
                "{\"a\": 1, \"b\": 2}",
                "True",
                ""
            ),
            output
        );
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "json_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
