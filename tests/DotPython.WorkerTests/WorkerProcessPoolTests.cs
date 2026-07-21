using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DotPython.Protocol;
using DotPython.Worker;
using Xunit;

namespace DotPython.WorkerTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
public sealed class WorkerProcessPoolTests
{
    [Fact]
    public async Task Worker_ExecutesManagedCodeAndShutsDownCleanly()
    {
        await using var pool = new WorkerProcessPool(CreateOptions());
        await pool.StartAsync(TestContext.Current.CancellationToken);
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var result = await session.ExecuteAsync(
            "print('worker', 42)",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"worker 42{Environment.NewLine}", result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(WorkerProcessState.Running, pool.State);
        Assert.Equal("dotpython-managed", session.WorkerIdentity.RuntimeId);
    }

    [Fact]
    public async Task Worker_EnforcesOutputAndSessionLimitsWithoutCorruptingProcess()
    {
        var limits = new WorkerProtocolLimits(4096, 32, 1, 1);
        await using var pool = new WorkerProcessPool(
            CreateOptions(policy: new WorkerResourcePolicy { Limits = limits })
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var outputFault = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            session.ExecuteAsync(
                $"print('{new string('x', 64)}')",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        var sessionFault = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.OpenSessionAsync(TestContext.Current.CancellationToken)
        );
        var recovery = await session.ExecuteAsync(
            "print('ok')",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerProtocolFaultCodes.LimitExceeded, outputFault.Fault.Code);
        Assert.Equal(WorkerProtocolFaultCodes.LimitExceeded, sessionFault.Fault.Code);
        Assert.Equal($"ok{Environment.NewLine}", recovery.StandardOutput);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RejectsOversizedRequestBeforeSendingIt()
    {
        var limits = new WorkerProtocolLimits(1024, 128, 1, 2);
        await using var pool = new WorkerProcessPool(
            CreateOptions(policy: new WorkerResourcePolicy { Limits = limits })
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            session.ExecuteAsync(
                new string('x', 2048),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        var recovery = await session.ExecuteAsync(
            "print('bounded')",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerProtocolFaultCodes.LimitExceeded, exception.Fault.Code);
        Assert.Equal($"bounded{Environment.NewLine}", recovery.StandardOutput);
    }

    [Fact]
    public async Task Worker_CooperativelyCancelsWithoutRecyclingHealthyGeneration()
    {
        var policy = new WorkerResourcePolicy
        {
            ExecutionTimeout = TimeSpan.FromSeconds(5),
            TerminationGracePeriod = TimeSpan.FromSeconds(1),
        };
        await using var pool = new WorkerProcessPool(CreateOptions(policy: policy));
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var generation = session.WorkerIdentity.Generation;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExecuteAsync(
                "value = 0\nwhile True:\n    value = value + 1",
                instructionLimit: long.MaxValue,
                cancellationToken: cancellation.Token
            )
        );
        var recovery = await session.ExecuteAsync(
            "print('still-running')",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(generation, session.WorkerIdentity.Generation);
        Assert.Equal(WorkerProcessState.Running, pool.State);
        Assert.Equal($"still-running{Environment.NewLine}", recovery.StandardOutput);
    }

    [Fact]
    public async Task Worker_HardTimeoutTerminatesAndReplacesHungGeneration()
    {
        var policy = new WorkerResourcePolicy
        {
            ExecutionTimeout = TimeSpan.FromMilliseconds(75),
            TerminationGracePeriod = TimeSpan.FromMilliseconds(25),
        };
        await using var pool = new WorkerProcessPool(
            CreateOptions(policy: policy, enableTestFaultInjection: true)
        );
        await pool.StartAsync(TestContext.Current.CancellationToken);
        var first = Assert.IsType<WorkerIdentity>(pool.ActiveIdentity);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.InjectTestFaultAsync(WorkerTestFault.Hang, TestContext.Current.CancellationToken)
        );
        await pool.StartAsync(TestContext.Current.CancellationToken);
        var replacement = Assert.IsType<WorkerIdentity>(pool.ActiveIdentity);

        Assert.Equal(WorkerProtocolFaultCodes.HardTimeout, exception.Fault.Code);
        Assert.Equal(first.Generation + 1, replacement.Generation);
        Assert.NotEqual(first.WorkerId, replacement.WorkerId);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_CrashIsContainedAndGenerationCanBeReplaced()
    {
        await using var pool = new WorkerProcessPool(CreateOptions(enableTestFaultInjection: true));
        await pool.StartAsync(TestContext.Current.CancellationToken);
        var first = Assert.IsType<WorkerIdentity>(pool.ActiveIdentity);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.InjectTestFaultAsync(WorkerTestFault.Crash, TestContext.Current.CancellationToken)
        );
        await pool.StartAsync(TestContext.Current.CancellationToken);
        var replacement = Assert.IsType<WorkerIdentity>(pool.ActiveIdentity);

        Assert.Equal(WorkerProtocolFaultCodes.WorkerTerminated, exception.Fault.Code);
        Assert.Equal(first.Generation + 1, replacement.Generation);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RejectsTruncatedAndDuplicateResponses()
    {
        await using var truncatedPool = new WorkerProcessPool(
            CreateOptions(enableTestFaultInjection: true)
        );
        await truncatedPool.StartAsync(TestContext.Current.CancellationToken);
        var truncated = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            truncatedPool.InjectTestFaultAsync(
                WorkerTestFault.TruncatedMessage,
                TestContext.Current.CancellationToken
            )
        );

        await using var duplicatePool = new WorkerProcessPool(
            CreateOptions(enableTestFaultInjection: true)
        );
        await duplicatePool.StartAsync(TestContext.Current.CancellationToken);
        await duplicatePool.InjectTestFaultAsync(
            WorkerTestFault.DuplicateResponse,
            TestContext.Current.CancellationToken
        );
        await WaitForStateAsync(duplicatePool, WorkerProcessState.Faulted);

        Assert.Equal(WorkerProtocolFaultCodes.HandshakeFailed, truncated.Fault.Code);
        Assert.Equal(WorkerProcessState.Faulted, duplicatePool.State);
    }

    [Fact]
    public async Task Worker_RejectsProtocolMajorSkewDuringStartup()
    {
        var baseline = CreateOptions();
        var options = baseline with
        {
            Arguments = [.. baseline.Arguments, "--protocol-major", "2"],
        };
        await using var pool = new WorkerProcessPool(options);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(WorkerProtocolFaultCodes.HandshakeFailed, exception.Fault.Code);
        Assert.Equal(WorkerProcessState.Stopped, pool.State);
    }

    [Fact]
    public async Task RecyclingInvalidatesOldHandlesAndIncrementsGenerationDeterministically()
    {
        await using var pool = new WorkerProcessPool(CreateOptions());
        await using var oldSession = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var oldIdentity = oldSession.WorkerIdentity;
        var handle = new WorkerObjectHandle(
            oldIdentity.ProviderId,
            oldIdentity.WorkerId,
            oldIdentity.Generation,
            oldSession.SessionId,
            1
        );
        oldSession.ValidateHandle(handle);

        await pool.RecycleAsync(TestContext.Current.CancellationToken);
        await using var newSession = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var stale = Assert.Throws<WorkerProtocolException>(() => oldSession.ValidateHandle(handle));

        Assert.Equal(WorkerProtocolFaultCodes.StaleHandle, stale.Fault.Code);
        Assert.Equal(oldIdentity.Generation + 1, newSession.WorkerIdentity.Generation);
    }

    [Fact]
    public async Task RequestCountPolicyRecyclesBeforeOpeningNextSession()
    {
        var policy = new WorkerResourcePolicy { MaxRequestsPerWorker = 1 };
        await using var pool = new WorkerProcessPool(CreateOptions(policy: policy));
        await using (
            var firstSession = await pool.OpenSessionAsync(TestContext.Current.CancellationToken)
        )
        {
            _ = await firstSession.ExecuteAsync(
                "value = 1",
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        var first = Assert.IsType<WorkerIdentity>(pool.ActiveIdentity);
        await using var secondSession = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal(first.Generation + 1, secondSession.WorkerIdentity.Generation);
    }

    [Fact]
    public void LaunchPolicy_DoesNotInheritAmbientEnvironment()
    {
        var options = CreateOptions() with
        {
            EnvironmentVariables = new Dictionary<string, string> { ["DOTPYTHON_SAFE"] = "1" },
        };
        var startInfo = WorkerProcessClient.CreateStartInfo(options, Guid.NewGuid(), 1);

        Assert.Single(startInfo.Environment);
        Assert.Equal("1", startInfo.Environment["DOTPYTHON_SAFE"]);
        Assert.False(startInfo.Environment.ContainsKey("PATH"));
        Assert.False(startInfo.Environment.ContainsKey("HOME"));
        Assert.False(startInfo.UseShellExecute);
    }

    private static WorkerProcessOptions CreateOptions(
        WorkerResourcePolicy? policy = null,
        bool enableTestFaultInjection = false
    )
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "worker", "DotPython.Worker.App.dll");
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot =
            runtimeDirectory.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("The dotnet host root could not be resolved.");
        var dotnetHost = Path.Combine(
            dotnetRoot.FullName,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"
        );
        return new WorkerProcessOptions
        {
            FileName = dotnetHost,
            Arguments = [appPath],
            WorkingDirectory = Path.GetFullPath(AppContext.BaseDirectory),
            EnvironmentHash = "sha256:worker-tests",
            Policy = policy ?? new WorkerResourcePolicy(),
            EnableTestFaultInjection = enableTestFaultInjection,
        };
    }

    private static async Task WaitForStateAsync(WorkerProcessPool pool, WorkerProcessState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (pool.State != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
