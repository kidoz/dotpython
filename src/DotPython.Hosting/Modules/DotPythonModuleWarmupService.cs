using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotPython.Hosting;

internal sealed class DotPythonModuleWarmupService<TService>(
    DotPythonModuleHostingRegistration<TService> configuration,
    PerRuntimePythonModuleProvider provider,
    ILoggerFactory loggerFactory
) : IHostedService
    where TService : class
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DotPython.Hosting.ModuleWarmup");

    private static readonly Action<ILogger, string, Exception?> WarmupStarting =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(6000, nameof(WarmupStarting)),
            "Warming DotPython module {ModuleName}."
        );

    private static readonly Action<ILogger, string, Exception?> WarmupSucceeded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(6001, nameof(WarmupSucceeded)),
            "Warmed DotPython module {ModuleName}."
        );

    private static readonly Action<ILogger, string, Exception?> WarmupFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(6002, nameof(WarmupFailed)),
            "Failed to warm DotPython module {ModuleName}."
        );

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var definition = configuration.Registration.Definition;
        var moduleName = definition.Contract.ModuleName;
        provider.ConfigureInitialization(definition, configuration.MaximumInitializationAttempts);
        WarmupStarting(_logger, moduleName, null);
        try
        {
            await provider.WarmUpAsync(definition, cancellationToken).ConfigureAwait(false);
            DotPythonHostingTelemetry.RecordWarmupSuccess(definition);
            WarmupSucceeded(_logger, moduleName, null);
        }
        catch (Exception exception)
        {
            DotPythonHostingTelemetry.RecordWarmupFailure(definition, exception);
            WarmupFailed(_logger, moduleName, exception);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed record DotPythonModuleHostingRegistration<TService>(
    PythonModuleRegistration<TService> Registration,
    int MaximumInitializationAttempts
)
    where TService : class;
