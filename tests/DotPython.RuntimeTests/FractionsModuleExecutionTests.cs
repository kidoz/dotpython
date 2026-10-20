using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FractionsModuleExecutionTests
{
    /// <summary>
    /// The runtime's `fractions`: `Fraction` over exact integer arithmetic with CPython
    /// 3.14's construction rules (integers, rationals, floats by their exact ratio,
    /// numeric strings with underscores and exponents), the normalized two-field state,
    /// the mixed-mode arithmetic that promotes to float and complex, the comparison and
    /// hashing rules shared with `int` and `float`, `limit_denominator`, the `from_*`
    /// constructors, copy and pickle, and the general and float-style format grammars.
    /// The `numbers` ABCs are absent from the runtime, so the MRO stops at `object`;
    /// that local answer, with `from_decimal` now exercised against the decimal module,
    /// is pinned at the end.
    /// </summary>
    [Fact]
    public void TheFractionsModuleAnswersAsCpythonDoes()
    {
        var output = Run(
            """"
            from fractions import Fraction as F
            import math, copy, pickle
            def show(label, *thunks):
                results = []
                for thunk in thunks:
                    try:
                        results.append(repr(thunk()))
                    except Exception as error:
                        results.append(type(error).__name__ + ": " + str(error))
                print(label, *results)
            show("construct", lambda: (F(), F(3), F(8, 6), F(-8, 6), F(8, -6), F(-8, -6), F(0, 5), F(True), F(1, True)))
            show("rational", lambda: (F(F(1, 7)), F(F(1, 7), 5), F(F(1, 7), F(2, 3)), F(3, F(2, 3))))
            show("float", lambda: (F(1.5), F(0.1), F(2.25), F(-0.0), F(1e300)))
            show("strings", lambda: (F("3/2"), F("-3/2"), F(" +3 / 4 "), F("1_000/2_000"), F("1.5"), F(".5"), F("1."), F("-.25"), F("1e3"), F("1e-3"), F("1E+2"), F("1_0.2_5e1_0"), F("  7  "), F("+0"), F("1 / 2 "), F("+1.5e+2"), F("007"), F("0.000"), F("-0"), F("\t -5 \n")))
            show("badstrings", lambda: F("1/0"), lambda: F("abc"), lambda: F("1.5/2"), lambda: F("1e"), lambda: F("1_"), lambda: F("_1"), lambda: F("1__0"), lambda: F(""), lambda: F("1/2/3"))
            show("special", lambda: F(float("inf")), lambda: F(float("nan")), lambda: F("inf"))
            show("types", lambda: F(object()), lambda: F(1, 2.0), lambda: F(1.5, 2), lambda: F(complex(1, 2)), lambda: F(1, 0), lambda: F(F(1, 2), 0))
            show("props", lambda: (F(3, 4).numerator, F(3, 4).denominator, F(-3, 4).numerator))
            show("repr", lambda: (repr(F(3, 4)), str(F(3, 4)), repr(F(5)), str(F(-5, 7)), repr(F(-5, 7))))
            show("math", lambda: (int(F(7, 2)), int(F(-7, 2)), float(F(1, 3)), bool(F(0)), bool(F(-1)), math.trunc(F(-7, 2)), math.floor(F(-7, 2)), math.ceil(F(-7, 2)), round(F(7, 2)), round(F(5, 2)), round(F(-7, 2)), round(F(-5, 2))))
            show("round2", lambda: (round(F(1234, 100), 1), round(F(15, 10), 0), round(F(25, 10), 0), round(F(-15, 10), 0), round(F(1234, 100), -1), round(F(1, 3), 5), round(F(2, 3), -5), round(F(1, 2), 100), round(F(1, 2), -100)))
            show("intfloat", lambda: (int(F(-5, 2)), float(F(-1, 3)), float(F(10**400, 3)), int(F(10**400, 10**399))))
            show("arith", lambda: (F(1,2)+F(1,3), F(1,2)-F(1,3), F(1,2)*F(2,3), F(1,2)/F(3,4), F(1,2)//F(1,3), F(1,2)%F(1,3), divmod(F(1,2), F(1,3)), F(1,2)**2, F(1,2)**-2, F(-1,2)**-3, F(1,2)**F(1,2), 2**F(1,2), F(2)**F(3,1)))
            show("mixed", lambda: (F(1,2)+1, 1+F(1,2), F(1,2)+0.5, 0.5+F(1,2), F(1,2)*2, 2*F(1,2), F(1,2)/2, 2/F(1,2), F(1,2)//2, 2//F(1,2), F(1,2)%1, 1%F(1,2), divmod(F(1,2), 1), divmod(1, F(1,2)), F(1,2)+1j, 1j+F(1,2)))
            show("mods", lambda: (F(-7,3) % 2, F(7,-3) % 2, F(-7,3) // 2, divmod(F(-7,3), 2), divmod(F(7,3), F(-2,5)), divmod(7, F(3,1)), (-7) % F(3,2)))
            show("pow2", lambda: F(0)**-1, lambda: F(0)**F(1,2), lambda: F(-1)**F(1,2), lambda: F(-1)**0.5, lambda: 2.0**F(1,2), lambda: F(1,2)**complex(1,1), lambda: pow(F(1,2), 2, 3), lambda: pow(2, 10, 1000))
            show("cmp", lambda: (F(1,2)==F(1,2), F(1,2)==0.5, F(1,2)==1, F(1,2)<F(2,3), F(1,2)<0.6, 0.4<F(1,2), F(1,2)!=0.5, F(1,2)==complex(0.5,0), F(1,2)>float("nan"), F(1,2)<float("inf"), F(1,2)==float("inf"), F(1,2) < "x"))
            show("hash", lambda: (hash(F(1,2))==hash(0.5), hash(F(3,1))==hash(3), hash(F(1,3)), hash(F(2,3)), hash(F(-1,3)), hash(F(0)), hash(F(1, 2305843009213693951)), hash(F(2305843009213693951, 1))))
            show("unary", lambda: (+F(1,2), -F(1,2), abs(F(-1,2)), abs(F(1,2)), -F(0)))
            show("errors", lambda: F(1,2)+"x", lambda: "x"+F(1,2), lambda: F(1,2)/0, lambda: F(1,2)//0, lambda: F(1,2)%0, lambda: divmod(F(1,2), 0), lambda: F(1,2)**"x")
            show("limit", lambda: F(3.141592653589793).limit_denominator(10), lambda: F(3.141592653589793).limit_denominator(100), lambda: F(4321, 8765).limit_denominator(10000), lambda: F(1,3).limit_denominator(), lambda: F(-1,3).limit_denominator(1), lambda: F(3,2).limit_denominator(1), lambda: F(1,3).limit_denominator(0))
            show("from", lambda: F.from_float(0.5), lambda: F.from_float(3), lambda: F.from_number(0.5), lambda: F.from_number(3), lambda: F.from_float("x"), lambda: F.from_number("x"), lambda: F.from_number(True), lambda: F.from_float(True))
            show("methods", lambda: (F(3,4).as_integer_ratio(), F(4,2).is_integer(), F(3,2).is_integer()))
            show("copy", lambda: (copy.copy(F(1,2)) == F(1,2), copy.deepcopy(F(1,2)) == F(1,2), pickle.loads(pickle.dumps(F(1,2))) == F(1,2)))
            class Sub(F):
                pass
            s = Sub(3, 4)
            show("sub", lambda: (type(s).__name__, repr(s), str(s), isinstance(s, F), s + F(1, 4), type(s + F(1, 4)).__name__, hash(s) == hash(F(3, 4)), +s, -s, type(+s).__name__))
            show("sub-copy", lambda: (type(copy.copy(s)).__name__, type(pickle.loads(pickle.dumps(s))).__name__))
            show("setattr", lambda: setattr(F(1,2), "x", 1), lambda: setattr(F(1,2), "numerator", 5), lambda: delattr(F(1,2), "numerator"), lambda: F(1,2).__dict__)
            show("slots", lambda: (F.__slots__, F.__module__, F.__qualname__, F.__doc__[:20], __import__("fractions").__all__))
            show("general", lambda: format(F(3,4), ""), lambda: format(F(3,4), "10"), lambda: format(F(-3,4), "10"), lambda: format(F(5), "5"), lambda: format(F(5), "#5"), lambda: format(F(3,4), "<10"), lambda: format(F(3,4), "^10"), lambda: format(F(3,4), "=10"), lambda: format(F(-3,4), "+10"), lambda: format(F(3,4), "010"), lambda: format(F(3,4), "0>10"), lambda: format(F(3,4), "*^10"), lambda: format(F(1234567,8), ","), lambda: format(F(1234567,8), "_"), lambda: format(F(3,4), "x"), lambda: format(F(3,4), ".2"))
            show("floatstyle", lambda: format(F(1,3), "f"), lambda: format(F(1,3), ".2f"), lambda: format(F(1,3), ".0f"), lambda: format(F(2,3), ".0f"), lambda: format(F(1,3), "e"), lambda: format(F(1,3), ".2e"), lambda: format(F(1,3), "E"), lambda: format(F(1,3), "g"), lambda: format(F(1,3), ".2g"), lambda: format(F(1,3), "G"), lambda: format(F(1,4), "%"), lambda: format(F(1,4), ".1%"), lambda: format(F(3,4), "+.2f"), lambda: format(F(-3,4), " .2f"), lambda: format(F(1,3), "#.0f"), lambda: format(F(1,3), "#.2g"), lambda: format(F(1234567,8), ",.2f"), lambda: format(F(1234567,8), "_.2f"))
            show("float2", lambda: format(F(123456789, 1000), ",.2f"), lambda: format(F(123456789, 1000), ",.3e"), lambda: format(F(123456789, 1000), ",.3g"), lambda: format(F(1, 8), ".3g"), lambda: format(F(1000), ".3g"), lambda: format(F(1000), "#.3g"), lambda: format(F(1, 10000), ".3g"), lambda: format(F(0), ".3g"), lambda: format(F(0), "f"), lambda: format(F(-0, 1), ".1f"))
            show("pad", lambda: format(F(1,3), "10.2f"), lambda: format(F(1,3), "<10.2f"), lambda: format(F(1,3), "^10.2f"), lambda: format(F(1,3), "=10.2f"), lambda: format(F(1,3), "010.2f"), lambda: format(F(-1,3), "010.2f"), lambda: format(F(-1,3), "0=10.2f"), lambda: format(F(1,3), "*>10.2f"), lambda: format(F(12345, 100), "010.2f"), lambda: format(F(12345, 100), ",010.2f"))
            show("bad", lambda: format(F(1,3), "z"), lambda: format(F(1,3), ".2z"), lambda: format(F(1,3), "0<10.2f"), lambda: format(F(1,3), "!"), lambda: format(F(1,3), ".f"), lambda: format(F(1,3), "10.2x"), lambda: format(F(1,3), 5))
            show("negzero", lambda: format(F(-0), "z.1f"), lambda: format(F(1, 1000), ".1f"), lambda: format(F(-1, 1000), ".1f"))
            show("fmt-signs", lambda: format(F(-3,4), "08.2f"), lambda: format(F(3,4), "+08.2f"), lambda: format(F(-3,4), " 08.2f"), lambda: format(F(-3,4), "z.1f"), lambda: format(F(-1,100), "z.1f"), lambda: format(F(3,4), "#"), lambda: format(F(-5,1), "#"))
            show("fmt-group", lambda: format(F(12345678, 1), ","), lambda: format(F(12345678, 1), "_"), lambda: format(F(-12345678, 1), ","), lambda: format(F(1234567, 1000), "_.2f"), lambda: format(F(1234567, 1000), ",_.2f"), lambda: format(F(1, 3), ".3%"), lambda: format(F(1, 3), "010.3%"))
            show("fmt-types", lambda: format(F(1, 8), "F"), lambda: format(F(1000000), "E"), lambda: format(F(1, 1000000), "G"), lambda: format(F(1234, 100), "g"), lambda: format(F(1234, 100), "#g"), lambda: format(F(0), "e"), lambda: format(F(0), "g"))
            show("fmt-pad", lambda: format(F(3,4), "*>10"), lambda: format(F(3,4), "*<10"), lambda: format(F(3,4), "*^10"), lambda: format(F(-3,4), "*=10"), lambda: format(F(3,4), "=10.2f"), lambda: format(F(3,4), "0=10.2f"))
            class G(F):
                def __repr__(self): return "G!"
            show("subfmt", lambda: (format(G(1,3), ".2f"), G(1,3).__format__(""), f"{G(1,3)}"))
            show("local-decimal", lambda: F.from_decimal(3))
            show("local-mro", lambda: F.__mro__)
            show("local-bases", lambda: F.__bases__)
            show("local-names", lambda: sorted(n for n in dir(__import__("fractions")) if not n.startswith("_")))
            """"
        );
        Assert.Equal(
            Lines(
                "construct (Fraction(0, 1), Fraction(3, 1), Fraction(4, 3), Fraction(-4, 3), Fraction(-4, 3), Fraction(4, 3), Fraction(0, 1), Fraction(1, 1), Fraction(1, 1))",
                "rational (Fraction(1, 7), Fraction(1, 35), Fraction(3, 14), Fraction(9, 2))",
                "float (Fraction(3, 2), Fraction(3602879701896397, 36028797018963968), Fraction(9, 4), Fraction(0, 1), Fraction(1000000000000000052504760255204420248704468581108159154915854115511802457988908195786371375080447864043704443832883878176942523235360430575644792184786706982848387200926575803737830233794788090059368953234970799945081119038967640880074652742780142494579258788820056842838115669472196386865459400540160, 1))",
                "strings (Fraction(3, 2), Fraction(-3, 2), Fraction(3, 4), Fraction(1, 2), Fraction(3, 2), Fraction(1, 2), Fraction(1, 1), Fraction(-1, 4), Fraction(1000, 1), Fraction(1, 1000), Fraction(100, 1), Fraction(102500000000, 1), Fraction(7, 1), Fraction(0, 1), Fraction(1, 2), Fraction(150, 1), Fraction(7, 1), Fraction(0, 1), Fraction(0, 1), Fraction(-5, 1))",
                "badstrings ZeroDivisionError: Fraction(1, 0) ValueError: Invalid literal for Fraction: 'abc' ValueError: Invalid literal for Fraction: '1.5/2' ValueError: Invalid literal for Fraction: '1e' ValueError: Invalid literal for Fraction: '1_' ValueError: Invalid literal for Fraction: '_1' ValueError: Invalid literal for Fraction: '1__0' ValueError: Invalid literal for Fraction: '' ValueError: Invalid literal for Fraction: '1/2/3'",
                "special OverflowError: cannot convert Infinity to integer ratio ValueError: cannot convert NaN to integer ratio ValueError: Invalid literal for Fraction: 'inf'",
                "types TypeError: argument should be a string or a Rational instance or have the as_integer_ratio() method TypeError: both arguments should be Rational instances TypeError: both arguments should be Rational instances TypeError: argument should be a string or a Rational instance or have the as_integer_ratio() method ZeroDivisionError: Fraction(1, 0) ZeroDivisionError: Fraction(1, 0)",
                "props (3, 4, -3)",
                "repr ('Fraction(3, 4)', '3/4', 'Fraction(5, 1)', '-5/7', 'Fraction(-5, 7)')",
                "math (3, -3, 0.3333333333333333, False, True, -3, -4, -3, 4, 2, -4, -2)",
                "round2 (Fraction(123, 10), Fraction(2, 1), Fraction(2, 1), Fraction(-2, 1), Fraction(10, 1), Fraction(33333, 100000), Fraction(0, 1), Fraction(1, 2), Fraction(0, 1))",
                "intfloat OverflowError: integer division result too large for a float",
                "arith (Fraction(5, 6), Fraction(1, 6), Fraction(1, 3), Fraction(2, 3), 1, Fraction(1, 6), (1, Fraction(1, 6)), Fraction(1, 4), Fraction(4, 1), Fraction(-8, 1), 0.7071067811865476, 1.4142135623730951, Fraction(8, 1))",
                "mixed (Fraction(3, 2), Fraction(3, 2), 1.0, 1.0, Fraction(1, 1), Fraction(1, 1), Fraction(1, 4), Fraction(4, 1), 0, 4, Fraction(1, 2), Fraction(0, 1), (0, Fraction(1, 2)), (2, Fraction(0, 1)), (0.5+1j), (0.5+1j))",
                "mods (Fraction(5, 3), Fraction(5, 3), -2, (-2, Fraction(5, 3)), (-6, Fraction(-1, 15)), (2, Fraction(1, 1)), Fraction(1, 2))",
                "pow2 ZeroDivisionError: Fraction(1, 0) 0.0 (6.123233995736766e-17+1j) (6.123233995736766e-17+1j) 1.4142135623730951 (0.38461945068198605-0.3194806381568174j) TypeError: unsupported operand type(s) for ** or pow(): 'Fraction', 'int', 'int' 24",
                "cmp TypeError: '<' not supported between instances of 'Fraction' and 'str'",
                "hash (True, True, 1537228672809129301, 768614336404564651, -1537228672809129301, 0, 314159, 0)",
                "unary (Fraction(1, 2), Fraction(-1, 2), Fraction(1, 2), Fraction(1, 2), Fraction(0, 1))",
                "errors TypeError: unsupported operand type(s) for +: 'Fraction' and 'str' TypeError: can only concatenate str (not \"Fraction\") to str ZeroDivisionError: Fraction(1, 0) ZeroDivisionError: division by zero ZeroDivisionError: division by zero ZeroDivisionError: division by zero TypeError: unsupported operand type(s) for ** or pow(): 'Fraction' and 'str'",
                "limit Fraction(22, 7) Fraction(311, 99) Fraction(4321, 8765) Fraction(1, 3) Fraction(0, 1) Fraction(1, 1) ValueError: max_denominator should be at least 1",
                "from Fraction(1, 2) Fraction(3, 1) Fraction(1, 2) Fraction(3, 1) TypeError: Fraction.from_float() only takes floats, not 'x' (str) TypeError: argument should be a Rational instance or have the as_integer_ratio() method Fraction(1, 1) Fraction(1, 1)",
                "methods ((3, 4), True, False)",
                "copy (True, True, True)",
                "sub ('Sub', 'Sub(3, 4)', '3/4', True, Fraction(1, 1), 'Fraction', True, Fraction(3, 4), Fraction(-3, 4), 'Fraction')",
                "sub-copy ('Sub', 'Sub')",
                "setattr AttributeError: 'Fraction' object has no attribute 'x' and no __dict__ for setting new attributes AttributeError: property 'numerator' of 'Fraction' object has no setter AttributeError: property 'numerator' of 'Fraction' object has no deleter AttributeError: 'Fraction' object has no attribute '__dict__'",
                "slots (('_numerator', '_denominator'), 'fractions', 'Fraction', 'This class implement', ['Fraction'])",
                "general '3/4' '       3/4' '      -3/4' '    5' '  5/1' '3/4       ' '   3/4    ' '       3/4' '      -3/4' ValueError: Invalid format specifier '010' for object of type 'Fraction' '00000003/4' '***3/4****' '1,234,567/8' '1_234_567/8' ValueError: Invalid format specifier 'x' for object of type 'Fraction' ValueError: Invalid format specifier '.2' for object of type 'Fraction'",
                "floatstyle '0.333333' '0.33' '0' '1' '3.333333e-01' '3.33e-01' '3.333333E-01' '0.333333' '0.33' '0.333333' '25.000000%' '25.0%' '+0.75' '-0.75' '0.' '0.33' '154,320.88' '154_320.88'",
                "float2 '123,456.79' '1.235e+05' '1.23e+05' '0.125' '1e+03' '1.00e+03' '0.0001' '0' '0.000000' '0.0'",
                "pad '      0.33' '0.33      ' '   0.33   ' '      0.33' '0000000.33' '-000000.33' '-000000.33' '******0.33' '0000123.45' ValueError: Invalid format specifier ',010.2f' for object of type 'Fraction'",
                "bad ValueError: Invalid format specifier 'z' for object of type 'Fraction' ValueError: Invalid format specifier '.2z' for object of type 'Fraction' '0.33000000' ValueError: Invalid format specifier '!' for object of type 'Fraction' ValueError: Invalid format specifier '.f' for object of type 'Fraction' ValueError: Invalid format specifier '10.2x' for object of type 'Fraction' TypeError: format() argument 2 must be str, not int",
                "negzero '0.0' '0.0' '-0.0'",
                "fmt-signs '-0000.75' '+0000.75' '-0000.75' '-0.8' '0.0' '3/4' '-5/1'",
                "fmt-group '12,345,678' '12_345_678' '-12,345,678' '1_234.57' ValueError: Invalid format specifier ',_.2f' for object of type 'Fraction' '33.333%' '00033.333%'",
                "fmt-types '0.125000' '1.000000E+06' '1E-06' '12.34' '12.3400' '0.000000e+00' '0'",
                "fmt-pad '*******3/4' '3/4*******' '***3/4****' '-******3/4' '      0.75' '0000000.75'",
                "subfmt ('0.33', '1/3', '1/3')",
                "local-decimal Fraction(3, 1)",
                "local-mro (<class 'fractions.Fraction'>, <class 'object'>)",
                "local-bases (<class 'object'>,)",
                "local-names ['Fraction']"
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
