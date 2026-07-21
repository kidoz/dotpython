using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DotPython.Protocol;
using DotPython.Runtime.Native;
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
    public async Task Worker_LoadsInvokesAndReleasesStableAbiFixture()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(CreateOptions(stableAbiFixture: true));
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        await using var module = await session.LoadStableAbiFixtureAsync(
            TestContext.Current.CancellationToken
        );

        session.ValidateHandle(module.Handle);
        var result = await module.InvokeLongAsync(
            "increment",
            41,
            TestContext.Current.CancellationToken
        );
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            module.InvokeLongAsync("fail", cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal(42, result);
        Assert.Equal("dotpython_fixture", module.ModuleName);
        Assert.Equal("dotpython-abi3-fixture-v1", module.ManifestVersion);
        Assert.Equal(
            StableAbiFixtureLoader.ComputeSha256(NativeFixturePath("dotpython_fixture.abi3.so")),
            module.ArtifactSha256
        );
        Assert.True(module.MultiPhase);
        Assert.Equal(1, module.ReadyValue);
        Assert.Equal("DPY8005", failure.Fault.Code);
        Assert.Contains("ValueError: fixture failure", failure.Message, StringComparison.Ordinal);
        Assert.Equal("Invocation", failure.Fault.Details?["nativePhase"]);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ExecutesPinnedAnyverModuleThroughTypedProtocol()
    {
        SkipAnyverWhenUnavailable();
        await using var pool = new WorkerProcessPool(
            CreateOptions(
                stableAbiFixture: true,
                nativeFixtureFileName: "anyver._anyver.abi3.so",
                nativeManifestFileName: "anyver-symbol-manifest.json"
            )
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        await using var module = await session.LoadStableAbiModuleAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal("anyver._anyver", module.ModuleName);
        Assert.Equal(
            "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",
            module.ArtifactSha256
        );
        Assert.Equal(
            StableAbiFixtureLoader.ComputeSha256(NativeFixturePath("anyver._anyver.abi3.so")),
            module.NativeEntrySha256
        );
        Assert.Equal(
            -1,
            await module.CompareAnyverAsync(
                "1.0",
                "2.0",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            ["1.0-alpha", "1.0", "2.0"],
            await module.SortAnyverAsync(
                ["2.0", "1.0-alpha", "1.0"],
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        var version = await module.DescribeAnyverVersionAsync(
            "1.2.3",
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal("1.2.3", version.Raw);
        Assert.Equal(1, version.Major);
        Assert.False(version.IsPrerelease);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ImportsUnchangedAnyverWheelThroughGenericStableAbiObjects()
    {
        SkipAnyverPackageWhenUnavailable();
        var packageRoot = NativeFixturePath("anyver-package");
        await using var pool = new WorkerProcessPool(
            CreateOptions(
                stableAbiFixture: true,
                nativeManifestFileName: "anyver-symbol-manifest.json",
                nativeFixturePath: Path.Combine(packageRoot, "anyver", "_anyver.abi3.so"),
                packageRoots: [packageRoot]
            )
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var result = await session.ExecuteAsync(
            """
            import anyver
            print(anyver.__version__)
            print(anyver.compare("1.0", "2.0"))
            print(anyver.eq("1.0", "1.0.0"))
            print(anyver.sort_versions(["2.0", "1.0-alpha", "1.0"]))
            value = anyver.Version("1.2.3-rc.1+build.42")
            print(value)
            print(value.raw)
            print(value.major)
            print(value.minor)
            print(value.patch)
            print(value.is_prerelease)
            print(value[0])
            print(value.to_dict()["raw"])
            """,
            fileName: "<generic-anyver-qualification>",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "1.1.0",
                "-1",
                "True",
                "['1.0-alpha', '1.0', '2.0']",
                "1.2.3-rc.1+build.42",
                "1.2.3-rc.1+build.42",
                "1",
                "2",
                "3",
                "True",
                "1",
                "1.2.3-rc.1+build.42"
            ) + Environment.NewLine,
            result.StandardOutput
        );
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ReusesPinnedAnyverCachesAcrossLogicalModuleLoads()
    {
        SkipAnyverWhenUnavailable();
        await using var pool = new WorkerProcessPool(
            CreateOptions(
                stableAbiFixture: true,
                nativeFixtureFileName: "anyver._anyver.abi3.so",
                nativeManifestFileName: "anyver-symbol-manifest.json"
            )
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var module = await session.LoadStableAbiModuleAsync(
                TestContext.Current.CancellationToken
            );
            Assert.Equal(
                0,
                await module.CompareAnyverAsync(
                    "2.0",
                    "2.0",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
        }

        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ContainsRepeatedAnyverFailuresWithoutPoisoningOwnerLane()
    {
        SkipAnyverWhenUnavailable();
        await using var pool = new WorkerProcessPool(
            CreateOptions(
                stableAbiFixture: true,
                nativeFixtureFileName: "anyver._anyver.abi3.so",
                nativeManifestFileName: "anyver-symbol-manifest.json"
            )
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        await using var module = await session.LoadStableAbiModuleAsync(
            TestContext.Current.CancellationToken
        );

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
                module.CompareAnyverAsync(
                    "1.0",
                    "2.0",
                    "dotpython-invalid-ecosystem",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal("DPY8005", exception.Fault.Code);
            Assert.Equal("Invocation", exception.Fault.Details?["nativePhase"]);
        }

        Assert.Equal(
            -1,
            await module.CompareAnyverAsync(
                "1.0",
                "2.0",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RestartsPinnedAnyverAfterOrderlyProcessShutdown()
    {
        SkipAnyverWhenUnavailable();

        for (var iteration = 0; iteration < 4; iteration++)
        {
            await using var pool = new WorkerProcessPool(
                CreateOptions(
                    stableAbiFixture: true,
                    nativeFixtureFileName: "anyver._anyver.abi3.so",
                    nativeManifestFileName: "anyver-symbol-manifest.json"
                )
            );
            await using var session = await pool.OpenSessionAsync(
                TestContext.Current.CancellationToken
            );
            await using var module = await session.LoadStableAbiModuleAsync(
                TestContext.Current.CancellationToken
            );

            var version = await module.DescribeAnyverVersionAsync(
                $"1.2.{iteration}",
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.Equal($"1.2.{iteration}", version.Raw);
            Assert.Equal(WorkerProcessState.Running, pool.State);
        }
    }

    [Fact]
    public async Task Worker_RejectsUnconfiguredStableAbiCapabilityWithoutFallback()
    {
        await using var pool = new WorkerProcessPool(CreateOptions());
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            session.LoadStableAbiFixtureAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal("DPY8000", exception.Fault.Code);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_InvalidatesNativeHandleAfterCrash()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(
            CreateOptions(enableTestFaultInjection: true, stableAbiFixture: true)
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        await using var module = await session.LoadStableAbiFixtureAsync(
            TestContext.Current.CancellationToken
        );

        _ = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.InjectTestFaultAsync(WorkerTestFault.Crash, TestContext.Current.CancellationToken)
        );
        var stale = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            module.InvokeLongAsync("increment", 1, TestContext.Current.CancellationToken)
        );

        Assert.Equal(WorkerProtocolFaultCodes.StaleHandle, stale.Fault.Code);
    }

    [Fact]
    public async Task Worker_ReportsNativeHashAndInitializationFailuresWithPhase()
    {
        SkipNativeFixtureOnWindows();
        var invalidHashOptions = CreateOptions(stableAbiFixture: true);
        invalidHashOptions = invalidHashOptions with
        {
            StableAbiFixture = invalidHashOptions.StableAbiFixture! with
            {
                FixtureSha256 = new string('0', 64),
            },
        };
        await using var hashPool = new WorkerProcessPool(invalidHashOptions);
        await using var hashSession = await hashPool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var hashFailure = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            hashSession.LoadStableAbiFixtureAsync(TestContext.Current.CancellationToken)
        );

        await using var initPool = new WorkerProcessPool(
            CreateOptions(
                stableAbiFixture: true,
                nativeFixtureFileName: "dotpython_fixture_failure.abi3.so"
            )
        );
        await using var initSession = await initPool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var initFailure = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            initSession.LoadStableAbiFixtureAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal("DPY8001", hashFailure.Fault.Code);
        Assert.Equal("Policy", hashFailure.Fault.Details?["nativePhase"]);
        Assert.Equal("DPY8005", initFailure.Fault.Code);
        Assert.Equal("ModuleInitialization", initFailure.Fault.Details?["nativePhase"]);
        Assert.Equal(
            "dotpython_fixture_failure.abi3.so",
            initFailure.Fault.Details?["nativeEntry"]
        );
        Assert.Equal(WorkerProcessState.Running, initPool.State);
    }

    [Fact]
    public async Task Worker_RecyclesAfterNativeLoaderFailureMarksProcessUnusable()
    {
        SkipNativeFixtureOnWindows();
        var invalidFixture = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-invalid-native-{Guid.NewGuid():N}.so"
        );
        var bytes = new byte[4096];
        if (OperatingSystem.IsMacOS())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x0100000c);
        }
        else
        {
            bytes[0] = 0x7f;
            bytes[1] = (byte)'E';
            bytes[2] = (byte)'L';
            bytes[3] = (byte)'F';
            bytes[4] = 2;
            bytes[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 62);
        }

        await File.WriteAllBytesAsync(invalidFixture, bytes, TestContext.Current.CancellationToken);
        try
        {
            await using var pool = new WorkerProcessPool(
                CreateOptions(stableAbiFixture: true, nativeFixturePath: invalidFixture)
            );
            await using var session = await pool.OpenSessionAsync(
                TestContext.Current.CancellationToken
            );
            var failedIdentity = session.WorkerIdentity;

            var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
                session.LoadStableAbiFixtureAsync(TestContext.Current.CancellationToken)
            );

            Assert.Equal("DPY8004", exception.Fault.Code);
            Assert.False(exception.Fault.WorkerUsable);
            Assert.Equal(WorkerProcessState.Faulted, pool.State);

            await using var replacement = await pool.OpenSessionAsync(
                TestContext.Current.CancellationToken
            );
            Assert.Equal(failedIdentity.Generation + 1, replacement.WorkerIdentity.Generation);
        }
        finally
        {
            File.Delete(invalidFixture);
        }
    }

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
        bool enableTestFaultInjection = false,
        bool stableAbiFixture = false,
        string nativeFixtureFileName = "dotpython_fixture.abi3.so",
        string nativeManifestFileName = "symbol-manifest.json",
        string? nativeFixturePath = null,
        IReadOnlyList<string>? packageRoots = null
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
        WorkerStableAbiFixtureOptions? nativeOptions = null;
        if (stableAbiFixture)
        {
            var bridge = NativeFixturePath(
                OperatingSystem.IsMacOS() ? "libdotpython_abi3.dylib" : "libdotpython_abi3.so"
            );
            var fixture = nativeFixturePath ?? NativeFixturePath(nativeFixtureFileName);
            var manifest = NativeFixturePath(nativeManifestFileName);
            nativeOptions = new WorkerStableAbiFixtureOptions
            {
                BridgePath = bridge,
                FixturePath = fixture,
                ManifestPath = manifest,
                BridgeSha256 = StableAbiFixtureLoader.ComputeSha256(bridge),
                FixtureSha256 = StableAbiFixtureLoader.ComputeSha256(fixture),
                ManifestSha256 = StableAbiFixtureLoader.ComputeSha256(manifest),
            };
        }

        return new WorkerProcessOptions
        {
            FileName = dotnetHost,
            Arguments = [appPath],
            WorkingDirectory = Path.GetFullPath(AppContext.BaseDirectory),
            EnvironmentHash = "sha256:worker-tests",
            Policy = policy ?? new WorkerResourcePolicy(),
            EnableTestFaultInjection = enableTestFaultInjection,
            StableAbiFixture = nativeOptions,
            PackageRoots = packageRoots ?? Array.Empty<string>(),
            RequiredFeatures = stableAbiFixture
                ?
                [
                    "managed-execution",
                    nativeManifestFileName == "anyver-symbol-manifest.json"
                        ? "managed-stable-abi-anyver-1.1.0"
                        : "managed-stable-abi-fixture-v1",
                ]
                : ["managed-execution"],
        };
    }

    private static string NativeFixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "native", fileName);

    private static void SkipNativeFixtureOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The initial Stable-ABI experiment supports osx-arm64 and linux-x64.");
        }
    }

    private static void SkipAnyverWhenUnavailable()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(NativeFixturePath("anyver._anyver.abi3.so")))
        {
            Assert.Skip("Set DOTPYTHON_ANYVER_WHEEL to the pinned macOS ARM64 Anyver 1.1.0 wheel.");
        }
    }

    private static void SkipAnyverPackageWhenUnavailable()
    {
        SkipAnyverWhenUnavailable();
        if (!Directory.Exists(NativeFixturePath("anyver-package")))
        {
            Assert.Skip("The pinned Anyver wheel package was not staged for qualification.");
        }
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
