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
    public async Task Worker_ImportsStableAbiModuleThroughManagedExecution()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(CreateOptions(stableAbiModule: true));
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var result = await session.ExecuteAsync(
            "import dotpython_fixture\nprint(dotpython_fixture.increment(41))",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var failure = await session.ExecuteAsync(
            "import dotpython_fixture\ndotpython_fixture.fail()",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("42" + Environment.NewLine, result.StandardOutput);
        Assert.False(failure.Success);
        Assert.Contains(
            failure.Diagnostics,
            diagnostic =>
                diagnostic.Code == "DPY8005"
                && diagnostic.Message.Contains(
                    "ValueError: fixture failure",
                    StringComparison.Ordinal
                )
        );
        Assert.Contains("managed-stable-abi-fixture-v3", session.WorkerIdentity.Features);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ImportsIndependentStableAbiModulesFromQualifiedCatalog()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(
            CreateOptions(stableAbiModule: true, secondaryStableAbiModule: true)
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var result = await session.ExecuteAsync(
            """
            import dotpython_fixture
            import dotpython_fixture_secondary
            print(dotpython_fixture.increment(41))
            print(dotpython_fixture_secondary.double(21))
            """,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondaryFailure = await session.ExecuteAsync(
            "import dotpython_fixture_secondary\ndotpython_fixture_secondary.fail()",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var primaryAfterFailure = await session.ExecuteAsync(
            "import dotpython_fixture\nprint(dotpython_fixture.increment(9))",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal($"42{Environment.NewLine}42{Environment.NewLine}", result.StandardOutput);
        Assert.False(secondaryFailure.Success);
        Assert.Contains(
            secondaryFailure.Diagnostics,
            diagnostic => diagnostic.Message.Contains("secondary fixture failure", StringComparison.Ordinal)
        );
        Assert.True(
            primaryAfterFailure.Success,
            string.Join(Environment.NewLine, primaryAfterFailure.Diagnostics)
        );
        Assert.Equal($"10{Environment.NewLine}", primaryAfterFailure.StandardOutput);
        Assert.Contains("managed-stable-abi-fixture-v3", session.WorkerIdentity.Features);
        Assert.Contains(
            "managed-stable-abi-fixture-secondary-v1",
            session.WorkerIdentity.Features
        );
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public void Worker_RejectsDuplicateStableAbiCatalogArtifactsBeforeStartup()
    {
        SkipNativeFixtureOnWindows();
        var options = CreateOptions(stableAbiModule: true);
        var module = options.StableAbiModules.Single();
        var invalid = options with { StableAbiModules = [module, module] };

        var exception = Assert.Throws<ArgumentException>(() => new WorkerProcessPool(invalid));

        Assert.Equal("StableAbiModules", exception.ParamName);
        Assert.Contains("must be unique", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_FreezesStableAbiCatalogAtPoolConstruction()
    {
        SkipNativeFixtureOnWindows();
        var options = CreateOptions(stableAbiModule: true);
        var mutableCatalog = options.StableAbiModules.ToList();
        await using var pool = new WorkerProcessPool(
            options with { StableAbiModules = mutableCatalog }
        );
        mutableCatalog.Clear();

        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var result = await session.ExecuteAsync(
            "import dotpython_fixture\nprint(dotpython_fixture.increment(4))",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal($"5{Environment.NewLine}", result.StandardOutput);
    }

    [Fact]
    public async Task Worker_ImportsUnchangedAnyverWheelThroughGenericStableAbiObjects()
    {
        SkipAnyverPackageWhenUnavailable();
        await using var pool = new WorkerProcessPool(CreateQualifiedAnyverOptions());
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
        SkipAnyverPackageWhenUnavailable();
        await using var pool = new WorkerProcessPool(CreateQualifiedAnyverOptions());

        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var session = await pool.OpenSessionAsync(
                TestContext.Current.CancellationToken
            );
            var result = await session.ExecuteAsync(
                "import anyver\nprint(anyver.compare('2.0', '2.0'))",
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal("0" + Environment.NewLine, result.StandardOutput);
        }

        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_ContainsRepeatedAnyverFailuresWithoutPoisoningOwnerLane()
    {
        SkipAnyverPackageWhenUnavailable();
        await using var pool = new WorkerProcessPool(CreateQualifiedAnyverOptions());
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var failure = await session.ExecuteAsync(
                "import anyver\nanyver.compare('1.0', '2.0', 'dotpython-invalid-ecosystem')",
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.False(failure.Success);
            Assert.Contains(
                failure.Diagnostics,
                diagnostic => diagnostic.Message.Contains("ValueError", StringComparison.Ordinal)
            );
        }

        var success = await session.ExecuteAsync(
            "import anyver\nprint(anyver.compare('1.0', '2.0'))",
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(success.Success, string.Join(Environment.NewLine, success.Diagnostics));
        Assert.Equal("-1" + Environment.NewLine, success.StandardOutput);
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RestartsPinnedAnyverAfterOrderlyProcessShutdown()
    {
        SkipAnyverPackageWhenUnavailable();

        for (var iteration = 0; iteration < 4; iteration++)
        {
            await using var pool = new WorkerProcessPool(CreateQualifiedAnyverOptions());
            await using var session = await pool.OpenSessionAsync(
                TestContext.Current.CancellationToken
            );
            var result = await session.ExecuteAsync(
                $"import anyver\nprint(anyver.Version('1.2.{iteration}').raw)",
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal($"1.2.{iteration}{Environment.NewLine}", result.StandardOutput);
            Assert.Equal(WorkerProcessState.Running, pool.State);
        }
    }

    [Fact]
    public async Task Worker_RejectsUnconfiguredStableAbiImportWithoutFallback()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(
            CreateOptions(packageRoots: [NativeFixturePath(string.Empty)])
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var result = await session.ExecuteAsync(
            "import dotpython_fixture",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DPY4027");
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RecreatesNativeModuleStateAfterCrash()
    {
        SkipNativeFixtureOnWindows();
        await using var pool = new WorkerProcessPool(
            CreateOptions(enableTestFaultInjection: true, stableAbiModule: true)
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var initial = await session.ExecuteAsync(
            "import dotpython_fixture\nprint(dotpython_fixture.increment(1))",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var failedIdentity = session.WorkerIdentity;
        Assert.True(initial.Success, string.Join(Environment.NewLine, initial.Diagnostics));

        _ = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            pool.InjectTestFaultAsync(WorkerTestFault.Crash, TestContext.Current.CancellationToken)
        );
        await using var replacement = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var restarted = await replacement.ExecuteAsync(
            "import dotpython_fixture\nprint(dotpython_fixture.increment(41))",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(failedIdentity.Generation + 1, replacement.WorkerIdentity.Generation);
        Assert.True(restarted.Success, string.Join(Environment.NewLine, restarted.Diagnostics));
        Assert.Equal("42" + Environment.NewLine, restarted.StandardOutput);
    }

    [Fact]
    public async Task Worker_ReportsNativeHashFailureThroughImport()
    {
        SkipNativeFixtureOnWindows();
        var invalidHashOptions = CreateOptions(stableAbiModule: true);
        invalidHashOptions = invalidHashOptions with
        {
            StableAbiModules =
            [
                invalidHashOptions.StableAbiModules.Single() with
                {
                    ModuleSha256 = new string('0', 64),
                },
            ],
        };
        await using var hashPool = new WorkerProcessPool(invalidHashOptions);
        await using var hashSession = await hashPool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var hashFailure = await hashSession.ExecuteAsync(
            "import dotpython_fixture",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(hashFailure.Success);
        Assert.Contains(hashFailure.Diagnostics, diagnostic => diagnostic.Code == "DPY8001");
        Assert.Equal(WorkerProcessState.Running, hashPool.State);
    }

    [Fact]
    public async Task Worker_ContainsNativePreflightFailureWithoutPoisoningManagedExecution()
    {
        SkipNativeFixtureOnWindows();
        using var temporary = new TemporaryDirectory();
        var invalidModule = Path.Combine(temporary.Path, "dotpython_fixture.abi3.so");
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

        await File.WriteAllBytesAsync(invalidModule, bytes, TestContext.Current.CancellationToken);
        await using var pool = new WorkerProcessPool(
            CreateOptions(stableAbiModule: true, nativeModulePath: invalidModule)
        );
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );

        var failure = await session.ExecuteAsync(
            "import dotpython_fixture",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var managed = await session.ExecuteAsync(
            "print(42)",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(failure.Success);
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "DPY8004");
        Assert.True(managed.Success);
        Assert.Equal("42" + Environment.NewLine, managed.StandardOutput);
        Assert.Equal(WorkerProcessState.Running, pool.State);
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
            Arguments = [.. baseline.Arguments, "--protocol-major", "4"],
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
        bool stableAbiModule = false,
        bool secondaryStableAbiModule = false,
        string nativeModuleFileName = "dotpython_fixture.abi3.so",
        string nativeManifestFileName = "symbol-manifest.json",
        string? nativeModulePath = null,
        IReadOnlyList<string>? packageRoots = null
    )
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "worker", "DotPython.Worker.Host.dll");
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot =
            runtimeDirectory.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("The dotnet host root could not be resolved.");
        var dotnetHost = Path.Combine(
            dotnetRoot.FullName,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"
        );
        var nativeOptions = new List<WorkerStableAbiModuleOptions>();
        if (stableAbiModule)
        {
            var bridge = NativeFixturePath(
                OperatingSystem.IsMacOS() ? "libdotpython_abi3.dylib" : "libdotpython_abi3.so"
            );
            var module = nativeModulePath ?? NativeFixturePath(nativeModuleFileName);
            var manifest = NativeFixturePath(nativeManifestFileName);
            nativeOptions.Add(CreateStableAbiModuleOptions(bridge, module, manifest));
            if (secondaryStableAbiModule)
            {
                nativeOptions.Add(
                    CreateStableAbiModuleOptions(
                        bridge,
                        NativeFixturePath("dotpython_fixture_secondary.abi3.so"),
                        NativeFixturePath("secondary-symbol-manifest.json")
                    )
                );
            }
        }

        return new WorkerProcessOptions
        {
            FileName = dotnetHost,
            Arguments = [appPath],
            WorkingDirectory = Path.GetFullPath(AppContext.BaseDirectory),
            EnvironmentHash = "sha256:worker-tests",
            Policy = policy ?? new WorkerResourcePolicy(),
            EnableTestFaultInjection = enableTestFaultInjection,
            StableAbiModules = nativeOptions,
            PackageRoots =
                packageRoots
                ?? (
                    nativeOptions.Count == 0
                        ? Array.Empty<string>()
                        : [Path.GetDirectoryName(nativeOptions[0].ModulePath)!]
                ),
            RequiredFeatures = RequiredStableAbiFeatures(
                stableAbiModule,
                secondaryStableAbiModule,
                nativeManifestFileName
            ),
        };
    }

    private static WorkerStableAbiModuleOptions CreateStableAbiModuleOptions(
        string bridge,
        string module,
        string manifest
    ) =>
        new()
        {
            BridgePath = bridge,
            ModulePath = module,
            ManifestPath = manifest,
            BridgeSha256 = StableAbiModuleLoader.ComputeSha256(bridge),
            ModuleSha256 = StableAbiModuleLoader.ComputeSha256(module),
            ManifestSha256 = StableAbiModuleLoader.ComputeSha256(manifest),
        };

    private static List<string> RequiredStableAbiFeatures(
        bool stableAbiModule,
        bool secondaryStableAbiModule,
        string manifestFileName
    )
    {
        if (!stableAbiModule)
        {
            return ["managed-execution"];
        }

        var features = new List<string>
        {
            "managed-execution",
            manifestFileName == "anyver-symbol-manifest.json"
                ? "managed-stable-abi-qualified-v1"
                : "managed-stable-abi-fixture-v3",
        };
        if (secondaryStableAbiModule)
        {
            features.Add("managed-stable-abi-fixture-secondary-v1");
        }

        return features;
    }

    private static WorkerProcessOptions CreateQualifiedAnyverOptions()
    {
        var packageRoot = NativeFixturePath("anyver-package");
        return CreateOptions(
            stableAbiModule: true,
            nativeManifestFileName: "anyver-symbol-manifest.json",
            nativeModulePath: Path.Combine(packageRoot, "anyver", "_anyver.abi3.so"),
            packageRoots: [packageRoot]
        );
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

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dotpython-worker-abi3-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
