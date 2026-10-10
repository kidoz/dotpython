using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FStringExecutionTests
{
    [Fact]
    public void FStringSurface()
    {
        var output = Run(
            """"
            x = 42
            width = 8
            prec = 3
            star = "*"
            name = "abc"
            print(f"{x=}")
            print(f"{x = }")
            print(f"{x=:>6}")
            print(f"{x=!s}")
            print(f"{x=!r:^8}")
            print(f"{x+1=}")
            print(f"{'a b'=}")
            print(f"{x:{width}}")
            print(f"{x:>{width}}")
            print(f"{x:0{width}d}")
            print(f"{3.14159:{width}.{prec}f}")
            print(f"{'ab':{'>'}{width}}")
            print(f"{x:{width}}.{x:{width}}")
            print(f"{x:{star}>{width}}")
            print(f"{(lambda a: a + 1)(1)}")
            print(f"{ x }")
            print(f"{ {'k': 1}['k'] = }")
            print(f"{x:\t<3}")
            print(f"{x:\x20>3}")
            print(f"""{
               x
            }""")
            print(rf"{x:{width}}")
            template = t"{x=}"
            print(template.strings, template.interpolations[0].expression, repr(template.interpolations[0].value), template.interpolations[0].conversion, repr(template.interpolations[0].format_spec))
            template2 = t"{x=:>4}"
            print(template2.strings, template2.interpolations[0].conversion, repr(template2.interpolations[0].format_spec))
            template3 = t"{x:{width}}"
            print(template3.strings, repr(template3.interpolations[0].format_spec))
            """"
        );
        Assert.Equal(
            Lines(
                "x=42",
                "x = 42",
                "x=    42",
                "x=42",
                "x=   42   ",
                "x+1=43",
                "'a b'='a b'",
                "      42",
                "      42",
                "00000042",
                "   3.142",
                "      ab",
                "      42.      42",
                "******42",
                "2",
                "42",
                " {'k': 1}['k'] = 1",
                "42	",
                " 42",
                "42",
                "      42",
                "('x=', '') x 42 r ''",
                "('x=', '') None '>4'",
                "('', '') '8'"
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
