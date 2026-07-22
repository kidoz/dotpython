using Microsoft.Extensions.Hosting;

namespace DotPython.Hosting;

internal sealed class DotPythonModuleWarmupService<TService>(
    PythonModuleRegistration<TService> registration,
    PerRuntimePythonModuleProvider provider
) : IHostedService
    where TService : class
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        provider.WarmUpAsync(registration.Definition, cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
