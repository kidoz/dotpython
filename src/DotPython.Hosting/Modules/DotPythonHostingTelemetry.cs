using System.Diagnostics;
using System.Diagnostics.Metrics;
using DotPython.Contracts;

namespace DotPython.Hosting;

internal static class DotPythonHostingTelemetry
{
    internal const string MeterName = "DotPython.Hosting";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> InitializationAttempts = Meter.CreateCounter<long>(
        "dotpython.module.initialization.attempts",
        "{attempt}",
        "Number of DotPython module initialization attempts."
    );
    private static readonly Counter<long> InitializationFailures = Meter.CreateCounter<long>(
        "dotpython.module.initialization.failures",
        "{failure}",
        "Number of failed DotPython module initialization attempts."
    );
    private static readonly Counter<long> WarmupSuccesses = Meter.CreateCounter<long>(
        "dotpython.module.warmup.successes",
        "{warmup}",
        "Number of successful DotPython module warmups."
    );
    private static readonly Counter<long> WarmupFailures = Meter.CreateCounter<long>(
        "dotpython.module.warmup.failures",
        "{failure}",
        "Number of failed DotPython module warmups."
    );

    internal static void RecordInitializationAttempt(
        PythonModuleDefinition definition,
        int attempt
    ) => InitializationAttempts.Add(1, CreateTags(definition, attempt));

    internal static void RecordInitializationFailure(
        PythonModuleDefinition definition,
        int attempt,
        Exception exception
    ) => InitializationFailures.Add(1, CreateTags(definition, attempt, GetFailureCode(exception)));

    internal static void RecordWarmupSuccess(PythonModuleDefinition definition) =>
        WarmupSuccesses.Add(1, CreateTags(definition));

    internal static void RecordWarmupFailure(
        PythonModuleDefinition definition,
        Exception exception
    ) => WarmupFailures.Add(1, CreateTags(definition, failureCode: GetFailureCode(exception)));

    private static TagList CreateTags(
        PythonModuleDefinition definition,
        int? attempt = null,
        string? failureCode = null
    )
    {
        TagList tags = default;
        tags.Add("module.name", definition.Contract.ModuleName);
        tags.Add("module.state_policy", definition.Contract.StatePolicy.ToString());
        if (attempt is not null)
        {
            tags.Add("attempt", attempt.Value);
        }

        if (failureCode is not null)
        {
            tags.Add("failure.code", failureCode);
        }

        return tags;
    }

    private static string GetFailureCode(Exception exception) =>
        exception is DotPythonException dotPythonException
            ? dotPythonException.Code
            : exception.GetType().Name;
}
