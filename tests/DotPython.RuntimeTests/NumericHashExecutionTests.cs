using System.Numerics;
using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class NumericHashExecutionTests
{
    [Fact]
    public void ComplexConstructionPreservesScalarSignedZerosAndInfiniteComponents()
    {
        var negativeZero = BitConverter.UInt64BitsToDouble(0x8000000000000000UL);
        // Component bits observed with CPython 3.14.7, including its two-argument
        // rotation when either argument is already complex.
        (
            PythonValue Real,
            PythonValue Imaginary,
            ulong RealBits,
            ulong ImaginaryBits
        )[] samples =
        [
            (new PythonFloatingPointValue(0), new PythonFloatingPointValue(0), 0, 0),
            (
                new PythonFloatingPointValue(negativeZero),
                new PythonFloatingPointValue(0),
                0x8000000000000000UL,
                0
            ),
            (
                new PythonFloatingPointValue(0),
                new PythonFloatingPointValue(negativeZero),
                0,
                0x8000000000000000UL
            ),
            (
                new PythonFloatingPointValue(negativeZero),
                new PythonFloatingPointValue(negativeZero),
                0x8000000000000000UL,
                0x8000000000000000UL
            ),
            (
                new PythonFloatingPointValue(1),
                new PythonFloatingPointValue(double.PositiveInfinity),
                0x3ff0000000000000UL,
                0x7ff0000000000000UL
            ),
            (
                new PythonFloatingPointValue(1),
                new PythonFloatingPointValue(double.NegativeInfinity),
                0x3ff0000000000000UL,
                0xfff0000000000000UL
            ),
            (
                new PythonComplexValue(new Complex(1, double.PositiveInfinity)),
                new PythonFloatingPointValue(2),
                0x3ff0000000000000UL,
                0x7ff0000000000000UL
            ),
            (
                new PythonFloatingPointValue(2),
                new PythonComplexValue(new Complex(1, double.PositiveInfinity)),
                0xfff0000000000000UL,
                0x3ff0000000000000UL
            ),
            (
                new PythonComplexValue(new Complex(double.PositiveInfinity, 1)),
                new PythonComplexValue(new Complex(2, 3)),
                0x7ff0000000000000UL,
                0x4008000000000000UL
            ),
        ];
        foreach (var (real, imaginary, realBits, imaginaryBits) in samples)
        {
            var result = Assert.IsType<PythonComplexValue>(
                ManagedObjectProtocols.Call(
                    PythonBuiltinFunctions.Complex,
                    [real, imaginary],
                    default
                )
            );
            Assert.Equal(realBits, BitConverter.DoubleToUInt64Bits(result.Value.Real));
            Assert.Equal(imaginaryBits, BitConverter.DoubleToUInt64Bits(result.Value.Imaginary));
        }

        var existing = new PythonComplexValue(new Complex(double.NaN, negativeZero));
        Assert.Same(
            existing,
            ManagedObjectProtocols.Call(PythonBuiltinFunctions.Complex, [existing], default)
        );
    }

    [Fact]
    public void FloatBitPatternsMatchCPythonGoldenHashesWithoutAnExecution()
    {
        // CPython 3.14.7, 64-bit hash width: signed zero, subnormal and normal
        // boundaries, adjacent representable values, infinities, and seeded samples.
        (ulong Bits, long Hash)[] samples =
        [
            (0x0000000000000000UL, 0L),
            (0x0000000000000001UL, 16777216L),
            (0x0000000000000002UL, 33554432L),
            (0x000fffffffffffffUL, 2305843009196949503L),
            (0x0010000000000000UL, 32768L),
            (0x0010000000000001UL, 16809984L),
            (0x3fb999999999999aUL, 230584300921369408L),
            (0x3fe0000000000000UL, 1152921504606846976L),
            (0x3fefffffffffffffUL, 2305843009213693696L),
            (0x3ff0000000000000UL, 1L),
            (0x3ff0000000000001UL, 513L),
            (0x433fffffffffffffUL, 9007199254740991L),
            (0x4340000000000000UL, 9007199254740992L),
            (0x43c0000000000000UL, 1L),
            (0x7fefffffffffffffUL, 2234066890152476671L),
            (0x7ff0000000000000UL, 314159L),
            (0x8000000000000000UL, 0L),
            (0x8000000000000001UL, -16777216L),
            (0x8000000000000002UL, -33554432L),
            (0x800fffffffffffffUL, -2305843009196949503L),
            (0x8010000000000000UL, -32768L),
            (0x8010000000000001UL, -16809984L),
            (0xbfb999999999999aUL, -230584300921369408L),
            (0xbfe0000000000000UL, -1152921504606846976L),
            (0xbfefffffffffffffUL, -2305843009213693696L),
            (0xbff0000000000000UL, -2L),
            (0xbff0000000000001UL, -513L),
            (0xc33fffffffffffffUL, -9007199254740991L),
            (0xc340000000000000UL, -9007199254740992L),
            (0xc3c0000000000000UL, -2L),
            (0xffefffffffffffffUL, -2234066890152476671L),
            (0xfff0000000000000UL, -314159L),
            (0x12ecada316a3faa9UL, 1887406540521999958L),
            (0x79fad98375eea554UL, 62341937058821555L),
            (0x33b714dabc450ad0UL, 406053384900781L),
            (0xf1d3f42d407683d7UL, -2092854540028461095L),
            (0x5bbfcd5f1e81e566UL, 2051960919302601131L),
            (0x5aa6d175bbc21905UL, 982606567271696897L),
            (0x9e7792eb377f09e2UL, -1931445841550126885L),
            (0x3dfed9b1a34077a0UL, 1161339366664266566L),
        ];
        foreach (var (bits, expected) in samples)
        {
            var value = new PythonFloatingPointValue(BitConverter.UInt64BitsToDouble(bits));
            Assert.Equal(new BigInteger(expected), ManagedObjectProtocols.ComputePythonHash(value));
        }
    }

    [Fact]
    public void ComplexHashesMatchCPythonForFractionalComponentsAndSignedOverflow()
    {
        (double Real, double Imaginary, long Hash)[] samples =
        [
            (0.1, 0.2, 1614090106475185856L),
            (1.5, -2.75, -4035225266125964421L),
            (0, 1, 1000003L),
            (1, 1, 1000004L),
            (-1, 0, -2L),
            (double.PositiveInfinity, double.NegativeInfinity, -314159628318L),
            (double.MaxValue, double.MaxValue, 9098397147194244540L),
            (double.Epsilon, -0.5, -3458764513803763712L),
        ];
        foreach (var (real, imaginary, expected) in samples)
        {
            var value = new PythonComplexValue(new Complex(real, imaginary));
            Assert.Equal(new BigInteger(expected), ManagedObjectProtocols.ComputePythonHash(value));
        }
    }

    [Fact]
    public void IntegerHashGoldenBoundariesRemainEqualToIntegralFloatHashes()
    {
        (BigInteger Value, long Hash)[] samples =
        [
            (long.MinValue, -4L),
            (long.MaxValue, 3L),
            (BigInteger.One << 63, 4L),
            ((BigInteger.One << 63) * -1 - 1, -5L),
            (BigInteger.One << 100, 549755813888L),
            (-(BigInteger.One << 100), -549755813888L),
            ((BigInteger.One << 100) + 1, 549755813889L),
            (-(BigInteger.One << 100) - 1, -549755813889L),
        ];
        foreach (var (number, expected) in samples)
        {
            var whole = PythonWholeNumberValue.Create(number);
            Assert.Equal(new BigInteger(expected), ManagedObjectProtocols.ComputePythonHash(whole));
            var floating = new PythonFloatingPointValue((double)number);
            if (new BigInteger(floating.Value) == number)
                Assert.Equal(
                    ManagedObjectProtocols.ComputePythonHash(whole),
                    ManagedObjectProtocols.ComputePythonHash(floating)
                );
        }
    }

    [Fact]
    public void NaNHashesRemainStableForTheirOwnersAndSameIdentityLookups()
    {
        PythonValue[] values =
        [
            new PythonFloatingPointValue(double.NaN),
            new PythonFloatingPointValue(BitConverter.UInt64BitsToDouble(0x7ff8000000000001UL)),
            new PythonComplexValue(new Complex(double.NaN, 0)),
            new PythonComplexValue(new Complex(0, double.NaN)),
            new PythonComplexValue(new Complex(double.NaN, double.NaN)),
        ];
        foreach (var value in values)
        {
            var hash = ManagedObjectProtocols.ComputePythonHash(value);
            Assert.NotEqual(BigInteger.MinusOne, hash);
            var dictionary = new PythonDictionaryValue([]);
            var marker = new PythonListValue([]);
            ManagedObjectProtocols.SetItem(dictionary, value, marker);
            for (var index = 0; index < 16; index++)
            {
                Assert.Equal(hash, ManagedObjectProtocols.ComputePythonHash(value));
                Assert.Same(marker, ManagedObjectProtocols.GetItem(dictionary, value));
            }
        }
    }

    [Fact]
    public void SavedHashUsesCurrentCallbackOutputCancellationAndLargeResultNormalization()
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        using var previousCancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            """
            class Key:
                value = 9223372036854775807
                def __hash__(self):
                    print('hash callback')
                    return self.value
            key = Key()
            operation = hash
            """,
            "numeric_hash_saved_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(operation(key))
            key.value = 9223372036854775808
            print(operation(key))
            key.value = -9223372036854775809
            print(operation(key))
            """,
            "numeric_hash_saved_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(
            Lines(
                "hash callback",
                "9223372036854775807",
                "hash callback",
                "4",
                "hash callback",
                "-5"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void HashCallbackUsesCurrentBudgetAndAllowsRecoveryAfterUncatchableFailure()
    {
        var engine = PrepareCallbackEngine();
        using var output = new StringWriter();
        var original = Global(engine, "target");
        var result = engine.Execute(
            """
            Key.slow = True
            try:
                target = operation(key)
            except BaseException:
                print('swallowed')
            finally:
                print('cleanup')
            """,
            "numeric_hash_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Same(original, Global(engine, "target"));
        AssertRecovery(engine);
    }

    [Fact]
    public void HashCallbackUsesCurrentCancellationAndAllowsRecoveryWithoutPublishingAResult()
    {
        var engine = PrepareCallbackEngine();
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);
        var original = Global(engine, "target");
        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "Key.cancel = True\ntarget = operation(key)",
                "numeric_hash_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );
        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.Same(original, Global(engine, "target"));
        AssertRecovery(engine);
    }

    [Fact]
    public void StoredNormalizedHashesSurviveCopiesAndLaterUserHashMutation()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Key:
                calls = 0
                value = 1267650600228229401496703205376
                def __hash__(self):
                    Key.calls += 1
                    return self.value
            key = Key()
            source = {key}
            mapped = {key: 'value'}
            key.value = 2535301200456458802993406410752
            copied = source.copy()
            frozen = frozenset(source)
            from_keys = dict.fromkeys(frozen)
            copied_mapping = dict(mapped)
            print(Key.calls, hash(key), Key.calls)
            """,
            "numeric_hash_cached_normalization.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("2 1099511627776 3"), output.ToString());
        foreach (var name in new[] { "source", "copied", "frozen" })
            Assert.Equal(
                new BigInteger(549755813888L),
                Assert.Single(Assert.IsType<PythonSetValue>(Global(engine, name)).Entries).Hash
            );
        foreach (var name in new[] { "mapped", "from_keys", "copied_mapping" })
            Assert.Equal(
                new BigInteger(549755813888L),
                Assert
                    .Single(Assert.IsType<PythonDictionaryValue>(Global(engine, name)).Items)
                    .KeyHash
            );
    }

    private static ManagedPythonEngine PrepareCallbackEngine()
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var result = engine.Execute(
            """
            class Key:
                slow = False
                cancel = False
                def __hash__(self):
                    if self.cancel: print('cancel')
                    if self.slow:
                        for index in range(5000): pass
                    return 1267650600228229401496703205376
            key = Key()
            operation = hash
            target = 'original'
            """,
            "numeric_hash_callback_setup.py",
            previous,
            cancellationToken: cancellation.Token
        );
        Assert.True(result.Success);
        cancellation.Cancel();
        return engine;
    }

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "Key.slow = False\nKey.cancel = False\ntarget = operation(key)\nprint(target)",
            "numeric_hash_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("549755813888"), output.ToString());
    }

    private static PythonValue Global(ManagedPythonEngine engine, string name)
    {
        var globals = Assert.IsType<PythonGlobalNamespace>(
            typeof(ManagedPythonEngine)
                .GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)
        );
        Assert.True(globals.TryGetValue(name, out var value));
        return value;
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            cancellation.Cancel();
        }
    }
}
