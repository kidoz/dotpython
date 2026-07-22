using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Assert.Contains("managed-stable-abi-fixture-v4", session.WorkerIdentity.Features);
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
            diagnostic =>
                diagnostic.Message.Contains("secondary fixture failure", StringComparison.Ordinal)
        );
        Assert.True(
            primaryAfterFailure.Success,
            string.Join(Environment.NewLine, primaryAfterFailure.Diagnostics)
        );
        Assert.Equal($"10{Environment.NewLine}", primaryAfterFailure.StandardOutput);
        Assert.Contains("managed-stable-abi-fixture-v4", session.WorkerIdentity.Features);
        Assert.Contains("managed-stable-abi-fixture-secondary-v2", session.WorkerIdentity.Features);
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
            options with
            {
                StableAbiModules = mutableCatalog,
            }
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
            gate = anyver.Version("1.2.3")
            print(repr(gate))
            print(len(gate))
            print(gate < anyver.Version("2.0"))
            print(anyver.Version.from_dict(gate.to_dict()) == gate)
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
                "1.2.3-rc.1+build.42",
                "Version('1.2.3')",
                "3",
                "True",
                "True"
            ) + Environment.NewLine,
            result.StandardOutput
        );
        Assert.Equal(WorkerProcessState.Running, pool.State);
    }

    [Fact]
    public async Task Worker_RecordsUnchangedAnyverUpstreamSuiteQualification()
    {
        SkipAnyverPackageWhenUnavailable();
        var sourceRoot = Environment.GetEnvironmentVariable("DOTPYTHON_ANYVER_SOURCE");
        var pythonPath = Environment.GetEnvironmentVariable("DOTPYTHON_ANYVER_PYTHON");
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(pythonPath))
        {
            Assert.Skip(
                "Set DOTPYTHON_ANYVER_SOURCE and DOTPYTHON_ANYVER_PYTHON to qualify the pinned upstream suite."
            );
        }

        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        var fullPythonPath = Path.GetFullPath(pythonPath);
        Assert.True(Directory.Exists(fullSourceRoot));
        Assert.True(File.Exists(fullPythonPath));
        var testPath = Path.Combine(fullSourceRoot, "tests", "test_anyver.py");
        var testBytes = await File.ReadAllBytesAsync(
            testPath,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            "9432cc519e7caa01295df0ec83c4f37ad246073c0841a1ca58578a281552fe6e",
            Convert.ToHexStringLower(SHA256.HashData(testBytes))
        );
        var testSource = Encoding.UTF8.GetString(testBytes);

        var packageRoot = NativeFixturePath("anyver-package");
        var pythonVersion = await RunQualificationProcessAsync(
            fullPythonPath,
            ["--version"],
            fullSourceRoot,
            packageRoot
        );
        var pytestVersion = await RunQualificationProcessAsync(
            fullPythonPath,
            ["-B", "-m", "pytest", "--version"],
            fullSourceRoot,
            packageRoot
        );
        var collection = await RunQualificationProcessAsync(
            fullPythonPath,
            [
                "-B",
                "-m",
                "pytest",
                "-p",
                "no:cacheprovider",
                "--color=no",
                "--collect-only",
                "-q",
                "tests/test_anyver.py",
            ],
            fullSourceRoot,
            packageRoot
        );
        var nodeIds = collection
            .StandardOutput.Split(
                '\n',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
            .Where(line => line.StartsWith("tests/test_anyver.py::", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(325, nodeIds.Length);
        Assert.Equal(nodeIds.Length, nodeIds.Distinct(StringComparer.Ordinal).Count());

        await using var pool = new WorkerProcessPool(CreateQualifiedAnyverOptions());
        await using var session = await pool.OpenSessionAsync(
            TestContext.Current.CancellationToken
        );
        var execution = await session.ExecuteAsync(
            testSource,
            fileName: "tests/test_anyver.py",
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(execution.Success);
        var blocker = Assert.IsType<WorkerDiagnostic>(execution.Diagnostics[0]);
        Assert.Equal("DPY2004", blocker.Code);
        Assert.Equal("The 'assert' statement is not supported in this position.", blocker.Message);

        var evidence = new AnyverQualificationEvidence
        {
            SchemaVersion = 1,
            Package = "anyver",
            PackageVersion = "1.1.0",
            Wheel = "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl",
            WheelSha256 = "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",
            SourceRevision = "3dc892e3eb9d1a4baf7a315a6ce4a41b3893337e",
            SourceTestFile = "tests/test_anyver.py",
            SourceTestFileSha256 =
                "9432cc519e7caa01295df0ec83c4f37ad246073c0841a1ca58578a281552fe6e",
            Platform = "macos-arm64",
            Collector = new AnyverQualificationCollector
            {
                PythonVersion = pythonVersion.StandardOutput.Trim(),
                PytestVersion = pytestVersion.StandardOutput.Trim(),
                Command =
                    "python -B -m pytest -p no:cacheprovider --color=no --collect-only -q tests/test_anyver.py",
            },
            Execution = new AnyverQualificationExecution
            {
                Provider = "dotpython-managed-abi3",
                ProviderVersion = "0.1.0",
                LanguageProfile = "3.14",
                Isolation = "worker-process",
                SourceModified = false,
                SuiteAdmissionAttempts = 1,
                AttemptedCases = 0,
                Blockers =
                [
                    new AnyverQualificationBlocker
                    {
                        Id = "managed-parser-assert-statement",
                        DiagnosticCode = blocker.Code,
                        Message = blocker.Message,
                    },
                ],
            },
            Summary = new AnyverQualificationSummary
            {
                Collected = nodeIds.Length,
                Passed = 0,
                Failed = 0,
                Skipped = nodeIds.Length,
            },
            Cases = nodeIds
                .Select(nodeId => new AnyverQualificationCase
                {
                    NodeId = nodeId,
                    Outcome = "skipped",
                    Blocker = "managed-parser-assert-statement",
                })
                .ToArray(),
        };
        var generated =
            JsonSerializer.Serialize(
                evidence,
                AnyverQualificationJsonContext.Default.AnyverQualificationEvidence
            ) + "\n";
        var evidencePath = Path.Combine(
            FindRepositoryRoot(),
            "native",
            "dotpython-abi3",
            "anyver-upstream-qualification.json"
        );
        if (
            string.Equals(
                Environment.GetEnvironmentVariable("DOTPYTHON_ANYVER_UPDATE_EVIDENCE"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            await File.WriteAllTextAsync(
                evidencePath,
                generated,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                TestContext.Current.CancellationToken
            );
        }
        else
        {
            Assert.Equal(
                generated,
                (
                    await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken)
                ).ReplaceLineEndings("\n")
            );
        }
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
                ? "managed-stable-abi-qualified-v2"
                : "managed-stable-abi-fixture-v4",
        };
        if (secondaryStableAbiModule)
        {
            features.Add("managed-stable-abi-fixture-secondary-v2");
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

    private static async Task<QualificationProcessResult> RunQualificationProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string pythonPath
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["PYTHONPATH"] = pythonPath;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken
        );
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken
        );
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var result = new QualificationProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError
        );
        Assert.True(
            result.ExitCode == 0,
            $"Qualification process failed with exit code {result.ExitCode}:{Environment.NewLine}{result.StandardError}"
        );
        return result;
    }

    private static string FindRepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotPython.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The DotPython repository root could not be located.");
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

    private sealed record QualificationProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );
}

internal sealed class AnyverQualificationEvidence
{
    public required int SchemaVersion { get; init; }

    public required string Package { get; init; }

    public required string PackageVersion { get; init; }

    public required string Wheel { get; init; }

    public required string WheelSha256 { get; init; }

    public required string SourceRevision { get; init; }

    public required string SourceTestFile { get; init; }

    public required string SourceTestFileSha256 { get; init; }

    public required string Platform { get; init; }

    public required AnyverQualificationCollector Collector { get; init; }

    public required AnyverQualificationExecution Execution { get; init; }

    public required AnyverQualificationSummary Summary { get; init; }

    public required IReadOnlyList<AnyverQualificationCase> Cases { get; init; }
}

internal sealed class AnyverQualificationCollector
{
    public required string PythonVersion { get; init; }

    public required string PytestVersion { get; init; }

    public required string Command { get; init; }
}

internal sealed class AnyverQualificationExecution
{
    public required string Provider { get; init; }

    public required string ProviderVersion { get; init; }

    public required string LanguageProfile { get; init; }

    public required string Isolation { get; init; }

    public required bool SourceModified { get; init; }

    public required int SuiteAdmissionAttempts { get; init; }

    public required int AttemptedCases { get; init; }

    public required IReadOnlyList<AnyverQualificationBlocker> Blockers { get; init; }
}

internal sealed class AnyverQualificationBlocker
{
    public required string Id { get; init; }

    public required string DiagnosticCode { get; init; }

    public required string Message { get; init; }
}

internal sealed class AnyverQualificationSummary
{
    public required int Collected { get; init; }

    public required int Passed { get; init; }

    public required int Failed { get; init; }

    public required int Skipped { get; init; }
}

internal sealed class AnyverQualificationCase
{
    public required string NodeId { get; init; }

    public required string Outcome { get; init; }

    public required string Blocker { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AnyverQualificationEvidence))]
internal sealed partial class AnyverQualificationJsonContext : JsonSerializerContext;
