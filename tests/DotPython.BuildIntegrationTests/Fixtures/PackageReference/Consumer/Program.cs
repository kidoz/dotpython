using System;
using System.Numerics;
using Generated.Pricing;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddDotPythonManaged().AddDotPythonModule(PricingModule.Registration);
await using var serviceProvider = services.BuildServiceProvider();
var pricing = serviceProvider.GetRequiredService<IPricingModule>();

Console.WriteLine(await pricing.AddAsync(new BigInteger(20), new BigInteger(22)));
