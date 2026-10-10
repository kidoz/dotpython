using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StringFoldingExecutionTests
{
    [Fact]
    public void StringFoldingSurface()
    {
        var output = Run(
            """
            x = 5
            print("a" "b")
            print("a\n" "b")
            print(r"a\n" "b")
            print("" "" "x")
            print(b"a" b"b")
            print(b"\x41" b"B")
            print("%s" "%s" % (1, 2))
            print(f"x={x}" " tail")
            print("head " f"x={x}")
            print(f"a{x}" f"b{x}")
            print(f"h" "b" f"c{x}")
            print("literal {" f"x")
            print(r"a\n" f"x={x}")
            print(f"x={x}" r"a\n")
            print(rf"a\n{x}" "b")
            print("a" rf"b\n{x}")
            print(("one "
                   "two"))
            print("\N{BULLET}" "\n" "end")
            data = "a" "b" "c"
            print(data, len(data), type(data).__name__)
            print(b"a" b"b" == b"ab")


            def doc():
                "first " "second"


            print(repr(doc.__doc__))


            class Documented:
                "class " "doc"


            print(repr(Documented.__doc__))


            def call2(label, value):
                print(label, value)


            call2("kw", "a" "b")
            print(f"{x}" f"" f"{x}")
            print("{0}" "{1}".format(1, 2))
            print("multi" "part" + "sum")
            values = ["p" "q"]
            print(values)
            template = t"a{x}" t"b"
            print(type(template).__name__, template.strings, [part.expression for part in template.interpolations])
            """
        );
        Assert.Equal(
            Lines(
                "ab",
                "a",
                "b",
                "a\\nb",
                "x",
                "b'ab'",
                "b'AB'",
                "12",
                "x=5 tail",
                "head x=5",
                "a5b5",
                "hbc5",
                "literal {x",
                "a\\nx=5",
                "x=5a\\n",
                "a\\n5b",
                "ab\\n5",
                "one two",
                "•",
                "end",
                "abc 3 str",
                "True",
                "'first second'",
                "'class doc'",
                "kw ab",
                "55",
                "12",
                "multipartsum",
                "['pq']",
                "Template ('a', 'b') ['x']"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
