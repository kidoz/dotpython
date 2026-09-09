using System.ComponentModel;
using System.Diagnostics;
using DotPython.Cli;
using DotPython.Language;
using Xunit;

namespace DotPython.DifferentialTests;

internal static class CompatibilityOracle
{
    internal static async Task AssertMatchesAsync(string source)
    {
        var version =
            Environment.GetEnvironmentVariable("DOTPYTHON_REFERENCE_PYTHON_VERSION")
            ?? PythonLanguageVersion.Current.ToString(2);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTPYTHON_REFERENCE_PYTHON"),
            $"python{version}",
            "python3",
        };
        string? oracle = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var result = await RunAsync(candidate, "--version").ConfigureAwait(false);
                if (
                    result.ExitCode == 0
                    && (result.Output + result.Error).StartsWith(
                        $"Python {version}.",
                        StringComparison.Ordinal
                    )
                )
                {
                    oracle = candidate;
                    break;
                }
            }
            catch (Win32Exception)
            {
                // Try the next configured/default executable.
            }
        }

        if (oracle is null)
        {
            Assert.Skip($"A Python {version} executable is required for this differential test.");
            return;
        }

        var reference = await RunAsync(oracle, "-c", source).ConfigureAwait(false);
        Assert.True(reference.ExitCode == 0, reference.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = DotPythonCommand.Run(
            ["-c", source],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );
        Assert.True(exitCode == reference.ExitCode, error.ToString());
        Assert.Equal(reference.Output, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable,
        params string[] arguments
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false)
            );
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
