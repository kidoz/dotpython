using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using Xunit;

namespace DotPython.BuildIntegrationTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
public sealed class DotPythonProjectReferenceTests
{
    [Fact]
    public async Task CSharpProjectReference_BuildsRunsIncrementallyAndRebuildsDeterministically()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "dotpython-build-integration-" + Guid.NewGuid().ToString("N")
        );
        var fixtureRoot = Path.Combine(temporaryRoot, "fixture");
        var packageFeed = Path.Combine(temporaryRoot, "feed");
        Directory.CreateDirectory(packageFeed);

        try
        {
            CopyDirectory(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProjectReference"),
                fixtureRoot
            );
            await RunDotNetAsync(
                repositoryRoot,
                temporaryRoot,
                "pack",
                Path.Combine(repositoryRoot, "src", "DotPython.Sdk", "DotPython.Sdk.csproj"),
                "--configuration",
                "Release",
                "--output",
                packageFeed
            );
            await RunDotNetAsync(
                repositoryRoot,
                temporaryRoot,
                "build",
                Path.Combine(
                    repositoryRoot,
                    "src",
                    "DotPython.Runtime.Managed",
                    "DotPython.Runtime.Managed.csproj"
                ),
                "--configuration",
                "Release"
            );
            CopyRuntimeAssemblies(repositoryRoot, fixtureRoot);
            WriteNuGetConfiguration(fixtureRoot, packageFeed);

            var buildArguments = new[]
            {
                "build",
                Path.Combine(fixtureRoot, "Consumer", "Consumer.csproj"),
                "--configuration",
                "Release",
            };
            await RunDotNetAsync(fixtureRoot, temporaryRoot, buildArguments);

            var generatedDirectory = Path.Combine(
                fixtureRoot,
                "PricingRules",
                "obj",
                "Release",
                "net10.0",
                "dotpython"
            );
            var artifactPath = Path.Combine(generatedDirectory, "pricing.dpyc");
            var contractPath = Path.Combine(generatedDirectory, "pricing.contract.json");
            var facadePath = Path.Combine(generatedDirectory, "PricingModule.g.cs");
            var assemblyPath = Path.Combine(
                fixtureRoot,
                "PricingRules",
                "bin",
                "Release",
                "net10.0",
                "PricingRules.dll"
            );
            Assert.True(File.Exists(artifactPath));
            Assert.True(File.Exists(contractPath));
            Assert.True(File.Exists(facadePath));
            Assert.True(File.Exists(assemblyPath));
            var facade = await File.ReadAllTextAsync(
                facadePath,
                TestContext.Current.CancellationToken
            );
            Assert.Contains("IDotPythonModuleRuntime", facade, StringComparison.Ordinal);
            Assert.DoesNotContain("DotPython.Runtime.Managed", facade, StringComparison.Ordinal);

            using (var assemblyStream = File.OpenRead(assemblyPath))
            using (var portableExecutable = new PEReader(assemblyStream))
            {
                var metadata = portableExecutable.GetMetadataReader();
                var resources = metadata
                    .ManifestResources.Select(handle =>
                        metadata.GetString(metadata.GetManifestResource(handle).Name)
                    )
                    .ToArray();
                Assert.Contains("DotPython.Module.pricing.dpyc", resources);
                Assert.Contains("DotPython.Contract.pricing.json", resources);
            }

            var firstWrite = File.GetLastWriteTimeUtc(facadePath);
            await RunDotNetAsync(fixtureRoot, temporaryRoot, buildArguments);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(facadePath));

            var firstHashes = HashOutputs(artifactPath, contractPath, facadePath, assemblyPath);
            await RunDotNetAsync(
                fixtureRoot,
                temporaryRoot,
                "clean",
                Path.Combine(fixtureRoot, "Consumer", "Consumer.csproj"),
                "--configuration",
                "Release"
            );
            await RunDotNetAsync(fixtureRoot, temporaryRoot, buildArguments);
            Assert.Equal(
                firstHashes,
                HashOutputs(artifactPath, contractPath, facadePath, assemblyPath)
            );

            var run = await RunDotNetAsync(
                fixtureRoot,
                temporaryRoot,
                "run",
                "--project",
                Path.Combine(fixtureRoot, "Consumer", "Consumer.csproj"),
                "--configuration",
                "Release",
                "--no-build"
            );
            Assert.Equal("42", run.StandardOutput.Trim());

            await File.WriteAllTextAsync(
                Path.Combine(fixtureRoot, "PricingRules", "pricing.pyi"),
                "def missing(value: int) -> int: ...\n",
                TestContext.Current.CancellationToken
            );
            var failure = await RunDotNetExpectFailureAsync(
                fixtureRoot,
                temporaryRoot,
                buildArguments
            );
            Assert.Contains(
                "DPY7004",
                failure.StandardError + failure.StandardOutput,
                StringComparison.Ordinal
            );
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static string[] HashOutputs(params string[] paths) =>
        paths
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToArray();

    private static async Task<ProcessResult> RunDotNetAsync(
        string workingDirectory,
        string temporaryRoot,
        params string[] arguments
    )
    {
        var result = await StartDotNetAsync(workingDirectory, temporaryRoot, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
        );
        return result;
    }

    private static async Task<ProcessResult> RunDotNetExpectFailureAsync(
        string workingDirectory,
        string temporaryRoot,
        params string[] arguments
    )
    {
        var result = await StartDotNetAsync(workingDirectory, temporaryRoot, arguments);
        Assert.NotEqual(0, result.ExitCode);
        return result;
    }

    private static async Task<ProcessResult> StartDotNetAsync(
        string workingDirectory,
        string temporaryRoot,
        IReadOnlyList<string> arguments
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(
            temporaryRoot,
            "dotnet-home"
        );
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.StartInfo.Environment["NUGET_PACKAGES"] = Path.Combine(temporaryRoot, "packages");
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken
        );
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken
        );
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var result = new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        return result;
    }

    private static void WriteNuGetConfiguration(string fixtureRoot, string packageFeed)
    {
        var escapedFeed = SecurityElement.Escape(packageFeed);
        File.WriteAllText(
            Path.Combine(fixtureRoot, "NuGet.Config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="DotPython integration feed" value="{escapedFeed}" />
              </packageSources>
            </configuration>
            """
        );
    }

    private static void CopyRuntimeAssemblies(string repositoryRoot, string fixtureRoot)
    {
        var source = Path.Combine(
            repositoryRoot,
            "src",
            "DotPython.Runtime.Managed",
            "bin",
            "Release",
            "net10.0"
        );
        var destination = Path.Combine(fixtureRoot, "Runtime");
        Directory.CreateDirectory(destination);
        foreach (
            var fileName in new[]
            {
                "DotPython.Abstractions.dll",
                "DotPython.Compiler.dll",
                "DotPython.Language.dll",
                "DotPython.ParserGenerator.dll",
                "DotPython.Runtime.Managed.dll",
            }
        )
        {
            File.Copy(Path.Combine(source, fileName), Path.Combine(destination, fileName));
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
