using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Contracts;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed;
using Xunit;

namespace DotPython.RuntimeTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible for improved performance",
    Justification = "The tests verify that generated-facade consumers need only the abstraction interfaces."
)]
public sealed class ManagedPythonModuleRuntimeTests
{
    [Fact]
    public async Task GeneratedFacadeShape_LoadsAndInvokesCompiledModuleThroughAbstractions()
    {
        var definition = CreateDefinition(
            "calculator",
            "def add(left, right): return left + right",
            Function(
                "add",
                "AddAsync",
                [Parameter("left", BigIntegerType()), Parameter("right", BigIntegerType())],
                BigIntegerType()
            )
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );
        var facade = new CalculatorFacade(module);

        var result = await facade.AddAsync(
            new BigInteger(20),
            new BigInteger(22),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new BigInteger(42), result);
        Assert.Same(definition.Contract, module.Contract);
    }

    [Fact]
    public async Task Invocation_ExecutesClosuresFromSerializedModuleArtifacts()
    {
        var definition = CreateDefinition(
            "closure",
            "def calculate(value):\n"
                + "    def add(other): return value + other\n"
                + "    return add(2)",
            Function(
                "calculate",
                "CalculateAsync",
                [Parameter("value", BigIntegerType())],
                BigIntegerType()
            )
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        var result = await module.InvokeAsync<BigInteger>(
            new PythonFunctionInvocation("calculate", [new BigInteger(40)]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new BigInteger(42), result);
    }

    [Fact]
    public async Task LoadModuleAsync_ReusesPerRuntimeStateOnlyForIdenticalDefinition()
    {
        var definition = CreateDefinition(
            "identity",
            "def echo(value): return value",
            Function("echo", "EchoAsync", [Parameter("value", TextType())], TextType())
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();
        await using var first = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );
        await using var second = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        var value = await second.InvokeAsync<string>(
            new PythonFunctionInvocation("echo", ["value"]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("value", value);
        await Assert.ThrowsAsync<DotPythonException>(async () =>
            await runtime.LoadModuleAsync(
                CreateDefinition(
                    "identity",
                    "def echo(value): return value + '!'",
                    Function("echo", "EchoAsync", [Parameter("value", TextType())], TextType())
                ),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task LoadModuleAsync_CreatesDistinctPerSessionState()
    {
        var definition = CreateDefinition(
            "session_state",
            "state = [0]\n"
                + "def next_value():\n"
                + "    state[0] = state[0] + 1\n"
                + "    return state[0]",
            PythonModuleStatePolicy.PerSession,
            Function("next_value", "NextValueAsync", [], BigIntegerType())
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();
        await using var first = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );
        await using var second = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            BigInteger.One,
            await first.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("next_value"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            new BigInteger(2),
            await first.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("next_value"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            BigInteger.One,
            await second.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("next_value"),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ConcurrentInvocations_RunSeriallyOnTheRuntimeOwningThread()
    {
        var definition = CreateDefinition(
            "reporter",
            "def report(value):\n" + "    print(value)\n" + "    return value",
            Function(
                "report",
                "ReportAsync",
                [Parameter("value", BigIntegerType())],
                BigIntegerType()
            )
        );
        var output = new ThreadRecordingWriter();
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime(
            output: output
        );
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        var calls = Enumerable
            .Range(0, 16)
            .Select(value =>
                module
                    .InvokeAsync<BigInteger>(
                        new PythonFunctionInvocation("report", [new BigInteger(value)]),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask()
            );
        var results = await Task.WhenAll(calls);

        Assert.Equal(Enumerable.Range(0, 16).Select(value => new BigInteger(value)), results);
        Assert.Single(output.ThreadIds.Distinct());
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, output.ThreadIds);
    }

    [Fact]
    public async Task HostCallback_CanSynchronouslyReenterTheSameModuleRuntime()
    {
        var definition = CreateDefinition(
            "callbacks",
            "def outer(value):\n"
                + "    print(value)\n"
                + "    return value\n"
                + "def inner(value): return value + 1",
            Function(
                "outer",
                "OuterAsync",
                [Parameter("value", BigIntegerType())],
                BigIntegerType()
            ),
            Function(
                "inner",
                "InnerAsync",
                [Parameter("value", BigIntegerType())],
                BigIntegerType()
            )
        );
        var output = new ReentrantInvocationWriter();
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime(
            output: output
        );
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );
        output.Module = module;

        var result = await module.InvokeAsync<BigInteger>(
            new PythonFunctionInvocation("outer", [new BigInteger(41)]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new BigInteger(41), result);
        Assert.Equal(new BigInteger(42), output.CallbackResult);
        Assert.Equal(1, output.CallbackCount);
        Assert.NotEqual(Environment.CurrentManagedThreadId, output.CallbackThreadId);
    }

    [Fact]
    public async Task Invocation_ReportsStructuredContractAndRuntimeFailures()
    {
        var definition = CreateDefinition(
            "division",
            "def divide(left, right): return left // right",
            Function(
                "divide",
                "DivideAsync",
                [Parameter("left", BigIntegerType()), Parameter("right", BigIntegerType())],
                BigIntegerType()
            )
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        var missing = await Assert.ThrowsAsync<DotPythonException>(async () =>
            await module.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("missing"),
                TestContext.Current.CancellationToken
            )
        );
        var conversion = await Assert.ThrowsAsync<DotPythonException>(async () =>
            await module.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("divide", ["wrong", BigInteger.One]),
                TestContext.Current.CancellationToken
            )
        );
        var runtimeFault = await Assert.ThrowsAsync<DotPythonException>(async () =>
            await module.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("divide", [BigInteger.One, BigInteger.Zero]),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("DPY6005", missing.Code);
        Assert.Equal(DotPythonFailurePhase.Invocation, missing.Phase);
        Assert.Equal("DPY6007", conversion.Code);
        Assert.Equal(DotPythonFailurePhase.Conversion, conversion.Phase);
        Assert.Equal("DPY4004", runtimeFault.Code);
        Assert.Equal("division", runtimeFault.ModuleName);
        Assert.Equal("divide", runtimeFault.FunctionName);
    }

    [Fact]
    public async Task Invocation_CatchesManagedOperationFaultsInsideAnExport()
    {
        using var output = new StringWriter();
        var definition = CreateDefinition(
            "safe_division",
            "def divide(left, right):\n"
                + "    try:\n"
                + "        return left // right\n"
                + "    except ZeroDivisionError as error:\n"
                + "        print(error)\n"
                + "        return 0\n",
            Function(
                "divide",
                "DivideAsync",
                [Parameter("left", BigIntegerType()), Parameter("right", BigIntegerType())],
                BigIntegerType()
            )
        );
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime(
            output: output
        );
        await using var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );

        var result = await module.InvokeAsync<BigInteger>(
            new PythonFunctionInvocation("divide", [BigInteger.One, BigInteger.Zero]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(BigInteger.Zero, result);
        Assert.Equal($"Division by zero.{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task LoadModuleAsync_RejectsArtifactContractMismatch()
    {
        var definition = CreateDefinition(
            "actual",
            "def run(): return None",
            Function("run", "RunAsync", [], VoidType())
        );
        var mismatchedContract = new PythonModuleContract(
            DotPythonContractFormat.CurrentVersion,
            "different",
            "Generated",
            "DifferentModule",
            PythonModuleStatePolicy.PerRuntime,
            definition.Contract.Functions
        );
        var mismatch = new PythonModuleDefinition(mismatchedContract, definition.Artifact.Span);
        await using IDotPythonModuleRuntime runtime = new ManagedPythonModuleRuntime();

        var exception = await Assert.ThrowsAsync<DotPythonException>(async () =>
            await runtime.LoadModuleAsync(mismatch, TestContext.Current.CancellationToken)
        );

        Assert.Equal("DPY6002", exception.Code);
        Assert.Equal(DotPythonFailurePhase.ModuleLoad, exception.Phase);
    }

    [Fact]
    public async Task DisposedHandlesAndRuntimeRejectFurtherWorkAndCancellationPropagates()
    {
        var definition = CreateDefinition(
            "sample",
            "def run(): return None",
            Function("run", "RunAsync", [], VoidType())
        );
        var runtime = new ManagedPythonModuleRuntime();
        var module = await runtime.LoadModuleAsync(
            definition,
            TestContext.Current.CancellationToken
        );
        await module.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await module.InvokeAsync(
                new PythonFunctionInvocation("run"),
                TestContext.Current.CancellationToken
            )
        );
        await module.DisposeAsync();
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await runtime.LoadModuleAsync(definition, TestContext.Current.CancellationToken)
        );

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var activeRuntime = new ManagedPythonModuleRuntime();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await activeRuntime.LoadModuleAsync(definition, cancellation.Token)
        );
    }

    private static PythonModuleDefinition CreateDefinition(
        string moduleName,
        string source,
        params PythonFunctionContract[] functions
    ) => CreateDefinition(moduleName, source, PythonModuleStatePolicy.PerRuntime, functions);

    private static PythonModuleDefinition CreateDefinition(
        string moduleName,
        string source,
        PythonModuleStatePolicy statePolicy,
        params PythonFunctionContract[] functions
    )
    {
        var text = new SourceText(source, moduleName + ".py");
        var parse = PythonParser.Parse(text);
        Assert.True(parse.Success, string.Join(Environment.NewLine, parse.Diagnostics));
        var compilation = PythonCompiler.Compile(parse.Module, text.FilePath!);
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));
        var contract = new PythonModuleContract(
            DotPythonContractFormat.CurrentVersion,
            moduleName,
            "Generated",
            moduleName + "Module",
            statePolicy,
            functions
        );
        var exports = functions.Select(function => new DotPythonModuleExport(
            function.PythonName,
            function.ClrName,
            DotPythonExportKind.Function
        ));
        var artifact = DotPythonModuleArtifact.Create(moduleName, compilation.Code, exports);
        return new PythonModuleDefinition(
            contract,
            DotPythonModuleArtifactSerializer.Serialize(artifact)
        );
    }

    private static PythonFunctionContract Function(
        string pythonName,
        string clrName,
        IEnumerable<PythonParameterContract> parameters,
        PythonTypeContract returnType
    ) => new(pythonName, clrName, PythonCallShape.Synchronous, parameters, returnType);

    private static PythonParameterContract Parameter(string name, PythonTypeContract type) =>
        new(name, name, PythonParameterKind.PositionalOrKeyword, false, type);

    private static PythonTypeContract BigIntegerType() =>
        new("builtins.int", "System.Numerics.BigInteger", false, true, true);

    private static PythonTypeContract TextType() =>
        new("builtins.str", "System.String", false, false, true);

    private static PythonTypeContract VoidType() =>
        new("builtins.None", "System.Void", false, true, true);

    private sealed class CalculatorFacade(IDotPythonModule module)
    {
        internal ValueTask<BigInteger> AddAsync(
            BigInteger left,
            BigInteger right,
            CancellationToken cancellationToken
        ) =>
            module.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("add", [left, right]),
                cancellationToken
            );
    }

    private sealed class ThreadRecordingWriter : TextWriter
    {
        internal List<int> ThreadIds { get; } = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) =>
            ThreadIds.Add(Environment.CurrentManagedThreadId);
    }

    private sealed class ReentrantInvocationWriter : TextWriter
    {
        internal BigInteger? CallbackResult { get; private set; }

        internal int CallbackCount { get; private set; }

        internal int CallbackThreadId { get; private set; }

        internal IDotPythonModule? Module { get; set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            CallbackCount++;
            CallbackThreadId = Environment.CurrentManagedThreadId;
            var invocation = Module!.InvokeAsync<BigInteger>(
                new PythonFunctionInvocation("inner", [new BigInteger(41)])
            );
            if (!invocation.IsCompleted)
            {
                throw new InvalidOperationException(
                    "A synchronous runtime callback did not complete inline."
                );
            }

            CallbackResult = invocation.GetAwaiter().GetResult();
        }
    }
}
