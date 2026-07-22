using DotPython.Samples.SessionCounter;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddDotPythonManaged().AddDotPythonModule(SessionCounterModule.Registration);
await using var serviceProvider = services.BuildServiceProvider(
    new ServiceProviderOptions { ValidateScopes = true }
);

await using var firstScope = serviceProvider.CreateAsyncScope();
var firstCounter = firstScope.ServiceProvider.GetRequiredService<ISessionCounterModule>();
var firstValue = await firstCounter.NextValueAsync();
var secondValue = await firstCounter.NextValueAsync();

await using var secondScope = serviceProvider.CreateAsyncScope();
var secondCounter = secondScope.ServiceProvider.GetRequiredService<ISessionCounterModule>();
var isolatedValue = await secondCounter.NextValueAsync();

Console.WriteLine($"{firstValue},{secondValue},{isolatedValue}");
