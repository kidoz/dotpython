using System;
using System.Numerics;
using DotPython.Runtime.Managed;
using Generated.Pricing;

await using var runtime = new ManagedPythonModuleRuntime();
await using var pricing = await PricingModule.LoadAsync(runtime);
var result = await pricing.AddAsync(new BigInteger(20), new BigInteger(22));
Console.WriteLine(result);
