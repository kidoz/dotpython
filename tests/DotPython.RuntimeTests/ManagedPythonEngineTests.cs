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
    public void Execute_HandlesRaisedExceptionsAndRunsElseAndFinally()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
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
                + "classify(True)\n",
            "exceptions.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"clean{Environment.NewLine}done{Environment.NewLine}"
                + $"caught bad{Environment.NewLine}done{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_ReraisesAcrossFunctionFramesAndRunsHandlerCleanup()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def fail():\n"
                + "    try:\n"
                + "        raise ValueError('inner')\n"
                + "    except ValueError as error:\n"
                + "        print(error)\n"
                + "        raise\n"
                + "try:\n"
                + "    fail()\n"
                + "except Exception as error:\n"
                + "    print('outer', error)\n",
            "reraise.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"inner{Environment.NewLine}outer inner{Environment.NewLine}",
            output.ToString()
        );
    }

    [Theory]
    [InlineData("missing", "NameError")]
    [InlineData("1 / 0", "ZeroDivisionError")]
    [InlineData("'left' - 'right'", "TypeError")]
    [InlineData("[1][2]", "IndexError")]
    [InlineData("{}['missing']", "KeyError")]
    [InlineData("for item in 1: print(item)", "TypeError")]
    [InlineData("value = 1\nvalue.missing", "AttributeError")]
    [InlineData("import missing", "ModuleNotFoundError")]
    public void Execute_ConvertsOperationFaultsIntoCatchablePythonExceptions(
        string body,
        string exceptionType
    )
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(exceptionType);
        using var output = new StringWriter();
        var indentedBody = string.Join(
            Environment.NewLine,
            body.Split('\n').Select(line => "    " + line)
        );
        var source =
            $"try:{Environment.NewLine}{indentedBody}{Environment.NewLine}"
            + $"except {exceptionType} as error:{Environment.NewLine}"
            + "    print('caught', error)";

        var result = new ManagedPythonEngine().Execute(
            source,
            "operation_fault.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.StartsWith("caught ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_CatchesOperationFaultsAcrossFunctionAndImportFrames()
    {
        using var output = new StringWriter();
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["broken"] = new("print(1 / 0)", "broken.py"),
        };

        var result = new ManagedPythonEngine(modules).Execute(
            "def divide(): return 1 / 0\n"
                + "try:\n"
                + "    divide()\n"
                + "except ArithmeticError:\n"
                + "    print('function')\n"
                + "try:\n"
                + "    import broken\n"
                + "except ZeroDivisionError:\n"
                + "    print('import one')\n"
                + "try:\n"
                + "    import broken\n"
                + "except ZeroDivisionError:\n"
                + "    print('import two')\n",
            "operation_frames.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"function{Environment.NewLine}"
                + $"import one{Environment.NewLine}"
                + $"import two{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_DistinguishesFromImportAndAttributeFailures()
    {
        using var output = new StringWriter();
        var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["helper"] = new("value = 1", "helper.py"),
        };

        var result = new ManagedPythonEngine(modules).Execute(
            "try:\n"
                + "    from helper import missing\n"
                + "except ImportError:\n"
                + "    print('import')\n"
                + "import helper\n"
                + "try:\n"
                + "    helper.missing\n"
                + "except AttributeError:\n"
                + "    print('attribute')\n",
            "operation_imports.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"import{Environment.NewLine}attribute{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_BareReraisePreservesTheOriginatingRuntimeDiagnostic()
    {
        var source =
            "def fail():\n"
            + "    try:\n"
            + "        return 1 / 0\n"
            + "    except ZeroDivisionError:\n"
            + "        raise\n"
            + "fail()\n";

        var result = new ManagedPythonEngine().Execute(
            source,
            "operation_reraise.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4004", diagnostic.Code);
        Assert.Equal("Division by zero.", diagnostic.Message);
        Assert.Equal(
            new TextSpan(source.IndexOf("1 / 0", StringComparison.Ordinal), 5),
            diagnostic.Span
        );
    }

    [Fact]
    public void Execute_DoesNotAllowPythonHandlersToSwallowTheInstructionLimit()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "try:\n"
                + "    while True:\n"
                + "        1\n"
                + "except Exception:\n"
                + "    print('swallowed')\n"
                + "finally:\n"
                + "    print('cleanup')\n",
            "operation_limit.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 20 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal($"cleanup{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_FinallyReturnOverridesPendingReturn()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def choose():\n"
                + "    try:\n"
                + "        return 'try'\n"
                + "    finally:\n"
                + "        return 'finally'\n"
                + "print(choose())\n",
            "finally_return.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"finally{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RunsFinallyBeforeAnExistingRuntimeFaultEscapes()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "try:\n" + "    missing_name\n" + "finally:\n" + "    print('cleanup')\n",
            "runtime_fault.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4002", Assert.Single(result.Diagnostics).Code);
        Assert.Equal($"cleanup{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesPendingExceptionAcrossAHandledExceptionInsideFinally()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "try:\n"
                + "    raise ValueError('outer')\n"
                + "finally:\n"
                + "    try:\n"
                + "        raise TypeError('inner')\n"
                + "    except TypeError as error:\n"
                + "        print('caught', error)\n",
            "nested_finally.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4031", diagnostic.Code);
        Assert.Equal("ValueError: outer", diagnostic.Message);
        Assert.Equal($"caught inner{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_DefersCancellationWhileFinallyRuns()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            new ManagedPythonEngine().Execute(
                "try:\n" + "    print('body')\n" + "finally:\n" + "    print('cleanup')\n",
                "cancelled_finally.py",
                output,
                cancellationToken: cancellation.Token
            )
        );
        Assert.Equal($"body{Environment.NewLine}cleanup{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_DefersTheInstructionLimitWhileFinallyRuns()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "try:\n"
                + "    while True:\n"
                + "        1\n"
                + "finally:\n"
                + "    print('cleanup')\n",
            "limited_finally.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 20 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal($"cleanup{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_BoundsDeferredFinallyCleanup()
    {
        var result = new ManagedPythonEngine().Execute(
            "try:\n"
                + "    raise ValueError('original')\n"
                + "finally:\n"
                + "    while True:\n"
                + "        1\n",
            "unbounded_finally.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4032", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Execute_ReportsAnUncaughtPythonExceptionAtItsRaiseSpan()
    {
        var source = "raise RuntimeError('failed')";

        var result = new ManagedPythonEngine().Execute(
            source,
            "uncaught.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4031", diagnostic.Code);
        Assert.Equal("RuntimeError: failed", diagnostic.Message);
        Assert.Equal(new TextSpan(0, source.Length), diagnostic.Span);
    }

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
    public void Execute_ConstructsManagedClassesAndBindsUserMethods()
    {
        using var output = new StringWriter();
        const string code =
            "class Counter:\n"
            + "    kind = 'counter'\n"
            + "    def __init__(self, value):\n"
            + "        self.value = value\n"
            + "    def increment(self, amount=1):\n"
            + "        self.value += amount\n"
            + "        return self.value\n"
            + "counter = Counter(value=40)\n"
            + "print(counter.increment(), counter.increment(amount=1), counter.value, Counter.kind)";

        var result = new ManagedPythonEngine().Execute(
            code,
            "class.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"41 42 42 counter{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_ClassMethodsCloseOverEnclosingFunctionState()
    {
        using var output = new StringWriter();
        const string code =
            "def make(seed):\n"
            + "    class Value:\n"
            + "        seed = 100\n"
            + "        copy = seed\n"
            + "        def read(self): return seed\n"
            + "    return Value()\n"
            + "value = make(42)\n"
            + "print(value.read(), value.seed, value.copy)";

        var result = new ManagedPythonEngine().Execute(
            code,
            "closure-class.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 100 100{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RejectsNonNoneInitializerReturnValues()
    {
        var result = new ManagedPythonEngine().Execute(
            "class Invalid:\n    def __init__(self): return 1\nInvalid()",
            "invalid-init.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4009", diagnostic.Code);
        Assert.Equal("__init__() must return None.", diagnostic.Message);
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

    [Fact]
    public void Execute_BreaksAndContinuesLoopsAndSkipsTheLoopElse()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "for value in [1, 2, 3, 4]:\n"
                + "    if value == 3:\n"
                + "        break\n"
                + "    print('for', value)\n"
                + "else:\n"
                + "    print('for-else')\n"
                + "count = 0\n"
                + "while count < 5:\n"
                + "    count = count + 1\n"
                + "    if count == 2:\n"
                + "        continue\n"
                + "    if count == 4:\n"
                + "        break\n"
                + "    print('while', count)\n"
                + "else:\n"
                + "    print('while-else')\n"
                + "for value in [1, 2]:\n"
                + "    print('kept', value)\n"
                + "else:\n"
                + "    print('kept-else')\n",
            "loops.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"for 1{Environment.NewLine}for 2{Environment.NewLine}"
                + $"while 1{Environment.NewLine}while 3{Environment.NewLine}"
                + $"kept 1{Environment.NewLine}kept 2{Environment.NewLine}"
                + $"kept-else{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RunsFinallyBlocksWhenLoopControlLeavesATryStatement()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "for value in [1, 2, 3]:\n"
                + "    try:\n"
                + "        try:\n"
                + "            if value == 2:\n"
                + "                break\n"
                + "            print('body', value)\n"
                + "        finally:\n"
                + "            print('inner', value)\n"
                + "    finally:\n"
                + "        print('outer', value)\n"
                + "count = 0\n"
                + "while count < 3:\n"
                + "    count = count + 1\n"
                + "    try:\n"
                + "        if count == 2:\n"
                + "            continue\n"
                + "        print('kept', count)\n"
                + "    finally:\n"
                + "        print('cleanup', count)\n"
                + "print('done', count)\n",
            "loop-finally.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"body 1{Environment.NewLine}inner 1{Environment.NewLine}outer 1{Environment.NewLine}"
                + $"inner 2{Environment.NewLine}outer 2{Environment.NewLine}"
                + $"kept 1{Environment.NewLine}cleanup 1{Environment.NewLine}"
                + $"cleanup 2{Environment.NewLine}"
                + $"kept 3{Environment.NewLine}cleanup 3{Environment.NewLine}"
                + $"done 3{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_BreaksOutOfAnExceptHandlerAndClearsTheActiveException()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "for value in [1, 2, 3]:\n"
                + "    try:\n"
                + "        raise ValueError('boom')\n"
                + "    except ValueError as error:\n"
                + "        print('caught', value)\n"
                + "        if value == 2:\n"
                + "            break\n"
                + "    finally:\n"
                + "        print('cleanup', value)\n"
                + "try:\n"
                + "    raise\n"
                + "except RuntimeError as error:\n"
                + "    print('no-active', error)\n",
            "handler-break.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"caught 1{Environment.NewLine}cleanup 1{Environment.NewLine}"
                + $"caught 2{Environment.NewLine}cleanup 2{Environment.NewLine}"
                + $"no-active No active exception to reraise.{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_BindsDefaultAndKeywordArguments()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def greet(name, greeting='hello', punctuation='!'):\n"
                + "    return greeting + ', ' + name + punctuation\n"
                + "print(greet('world'))\n"
                + "print(greet('world', 'hi'))\n"
                + "print(greet('world', punctuation='?'))\n"
                + "print(greet(punctuation='.', name='all', greeting='hey'))\n"
                + "base = 10\n"
                + "def scaled(value, factor=base):\n"
                + "    return value * factor\n"
                + "base = 99\n"
                + "print(scaled(3))\n",
            "keywords.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"hello, world!{Environment.NewLine}hi, world!{Environment.NewLine}"
                + $"hello, world?{Environment.NewLine}hey, all.{Environment.NewLine}"
                + $"30{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RaisesCatchableTypeErrorsForKeywordBindingFailures()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def act(first, second=2):\n"
                + "    return first\n"
                + "def attempt(callback):\n"
                + "    try:\n"
                + "        callback()\n"
                + "    except TypeError as error:\n"
                + "        print('caught', error)\n"
                + "def unexpected():\n"
                + "    act(1, wrong=3)\n"
                + "def duplicated():\n"
                + "    act(1, first=3)\n"
                + "def missing():\n"
                + "    act(second=3)\n"
                + "attempt(unexpected)\n"
                + "attempt(duplicated)\n"
                + "attempt(missing)\n",
            "keyword-errors.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"caught Function 'act' received an unexpected keyword argument 'wrong'.{Environment.NewLine}"
                + $"caught Function 'act' received multiple values for argument 'first'.{Environment.NewLine}"
                + $"caught Function 'act' is missing a value for argument 'first'.{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RebindsGlobalsAndNonlocalsThroughDeclarations()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "counter = 0\n"
                + "def bump(step=1):\n"
                + "    global counter\n"
                + "    counter = counter + step\n"
                + "bump()\n"
                + "bump(step=5)\n"
                + "print(counter)\n"
                + "def outer():\n"
                + "    total = 0\n"
                + "    def add(amount=2):\n"
                + "        nonlocal total\n"
                + "        total = total + amount\n"
                + "    add()\n"
                + "    add(amount=10)\n"
                + "    return total\n"
                + "print(outer())\n"
                + "def deep():\n"
                + "    value = 1\n"
                + "    def middle():\n"
                + "        def inner():\n"
                + "            nonlocal value\n"
                + "            value = value + 10\n"
                + "        inner()\n"
                + "        return value\n"
                + "    return middle()\n"
                + "print(deep())\n",
            "declarations.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"6{Environment.NewLine}12{Environment.NewLine}11{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_DispatchesBuiltinMethodsAcrossValueKinds()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "values = [3, 1, 2]\n"
                + "values.append(4)\n"
                + "values.extend([5])\n"
                + "values.insert(0, 0)\n"
                + "print(values, values.pop(), values.pop(0))\n"
                + "values.sort()\n"
                + "print(values, values.index(2), values.count(1))\n"
                + "print(' hi '.strip(), 'a,b'.split(','), '-'.join(['x', 'y']))\n"
                + "print('banana'.replace('an', 'A'), 'abc'.upper(), 'banana'.find('na'))\n"
                + "mapping = {'a': 1, 'b': 2}\n"
                + "print(mapping.get('a'), mapping.get('z', 9), mapping.keys(), mapping.items())\n"
                + "mapping.update({'c': 3})\n"
                + "print(mapping.pop('b'), mapping.setdefault('d', 4), mapping)\n"
                + "print((1, 2, 2).count(2), (1, 2, 2).index(2))\n",
            "methods.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"[3, 1, 2, 4] 5 0{Environment.NewLine}"
                + $"[1, 2, 3, 4] 1 1{Environment.NewLine}"
                + $"hi ['a', 'b'] x-y{Environment.NewLine}"
                + $"bAAa ABC 2{Environment.NewLine}"
                + $"1 9 dict_keys(['a', 'b']) dict_items([('a', 1), ('b', 2)]){Environment.NewLine}"
                + $"2 4 {{'a': 1, 'c': 3, 'd': 4}}{Environment.NewLine}"
                + $"2 1{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_SlicesSequencesAndAssignsListSlices()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "letters = ['a', 'b', 'c', 'd', 'e']\n"
                + "print(letters[1:3], letters[:2], letters[::2], letters[::-1], letters[-2:])\n"
                + "print('abcdef'[1:4], 'abcdef'[::-1], (1, 2, 3, 4)[1:3])\n"
                + "letters[1:3] = ['B', 'C', 'X']\n"
                + "print(letters)\n"
                + "letters[::3] = ['1', '2']\n"
                + "print(letters)\n",
            "slices.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"['b', 'c'] ['a', 'b'] ['a', 'c', 'e'] ['e', 'd', 'c', 'b', 'a'] "
                + $"['d', 'e']{Environment.NewLine}"
                + $"bcd fedcba (2, 3){Environment.NewLine}"
                + $"['a', 'B', 'C', 'X', 'd', 'e']{Environment.NewLine}"
                + $"['1', 'B', 'C', '2', 'd', 'e']{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_EvaluatesMembershipIdentityAndInPlaceOperators()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "print(2 in [1, 2], 3 not in (1, 2), 'an' in 'banana', 'k' in {'k': 1})\n"
                + "print(None is None, [] is [], 1 is not None)\n"
                + "n = 10\n"
                + "n += 5\n"
                + "n *= 2\n"
                + "n //= 4\n"
                + "print(n)\n"
                + "items = [1]\n"
                + "alias = items\n"
                + "items += [2, 3]\n"
                + "items *= 2\n"
                + "items[0] += 9\n"
                + "print(items, alias is items)\n",
            "operators.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"True True True True{Environment.NewLine}"
                + $"True False True{Environment.NewLine}"
                + $"7{Environment.NewLine}"
                + $"[10, 2, 3, 1, 2, 3] True{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RaisesCatchableCollectionProtocolErrors()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "try:\n    'abc'.nope\nexcept AttributeError:\n    print('attr')\n"
                + "try:\n    [].pop()\nexcept IndexError:\n    print('pop-empty')\n"
                + "try:\n    [1, 'a'].sort()\nexcept TypeError:\n    print('unorderable')\n"
                + "try:\n    [1, 2][::0]\nexcept ValueError:\n    print('zero-step')\n"
                + "try:\n    print(1 in 'abc')\nexcept TypeError:\n    print('membership')\n"
                + "try:\n    {'a': 1}.pop('zz')\nexcept KeyError:\n    print('pop-missing')\n",
            "collection-errors.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"attr{Environment.NewLine}pop-empty{Environment.NewLine}"
                + $"unorderable{Environment.NewLine}zero-step{Environment.NewLine}"
                + $"membership{Environment.NewLine}pop-missing{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_IteratesRangesEnumerateAndZipLazily()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "total = 0\n"
                + "for value in range(2, 20, 3):\n"
                + "    total += value\n"
                + "print(total, range(5), range(1, 7, 2), len(range(10)))\n"
                + "print(range(10)[3], range(10)[-1], range(0, 20, 2)[2:5], 4 in range(0, 10, 2))\n"
                + "for pair in enumerate('xy', 10):\n"
                + "    print(pair)\n"
                + "for triple in zip([1, 2, 3], 'abcd', range(9)):\n"
                + "    print(triple)\n",
            "iteration.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"57 range(0, 5) range(1, 7, 2) 10{Environment.NewLine}"
                + $"3 9 range(4, 10, 2) True{Environment.NewLine}"
                + $"(10, 'x'){Environment.NewLine}(11, 'y'){Environment.NewLine}"
                + $"(1, 'a', 0){Environment.NewLine}(2, 'b', 1){Environment.NewLine}"
                + $"(3, 'c', 2){Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_UnpacksTupleTargetsAndBareTupleDisplays()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "a, b = 1, 2\n"
                + "a, b = b, a\n"
                + "print(a, b)\n"
                + "(c, d), e = (5, 6), 7\n"
                + "print(c, d, e)\n"
                + "data = {'a': 1, 'b': 2}\n"
                + "for index, (key, value) in enumerate(data.items()):\n"
                + "    print(index, key, value)\n"
                + "def swap(p, q):\n"
                + "    return q, p\n"
                + "r, s = swap(1, 2)\n"
                + "print(r, s)\n"
                + "try:\n"
                + "    a, b, c = 1, 2\n"
                + "except ValueError as error:\n"
                + "    print('short', error)\n"
                + "try:\n"
                + "    a, b = 1, 2, 3\n"
                + "except ValueError as error:\n"
                + "    print('long', error)\n",
            "unpacking.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"2 1{Environment.NewLine}"
                + $"5 6 7{Environment.NewLine}"
                + $"0 a 1{Environment.NewLine}1 b 2{Environment.NewLine}"
                + $"2 1{Environment.NewLine}"
                + $"short Not enough values to unpack (expected 3, received 2).{Environment.NewLine}"
                + $"long Too many values to unpack (expected 2).{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_EvaluatesComprehensionsInIsolatedScopes()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "x = 99\n"
                + "print([x * 2 for x in range(5)], x)\n"
                + "print([a + b for a in range(3) for b in range(3) if a != b])\n"
                + "print({k: v * 10 for k, v in zip('ab', range(2))})\n"
                + "factor = 3\n"
                + "def scale(values):\n"
                + "    return [v * factor for v in values]\n"
                + "print(scale([1, 2]))\n"
                + "print([[y for y in range(n)] for n in range(3)])\n",
            "comprehensions.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"[0, 2, 4, 6, 8] 99{Environment.NewLine}"
                + $"[1, 2, 1, 3, 2, 3]{Environment.NewLine}"
                + $"{{'a': 0, 'b': 10}}{Environment.NewLine}"
                + $"[3, 6]{Environment.NewLine}"
                + $"[[], [0], [0, 1]]{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RaisesCatchableAssertionErrorsAndDeletesBindings()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "assert True\n"
                + "try:\n"
                + "    assert 1 == 2, 'one is not two'\n"
                + "except AssertionError as error:\n"
                + "    print('caught', error)\n"
                + "values = [1, 2, 3, 4, 5]\n"
                + "del values[0]\n"
                + "del values[::2]\n"
                + "print(values)\n"
                + "mapping = {'a': 1, 'b': 2}\n"
                + "del mapping['a']\n"
                + "print(mapping)\n"
                + "name = 'temp'\n"
                + "del name\n"
                + "try:\n"
                + "    print(name)\n"
                + "except NameError:\n"
                + "    print('deleted-global')\n"
                + "try:\n"
                + "    raise ValueError('boom')\n"
                + "except ValueError as error:\n"
                + "    print('handled', error)\n"
                + "try:\n"
                + "    print(error)\n"
                + "except NameError:\n"
                + "    print('target-deleted')\n"
                + "class Sample:\n"
                + "    pass\n"
                + "instance = Sample()\n"
                + "instance.value = 42\n"
                + "del instance.value\n"
                + "try:\n"
                + "    print(instance.value)\n"
                + "except AttributeError:\n"
                + "    print('attribute-deleted')\n"
                + "def capture_error():\n"
                + "    try:\n"
                + "        raise ValueError('captured')\n"
                + "    except ValueError as captured:\n"
                + "        def read():\n"
                + "            return captured\n"
                + "    return read\n"
                + "read_error = capture_error()\n"
                + "try:\n"
                + "    read_error()\n"
                + "except NameError:\n"
                + "    print('captured-target-deleted')\n"
                + "def delete_cell():\n"
                + "    value = 1\n"
                + "    def read():\n"
                + "        return value\n"
                + "    del value\n"
                + "    return read\n"
                + "read_cell = delete_cell()\n"
                + "try:\n"
                + "    read_cell()\n"
                + "except NameError:\n"
                + "    print('cell-deleted')\n",
            "assert-delete.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"caught one is not two{Environment.NewLine}"
                + $"[3, 5]{Environment.NewLine}"
                + $"{{'b': 2}}{Environment.NewLine}"
                + $"deleted-global{Environment.NewLine}"
                + $"handled boom{Environment.NewLine}"
                + $"target-deleted{Environment.NewLine}"
                + $"attribute-deleted{Environment.NewLine}"
                + $"captured-target-deleted{Environment.NewLine}"
                + $"cell-deleted{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_ConstructsAndClassifiesBuiltinTypes()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "print(int('42'), int(3.9), int(True), float('2.5'), str(42), bool([]))\n"
                + "print(list('abc'), tuple([1, 2]), dict([('a', 1)]))\n"
                + "print(isinstance(1, int), isinstance(True, int), isinstance(1, bool))\n"
                + "print(isinstance([1], (int, list)), isinstance(ValueError('v'), Exception))\n"
                + "print(type(1), type('x'), type(1) is int)\n"
                + "print([1, 2] + [3], (1,) + (2,), [0] * 3, 2 * (1, 2))\n"
                + "try:\n"
                + "    print([] * (10 ** 100))\n"
                + "except OverflowError:\n"
                + "    print('repeat-overflow')\n"
                + "print(sum([1, 2, 3]), sum(range(5), 100))\n"
                + "print(min([3, 1, 2]), max(4, 2, 9), sorted([3, 1, 2]), abs(-5), abs(-2.5))\n"
                + "try:\n"
                + "    int('abc')\n"
                + "except ValueError:\n"
                + "    print('bad-int')\n"
                + "try:\n"
                + "    min([])\n"
                + "except ValueError:\n"
                + "    print('empty-min')\n",
            "types.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"42 3 1 2.5 42 False{Environment.NewLine}"
                + $"['a', 'b', 'c'] (1, 2) {{'a': 1}}{Environment.NewLine}"
                + $"True True False{Environment.NewLine}"
                + $"True True{Environment.NewLine}"
                + $"<class 'int'> <class 'str'> True{Environment.NewLine}"
                + $"[1, 2, 3] (1, 2) [0, 0, 0] (1, 2, 1, 2){Environment.NewLine}"
                + $"repeat-overflow{Environment.NewLine}"
                + $"6 110{Environment.NewLine}"
                + $"1 9 [1, 2, 3] 5 2.5{Environment.NewLine}"
                + $"bad-int{Environment.NewLine}empty-min{Environment.NewLine}",
            output.ToString()
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

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        private bool _cancelled;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
        }
    }
}
