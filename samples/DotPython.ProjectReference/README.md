# DotPython project reference sample

This sample demonstrates the F#-like build and reference model for DotPython:

- `PricingRules.dpyproj` compiles `pricing.py` into a managed assembly.
- `pricing.pyi` declares the typed public API exposed to .NET callers.
- `Consumer.csproj` references the DotPython project with an ordinary `ProjectReference`.
- The console application creates one explicit `DotPythonHost` lifetime owner and retrieves the
  generated module like a normal typed service.
- ASP.NET Core and Generic Host applications can instead use
  `AddDotPythonManaged().AddDotPythonModule(PricingModule.Registration)` for DI-managed ownership.

Run it from the repository root:

```sh
dotnet run --project samples/DotPython.ProjectReference/Consumer/Consumer.csproj
```

The expected output is `126`.

The sample imports `DotPython.Sdk` directly from this repository because the SDK package has not
been published yet. A packaged project instead uses the additive SDK declaration documented in the
root README and pins its version through `global.json`.
