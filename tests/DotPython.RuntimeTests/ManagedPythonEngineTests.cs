using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedPythonEngineTests
{
    [Fact]
    public void Constructor_RejectsMissingAndDuplicateModuleSearchPaths()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"dotpython-missing-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentException>(() =>
            new ManagedPythonEngine(new ManagedModuleDiscoveryOptions { SearchPaths = [missing] })
        );

        var directory = CreateTemporaryDirectory();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                new ManagedPythonEngine(
                    new ManagedModuleDiscoveryOptions { SearchPaths = [directory, directory] }
                )
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Execute_DiscoversFilesystemPackagesMetadataAndUsesAnImmutableSnapshot()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var packageDirectory = Directory.CreateDirectory(Path.Combine(directory, "sample"));
            var metadataDirectory = Directory.CreateDirectory(
                Path.Combine(directory, "sample_dist-1.2.3.dist-info")
            );
            File.WriteAllText(
                Path.Combine(packageDirectory.FullName, "__init__.py"),
                "from importlib.metadata import version\n"
                    + "from . import values\n"
                    + "__version__ = version('Sample_Dist')\n"
                    + "answer = values.answer"
            );
            File.WriteAllText(Path.Combine(packageDirectory.FullName, "values.py"), "answer = 42");
            File.WriteAllText(
                Path.Combine(metadataDirectory.FullName, "METADATA"),
                "Metadata-Version: 2.4\nName: sample-dist\nVersion: 1.2.3\n"
            );

            var engine = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            );
            Directory.Delete(directory, recursive: true);
            using var output = new StringWriter();

            var result = engine.Execute(
                "import sample\nprint(sample.answer, sample.__version__)",
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal($"42 1.2.3{Environment.NewLine}", output.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Execute_UsesFirstModuleSearchPathPrecedence()
    {
        var first = CreateTemporaryDirectory();
        var second = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(first, "helper.py"), "answer = 42");
            File.WriteAllText(Path.Combine(second, "helper.py"), "answer = 99");
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [first, second] }
            ).Execute(
                "import helper\nprint(helper.answer)",
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal($"42{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Constructor_DoesNotReadShadowedLowerPriorityModules()
    {
        var first = CreateTemporaryDirectory();
        var second = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(first, "helper.py"), "answer = 42");
            using (var shadowed = File.Create(Path.Combine(second, "helper.py")))
            {
                shadowed.SetLength((8 * 1024 * 1024) + 1);
            }

            using var output = new StringWriter();
            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [first, second] }
            ).Execute(
                "import helper\nprint(helper.answer)",
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal($"42{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Execute_DiscoversValidatedDotPythonArtifacts()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var parseResult = PythonParser.Parse(new SourceText("answer = 42", "compiled.py"));
            var compilation = PythonCompiler.Compile(parseResult.Module, "compiled.py");
            var bytes = DotPythonModuleArtifactSerializer.Serialize(
                DotPythonModuleArtifact.Create("compiled", compilation.Code)
            );
            File.WriteAllBytes(Path.Combine(directory, "compiled.dpyc"), bytes);
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            ).Execute(
                "import compiled\nprint(compiled.answer)",
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Empty(parseResult.Diagnostics);
            Assert.Empty(compilation.Diagnostics);
            Assert.True(result.Success);
            Assert.Equal($"42{Environment.NewLine}", output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RejectsArtifactIdentityCollisionsWithinOneRoot()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "helper.py"), "answer = 1");
            var parseResult = PythonParser.Parse(new SourceText("answer = 2", "helper.py"));
            var compilation = PythonCompiler.Compile(parseResult.Module, "helper.py");
            File.WriteAllBytes(
                Path.Combine(directory, "helper.dpyc"),
                DotPythonModuleArtifactSerializer.Serialize(
                    DotPythonModuleArtifact.Create("helper", compilation.Code)
                )
            );

            Assert.Throws<InvalidDataException>(() =>
                new ManagedPythonEngine(
                    new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
                )
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Execute_StopsAnAnyverShapedPackageAtTheNativeBoundary()
    {
        var directory = CreateTemporaryDirectory();
        var packageDirectory = Directory.CreateDirectory(Path.Combine(directory, "anyver"));
        var metadataDirectory = Directory.CreateDirectory(
            Path.Combine(directory, "anyver-1.1.0.dist-info")
        );
        try
        {
            File.WriteAllText(
                Path.Combine(packageDirectory.FullName, "__init__.py"),
                "from importlib.metadata import version\n"
                    + "__version__ = version('anyver')\n"
                    + "from ._anyver import compare"
            );
            File.WriteAllBytes(
                Path.Combine(packageDirectory.FullName, "_anyver.abi3.so"),
                [0x7f, (byte)'E', (byte)'L', (byte)'F']
            );
            File.WriteAllText(
                Path.Combine(metadataDirectory.FullName, "METADATA"),
                "Metadata-Version: 2.4\nName: anyver\nVersion: 1.1.0\n"
            );

            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            ).Execute(
                "import anyver",
                "main.py",
                TextWriter.Null,
                cancellationToken: TestContext.Current.CancellationToken
            );

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("DPY4027", diagnostic.Code);
            Assert.Contains("_anyver.abi3.so", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Execute_ReportsMissingDistributionMetadata()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            ).Execute(
                "from importlib.metadata import version\nprint(version('missing'))",
                "main.py",
                TextWriter.Null,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Equal("DPY4028", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Execute_DoesNotFollowSymbolicLinkModulesOutsideTheSearchRoot()
    {
        var directory = CreateTemporaryDirectory();
        var outsideDirectory = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(outsideDirectory, "escape.py");
            File.WriteAllText(target, "answer = 42");
            try
            {
                File.CreateSymbolicLink(Path.Combine(directory, "escape.py"), target);
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or UnauthorizedAccessException
                            or PlatformNotSupportedException
                )
            {
                Assert.Skip($"Symbolic links are unavailable: {exception.Message}");
            }

            var result = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            ).Execute(
                "import escape",
                "main.py",
                TextWriter.Null,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Equal("DPY4020", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RejectsSubmodulesWithoutRegisteredParentPackages()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["package.module"] = new("answer = 42"),
        };

        Assert.Throws<ArgumentException>(() => new ManagedPythonEngine(modules));
    }

    [Fact]
    public void Constructor_RejectsModuleCatalogNamesBeyondTheBoundedLimit()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            [new string('a', 513)] = new("answer = 42"),
        };

        Assert.Throws<ArgumentException>(() => new ManagedPythonEngine(modules));
    }

    [Fact]
    public void Execute_RunsDeserializedModuleArtifact()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("value = 40 + 2; print(value)", "answer.py")
        );
        var compilation = PythonCompiler.Compile(parseResult.Module, "answer.py");
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("answer", compilation.Code)
        );
        var artifact = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            artifact,
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(compilation.Diagnostics);
        Assert.True(result.Success);
        Assert.Equal("answer.dpyc", result.Source.FilePath);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RunsAssignmentArithmeticAndBuiltinCall()
    {
        using var output = new StringWriter();
        var engine = new ManagedPythonEngine();

        var result = engine.Execute(
            "value = 40 + 2; print(value)",
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesGlobalsAcrossCallsOnTheSameEngine()
    {
        using var output = new StringWriter();
        var engine = new ManagedPythonEngine();

        var assignment = engine.Execute(
            "value = 21",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var print = engine.Execute(
            "print(value * 2)",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(assignment.Success);
        Assert.True(print.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_ImportsManagedSourceModulesAndCachesInitialization()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["helper"] = new(
                "print('initializing')\n"
                    + "answer = 40\n"
                    + "def add(value): return answer + value",
                "helper.py"
            ),
        };
        var engine = new ManagedPythonEngine(modules);
        using var output = new StringWriter();

        var first = engine.Execute(
            "import helper as module\n"
                + "from helper import add as calculate\n"
                + "print(module.answer, calculate(2), module)",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var second = engine.Execute(
            "def run():\n    import helper\n    return helper.add(2)\nprint(run())",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(
            $"initializing{Environment.NewLine}40 42 <module 'helper'>{Environment.NewLine}"
                + $"42{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_ImportsPackagesRelativeModulesAndSubmoduleFallbacks()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["package"] = new(
                "print('package')\nfrom . import tools\nanswer = tools.answer",
                "package/__init__.py"
            ),
            ["package.tools"] = new(
                "print('tools')\nfrom .values import answer",
                "package/tools.py"
            ),
            ["package.values"] = new("answer = 42", "package/values.py"),
        };
        using var output = new StringWriter();

        var result = new ManagedPythonEngine(modules).Execute(
            "def read_answer():\n    import package.tools\n    return package.tools.answer\n"
                + "import package.tools\n"
                + "import package.values as values\n"
                + "from package import (tools as imported_tools,)\n"
                + "print(package.answer, package.tools.answer, values.answer, imported_tools.answer, read_answer())",
            "main.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"package{Environment.NewLine}tools{Environment.NewLine}42 42 42 42 42{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_ResolvesRelativeImportsAcrossPackageLevels()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["package"] = new("marker = 1", "package/__init__.py"),
            ["package.values"] = new("answer = 42", "package/values.py"),
            ["package.inner"] = new("marker = 2", "package/inner/__init__.py"),
            ["package.inner.consumer"] = new(
                "from ..values import answer",
                "package/inner/consumer.py"
            ),
        };
        using var output = new StringWriter();

        var result = new ManagedPythonEngine(modules).Execute(
            "from package.inner import consumer\nprint(consumer.answer)",
            "main.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RemovesFailedSubmodulesFromTheirParentBeforeRetry()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["package"] = new("marker = 1", "package/__init__.py"),
            ["package.broken"] = new("print('attempt')\nprint(1 / 0)", "package/broken.py"),
        };
        var engine = new ManagedPythonEngine(modules);
        using var output = new StringWriter();

        var first = engine.Execute(
            "import package.broken",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var second = engine.Execute(
            "from package import broken",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4004", Assert.Single(first.Diagnostics).Code);
        Assert.Equal("DPY4004", Assert.Single(second.Diagnostics).Code);
        Assert.Equal(
            $"attempt{Environment.NewLine}attempt{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_KeepsSuccessfulChildModulesWhenParentInitializationFails()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["package"] = new(
                "from . import child\nprint('parent')\nprint(1 / 0)",
                "package/__init__.py"
            ),
            ["package.child"] = new("print('child')\nanswer = 42", "package/child.py"),
        };
        var engine = new ManagedPythonEngine(modules);
        using var output = new StringWriter();

        var first = engine.Execute(
            "import package",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var second = engine.Execute(
            "import package",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4004", Assert.Single(first.Diagnostics).Code);
        Assert.Equal("DPY4004", Assert.Single(second.Diagnostics).Code);
        Assert.Equal(
            $"child{Environment.NewLine}parent{Environment.NewLine}parent{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RejectsRelativeImportsWithoutAPackageContext()
    {
        var result = new ManagedPythonEngine().Execute(
            "from . import missing",
            "main.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4025", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Execute_BoundsRecursivePackageInitializationDepth()
    {
        const int moduleCount = 101;
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        var names = Enumerable.Range(0, moduleCount).Select(index => $"p{index}").ToArray();
        for (var index = 0; index < moduleCount; index++)
        {
            var name = string.Join('.', names.AsSpan(0, index + 1).ToArray());
            var source =
                index + 1 == moduleCount
                    ? "answer = 42"
                    : "import " + string.Join('.', names.AsSpan(0, index + 2).ToArray());
            modules.Add(name, new SourceText(source, name.Replace('.', '/') + ".py"));
        }

        var result = new ManagedPythonEngine(modules).Execute(
            "import p0",
            "main.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4024", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Execute_AllowsCyclicImportsToObservePartiallyInitializedModules()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["first"] = new("import second\nvalue = second.value + 1", "first.py"),
            ["second"] = new("import first\nvalue = 41", "second.py"),
        };
        using var output = new StringWriter();

        var result = new ManagedPythonEngine(modules).Execute(
            "import first\nprint(first.value)",
            "main.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("import missing", "DPY4020")]
    [InlineData("from helper import missing", "DPY4022")]
    public void Execute_ReturnsStructuredImportDiagnostics(string code, string expectedCode)
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["helper"] = new("answer = 42", "helper.py"),
        };

        var result = new ManagedPythonEngine(modules).Execute(
            code,
            "main.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expectedCode, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Execute_RemovesFailedModulesSoLaterImportsRetryInitialization()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["broken"] = new("print('attempt')\nprint(1 / 0)", "broken.py"),
        };
        var engine = new ManagedPythonEngine(modules);
        using var output = new StringWriter();

        var first = engine.Execute(
            "import broken",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var second = engine.Execute(
            "import broken",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4004", Assert.Single(first.Diagnostics).Code);
        Assert.Equal("DPY4004", Assert.Single(second.Diagnostics).Code);
        Assert.Equal(
            $"attempt{Environment.NewLine}attempt{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_ReportsModuleCompilationFailureAtTheImportSite()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["broken"] = new("value =", "broken.py"),
        };

        var result = new ManagedPythonEngine(modules).Execute(
            "import broken",
            "main.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4021", diagnostic.Code);
        Assert.Contains("could not be compiled", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ChargesImportedModuleInitializationToTheInstructionLimit()
    {
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["unbounded"] = new("while True: value = 1", "unbounded.py"),
        };

        var result = new ManagedPythonEngine(modules).Execute(
            "import unbounded",
            "main.py",
            TextWriter.Null,
            new ManagedExecutionOptions { InstructionLimit = 20 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("print(2 ** 3 ** 2)", "512")]
    [InlineData("print(-7 // 3, -7 % 3, 7 / 2)", "-3 2 3.5")]
    [InlineData("print('ab' * 3)", "ababab")]
    [InlineData("print(True + 1, +True, ~False, None)", "2 1 -1 None")]
    [InlineData("print(2j, 1 + 2j)", "2j (1+2j)")]
    [InlineData("print(123456789012345678901234567890 + 1)", "123456789012345678901234567891")]
    [InlineData(
        "print(-4 - 1, -5 - 1, 255 + 1, 256 + 1, 10 ** 40)",
        "-5 -6 256 257 10000000000000000000000000000000000000000"
    )]
    [InlineData("print(False and missing, True or missing)", "False True")]
    [InlineData("print('' or 'fallback', 'value' and 42)", "fallback 42")]
    [InlineData("print(not 0, not 'value', 1 < 2 < 3, 1 < 2 > 3)", "True False True False")]
    [InlineData("print(1 == True, None != 0, 'a' < 'b', b'a' <= b'ab')", "True True True True")]
    [InlineData(
        "print([], [1, 'two', (True, None)], (), (1,), (1, 2))",
        "[] [1, 'two', (True, None)] () (1,) (1, 2)"
    )]
    [InlineData("print([\"'\", '\"', '\\x85', '\U0001f40d'])", "[\"'\", '\"', '\\x85', '🐍']")]
    [InlineData("print(not [], not (), [1, [2]] == [1, [2]], [1] != (1,))", "True True True True")]
    [InlineData(
        "print({'a': 1, 'a': 2}, not {}, {'a': [1]} == {'a': [1]}, {1: 'int', True: 'bool'})",
        "{'a': 2} True True {1: 'bool'}"
    )]
    [InlineData(
        "values = [10, 20]; values[-1] = 42; mapping = {'value': values[0]}; print(values, mapping['value'], '🐍x'[0], b'ab'[1])",
        "[10, 42] 10 🐍 98"
    )]
    public void Execute_MatchesSupportedPythonNumericAndTextSemantics(string code, string expected)
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Execute_EvaluatesCollectionElementsFromLeftToRight()
    {
        const string code =
            "def mark(value): print(value); return value\n"
            + "print([mark(1), mark(2)], (mark(3),))";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"1{Environment.NewLine}2{Environment.NewLine}3{Environment.NewLine}"
                + $"[1, 2] (3,){Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_EvaluatesDictionaryAndSubscriptAssignmentInPythonOrder()
    {
        const string code =
            "def mark(value): print(value); return value\n"
            + "values = [0]\n"
            + "values[mark(2) - 2] = mark(1)\n"
            + "mapping = {mark(3): mark(4)}\n"
            + "print(values, mapping)";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"1{Environment.NewLine}2{Environment.NewLine}3{Environment.NewLine}"
                + $"4{Environment.NewLine}[1] {{3: 4}}{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_IteratesManagedCollectionsAndRunsForElse()
    {
        const string code =
            "for item in [1, 2]: print(item)\n"
            + "else: print('done')\n"
            + "for item in (3,): print(item)\n"
            + "for item in '🐍a': print(item)\n"
            + "for item in b'BC': print(item)\n"
            + "for key in {'x': 1, 'y': 2}: print(key)";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            string.Join(Environment.NewLine, "1", "2", "done", "3", "🐍", "a", "66", "67", "x", "y")
                + Environment.NewLine,
            output.ToString()
        );
    }

    [Fact]
    public void Execute_FormatsAndComparesRecursiveCollectionsSafely()
    {
        const string code =
            "first = {}\n"
            + "first['self'] = first\n"
            + "second = {}\n"
            + "second['self'] = second\n"
            + "items = [None]\n"
            + "items[0] = items\n"
            + "print(first, items, first == second)";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"{{'self': {{...}}}} [[...]] True{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RunsNestedIfWhileAndLoopElse()
    {
        const string code =
            "value = 0\n"
            + "while value < 3:\n"
            + "    if value != 1:\n"
            + "        print(value)\n"
            + "    value = value + 1\n"
            + "else:\n"
            + "    print('done')\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"0{Environment.NewLine}2{Environment.NewLine}done{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RunsFunctionsWithLocalsGlobalsEarlyReturnsAndRecursion()
    {
        const string code =
            "factor = 2\n"
            + "def calculate(value):\n"
            + "    local = value * factor\n"
            + "    if local > 10:\n"
            + "        return local\n"
            + "    return 0\n"
            + "def factorial(value):\n"
            + "    if value <= 1:\n"
            + "        return 1\n"
            + "    return value * factorial(value - 1)\n"
            + "print(calculate(21), calculate(2), factorial(6))\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 0 720{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesEachRecursiveFramesLocalSlots()
    {
        const string code =
            "def preserve(value):\n"
            + "    first = value\n"
            + "    second = value + 100\n"
            + "    if value > 0:\n"
            + "        preserve(value - 1)\n"
            + "    return first + second\n"
            + "print(preserve(6))\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"112{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesDefinedFunctionsAcrossEngineCalls()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var definition = engine.Execute(
            "def double(value): return value * 2",
            "definition.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var call = engine.Execute(
            "print(double(21))",
            "call.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(definition.Success);
        Assert.True(call.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_ReturnsNoneWhenFunctionFallsThrough()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def procedure(value):\n    value = value + 1\nprint(procedure(4))",
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"None{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_TreatsFunctionsAsFirstClassIdentityValues()
    {
        using var output = new StringWriter();
        const string code =
            "def double(value): return value * 2\n"
            + "def apply(function, value): return function(value)\n"
            + "alias = double\n"
            + "print(apply(alias, 21), alias == double, alias != double)";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 True False{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesCallerValuesAcrossNestedFunctionFrames()
    {
        using var output = new StringWriter();
        const string code =
            "def double(value): return value * 2\n"
            + "def add(left, right): return left + right\n"
            + "print(1 + double(20) + 1, add(1, add(20, 21)))";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_ReturnsIndependentClosuresThatRetainTheirFrames()
    {
        using var output = new StringWriter();
        const string code =
            "def make(value):\n"
            + "    def add(other): return value + other\n"
            + "    return add\n"
            + "first = make(40)\n"
            + "second = make(10)\n"
            + "print(first(2), second(5))";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 15{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_SharesMutatedCellsAcrossMultipleClosureLevels()
    {
        using var output = new StringWriter();
        const string code =
            "def outer(seed):\n"
            + "    value = seed\n"
            + "    def middle():\n"
            + "        def inner(): return value\n"
            + "        return inner\n"
            + "    value = value + 2\n"
            + "    return middle()\n"
            + "read = outer(40)\n"
            + "print(read())";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_SupportsRecursionThroughANestedFunctionCell()
    {
        using var output = new StringWriter();
        const string code =
            "def outer():\n"
            + "    def factorial(value):\n"
            + "        if value <= 1: return 1\n"
            + "        return value * factorial(value - 1)\n"
            + "    return factorial(6)\n"
            + "print(outer())";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"720{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("print(missing)", "DPY4002")]
    [InlineData("print(1 / 0)", "DPY4004")]
    [InlineData("print(0 ** -1)", "DPY4004")]
    [InlineData("print('a' - 'b')", "DPY4005")]
    [InlineData("value = 1\ndef invalid():\n    print(value)\n    value = 2\ninvalid()", "DPY4008")]
    [InlineData("def add(left, right): return left + right\nadd(1)", "DPY4009")]
    [InlineData(
        "def outer():\n    def inner(): return value\n    print(inner())\n    value = 42\nouter()",
        "DPY4010"
    )]
    [InlineData("values = (1,); values[0] = 2", "DPY4011")]
    [InlineData("print([1][2])", "DPY4012")]
    [InlineData("print({}['missing'])", "DPY4013")]
    [InlineData("print({[]: 1})", "DPY4014")]
    [InlineData("for item in 1: print(item)", "DPY4015")]
    [InlineData("mapping = {'a': 1}\nfor key in mapping: mapping['b'] = 2", "DPY4016")]
    [InlineData("value = 1\nprint(value.real)", "DPY4023")]
    public void Execute_ReturnsRuntimeDiagnostics(string code, string expectedCode)
    {
        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
    }

    [Fact]
    public void Execute_EnforcesInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 1 };

        var result = new ManagedPythonEngine().Execute(
            "print(42)",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_StopsAnInfiniteWhileLoopAtTheInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 20 };

        var result = new ManagedPythonEngine().Execute(
            "while True:\n    value = 1\n",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_StopsUnboundedRecursionAtTheInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 40 };

        var result = new ManagedPythonEngine().Execute(
            "def recurse(): return recurse()\nrecurse()",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_ObservesCancellationBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ManagedPythonEngine().Execute(
                "print(42)",
                "<test>",
                TextWriter.Null,
                cancellationToken: cancellation.Token
            )
        );
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-module-discovery-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        return directory;
    }
}
