using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FStringCompatibilityTests
{
    [Fact]
    public Task FStringSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
