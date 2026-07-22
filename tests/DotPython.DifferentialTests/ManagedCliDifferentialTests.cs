using System.ComponentModel;
using System.Diagnostics;
using DotPython.Cli;
using DotPython.Language;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
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
    [InlineData(
        "for value in [1, 2, 3, 4]:\n    if value == 3:\n        break\n    print(value)\nelse:\n    print('else')"
    )]
    [InlineData(
        "count = 0\nwhile count < 5:\n    count = count + 1\n    if count % 2 == 0:\n        continue\n    print(count)\nelse:\n    print('done', count)"
    )]
    [InlineData(
        "for value in [1, 2, 3]:\n    try:\n        if value == 2:\n            break\n        print('try', value)\n    finally:\n        print('finally', value)\nprint('after')"
    )]
    [InlineData(
        "for outer in [1, 2]:\n    for inner in [10, 20, 30]:\n        if inner == 20:\n            break\n        print(outer, inner)\n    print('row', outer)"
    )]
    [InlineData("pass\nif True:\n    pass\nprint('ok')")]
    [InlineData(
        "def greet(name, greeting='hello', punctuation='!'):\n    return greeting + ', ' + name + punctuation\nprint(greet('world'))\nprint(greet('world', 'hi'))\nprint(greet('world', punctuation='?'))\nprint(greet(punctuation='.', name='all', greeting='hey'))"
    )]
    [InlineData(
        "base = 10\ndef scaled(value, factor=base):\n    return value * factor\nbase = 99\nprint(scaled(3), scaled(3, 2), scaled(3, factor=4))"
    )]
    [InlineData(
        "counter = 0\ndef bump(step=1):\n    global counter\n    counter = counter + step\nbump()\nbump(step=5)\nprint(counter)"
    )]
    [InlineData(
        "def outer():\n    total = 0\n    def add(amount=2):\n        nonlocal total\n        total = total + amount\n    add()\n    add(amount=10)\n    return total\nprint(outer())"
    )]
    [InlineData(
        "def act(first, second=2):\n    return first\ntry:\n    act(1, wrong=3)\nexcept TypeError:\n    print('unexpected-keyword')\ntry:\n    act(second=3)\nexcept TypeError:\n    print('missing-argument')"
    )]
    [InlineData(
        "values = [3, 1, 2]\nvalues.append(4)\nvalues.extend([5])\nvalues.insert(0, 0)\nprint(values.pop(), values.pop(0), values)\nvalues.remove(3)\nvalues.sort()\nprint(values, values.index(2), values.count(1))\nvalues.reverse()\nprint(values, values.copy())"
    )]
    [InlineData(
        "print(' hi '.strip(), 'a,b,,c'.split(','), 'a b  c'.split(), '-'.join(['x', 'y']))\nprint('banana'.replace('an', 'A'), 'abc'.upper(), 'AbC'.lower(), 'hello world'.capitalize())\nprint('banana'.find('na'), 'banana'.find('xx'), 'banana'.count('a'), 'banana'.startswith('ban'), 'banana'.endswith('x'))"
    )]
    [InlineData(
        "d = {'a': 1, 'b': 2}\nprint(d.get('a'), d.get('z'), d.get('z', 9))\nprint(d.keys(), d.values(), d.items())\nd.update({'c': 3})\nprint(d.pop('b'), d.pop('zz', -1), d.setdefault('d', 4), d.setdefault('a', 99), d)\nprint((1, 2, 2).count(2), (1, 2, 2).index(2))"
    )]
    [InlineData(
        "print(2 in [1, 2], 3 not in (1, 2), 'an' in 'banana', 'xx' not in 'banana', 'k' in {'k': 1})\nprint(None is None, [] is [], 1 is not None)"
    )]
    [InlineData(
        "letters = ['a', 'b', 'c', 'd', 'e']\nprint(letters[1:3], letters[:2], letters[3:], letters[::2], letters[::-1], letters[-2:])\nprint('abcdef'[1:4], 'abcdef'[::-1], (1, 2, 3, 4)[1:3])\nletters[1:3] = ['B', 'C', 'X']\nprint(letters)\nletters[::3] = ['1', '2']\nprint(letters)"
    )]
    [InlineData(
        "n = 10\nn += 5\nn -= 3\nn *= 2\nn //= 5\nn **= 3\nn %= 7\nprint(n)\nlst = [1, 2]\nalias = lst\nlst += [3, 4]\nlst *= 2\nlst[0] += 9\nprint(lst, alias is lst)\ngrid = [[0, 1], [2, 3]]\ngrid[0][1] += 5\nprint(grid)"
    )]
    [InlineData(
        "try:\n    [1, 'a'].sort()\nexcept TypeError:\n    print('unorderable')\ntry:\n    [1, 2][::0]\nexcept ValueError:\n    print('zero-step')\ntry:\n    {'a': 1}.pop('zz')\nexcept KeyError:\n    print('pop-missing')\ntry:\n    'abc'.nope\nexcept AttributeError:\n    print('attr')"
    )]
    [InlineData(
        "total = 0\nfor value in range(2, 20, 3):\n    total += value\nprint(total, range(5), range(1, 7, 2), len(range(10)), len(range(10, 0, -3)))\nprint(range(10)[3], range(10)[-1], range(0, 20, 2)[2:5], range(5)[::-1])\nprint(3 in range(5), 7 in range(5), 4 in range(0, 10, 2))"
    )]
    [InlineData(
        "for pair in enumerate(['a', 'b', 'c']):\n    print(pair)\nfor pair in enumerate('xy', 10):\n    print(pair)\nfor triple in zip([1, 2, 3], 'abcd', range(9)):\n    print(triple)\nfor nothing in zip():\n    print('never')\nprint('done')"
    )]
    [InlineData(
        "a, b = 1, 2\na, b = b, a\nprint(a, b)\nx = 1,\nprint(x)\n(c, d), e = (5, 6), 7\nprint(c, d, e)\nm, n = [10, 20]\nprint(m, n)"
    )]
    [InlineData(
        "data = {'a': 1, 'b': 2}\nfor key, value in data.items():\n    print(key, value)\nfor index, (key, value) in enumerate(data.items()):\n    print(index, key, value)\nfor left, right in zip('ab', range(2)):\n    print(left, right)"
    )]
    [InlineData(
        "def swap(p, q):\n    return q, p\nr, s = swap(1, 2)\nprint(r, s)\nmatrix = [[1, 2], [3, 4]]\nmatrix[0][0], matrix[1][1] = 9, 8\nprint(matrix)\ntry:\n    r, s, t = swap(1, 2)\nexcept ValueError:\n    print('short')"
    )]
    [InlineData(
        "class Counter:\n    kind = 'counter'\n    def __init__(self, value):\n        self.value = value\n    def increment(self, amount=1):\n        self.value += amount\n        return self.value\ncounter = Counter(value=40)\nprint(counter.increment(), counter.increment(amount=1), counter.value, Counter.kind)"
    )]
    [InlineData(
        "x = 99\nprint([x * 2 for x in range(5)], x)\nprint([n for n in range(10) if n % 2 == 0])\nprint([a + b for a in range(3) for b in range(3) if a != b])"
    )]
    [InlineData(
        "print({k: v * 10 for k, v in zip('abc', range(3))})\nprint({v: k for k, v in {'a': 1, 'b': 2}.items()})\nprint([[y for y in range(n)] for n in range(4)])\nprint([cell for row in [[1, 2], [3, 4]] for cell in row])"
    )]
    [InlineData(
        "factor = 3\nprint([n * factor for n in [1, 2]])\ndef scale(values, factor):\n    return [v * factor for v in values]\nprint(scale([1, 2, 3], 5))\nwords = ['hello', 'world', 'hi']\nprint([w.upper() for w in words if len(w) > 2])\nprint([x for x in [y * 2 for y in range(3)]])"
    )]
    public void CommandExecution_MatchesReferencePythonForSupportedSubset(string code)
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
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

    [Fact]
    public void ManagedImports_MatchReferenceForTheSupportedTopLevelSubset()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        const string moduleSource =
            "print('initializing')\nanswer = 40\ndef add(value): return answer + value\n";
        const string code =
            "import helper as module\n"
            + "from helper import add as calculate\n"
            + "import helper\n"
            + "print(module.answer, calculate(2), helper == module)";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-import-differential-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "helper.py"), moduleSource);
            var reference = RunReference(python, code, directory);
            var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
            {
                ["helper"] = new(moduleSource, "helper.py"),
            };
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(modules).Execute(
                code,
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal(0, reference.ExitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ManagedPackageImports_MatchReferenceForDottedAndRelativeImports()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        const string packageSource = "print('package')\nfrom . import tools\n";
        const string toolsSource = "print('tools')\nfrom .values import answer\n";
        const string valuesSource = "answer = 42\n";
        const string code =
            "import package.tools\n"
            + "from package import (tools as imported_tools,)\n"
            + "print(package.tools.answer, imported_tools.answer, package.tools == imported_tools)";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-package-import-differential-{Guid.NewGuid():N}"
        );
        var packageDirectory = Path.Combine(directory, "package");
        Directory.CreateDirectory(packageDirectory);
        try
        {
            File.WriteAllText(Path.Combine(packageDirectory, "__init__.py"), packageSource);
            File.WriteAllText(Path.Combine(packageDirectory, "tools.py"), toolsSource);
            File.WriteAllText(Path.Combine(packageDirectory, "values.py"), valuesSource);
            var reference = RunReference(python, code, directory);
            var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
            {
                ["package"] = new(packageSource, "package/__init__.py"),
                ["package.tools"] = new(toolsSource, "package/tools.py"),
                ["package.values"] = new(valuesSource, "package/values.py"),
            };
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(modules).Execute(
                code,
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal(0, reference.ExitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScriptPackageAndDistributionMetadataDiscovery_MatchesReferencePython()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-script-discovery-differential-{Guid.NewGuid():N}"
        );
        var packageDirectory = Path.Combine(directory, "sample");
        var metadataDirectory = Path.Combine(directory, "sample_dist-1.2.3.dist-info");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(metadataDirectory);
        var scriptPath = Path.Combine(directory, "main.py");
        try
        {
            File.WriteAllText(
                Path.Combine(packageDirectory, "__init__.py"),
                "from importlib.metadata import version\n"
                    + "from . import values\n"
                    + "__version__ = version('sample-dist')\n"
                    + "answer = values.answer\n"
            );
            File.WriteAllText(Path.Combine(packageDirectory, "values.py"), "answer = 42\n");
            File.WriteAllText(
                Path.Combine(metadataDirectory, "METADATA"),
                "Metadata-Version: 2.4\nName: sample-dist\nVersion: 1.2.3\n"
            );
            File.WriteAllText(
                scriptPath,
                "import sample\nprint(sample.answer, sample.__version__)\n"
            );
            var reference = RunReferenceScript(python, scriptPath);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = DotPythonCommand.Run(
                [scriptPath],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(reference.ExitCode, exitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RaisedExceptionHandlersAndFinally_MatchReferencePython()
    {
        const string code =
            "def classify(value):\n"
            + "    try:\n"
            + "        if value:\n"
            + "            raise ValueError('bad')\n"
            + "    except TypeError:\n"
            + "        print('wrong')\n"
            + "    except (ValueError, RuntimeError) as error:\n"
            + "        print('caught', error)\n"
            + "    else:\n"
            + "        print('clean')\n"
            + "    finally:\n"
            + "        print('done')\n"
            + "classify(False)\n"
            + "classify(True)\n"
            + "try:\n"
            + "    try:\n"
            + "        raise ValueError('again')\n"
            + "    except ValueError:\n"
            + "        raise\n"
            + "except Exception as error:\n"
            + "    print('reraised', error)\n";
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
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

    private static ReferenceResult RunReference(
        string executable,
        string code,
        string? workingDirectory = null
    )
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ReferenceResult(process.ExitCode, standardOutput);
    }

    private static ReferenceResult RunReferenceScript(string executable, string scriptPath)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ReferenceResult(process.ExitCode, standardOutput);
    }

    private static string ReferenceVersion => PythonLanguageVersion.Current.ToString(2);

    private static string? FindReferencePython()
    {
        var configured = Environment.GetEnvironmentVariable("DOTPYTHON_REFERENCE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && IsReferencePython(configured))
        {
            return configured;
        }

        foreach (var candidate in new[] { $"python{ReferenceVersion}", "python3" })
        {
            if (IsReferencePython(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsReferencePython(string executable)
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
                && output.StartsWith($"Python {ReferenceVersion}.", StringComparison.Ordinal);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private sealed record ReferenceResult(int ExitCode, string StandardOutput);
}
