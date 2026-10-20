using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FractionsModuleCompatibilityTests
{
    /// <summary>
    /// The `fractions` module: construction from every accepted form (the exact float
    /// ratio included), normalization and zero denominators, the arithmetic and its
    /// reflected fallbacks across `int`, `float` and `complex`, the comparison rules for
    /// infinities and NaNs, the numeric hash shared with `int` and `float`, the integer
    /// conversions and rounding, `limit_denominator`, `from_float`/`from_number`, copy
    /// and the in-process pickle round trip, subclass behavior, the slotted attribute
    /// surface, and the general and float-style `__format__` grammars with CPython's
    /// refusal wording.
    /// </summary>
    [Fact]
    public Task TheFractionsModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            """"
        );
}
