using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FormatSpecExecutionTests
{
    [Fact]
    public void FormatSpecSurface()
    {
        var output = Run(
            """
            def show(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            print(name := "formatting")
            show("g", lambda: (f"{3.5:g}", f"{3.5:G}", f"{3.14159:.3g}", f"{0.00001:g}", f"{123456789:g}"))
            show("g-more", lambda: (f"{1e100:g}", f"{0:g}", f"{-2.5:g}", f"{100000.0:.3g}", f"{1e20:G}"))
            show("underscore", lambda: (f"{1234:_}", f"{1234567:_b}", f"{1234567:_o}", f"{1234567:_x}", f"{255:#_x}"))
            show("alternate", lambda: (f"{255:#x}", f"{255:#X}", f"{255:#o}", f"{255:#b}", f"{1234:#d}", f"{0:#x}"))
            show("grouping", lambda: (f"{1234567.891:_}", f"{1234567.891:,}", f"{1234567.891:,.2f}", f"{1.5:,.3g}"))
            show("alternate-float", lambda: (f"{1.5:#}", f"{1.5:#g}", f"{1.5:#.3g}", f"{1.5:#e}", f"{1.5:#.0f}", f"{1.5:#%}"))
            show("pad-group", lambda: (f"{-1234:015_}", f"{1234567:015,}", f"{255:#012x}", f"{1234:010_d}"))
            show("n", lambda: (f"{1234:n}", f"{1234.56789:n}", f"{0.00001:n}", f"{-1234.5:n}"))
            show("c", lambda: (f"{65:c}", f"{0x1F600:c}", f"{True:c}"))
            show("bool", lambda: (f"{True:d}", f"{True:g}", f"{True:_}"))
            show("special", lambda: (f"{float('inf'):g}", f"{float('nan'):g}", f"{float('-inf'):f}", f"{float('inf'):%}"))
            show("mixed", lambda: (f"{1234:_+015,.2f}", f"{'x':#^7}"))
            show("comma-hex", lambda: f"{255:,x}")
            show("underscore-str", lambda: f"{'a':_}")
            show("comma-str", lambda: f"{'a':,}")
            show("precision-int", lambda: f"{5:.2}")
            show("precision-hex", lambda: f"{255:.2x}")
            show("c-float", lambda: f"{1.5:c}")
            show("c-char", lambda: f"{'a':c}")
            show("c-big", lambda: f"{1114112:c}")
            show("bad-spec", lambda: f"{5:^+_12}")
            show("bad-spec-str", lambda: f"{'a':^+_12}")
            show("n-str", lambda: f"{'a':n}")
            show("hash-str", lambda: f"{'a':#}")
            show("byte-b", lambda: format(b"ab", "5"))
            show("nan", lambda: (f"{float('nan'):+f}", f"{float('nan'): f}", f"{float('-nan'):f}", f"{float('nan'):+}"))
            show("complex", lambda: (f"{1+2j}", f"{1+2j:.2f}", f"{1.5j:g}", f"{1-2j:+f}", f"{1+2j:^+20.1f}", f"{1+2j:>12.1f}"))
            show("complex-special", lambda: (f"{complex(0, float('nan')):f}", f"{complex(float('inf'), 1):f}"))
            show("complex-prec", lambda: (f"{1+2j:+.0e}", f"{1+2j:#.2f}", f"{1+2j:.0f}", f"{1+2j:,}"))
            show("complex-bad", lambda: (f"{1+2j:d}", f"{1+2j:%}", f"{1+2j:012}"))
            show("g-int", lambda: f"{5:g}")
            show("float-d", lambda: f"{1.5:d}")
            show("sign-str", lambda: f"{'a':+}")
            show("sign-space-str", lambda: f"{'a': }")
            show("sign-c", lambda: f"{65:+c}")
            show("align-str-eq", lambda: f"{'a':=5}")
            show("align-str-zero", lambda: f"{'a':05}")
            show("fill-str", lambda: f"{'a':0>5}")
            """
        );
        Assert.Equal(
            Lines(
                "formatting",
                "g ('3.5', '3.5', '3.14', '1e-05', '1.23457e+08')",
                "g-more ('1e+100', '0', '-2.5', '1e+05', '1E+20')",
                "underscore ('1_234', '1_0010_1101_0110_1000_0111', '455_3207', '12_d687', '0xff')",
                "alternate ('0xff', '0XFF', '0o377', '0b11111111', '1234', '0x0')",
                "grouping ('1_234_567.891', '1,234,567.891', '1,234,567.89', '1.5')",
                "alternate-float ('1.5', '1.50000', '1.50', '1.500000e+00', '2.', '150.000000%')",
                "pad-group ('-00_000_001_234', '000,001,234,567', '0x00000000ff', '00_001_234')",
                "n ('1234', '1234.57', '1e-05', '-1234.5')",
                "c ('A', '😀', '\\x01')",
                "bool ('1', '1', '1')",
                "special ('inf', 'nan', '-inf', 'inf%')",
                "mixed ValueError Invalid format specifier '_+015,.2f' for object of type 'int'",
                "comma-hex ValueError Cannot specify ',' with 'x'.",
                "underscore-str ValueError Cannot specify '_' with 's'.",
                "comma-str ValueError Cannot specify ',' with 's'.",
                "precision-int ValueError Precision not allowed in integer format specifier",
                "precision-hex ValueError Precision not allowed in integer format specifier",
                "c-float ValueError Unknown format code 'c' for object of type 'float'",
                "c-char ValueError Unknown format code 'c' for object of type 'str'",
                "c-big OverflowError %c arg not in range(0x110000)",
                "bad-spec ValueError Invalid format specifier '^+_12' for object of type 'int'",
                "bad-spec-str ValueError Invalid format specifier '^+_12' for object of type 'str'",
                "n-str ValueError Unknown format code 'n' for object of type 'str'",
                "hash-str ValueError Alternate form (#) not allowed in string format specifier",
                "byte-b TypeError unsupported format string passed to bytes.__format__",
                "nan ('+nan', ' nan', 'nan', '+nan')",
                "complex ('(1+2j)', '1.00+2.00j', '0+1.5j', '+1.000000-2.000000j', '     +1.0+2.0j      ', '    1.0+2.0j')",
                "complex-special ('0.000000+nanj', 'inf+1.000000j')",
                "complex-prec ('+1e+00+2e+00j', '1.00+2.00j', '1+2j', '(1+2j)')",
                "complex-bad ValueError Unknown format code 'd' for object of type 'complex'",
                "g-int '5'",
                "float-d ValueError Unknown format code 'd' for object of type 'float'",
                "sign-str ValueError Sign not allowed in string format specifier",
                "sign-space-str ValueError Space not allowed in string format specifier",
                "sign-c ValueError Sign not allowed with integer format specifier 'c'",
                "align-str-eq ValueError '=' alignment not allowed in string format specifier",
                "align-str-zero 'a0000'",
                "fill-str '0000a'"
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
