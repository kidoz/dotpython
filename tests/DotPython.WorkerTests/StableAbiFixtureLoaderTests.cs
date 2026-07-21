using System.Buffers.Binary;
using System.Text.Json.Nodes;
using DotPython.Runtime.Native;
using Xunit;

namespace DotPython.WorkerTests;

public sealed class StableAbiFixtureLoaderTests
{
    [Fact]
    public void Load_InitializesInvokesFailsAndCleansUpFixture()
    {
        SkipUnsupportedPlatform();
        var configuration = CreateConfiguration(FixturePath("dotpython_fixture.abi3.so"));
        var module = StableAbiFixtureLoader.Load(configuration);

        Assert.Equal("dotpython_fixture", module.ModuleName);
        Assert.Equal("dotpython-abi3-fixture-v1", module.ManifestVersion);
        Assert.True(module.MultiPhase);
        Assert.Equal(1, module.ReadyValue);
        Assert.Equal(42, module.InvokeLong("increment", 41));
        var exception = Assert.Throws<StableAbiLoadException>(() =>
            module.InvokeLong("fail", argument: null)
        );

        Assert.Equal("DPY8005", exception.Code);
        Assert.Equal(StableAbiLoadPhase.Invocation, exception.Phase);
        Assert.Contains("ValueError: fixture failure", exception.Message, StringComparison.Ordinal);
        module.Dispose();
        Assert.Equal(1, module.CleanupCountAfterDispose);
    }

    [Fact]
    public void Load_ExecutesPinnedAnyverModuleWithoutChangingTheWheelBinary()
    {
        SkipAnyverWhenUnavailable();
        var configuration = CreateAnyverConfiguration();
        using var module = StableAbiFixtureLoader.Load(configuration);

        Assert.Equal("anyver._anyver", module.ModuleName);
        Assert.Equal("dotpython-abi3-anyver-1.1.0-v1", module.ManifestVersion);
        Assert.Equal(
            "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",
            module.ArtifactSha256
        );
        Assert.Equal(configuration.FixtureSha256, module.NativeEntrySha256);
        Assert.True(module.MultiPhase);
        Assert.Equal(-1, module.CompareAnyver("1.0", "2.0", "generic"));
        Assert.Equal(
            ["1.0-alpha", "1.0", "2.0"],
            module.SortAnyver(["2.0", "1.0-alpha", "1.0"], "generic")
        );

        var version = module.DescribeAnyverVersion("1.2.3", "auto");
        Assert.Equal("1.2.3", version.Raw);
        Assert.Equal(1, version.Major);
        Assert.False(version.IsPrerelease);
    }

    [Fact]
    public void Load_RepeatedlyInitializesAndReleasesPinnedAnyverModule()
    {
        SkipAnyverWhenUnavailable();
        var configuration = CreateAnyverConfiguration();

        for (var iteration = 0; iteration < 25; iteration++)
        {
            using var module = StableAbiFixtureLoader.Load(configuration);
            Assert.Equal(0, module.CompareAnyver("2.0", "2.0", "generic"));
        }
    }

    [Fact]
    public void Load_SurvivesPinnedAnyverReferenceAndFailureChurn()
    {
        SkipAnyverWhenUnavailable();
        using var module = StableAbiFixtureLoader.Load(CreateAnyverConfiguration());

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var raw = $"1.2.{iteration % 100}";
            var version = module.DescribeAnyverVersion(raw, "auto");
            Assert.Equal(raw, version.Raw);
            Assert.Equal(-1, module.CompareAnyver(raw, "2.0.0", "generic"));

            if (iteration % 32 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        for (var iteration = 0; iteration < 128; iteration++)
        {
            var exception = Assert.Throws<StableAbiLoadException>(() =>
                module.CompareAnyver("1.0", "2.0", "dotpython-invalid-ecosystem")
            );
            Assert.Equal("DPY8005", exception.Code);
            Assert.Equal(StableAbiLoadPhase.Invocation, exception.Phase);
            Assert.Contains("ValueError", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(-1, module.CompareAnyver("1.0", "2.0", "generic"));
    }

    [Fact]
    public void Load_RejectsArtifactHashMismatchBeforeLoading()
    {
        SkipUnsupportedPlatform();
        var configuration = CreateConfiguration(FixturePath("dotpython_fixture.abi3.so")) with
        {
            FixtureSha256 = new string('0', 64),
        };

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiFixtureLoader.Load(configuration)
        );

        Assert.Equal("DPY8001", exception.Code);
        Assert.Equal(StableAbiLoadPhase.Policy, exception.Phase);
        Assert.NotNull(exception.ArtifactSha256);
    }

    [Fact]
    public void Load_RejectsWrongArchitectureBeforeNativeLoaderEntry()
    {
        SkipUnsupportedPlatform();
        using var temporary = new TemporaryDirectory();
        var source = File.ReadAllBytes(FixturePath("dotpython_fixture.abi3.so"));
        if (OperatingSystem.IsMacOS())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(4), 0x01000007);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(18), 183);
        }

        var wrongArchitecture = Path.Combine(temporary.Path, "wrong-architecture.so");
        File.WriteAllBytes(wrongArchitecture, source);
        var configuration = CreateConfiguration(wrongArchitecture);

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiFixtureLoader.Load(configuration)
        );

        Assert.Equal("DPY8002", exception.Code);
        Assert.Equal(StableAbiLoadPhase.Architecture, exception.Phase);
    }

    [Fact]
    public void Load_ReportsMissingInitializationSymbolDeterministically()
    {
        SkipUnsupportedPlatform();
        var bridge = FixturePath(BridgeFileName);
        var configuration = CreateConfiguration(bridge);

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiFixtureLoader.Load(configuration)
        );

        Assert.Equal("DPY8003", exception.Code);
        Assert.Equal(StableAbiLoadPhase.SymbolResolution, exception.Phase);
        Assert.Equal("PyInit_dotpython_fixture", exception.MissingSymbol);
    }

    [Fact]
    public void Load_ReportsNativeLoaderFailureWithArtifactIdentity()
    {
        SkipUnsupportedPlatform();
        using var temporary = new TemporaryDirectory();
        var invalid = Path.Combine(temporary.Path, "invalid-native.so");
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
        File.WriteAllBytes(invalid, bytes);
        var configuration = CreateConfiguration(invalid);

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiFixtureLoader.Load(configuration)
        );

        Assert.Equal("DPY8004", exception.Code);
        Assert.Equal(StableAbiLoadPhase.FixtureLoad, exception.Phase);
        Assert.Equal(StableAbiFixtureLoader.ComputeSha256(invalid), exception.ArtifactSha256);
    }

    [Fact]
    public void Load_ReportsInitializationFailureWithoutLeakingLibraries()
    {
        SkipUnsupportedPlatform();
        var configuration = CreateConfiguration(FixturePath("dotpython_fixture_failure.abi3.so"));

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiFixtureLoader.Load(configuration)
        );

        Assert.Equal("DPY8005", exception.Code);
        Assert.Equal(StableAbiLoadPhase.ModuleInitialization, exception.Phase);
        Assert.Contains(
            "ValueError: fixture initialization failure",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Load_RepeatedInitializationFailuresDoNotPoisonLaterLoads()
    {
        SkipUnsupportedPlatform();
        var failureConfiguration = CreateConfiguration(
            FixturePath("dotpython_fixture_failure.abi3.so")
        );

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var exception = Assert.Throws<StableAbiLoadException>(() =>
                StableAbiFixtureLoader.Load(failureConfiguration)
            );
            Assert.Equal("DPY8005", exception.Code);
            Assert.Equal(StableAbiLoadPhase.ModuleInitialization, exception.Phase);
        }

        using var module = StableAbiFixtureLoader.Load(
            CreateConfiguration(FixturePath("dotpython_fixture.abi3.so"))
        );
        Assert.Equal(42, module.InvokeLong("increment", 41));
    }

    [Fact]
    public void Manifest_RejectsAccidentalAllowlistGrowth()
    {
        SkipUnsupportedPlatform();
        using var temporary = new TemporaryDirectory();
        var manifest =
            JsonNode.Parse(File.ReadAllText(FixturePath("symbol-manifest.json")))
            ?? throw new InvalidOperationException("Fixture manifest could not be parsed.");
        var symbols =
            manifest["allowedStableAbiSymbols"]?.AsArray()
            ?? throw new InvalidOperationException("Fixture symbol array is missing.");
        symbols.Add("PyObject_Call");
        var path = Path.Combine(temporary.Path, "grown-manifest.json");
        File.WriteAllText(path, manifest.ToJsonString());

        var exception = Assert.Throws<StableAbiLoadException>(() =>
            StableAbiSymbolManifest.Load(path)
        );

        Assert.Equal("DPY8003", exception.Code);
        Assert.Equal(StableAbiLoadPhase.Manifest, exception.Phase);
    }

    private static StableAbiFixtureConfiguration CreateConfiguration(string fixturePath)
    {
        var bridge = FixturePath(BridgeFileName);
        var manifest = FixturePath("symbol-manifest.json");
        return new StableAbiFixtureConfiguration(
            bridge,
            fixturePath,
            manifest,
            StableAbiFixtureLoader.ComputeSha256(bridge),
            StableAbiFixtureLoader.ComputeSha256(fixturePath),
            StableAbiFixtureLoader.ComputeSha256(manifest)
        );
    }

    private static StableAbiFixtureConfiguration CreateAnyverConfiguration()
    {
        var bridge = FixturePath(BridgeFileName);
        var fixture = FixturePath("anyver._anyver.abi3.so");
        var manifest = FixturePath("anyver-symbol-manifest.json");
        return new StableAbiFixtureConfiguration(
            bridge,
            fixture,
            manifest,
            StableAbiFixtureLoader.ComputeSha256(bridge),
            StableAbiFixtureLoader.ComputeSha256(fixture),
            StableAbiFixtureLoader.ComputeSha256(manifest)
        );
    }

    private static string BridgeFileName =>
        OperatingSystem.IsMacOS() ? "libdotpython_abi3.dylib" : "libdotpython_abi3.so";

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "native", fileName);

    private static void SkipUnsupportedPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The initial Stable-ABI experiment supports osx-arm64 and linux-x64.");
        }
    }

    private static void SkipAnyverWhenUnavailable()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(FixturePath("anyver._anyver.abi3.so")))
        {
            Assert.Skip("Set DOTPYTHON_ANYVER_WHEEL to the pinned macOS ARM64 Anyver 1.1.0 wheel.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dotpython-abi3-{Guid.NewGuid():N}"
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
