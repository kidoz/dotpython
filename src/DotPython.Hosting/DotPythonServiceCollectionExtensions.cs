using DotPython;
using DotPython.Contracts;
using DotPython.Hosting;
using DotPython.Runtime.Managed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers DotPython runtime hosting services.</summary>
public static class DotPythonServiceCollectionExtensions
{
    /// <summary>Adds the managed DotPython runtime and its module providers.</summary>
    public static IServiceCollection AddDotPythonManaged(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDotPythonModuleRuntime>(_ => new ManagedPythonModuleRuntime());
        services.TryAddSingleton<PerRuntimePythonModuleProvider>(
            provider => new PerRuntimePythonModuleProvider(
                provider.GetRequiredService<IDotPythonModuleRuntime>()
            )
        );
        services.TryAddScoped<PerSessionPythonModuleProvider>(
            provider => new PerSessionPythonModuleProvider(
                provider.GetRequiredService<IDotPythonModuleRuntime>()
            )
        );
        return services;
    }

    /// <summary>Adds one generated typed DotPython module client.</summary>
    public static IServiceCollection AddDotPythonModule<TService>(
        this IServiceCollection services,
        PythonModuleRegistration<TService> registration
    )
        where TService : class => services.AddDotPythonModule(registration, static _ => { });

    /// <summary>Adds one generated typed DotPython module client with hosting options.</summary>
    public static IServiceCollection AddDotPythonModule<TService>(
        this IServiceCollection services,
        PythonModuleRegistration<TService> registration,
        Action<DotPythonModuleHostingOptions> configure
    )
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new DotPythonModuleHostingOptions();
        configure(options);
        services.TryAddSingleton(registration);
        switch (registration.StatePolicy)
        {
            case PythonModuleStatePolicy.PerRuntime:
                services.TryAddSingleton(provider =>
                    registration.CreateClient(
                        provider.GetRequiredService<PerRuntimePythonModuleProvider>()
                    )
                );
                if (options.WarmUpOnHostStart)
                {
                    services.TryAddEnumerable(
                        ServiceDescriptor.Singleton<
                            IHostedService,
                            DotPythonModuleWarmupService<TService>
                        >()
                    );
                }

                break;
            case PythonModuleStatePolicy.PerSession:
                if (options.WarmUpOnHostStart)
                {
                    throw new InvalidOperationException(
                        "Per-session modules cannot be warmed at host startup because their state belongs to an explicit service scope."
                    );
                }

                services.TryAddScoped(provider =>
                    registration.CreateClient(
                        provider.GetRequiredService<PerSessionPythonModuleProvider>()
                    )
                );
                break;
            default:
                throw new NotSupportedException(
                    $"The DI host does not support the '{registration.StatePolicy}' module state policy."
                );
        }

        return services;
    }
}
