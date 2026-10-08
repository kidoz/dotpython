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
                    && VersionBannerMatches(result.Output + result.Error, version)
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

    /// <summary>
    /// Determines whether a reference interpreter's <c>--version</c> banner reports the requested
    /// release, which may be pinned to an exact patch level.
    /// </summary>
    /// <remarks>
    /// The banner is <c>Python &lt;major&gt;.&lt;minor&gt;[.&lt;patch&gt;]</c>, possibly followed by
    /// a suffix such as a free-threaded marker. Versions are compared component by component, so
    /// <c>3.14</c> matches <c>Python 3.14.7</c>, <c>3.14.7</c> matches <c>Python 3.14.7</c>, and
    /// <c>3.14.7</c> rejects a longer patch number such as <c>Python 3.14.70</c>.
    /// </remarks>
    internal static bool VersionBannerMatches(string banner, string version)
    {
        const string prefix = "Python ";
        if (version.Length == 0 || !banner.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var reported = ReadVersionToken(banner[prefix.Length..]).Split('.');
        var requested = version.Split('.');
        if (requested.Length > reported.Length)
        {
            return false;
        }

        for (var index = 0; index < requested.Length; index++)
        {
            if (!string.Equals(requested[index], reported[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadVersionToken(string banner)
    {
        var length = 0;
        while (
            length < banner.Length && (char.IsAsciiDigit(banner[length]) || banner[length] == '.')
        )
        {
            length++;
        }

        return banner[..length];
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
