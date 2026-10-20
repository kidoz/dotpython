using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DecimalModuleCompatibilityTests
{
    /// <summary>
    /// The `decimal` module: construction from every accepted form with CPython's error
    /// wording, the special values and their signals, exact comparisons and the numeric
    /// hash, the ArithmeticError hierarchy, context precision and rounding modes with
    /// `localcontext`, the arithmetic operators and their zero/infinity rules,
    /// `quantize`, `sqrt`, integer and rational powers, `max`/`min`, and the `e`, `f`,
    /// `g` and `%` format behaviors.
    /// </summary>
    [Fact]
    public Task TheDecimalModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            from decimal import *
            from decimal import Decimal as D, getcontext, localcontext, ROUND_HALF_UP, ROUND_FLOOR
            def show(label, *thunks):
                results = []
                for thunk in thunks:
                    try:
                        results.append(repr(thunk()))
                    except Exception as error:
                        results.append(type(error).__name__ + ": " + str(error))
                print(label, *results)
            show("construct", lambda: D(), lambda: D(5), lambda: D(-5), lambda: D("1.5"), lambda: D("  -1.50  "), lambda: D("1E+3"), lambda: D("1_0.2_5"), lambda: D(".5"), lambda: D("5."), lambda: D("Infinity"), lambda: D("-Inf"), lambda: D("NaN"), lambda: D("sNaN"), lambda: D("NaN123"), lambda: D("sNaN456"))
            show("tuple", lambda: D((0, (1, 5), -1)), lambda: D((1, (1, 5), -1)), lambda: D((0, (0,), 0)), lambda: D((0, (), 0)), lambda: D((0, (1, 2), 3)))
            show("float", lambda: D(1.5), lambda: D(0.1), lambda: D(float("inf")), lambda: D(float("nan")), lambda: D(True))
            show("bad", lambda: D("abc"), lambda: D("1.2.3"), lambda: D(""), lambda: D(" "), lambda: D("_1"), lambda: D("1_"), lambda: D("1__0"), lambda: D("++1"), lambda: D(None), lambda: D(()) , lambda: D((0,(1,2))))
            show("repr", lambda: (str(D("1.50")), repr(D("1.50")), str(D("1E+3")), str(D("0.000001")), str(D("0.0000001")), str(D("1e-7")), str(D("-0")), str(D("0E-10")), str(D("100")), str(D("1E+2")), str(D("1.0")), str(D("-1.0E+2"))))
            show("tupleout", lambda: D("1.50").as_tuple(), lambda: D("-0.00").as_tuple(), lambda: D("Infinity").as_tuple(), lambda: D("NaN123").as_tuple(), lambda: D("1E+3").as_tuple())
            show("props", lambda: (D("1.50").sign, D("-1.50").sign, D("1.5").adjusted(), D("0.01").adjusted(), D("0").adjusted(), D("1E+3").adjusted()))
            show("pred", lambda: (D("1.5").is_finite(), D("Inf").is_finite(), D("Inf").is_infinite(), D("NaN").is_nan(), D("sNaN").is_snan(), D("NaN").is_qnan(), D("sNaN").is_qnan(), D("-1.5").is_signed(), D("-0").is_signed(), D("0").is_signed(), D("NaN").is_signed()))
            show("convert", lambda: (float(D("1.5")), int(D("1.9")), int(D("-1.9")), bool(D("0")), bool(D("0.0")), bool(D("-1")), bool(D("NaN"))))
            show("ratio", lambda: D("1.5").as_integer_ratio(), lambda: D("-0.25").as_integer_ratio(), lambda: D("1E+2").as_integer_ratio(), lambda: D("Inf").as_integer_ratio(), lambda: D("NaN").as_integer_ratio())
            show("cmp", lambda: (D("1.0") == D("1"), D("1.0") == 1, D("1.5") < D("2"), D("1.5") < 2, D("1.5") < 1.6, D("NaN") == D("NaN"), D("NaN") == 1, D("NaN") != D("NaN"), D("NaN") < D("1"), D("Inf") > D("1e999999"), D("-Inf") < D("1")), lambda: hash(D("1.0")) == hash(D("1")), lambda: hash(D("1.5")) == hash(1.5), lambda: hash(D("0.1")) == hash(D("0.100")))
            show("arith", lambda: (D("1.2") + D("3.4"), D("1.2") - D("3.4"), D("1.2") * D("3.4"), D("1") / D("3"), D("1.50") + D("2.50"), D("1E+3") + D("1"), D("10") / D("4"), D("1.0") * D("1.0"), D("0.00") + D("0")))
            show("arith2", lambda: (D("7") // D("2"), D("7") % D("2"), divmod(D("7"), D("2")), D("-7") // D("2"), D("-7") % D("2"), divmod(D("-7"), D("2")), D("7") // D("-2"), D("7") % D("-2"), D("1.5") // D("0.5")), lambda: D("2") ** 10, lambda: D("2") ** -2, lambda: D("1.5") ** 2, lambda: D("0") ** -1)
            show("specialarith", lambda: D("Inf") + D("1"), lambda: D("Inf") - D("Inf"), lambda: D("NaN") + D("1"), lambda: D("sNaN") + D("1"), lambda: D("1") / D("0"), lambda: D("0") / D("0"), lambda: D("Inf") * D("0"), lambda: D("1") / D("Inf"))
            show("ctx", lambda: getcontext().prec, lambda: getcontext().rounding, lambda: (1 / D("3") if False else D("1") / D("3")))
            getcontext().prec = 5
            show("ctx5", lambda: D("1") / D("3"), lambda: D("1.234567") + D("0"), lambda: D("123456") + D("1"), lambda: D("9.99999") * D("1"))
            getcontext().prec = 28
            show("quant", lambda: D("1.2345").quantize(D("0.01")), lambda: D("1.235").quantize(D("0.01")), lambda: D("1.2345").quantize(D("0.001")), lambda: D("1E+3").quantize(D("1")), lambda: D("1234").quantize(D("1E+2")))
            show("sqrt", lambda: D("2").sqrt(), lambda: D("4").sqrt(), lambda: D("0.25").sqrt(), lambda: D("-1").sqrt(), lambda: D("Inf").sqrt())
            show("misc", lambda: D("1.5").copy_abs(), lambda: D("1.5").copy_negate(), lambda: D("1.5").copy_sign(D("-1")), lambda: D("1.5").max(D("2")), lambda: D("1.5").min(D("2")))
            show("fmt", lambda: format(D("1.5"), ""), lambda: format(D("1.5"), ".2f"), lambda: format(D("1.5"), "10.2f"), lambda: format(D("1.5"), "e"), lambda: format(D("1.5"), "g"), lambda: format(D("1.5"), "%"), lambda: format(D("1234567"), ","), lambda: format(D("1.5"), "010.2f"))

            show("foperr", lambda: D("1") + 1.5, lambda: D("1") - 1.5, lambda: D("1") * 1.5, lambda: D("1") / 1.5, lambda: D("1") // 1.5, lambda: D("1") % 1.5, lambda: D("1") ** 1.5, lambda: 1.5 + D("1"))
            show("prec", lambda: (getcontext().prec, getcontext().rounding))
            getcontext().prec = 6
            show("prec6", lambda: D("1") / D("3"), lambda: D("2") / D("3"), lambda: D("1.2345678") + D("0"), lambda: D("123456789") + D("0"), lambda: D("1.2345678") * D("1"), lambda: D("999999.5") + D("0"), lambda: D("0.0000012345678") + D("0"), lambda: D("1E+10") + D("1"))
            getcontext().prec = 28
            show("rounding-modes", lambda: [D("1.25").quantize(D("0.1"), rounding=m) for m in ("ROUND_HALF_EVEN","ROUND_HALF_UP","ROUND_HALF_DOWN","ROUND_DOWN","ROUND_UP","ROUND_CEILING","ROUND_FLOOR","ROUND_05UP")], lambda: [D("-1.25").quantize(D("0.1"), rounding=m) for m in ("ROUND_HALF_EVEN","ROUND_HALF_UP","ROUND_DOWN","ROUND_UP","ROUND_CEILING","ROUND_FLOOR")], lambda: [D("1.05").quantize(D("0.1"), rounding=m) for m in ("ROUND_05UP","ROUND_HALF_EVEN")], lambda: [D("1.15").quantize(D("0.1"), rounding=m) for m in ("ROUND_05UP","ROUND_HALF_EVEN")])
            getcontext().rounding = ROUND_HALF_UP
            show("ctxround", lambda: D("1") / D("8"), lambda: D("1") / D("3"))
            getcontext().rounding = ROUND_HALF_EVEN
            with localcontext() as ctx:
                ctx.prec = 3
                show("local", lambda: D("1") / D("3"), lambda: D("2") / D("3"), lambda: D("1.2345") + D("0"))
            show("local-after", lambda: D("1") / D("3"), lambda: getcontext().prec)
            show("ctxprops", lambda: (getcontext().prec, getcontext().rounding, getcontext().Emin, getcontext().Emax, getcontext().clamp, getcontext().capitals), lambda: repr(getcontext())[:40])
            show("intfloat", lambda: (D("1.5") == 1.5, D("1.5") < 1.6, D("1.5") > 1.4, D("1") == 1, D("1") < 2, 1 < D("2"), D("1.0000000000000000001") == 1.0, D("1e400") > 1.0, D("NaN") == float("nan"), D("NaN") != float("nan")), lambda: (int(D("-2.9")), int(D("2.9")), float(D("0.1")), bool(D("0E+5"))))
            show("divmore", lambda: D("1") / D("7"), lambda: D("10") / D("3"), lambda: D("-10") / D("3"), lambda: D("1") / D("0.1"), lambda: D("1.0") / D("3"), lambda: D("0.1") + D("0.2"), lambda: D("1E+5") / D("3"), lambda: D("1E-5") / D("3"), lambda: D("1234567890123456789012345678") * D("9"), lambda: D("1.5") * D("2.5") * D("4"))
            show("floormore", lambda: D("7") // D("0.3"), lambda: D("7") % D("0.3"), lambda: divmod(D("7"), D("0.3")), lambda: D("-7") // D("0.3"), lambda: D("1E+10") // D("3"), lambda: D("1") // D("3"), lambda: D("1") % D("3"))
            show("quantmore", lambda: D("0.000123").quantize(D("1E-5")), lambda: D("-0").quantize(D("0.01")), lambda: D("5.5").quantize(D("1E+1")), lambda: D("5E+1").quantize(D("1")), lambda: D("1.2345E+10").quantize(D("1E+5")), lambda: D("Inf").quantize(D("1")), lambda: D("sNaN").quantize(D("1")))
            show("sqrtmore", lambda: D("0.0004").sqrt(), lambda: D("987654321").sqrt(), lambda: D("1E+3").sqrt(), lambda: D("-0").sqrt(), lambda: D("3E-7").sqrt())
            show("powmore", lambda: D("1.1") ** 2, lambda: D("1.23456789") ** 10, lambda: D("2") ** 100, lambda: D("2") ** -3, lambda: 2 ** D("3"), lambda: 2 ** D("-1"), lambda: D("2") ** D("3.0"), lambda: D("9") ** D("0.5"), lambda: D("Inf") ** -1, lambda: D("-0") ** 3, lambda: D("-0") ** 2)
            show("errmore", lambda: D("1") / D("0"), lambda: D("0") / D("0"), lambda: D("Inf") - D("Inf"), lambda: D("Inf") * D("0"), lambda: D("1") % D("0"), lambda: D("1") // D("0"), lambda: divmod(D("1"), D("0")), lambda: D("-1").sqrt(), lambda: D("1E+30").quantize(D("1")), lambda: D("9e999999") * D("10"))
            show("strmore", lambda: [str(x) for x in (D("0.000001"), D("0.0000001"), D("1000000"), D("10000000"), D("1E+6"), D("1E+7"), D("0E+0"), D("0E-7"), D("0E-6"), D("-0E-7"), D("12.3400"), D("1.2300E+5"))], lambda: [repr(x) for x in (D("0.000001"), D("1E+7"), D("0E-7"), D("NaN1"), D("sNaN01"))])
            show("tupmore", lambda: [D(x).as_tuple() for x in ("0", "-0", "0.00", "1E+5", "-1.5E-3", "Inf", "-Inf", "NaN2", "sNaN03")])
            show("smaxmin", lambda: D("1.5").max(D("-Inf")), lambda: D("1.5").min(D("Inf")), lambda: D("-Inf").max(D("Inf")), lambda: D("1").max(D("1.0")), lambda: D("1").min(D("1.0")))
            """"
        );

    [Fact]
    public Task TheContextFlagsAndExtendedMethodsMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            from decimal import *
            def show(label, fn):
                try: print(label, "ok", repr(fn()))
                except Exception as e: print(label, type(e).__name__, e)
            for s in ["1","1.5","0.1","0.0000000000000000000000000001","9.999999999999999999999999999E+999999","1E-1000026","-1E-1000026","-0","0E-1000026","1.2345678901234567890123456789012345"]:
                d = Decimal(s)
                show("nextp "+s, lambda d=d: d.next_plus())
                show("nextm "+s, lambda d=d: d.next_minus())
            print("sign0", Decimal("0").next_minus().as_tuple(), Decimal("-0").next_plus().as_tuple(), Decimal("0").next_plus().as_tuple(), Decimal("-0").next_minus().as_tuple())
            show("inf-plus", lambda: Decimal("Infinity").next_plus())
            show("inf-minus", lambda: Decimal("Infinity").next_minus())
            show("ninf-plus", lambda: Decimal("-Infinity").next_plus())
            show("ninf-minus", lambda: Decimal("-Infinity").next_minus())
            print("toward", Decimal("1").next_toward(Decimal("2")), Decimal("1").next_toward(Decimal("0")), Decimal("1").next_toward(Decimal("1")))
            print("toward0", Decimal("1").next_toward(Decimal("0")).as_tuple(), Decimal("-1").next_toward(Decimal("0")).as_tuple())
            show("toward-nan", lambda: Decimal("1").next_toward(Decimal("NaN")))
            show("toward-snan", lambda: Decimal("1").next_toward(Decimal("sNaN")))
            print("toint", Decimal("1.9").to_integral_value(), Decimal("-1.9").to_integral_value(), Decimal("1.9").to_integral_exact())
            for s in ["1E+2","100","1.00","0E+5","-0.0"]:
                print("toint-"+s, Decimal(s).to_integral_value().as_tuple(), Decimal(s).to_integral_exact().as_tuple())
            with localcontext() as ctx:
                ctx.rounding = ROUND_DOWN
                print("toint-down", Decimal("-1.9").to_integral_value(), Decimal("1.9").to_integral_exact())
                ctx.rounding = ROUND_FLOOR
                print("toint-floor", Decimal("1.9").to_integral_value(), Decimal("-1.9").to_integral_value())
            show("bad-rounding", lambda: Decimal("1.5").to_integral_value(rounding="NOPE"))
            show("rounding-int", lambda: Decimal("1.5").to_integral_value(rounding=5))
            show("ctx-int", lambda: Decimal("1.5").to_integral_value(context=5))
            show("tol-int-inf", lambda: Decimal("Infinity").to_integral_value())
            show("tol-int-snan", lambda: Decimal("sNaN").to_integral_exact())
            print("compare", Decimal("1.5").compare(Decimal("1.50")), Decimal("1").compare(Decimal("2")), Decimal("2").compare(Decimal("1")))
            show("compare-nan", lambda: Decimal("NaN").compare(Decimal("1")))
            show("compare-snan", lambda: Decimal("sNaN").compare(Decimal("1")))
            print("ct", Decimal("1").compare_total(Decimal("1.0")), Decimal("1.0").compare_total(Decimal("1")))
            print("ctm", Decimal("1").compare_total_mag(Decimal("-1")), Decimal("-2").compare_total_mag(Decimal("1")))
            for a,b in [("NaN","1"),("1","NaN"),("sNaN","NaN"),("NaN","sNaN"),("NaN1","NaN2"),("NaN2","NaN1"),("-NaN","NaN"),("NaN10","NaN2"),("sNaN2","sNaN10"),("Infinity","NaN"),("-Infinity","-NaN"),("NaN","NaN"),("sNaN","sNaN")]:
                print("ctn", a, b, Decimal(a).compare_total(Decimal(b)))
            for a,b in [("NaN","1"),("1","NaN"),("-NaN","NaN"),("NaN1","NaN2"),("sNaN","NaN")]:
                print("ctm2", a, b, Decimal(a).compare_total_mag(Decimal(b)))
            print("remnear", Decimal("5").remainder_near(Decimal("3")), Decimal("7").remainder_near(Decimal("3")), Decimal("6").remainder_near(Decimal("3")), Decimal("-5").remainder_near(Decimal("3")))
            print("rem-tie", [(a,b,str(Decimal(a).remainder_near(Decimal(b)))) for a,b in [("5","2"),("7","2"),("-5","2"),("-7","2"),("4","2")]])
            show("remnear-zero", lambda: Decimal("5").remainder_near(Decimal("0")))
            show("rem-inf", lambda: Decimal("Infinity").remainder_near(Decimal("2")))
            show("rem-yinf", lambda: Decimal("2").remainder_near(Decimal("Infinity")))
            show("rem-nan", lambda: Decimal("NaN").remainder_near(Decimal("2")))
            show("rem-snan", lambda: Decimal("sNaN").remainder_near(Decimal("2")))
            print("logb", Decimal("100").logb(), Decimal("0.001").logb(), Decimal("1").logb(), Decimal("Infinity").logb())
            print("logb-small", Decimal("0.01").logb().as_tuple())
            show("logb-zero", lambda: Decimal("0").logb())
            show("logb-nan", lambda: Decimal("NaN").logb())
            show("logb-snan", lambda: Decimal("sNaN").logb())
            print("fma", Decimal("2").fma(Decimal("3"), Decimal("4")), Decimal("2").fma(Decimal("3"), Decimal("4"), context=getcontext()))
            print("fma-round", Decimal("1.23456789012345678901234567").fma(Decimal("1"), Decimal("0")))
            show("fma-inf", lambda: Decimal("0").fma(Decimal("Infinity"), Decimal("0")))
            show("fma-inf2", lambda: Decimal("Infinity").fma(Decimal("1"), Decimal("-Infinity")))
            show("fma-inf3", lambda: Decimal("Infinity").fma(Decimal("0"), Decimal("2")))
            show("fma-nan", lambda: Decimal("NaN").fma(Decimal("1"), Decimal("2")))
            show("fma-snan", lambda: Decimal("sNaN").fma(Decimal("1"), Decimal("2")))
            show("fma-qnan-inf", lambda: Decimal("NaN").fma(Decimal("Infinity"), Decimal("0")))
            print("fromnum", Decimal.from_number(3.5), Decimal.from_number(5), Decimal.from_number(Decimal("1.2")), Decimal.from_number(True))
            show("fromnum-str", lambda: Decimal.from_number("1.2"))
            show("fromnum-none", lambda: Decimal.from_number(None))
            show("fromnum-missing", lambda: Decimal.from_number())
            print("compare-arity", end=" ")
            show("compare-arity", lambda: Decimal("1").compare())
            show("compare-many", lambda: Decimal("1").compare(Decimal("2"), Decimal("3")))
            show("fma-missing", lambda: Decimal("1").fma(Decimal("2")))
            show("next-ctx-int", lambda: Decimal("1").next_plus(context=5))
            ctx = getcontext()
            print("ctx-basic", ctx.prec, ctx.rounding, ctx.Emin, ctx.Emax, ctx.capitals, ctx.clamp, ctx.Etiny(), ctx.Etop())
            print("flags-fresh", {k.__name__: v for k,v in ctx.flags.items()})
            print("traps-fresh", {k.__name__: v for k,v in ctx.traps.items()})
            ctx.clear_flags()
            Decimal("1.999").to_integral_exact()
            print("after-exact", {k.__name__: v for k,v in ctx.flags.items() if v})
            ctx.clear_flags()
            Decimal("1.000").to_integral_exact()
            print("after-1000", {k.__name__: v for k,v in ctx.flags.items() if v})
            ctx.clear_flags()
            Decimal("1.9").to_integral_value()
            print("after-value", {k.__name__: v for k,v in ctx.flags.items() if v})
            ctx.clear_flags()
            with localcontext() as sub:
                sub.traps[Inexact] = True
                show("trap-inexact", lambda: Decimal("1.999").to_integral_exact())
                print("trap-flags", {k.__name__: v for k,v in sub.flags.items() if v})
            print("outer-flags", {k.__name__: v for k,v in ctx.flags.items() if v})
            print("traps-back", ctx.traps[Inexact])
            print("ctx-create", ctx.create_decimal("1.2345678901234567890123456789012345"))
            print("ctx-create-float", ctx.create_decimal(0.1))
            ctx.clear_flags()
            ctx.create_decimal("1.2345678901234567890123456789012345")
            print("flags-create", {k.__name__: v for k,v in ctx.flags.items() if v})
            print("ctx-repr", repr(ctx))
            print("ctx-copy", repr(ctx.copy()))

            show("ctx-kw", lambda: Context(prec=2))
            show("ctx-prec", lambda: Context(prec=2).prec)
            show("ctx-add-create", lambda: Context(prec=2).create_decimal("1.2345"))
            show("quantize-ctx", lambda: Decimal("1.239").quantize(Decimal("0.01"), context=Context(prec=2)))
            show("quantize-ctx2", lambda: Decimal("1.239").quantize(Decimal("0.01"), context=Context(prec=4)))
            show("quantize-rounding", lambda: Decimal("1.239").quantize(Decimal("0.01"), rounding=ROUND_DOWN))
            show("quantize-bad-rounding", lambda: Decimal("1.239").quantize(Decimal("0.01"), rounding="NOPE"))
            """"
        );
}
