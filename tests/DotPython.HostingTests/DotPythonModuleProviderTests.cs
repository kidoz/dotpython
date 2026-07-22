using System.Diagnostics.CodeAnalysis;
using DotPython.Contracts;
using DotPython.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DotPython.HostingTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The provider or DI container owns the asynchronous fake lifetimes in these tests."
)]
public sealed class DotPythonModuleProviderTests
{
    private static readonly int[] ExpectedAnswers = [42, 42];

    [Fact]
    public async Task ConcurrentFirstCalls_LoadTheModuleOnce()
    {
        var module = new RecordingModule(CreateDefinition().Contract, 42);
        var runtime = new ControlledRuntime();
        await using var provider = new DotPythonModuleProvider(runtime);
        var definition = CreateDefinition();
        var invocation = new PythonFunctionInvocation("answer");

        var first = provider
            .InvokeAsync<int>(definition, invocation, TestContext.Current.CancellationToken)
            .AsTask();
        var second = provider
            .InvokeAsync<int>(definition, invocation, TestContext.Current.CancellationToken)
            .AsTask();

        Assert.Equal(1, runtime.LoadCount);
        runtime.Complete(module);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(ExpectedAnswers, results);
        Assert.Equal(2, module.InvocationCount);
    }

    [Fact]
    public async Task CanceledWaiter_DoesNotCancelSharedInitialization()
    {
        var module = new RecordingModule(CreateDefinition().Contract, 42);
        var runtime = new ControlledRuntime();
        await using var provider = new DotPythonModuleProvider(runtime);
        var definition = CreateDefinition();
        var invocation = new PythonFunctionInvocation("answer");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );

        var survivingCall = provider
            .InvokeAsync<int>(definition, invocation, TestContext.Current.CancellationToken)
            .AsTask();
        var canceledCall = provider
            .InvokeAsync<int>(definition, invocation, cancellation.Token)
            .AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        runtime.Complete(module);
        Assert.Equal(42, await survivingCall);
        Assert.Equal(1, runtime.LoadCount);
    }

    [Fact]
    public async Task FailedInitialization_IsStickyForTheProviderLifetime()
    {
        var runtime = new ControlledRuntime();
        await using var provider = new DotPythonModuleProvider(runtime);
        var definition = CreateDefinition();
        var invocation = new PythonFunctionInvocation("answer");
        var failure = new DotPythonException(
            "DPY6099",
            "The test module could not be loaded.",
            DotPythonFailurePhase.ModuleLoad,
            definition.Contract.ModuleName
        );

        var firstCall = provider
            .InvokeAsync<int>(definition, invocation, TestContext.Current.CancellationToken)
            .AsTask();
        runtime.Fail(failure);

        var firstFailure = await Assert.ThrowsAsync<DotPythonException>(() => firstCall);
        var secondFailure = await Assert.ThrowsAsync<DotPythonException>(() =>
            provider
                .InvokeAsync<int>(definition, invocation, TestContext.Current.CancellationToken)
                .AsTask()
        );

        Assert.Same(failure, firstFailure);
        Assert.Same(failure, secondFailure);
        Assert.Equal(1, runtime.LoadCount);
    }

    [Fact]
    public async Task ConflictingDefinition_IsRejectedWithoutAnotherRuntimeLoad()
    {
        var definition = CreateDefinition();
        var conflictingDefinition = CreateDefinition(artifact: [2]);
        var runtime = new ControlledRuntime(new RecordingModule(definition.Contract, 42));
        await using var provider = new DotPythonModuleProvider(runtime);

        await provider.WarmUpAsync(definition, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<DotPythonException>(() =>
            provider
                .WarmUpAsync(conflictingDefinition, TestContext.Current.CancellationToken)
                .AsTask()
        );

        Assert.Equal("DPY6002", exception.Code);
        Assert.Equal(DotPythonFailurePhase.ModuleLoad, exception.Phase);
        Assert.Equal(1, runtime.LoadCount);
    }

    [Fact]
    public async Task DisposeAsync_DrainsAdmittedInvocationBeforeReleasingHandle()
    {
        var definition = CreateDefinition();
        var module = new BlockingModule(definition.Contract);
        var runtime = new ControlledRuntime(module);
        var provider = new DotPythonModuleProvider(runtime);

        var invocation = provider
            .InvokeAsync<int>(
                definition,
                new PythonFunctionInvocation("answer"),
                TestContext.Current.CancellationToken
            )
            .AsTask();
        await module.InvocationStarted;
        var disposal = provider.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, module.DisposeCount);
        module.Complete(42);

        Assert.Equal(42, await invocation);
        await disposal;
        Assert.Equal(1, module.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesModuleHandlesInReverseLoadOrder()
    {
        var disposalOrder = new List<string>();
        var runtime = new TrackingRuntime(disposalOrder);
        var provider = new DotPythonModuleProvider(runtime);
        var first = CreateDefinition(moduleName: "first");
        var second = CreateDefinition(moduleName: "second");

        await provider.WarmUpAsync(first, TestContext.Current.CancellationToken);
        await provider.WarmUpAsync(second, TestContext.Current.CancellationToken);
        await provider.DisposeAsync();

        Assert.Equal(["second", "first"], disposalOrder);
    }

    [Fact]
    public async Task DisposeAsync_DisposesSharedHandleAndRejectsNewCalls()
    {
        var definition = CreateDefinition();
        var module = new RecordingModule(definition.Contract, 42);
        var runtime = new ControlledRuntime(module);
        var provider = new DotPythonModuleProvider(runtime);

        Assert.Equal(
            42,
            await provider.InvokeAsync<int>(
                definition,
                new PythonFunctionInvocation("answer"),
                TestContext.Current.CancellationToken
            )
        );
        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.Equal(1, module.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            provider
                .InvokeAsync<int>(
                    definition,
                    new PythonFunctionInvocation("answer"),
                    TestContext.Current.CancellationToken
                )
                .AsTask()
        );
    }

    [Fact]
    public async Task DependencyInjection_ResolvesTypedClientAndOwnsProviderLifetime()
    {
        var definition = CreateDefinition();
        var module = new RecordingModule(definition.Contract, 42);
        var runtime = new ControlledRuntime(module);
        var registration = new PythonModuleRegistration<IAnswerModule>(
            definition,
            provider => new AnswerModuleClient(provider, definition)
        );
        var services = new ServiceCollection();
        services.AddSingleton<IDotPythonModuleRuntime>(runtime);
        services.AddDotPythonManaged().AddDotPythonModule(registration);

        await using (var serviceProvider = services.BuildServiceProvider())
        {
            var first = serviceProvider.GetRequiredService<IAnswerModule>();
            var second = serviceProvider.GetRequiredService<IAnswerModule>();

            Assert.Same(first, second);
            Assert.Equal(42, await first.GetAnswerAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, runtime.LoadCount);
        }

        Assert.Equal(1, module.DisposeCount);
        Assert.Equal(0, runtime.DisposeCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DependencyInjection_DisposesProviderBeforeOwnedRuntime()
    {
        var events = new List<string>();
        var definition = CreateDefinition();
        var services = new ServiceCollection();
        services.AddSingleton<IDotPythonModuleRuntime>(_ => new LifecycleRuntime(events));
        services.AddDotPythonManaged().AddDotPythonModule(CreateRegistration(definition));

        await using (var serviceProvider = services.BuildServiceProvider())
        {
            var client = serviceProvider.GetRequiredService<IAnswerModule>();
            Assert.Equal(42, await client.GetAnswerAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(["module", "runtime"], events);
    }

    [Fact]
    public async Task HostStartup_WarmsConfiguredPerRuntimeModule()
    {
        var definition = CreateDefinition();
        var module = new RecordingModule(definition.Contract, 42);
        var runtime = new ControlledRuntime(module);
        var registration = CreateRegistration(definition);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IDotPythonModuleRuntime>(runtime);
        builder
            .Services.AddDotPythonManaged()
            .AddDotPythonModule(registration, options => options.WarmUpOnHostStart = true);
        var host = builder.Build();

        try
        {
            Assert.Equal(0, runtime.LoadCount);
            await host.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, runtime.LoadCount);
            Assert.Equal(0, module.InvocationCount);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task HostStartup_SurfacesWarmupFailure()
    {
        var definition = CreateDefinition();
        var runtime = new ControlledRuntime();
        runtime.Fail(
            new DotPythonException(
                "DPY6001",
                "The compiled module artifact is invalid.",
                DotPythonFailurePhase.ModuleLoad,
                definition.Contract.ModuleName
            )
        );
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IDotPythonModuleRuntime>(runtime);
        builder
            .Services.AddDotPythonManaged()
            .AddDotPythonModule(
                CreateRegistration(definition),
                options => options.WarmUpOnHostStart = true
            );
        var host = builder.Build();

        try
        {
            var exception = await Assert.ThrowsAsync<DotPythonException>(() =>
                host.StartAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal("DPY6001", exception.Code);
            Assert.Equal(1, runtime.LoadCount);
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public void PerSessionRegistration_RejectsHostStartupWarmup()
    {
        var registration = CreateRegistration(CreateDefinition(PythonModuleStatePolicy.PerSession));
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services
                .AddDotPythonManaged()
                .AddDotPythonModule(registration, options => options.WarmUpOnHostStart = true)
        );

        Assert.Contains("Per-session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerSessionRegistration_SharesWithinScopeAndSeparatesScopes()
    {
        var definition = CreateDefinition(PythonModuleStatePolicy.PerSession);
        var runtime = new FactoryRuntime();
        var services = new ServiceCollection();
        services.AddSingleton<IDotPythonModuleRuntime>(runtime);
        services.AddDotPythonManaged().AddDotPythonModule(CreateRegistration(definition));
        await using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );

        await using (var firstScope = serviceProvider.CreateAsyncScope())
        {
            var first = firstScope.ServiceProvider.GetRequiredService<IAnswerModule>();
            var repeated = firstScope.ServiceProvider.GetRequiredService<IAnswerModule>();
            Assert.Same(first, repeated);
            Assert.Equal(1, await first.GetAnswerAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await repeated.GetAnswerAsync(TestContext.Current.CancellationToken));
        }

        await using (var secondScope = serviceProvider.CreateAsyncScope())
        {
            var second = secondScope.ServiceProvider.GetRequiredService<IAnswerModule>();
            Assert.Equal(2, await second.GetAnswerAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, runtime.LoadCount);
        Assert.All(runtime.Modules, module => Assert.Equal(1, module.DisposeCount));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DotPythonHost_OwnsRuntimeAndLogicalSessions()
    {
        var runtimeDefinition = CreateDefinition();
        var sessionDefinition = CreateDefinition(PythonModuleStatePolicy.PerSession);
        var runtime = new FactoryRuntime();

        await using (var host = DotPythonHost.Create(runtime))
        {
            var runtimeClient = host.GetModule(CreateRegistration(runtimeDefinition));
            await host.WarmUpAsync(
                CreateRegistration(runtimeDefinition),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(
                1,
                await runtimeClient.GetAnswerAsync(TestContext.Current.CancellationToken)
            );

            await using var firstSession = host.CreateSession();
            var firstSessionClient = firstSession.GetModule(CreateRegistration(sessionDefinition));
            Assert.Equal(
                2,
                await firstSessionClient.GetAnswerAsync(TestContext.Current.CancellationToken)
            );

            var secondSession = host.CreateSession();
            var secondSessionClient = secondSession.GetModule(
                CreateRegistration(sessionDefinition)
            );
            Assert.Equal(
                3,
                await secondSessionClient.GetAnswerAsync(TestContext.Current.CancellationToken)
            );
        }

        Assert.Equal(3, runtime.LoadCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.All(runtime.Modules, module => Assert.Equal(1, module.DisposeCount));
    }

    [Fact]
    public async Task DotPythonHost_AwaitsSessionDisposalAlreadyInProgress()
    {
        var definition = CreateDefinition(PythonModuleStatePolicy.PerSession);
        var module = new BlockingModule(definition.Contract);
        var runtime = new ControlledRuntime(module);
        var host = DotPythonHost.Create(runtime);
        var session = host.CreateSession();
        var client = session.GetModule(CreateRegistration(definition));

        var invocation = client.GetAnswerAsync(TestContext.Current.CancellationToken).AsTask();
        await module.InvocationStarted;
        var sessionDisposal = session.DisposeAsync().AsTask();
        var hostDisposal = host.DisposeAsync().AsTask();

        Assert.False(sessionDisposal.IsCompleted);
        Assert.False(hostDisposal.IsCompleted);
        module.Complete(42);

        Assert.Equal(42, await invocation);
        await sessionDisposal;
        await hostDisposal;
        Assert.Equal(1, module.DisposeCount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    private static PythonModuleRegistration<IAnswerModule> CreateRegistration(
        PythonModuleDefinition definition
    ) => new(definition, provider => new AnswerModuleClient(provider, definition));

    private static PythonModuleDefinition CreateDefinition(
        PythonModuleStatePolicy statePolicy = PythonModuleStatePolicy.PerRuntime,
        string moduleName = "answer",
        byte[]? artifact = null
    ) =>
        new(
            new PythonModuleContract(
                1,
                moduleName,
                "DotPython.HostingTests.Generated",
                "AnswerModule",
                statePolicy,
                [
                    new PythonFunctionContract(
                        "answer",
                        "GetAnswerAsync",
                        PythonCallShape.Synchronous,
                        [],
                        new PythonTypeContract(
                            "int",
                            "System.Int32",
                            isNullable: false,
                            isValueType: true,
                            isClsCompliant: true
                        )
                    ),
                ]
            ),
            artifact ?? [1]
        );

    private interface IAnswerModule
    {
        ValueTask<int> GetAnswerAsync(CancellationToken cancellationToken = default);
    }

    private sealed class AnswerModuleClient(
        IDotPythonModuleProvider provider,
        PythonModuleDefinition definition
    ) : IAnswerModule
    {
        public ValueTask<int> GetAnswerAsync(CancellationToken cancellationToken = default) =>
            provider.InvokeAsync<int>(
                definition,
                new PythonFunctionInvocation("answer"),
                cancellationToken
            );
    }

    private sealed class ControlledRuntime : IDotPythonModuleRuntime
    {
        private readonly TaskCompletionSource<IDotPythonModule> _module = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal ControlledRuntime() { }

        internal ControlledRuntime(IDotPythonModule module)
        {
            Complete(module);
        }

        internal int DisposeCount { get; private set; }

        internal int LoadCount { get; private set; }

        public ValueTask<IDotPythonModule> LoadModuleAsync(
            PythonModuleDefinition definition,
            CancellationToken cancellationToken = default
        )
        {
            LoadCount++;
            return new ValueTask<IDotPythonModule>(_module.Task.WaitAsync(cancellationToken));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Complete(IDotPythonModule module) => _module.TrySetResult(module);

        internal void Fail(Exception exception) => _module.TrySetException(exception);
    }

    private sealed class FactoryRuntime : IDotPythonModuleRuntime
    {
        internal int DisposeCount { get; private set; }

        internal int LoadCount { get; private set; }

        internal List<RecordingModule> Modules { get; } = [];

        public ValueTask<IDotPythonModule> LoadModuleAsync(
            PythonModuleDefinition definition,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            var module = new RecordingModule(definition.Contract, LoadCount);
            Modules.Add(module);
            return ValueTask.FromResult<IDotPythonModule>(module);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingRuntime(List<string> disposalOrder) : IDotPythonModuleRuntime
    {
        public ValueTask<IDotPythonModule> LoadModuleAsync(
            PythonModuleDefinition definition,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IDotPythonModule>(
                new TrackingModule(definition.Contract, disposalOrder)
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LifecycleRuntime(List<string> events) : IDotPythonModuleRuntime
    {
        public ValueTask<IDotPythonModule> LoadModuleAsync(
            PythonModuleDefinition definition,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IDotPythonModule>(
                new LifecycleModule(definition.Contract, events)
            );
        }

        public ValueTask DisposeAsync()
        {
            events.Add("runtime");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingModule(PythonModuleContract contract) : IDotPythonModule
    {
        private readonly TaskCompletionSource<int> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<object?> _invocationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public PythonModuleContract Contract { get; } = contract;

        internal int DisposeCount { get; private set; }

        internal Task InvocationStarted => _invocationStarted.Task;

        public ValueTask InvokeAsync(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromException(new NotSupportedException());

        public async ValueTask<TResult> InvokeAsync<TResult>(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            _invocationStarted.TrySetResult(null);
            var result = await _completion.Task.WaitAsync(cancellationToken);
            return (TResult)(object)result;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Complete(int result) => _completion.TrySetResult(result);
    }

    private sealed class TrackingModule(PythonModuleContract contract, List<string> disposalOrder)
        : IDotPythonModule
    {
        public PythonModuleContract Contract { get; } = contract;

        public ValueTask InvokeAsync(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask<TResult> InvokeAsync<TResult>(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult((TResult)(object)42);

        public ValueTask DisposeAsync()
        {
            disposalOrder.Add(Contract.ModuleName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleModule(PythonModuleContract contract, List<string> events)
        : IDotPythonModule
    {
        public PythonModuleContract Contract { get; } = contract;

        public ValueTask InvokeAsync(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask<TResult> InvokeAsync<TResult>(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult((TResult)(object)42);

        public ValueTask DisposeAsync()
        {
            events.Add("module");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingModule(PythonModuleContract contract, int result)
        : IDotPythonModule
    {
        public PythonModuleContract Contract { get; } = contract;

        internal int DisposeCount { get; private set; }

        internal int InvocationCount { get; private set; }

        public ValueTask InvokeAsync(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            InvocationCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<TResult> InvokeAsync<TResult>(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            InvocationCount++;
            return ValueTask.FromResult((TResult)(object)result);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
