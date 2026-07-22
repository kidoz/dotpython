using System.Numerics;
using DotPython.Hosting;
using DotPython.Samples.PricingRules;

await using var python = DotPythonHost.CreateManaged();
var pricing = python.GetModule(PricingModule.Registration);

var total = await pricing.CalculateTotalAsync(new BigInteger(21), new BigInteger(6));

Console.WriteLine(total);
