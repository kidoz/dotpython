using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FormatSpecCompatibilityTests
{
    [Fact]
    public Task FormatSpecSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
