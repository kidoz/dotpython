using System.Reflection;
using System.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class NamedEncodingReplacementExecutionTests
{
    [Fact]
    public void SavedEncodeUsesCurrentContextForNamesAndFallbacksAcrossCodecFamilies()
    {
        var engine = Prepare("'Aÿ😀' + chr(0xd800) + 'B'", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var original = Global(engine, "values");
            var result = engine.Execute(
                """
                ascii_bytes = encode('ascii', 'namereplace')
                latin_bytes = encode('latin-1', 'namereplace')
                utf8_bytes = encode('utf-8', 'namereplace')
                utf16_bytes = encode('utf-16', 'namereplace')
                little_bytes = encode('utf-16-le', 'namereplace')
                big_bytes = encode('utf-16-be', 'namereplace')
                print('encoded')
                """,
                "named_encoding_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(Lines("encoded"), output.ToString());
            Assert.Empty(previousOutput.ToString());
            Assert.Same(original, Global(engine, "values"));
            Assert.Equal(
                Encoding.ASCII.GetBytes(
                    "A\\N{LATIN SMALL LETTER Y WITH DIAERESIS}\\N{GRINNING FACE}\\ud800B"
                ),
                Bytes(engine, "ascii_bytes")
            );
            Assert.Equal(
                new byte[] { 65, 255 }.Concat(
                    Encoding.ASCII.GetBytes("\\N{GRINNING FACE}\\ud800B")
                ),
                Bytes(engine, "latin_bytes")
            );
            const string escaped = "Aÿ😀\\ud800B";
            Assert.Equal(Encoding.UTF8.GetBytes(escaped), Bytes(engine, "utf8_bytes"));
            Assert.Equal(
                new byte[] { 255, 254 }.Concat(Encoding.Unicode.GetBytes(escaped)),
                Bytes(engine, "utf16_bytes")
            );
            Assert.Equal(Encoding.Unicode.GetBytes(escaped), Bytes(engine, "little_bytes"));
            Assert.Equal(Encoding.BigEndianUnicode.GetBytes(escaped), Bytes(engine, "big_bytes"));
        }
    }

    [Fact]
    public void NamedAndUnnamedScansChargeCurrentWorkAndRecoverAcrossCodecFamilies()
    {
        foreach (
            var codec in new[] { "ascii", "latin-1", "utf-8", "utf-16", "utf-16-le", "utf-16-be" }
        )
        {
            var expectedLength = codec switch
            {
                "ascii" or "latin-1" => 1_860_000,
                "utf-8" => 180_000,
                "utf-16" => 280_002,
                _ => 280_000,
            };
            var engine = Prepare("(chr(0xfbf9) + chr(0xd800)) * 20000", out var previousOutput);
            using (previousOutput)
            using (var output = new StringWriter())
            {
                var originalTarget = Global(engine, "target");
                var originalSource = Global(engine, "values");
                var expression = "encode('" + codec + "', 'namereplace')";
                var limited = engine.Execute(
                    "try:\n    target = "
                        + expression
                        + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                    "named_encoding_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 100 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Same(originalTarget, Global(engine, "target"));
                Assert.Same(originalSource, Global(engine, "values"));
                Assert.Empty(previousOutput.ToString());
                using var recoveredOutput = new StringWriter();
                var recovered = engine.Execute(
                    "target = " + expression,
                    "named_encoding_budget_recovery.py",
                    recoveredOutput,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(recovered.Success);
                Assert.Equal(expectedLength, Bytes(engine, "target").Length);
                Assert.Same(originalSource, Global(engine, "values"));
            }
        }
    }

    [Fact]
    public void LongNamesCountEveryOutputByteAndPermitExactCapBoundaryRecovery()
    {
        var engine = AssertCapAndRecovery(
            "chr(0xfbf9) * 114943",
            "ascii",
            "values[:-1] + 'A' * 46"
        );
        var encoded = Bytes(engine, "target");
        const string name =
            "\\N{ARABIC LIGATURE UIGHUR KIRGHIZ YEH WITH HAMZA ABOVE WITH ALEF MAKSURA ISOLATED FORM}";
        Assert.Equal(Encoding.ASCII.GetBytes(name), encoded[..87]);
        Assert.Equal(Enumerable.Repeat((byte)'A', 46), encoded[^46..]);
    }

    [Fact]
    public void UnnamedSurrogateFallbackCountsEncodedWidthAndNativeBomAgainstTheCap()
    {
        var utf8 = AssertCapAndRecovery(
            "chr(0xd800) * 1666667",
            "utf-8",
            "chr(0xd800) * 1666666 + 'AAAA'"
        );
        Assert.Equal(Encoding.ASCII.GetBytes("\\ud800"), Bytes(utf8, "target")[..6]);
        var utf16 = AssertCapAndRecovery(
            "chr(0xd800) * 833334",
            "utf-16",
            "chr(0xd800) * 833333 + 'A'"
        );
        Assert.Equal(new byte[] { 255, 254, 92, 0, 117, 0 }, Bytes(utf16, "target")[..6]);
        Assert.Equal(new byte[] { 65, 0 }, Bytes(utf16, "target")[^2..]);
    }

    [Fact]
    public void ChunkBoundariesKeepNamedSupplementaryUnassignedAndSurrogateCharactersSeparate()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            values = 'a' * 255 + '😀' + chr(0x378) + chr(0xd800) + chr(0xe000) + 'ÿ'
            named = values.encode('ascii', 'namereplace')
            algorithmic = (chr(0xac00) + chr(0x4e00) + chr(0x1f600)).encode('ascii', 'namereplace')
            print(len(values), len(named))
            """,
            "named_encoding_chunks.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        var expected = Encoding.ASCII.GetBytes(
            new string('a', 255)
                + "\\N{GRINNING FACE}\\u0378\\ud800\\ue000\\N{LATIN SMALL LETTER Y WITH DIAERESIS}"
        );
        Assert.Equal(Lines("260 " + expected.Length), output.ToString());
        Assert.Equal(expected, Bytes(engine, "named"));
        Assert.Equal(
            Encoding.ASCII.GetBytes(
                "\\N{HANGUL SYLLABLE GA}\\N{CJK UNIFIED IDEOGRAPH-4E00}\\N{GRINNING FACE}"
            ),
            Bytes(engine, "algorithmic")
        );
    }

    [Fact]
    public void InterruptedWorkDoesNotPoisonNameResolutionInFreshEngines()
    {
        var interrupted = Prepare("'😀' * 20000", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var original = Global(interrupted, "target");
            var limited = interrupted.Execute(
                "target = encode('ascii', 'namereplace')",
                "named_encoding_interrupted.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
            Assert.Same(original, Global(interrupted, "target"));
            Assert.Empty(previousOutput.ToString());
        }
        foreach (
            var (source, expected) in new[]
            {
                ("'😀'", "\\N{GRINNING FACE}"),
                ("chr(0xac00)", "\\N{HANGUL SYLLABLE GA}"),
                ("chr(0x4e00)", "\\N{CJK UNIFIED IDEOGRAPH-4E00}"),
            }
        )
        {
            var fresh = Prepare(source, out var setupOutput);
            using (setupOutput)
            using (var output = new StringWriter())
            {
                var result = fresh.Execute(
                    "target = encode('ascii', 'namereplace')\nprint(len(target) > 0)",
                    "named_encoding_fresh_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(result.Success);
                Assert.Equal(Lines("True"), output.ToString());
                Assert.Empty(setupOutput.ToString());
                Assert.Equal(Encoding.ASCII.GetBytes(expected), Bytes(fresh, "target"));
            }
        }
    }

    private static ManagedPythonEngine AssertCapAndRecovery(
        string source,
        string codec,
        string recovery
    )
    {
        var engine = Prepare(source, out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            var originalSource = Global(engine, "values");
            var limited = engine.Execute(
                "try: target = encode('"
                    + codec
                    + "', 'namereplace')\nexcept OverflowError as error: print(str(error))",
                "named_encoding_cap.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(limited.Success);
            Assert.Equal(
                Lines("encoded bytes exceed the managed materialization limit"),
                output.ToString()
            );
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalSource, Global(engine, "values"));
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = (" + recovery + ").encode('" + codec + "', 'namereplace')",
                "named_encoding_cap_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(10_000_000, Bytes(engine, "target").Length);
            Assert.Same(originalSource, Global(engine, "values"));
        }
        return engine;
    }

    private static ManagedPythonEngine Prepare(string source, out StringWriter previousOutput)
    {
        var engine = new ManagedPythonEngine();
        previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            "values = " + source + "\nencode = values.encode\ntarget = 'original'",
            "named_encoding_saved_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static byte[] Bytes(ManagedPythonEngine engine, string name) =>
        Assert.IsType<PythonByteSequenceValue>(Global(engine, name)).Value;

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
}
