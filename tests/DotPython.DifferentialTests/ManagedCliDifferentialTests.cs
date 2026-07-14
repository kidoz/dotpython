using System.ComponentModel;
using System.Diagnostics;
using DotPython.Cli;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ManagedCliDifferentialTests
{
    [Theory]
    [InlineData("print(40 + 2)")]
    [InlineData("value = 7; print(value ** 2)")]
    [InlineData("print(-7 // 3, -7 % 3, 7 / 2)")]
    [InlineData("print('ab' * 3)")]
    [InlineData("print(2 ** 3 ** 2)")]
    [InlineData("print(True + 1, +True, ~False, None)")]
    [InlineData("print(2j, 1 + 2j)")]
    [InlineData("print(False and missing, True or missing)")]
    [InlineData("print('' or 'fallback', 'value' and 42)")]
    [InlineData("print(not 0, not 'value', 1 < 2 < 3, 1 < 2 > 3)")]
    [InlineData("print(1 == True, None != 0, 'a' < 'b', b'a' <= b'ab')")]
    [InlineData("print([], [1, 'two', (True, None)], (), (1,), (1, 2))")]
    [InlineData("print(not [], not (), [1, [2]] == [1, [2]], [1] != (1,))")]
    [InlineData(
        "def mark(value): print(value); return value\nprint([mark(1), mark(2)], (mark(3),))"
    )]
    [InlineData(
        "values = [10, 20]\nvalues[-1] = 42\nmapping = {'a': 1, 'a': 2}\nmapping['b'] = values[0]\nprint(values, mapping, mapping['a'], '🐍x'[0], b'ab'[1])"
    )]
    [InlineData("print(not {}, {'a': [1]} == {'a': [1]}, {1: 'int', True: 'bool'})")]
    [InlineData(
        "def mark(value): print(value); return value\nvalues = [0]\nvalues[mark(2) - 2] = mark(1)\nprint({mark(3): mark(4)}, values)"
    )]
    [InlineData(
        "for item in [1, 2]: print(item)\nelse: print('done')\nfor item in (3,): print(item)\nfor item in '🐍a': print(item)\nfor item in b'BC': print(item)\nfor key in {'x': 1, 'y': 2}: print(key)"
    )]
    [InlineData(
        "value = 0\nwhile value < 3:\n    if value != 1:\n        print(value)\n    value = value + 1\nelse:\n    print('done')"
    )]
    [InlineData(
        "factor = 2\ndef calculate(value):\n    local = value * factor\n    if local > 10:\n        return local\n    return 0\nprint(calculate(21), calculate(2))"
    )]
    [InlineData(
        "def factorial(value):\n    if value <= 1:\n        return 1\n    return value * factorial(value - 1)\nprint(factorial(6))"
    )]
    [InlineData("def procedure(value):\n    value = value + 1\nprint(procedure(4))")]
    [InlineData(
        "def double(value): return value * 2\ndef apply(function, value): return function(value)\nalias = double\nprint(apply(alias, 21), alias == double, alias != double)"
    )]
    [InlineData(
        "def make(value):\n    def add(other): return value + other\n    return add\nfirst = make(40)\nsecond = make(10)\nprint(first(2), second(5))"
    )]
    [InlineData(
        "def outer(seed):\n    value = seed\n    def middle():\n        def inner(): return value\n        return inner\n    value = value + 2\n    return middle()\nread = outer(40)\nprint(read())"
    )]
    [InlineData(
        "def outer():\n    def factorial(value):\n        if value <= 1: return 1\n        return value * factorial(value - 1)\n    return factorial(6)\nprint(outer())"
    )]
    public void CommandExecution_MatchesPython314ForSupportedSubset(string code)
    {
        var python = FindPython314();
        if (python is null)
        {
            Assert.Skip("A Python 3.14 executable is required for this differential test.");
        }

        var reference = RunReference(python, code);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", code],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(reference.ExitCode, exitCode);
        Assert.Equal(reference.StandardOutput, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void CommandExecution_UsesProcessStyleExitCodesAndErrorStream()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", "print(missing)"],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("DPY4002", error.ToString(), StringComparison.Ordinal);
    }

    private static ReferenceResult RunReference(string executable, string code)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ReferenceResult(process.ExitCode, standardOutput);
    }

    private static string? FindPython314()
    {
        var configured = Environment.GetEnvironmentVariable("DOTPYTHON_REFERENCE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && IsPython314(configured))
        {
            return configured;
        }

        foreach (var candidate in new[] { "python3.14", "python3" })
        {
            if (IsPython314(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsPython314(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                && output.StartsWith("Python 3.14.", StringComparison.Ordinal);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private sealed record ReferenceResult(int ExitCode, string StandardOutput);
}
