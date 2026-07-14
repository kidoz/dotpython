# DotPython

[![Language: C#](https://img.shields.io/badge/language-C%23%2014-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A managed implementation of Python for **.NET 10 / C# 14**. DotPython owns the full
execution pipeline — tokenizer, PEG parser, AST, symbol binder, bytecode compiler, and a
managed stack VM — and runs Python **without loading or hosting CPython**.

DotPython is usable as a command-line application, as an embedded scripting/runtime service
inside a .NET solution, and as an early SDK-style `.dpyproj` project language whose compiled
libraries can be referenced from C# and other managed languages.

> **Status:** early, active development. Today the CLI executes a growing managed subset of the
> language (literals, names, assignment, arithmetic, calls, control flow, and functions). The
> compiler also emits deterministic `.dpyc` module artifacts, the interop layer statically
> compiles an initial `.pyi` subset into typed CLR export contracts, and the prototype
> `DotPython.Sdk` generates typed C# facades for single-module projects.

## Compatibility contract

- Targets the **Python 3.14** language surface through an explicit compatibility profile.
- CPython is a **differential reference only** — not an execution dependency or fallback.
- CPython bytecode and CPython C-extension binaries are **unsupported**.
- The managed interpreter is the semantic reference; any future JIT tier must fall back to it.
- Host/.NET access is **capability based**; arbitrary assembly loading and reflection are off by default.

## Requirements

- [.NET SDK **10.0.301**](https://dotnet.microsoft.com/download) or later (pinned in `global.json`).
- Optionally [`just`](https://github.com/casey/just) for the developer task shortcuts below.

## Getting started

```sh
# Restore, format-check, and build with analyzers and warnings-as-errors
just lint

# Run the CLI (arguments after `--` are passed through unchanged)
just run -- -c "print(1 + 2)"
```

Without `just`, invoke the CLI directly:

```sh
dotnet run --project src/DotPython.Cli/DotPython.Cli.csproj -- -c "print(1 + 2)"
```

### CLI usage

```text
dotpython -c command      # execute a Python snippet
dotpython -                # read and execute a program from stdin
dotpython script.py        # execute a Python source file
dotpython -V | --version   # print implementation and target language version
dotpython -h | --help      # print usage
```

Exit codes follow familiar conventions: `0` success, `1` execution/diagnostic error,
`2` usage error, `130` cancelled. Interactive (REPL) mode is not yet implemented.

## Typed module contracts

DotPython can parse typed module stubs without importing or executing Python:

```python
from decimal import Decimal
from contracts import OrderDto

def calculate(order: OrderDto, discount: Decimal | None = ...) -> Decimal: ...
async def validate(order: OrderDto) -> list[str]: ...
```

The initial contract mapper supports `None`, `bool`, arbitrary-size `int`, `float`, `str`,
`bytes`, selected `decimal`/`uuid`/`datetime` types, nullable `T | None`/`Optional[T]`, and
read-only list/dictionary shapes. Referenced DTO types require an explicit Python-to-CLR mapping;
DotPython does not load assemblies or evaluate annotations while parsing contracts. Contracts are
persisted as deterministic, versioned JSON for build tooling and generated facades.

## DotPython project references

The prototype `DotPython.Sdk` compiles one `.py` source and matching `.pyi` contract before the
normal C# `CoreCompile` boundary. It embeds the deterministic artifact and contract JSON, then
compiles an abstraction-only typed facade into the resulting managed assembly.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="DotPython.Sdk" />

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>PricingRules</RootNamespace>
    <DotPythonModuleName>pricing</DotPythonModuleName>
    <DotPythonClrTypeName>PricingModule</DotPythonClrTypeName>
  </PropertyGroup>

  <ItemGroup>
    <PythonCompile Include="pricing.py" />
    <PythonContract Include="pricing.pyi" />
  </ItemGroup>
</Project>
```

A C# project can use an ordinary project reference and bind the generated facade to an explicitly
owned runtime:

```xml
<ProjectReference Include="../PricingRules/PricingRules.dpyproj" />
```

```csharp
await using var rules = await PricingModule.LoadAsync(runtime, cancellationToken);
BigInteger total = await rules.AddAsync(left, right, cancellationToken);
```

The SDK is not published yet. The build-integration suite packs it into an isolated local feed and
proves restore, C# `ProjectReference`, embedded-resource execution, incremental reuse, and clean
rebuild equivalence. The initial SDK accepts one synchronous, positional, scalar-only module.

## Project layout

| Project | Purpose |
|---|---|
| `src/DotPython.Language` | Tokenizer, AST, syntax, source text, and diagnostics. |
| `src/DotPython.ParserGenerator` | PEG grammar and generated parser. |
| `src/DotPython.Compiler` | Symbol/scope binding and DotPython bytecode compilation. |
| `src/DotPython.Runtime.Managed` | Managed stack VM, object model, and execution engine. |
| `src/DotPython.Abstractions` | Backend-independent public API surface. |
| `src/DotPython.Hosting` | Embedded hosting, runtime/session lifecycle, and DI. |
| `src/DotPython.Interop` | Static `.pyi` contracts, value conversion, and the capability-limited .NET bridge. |
| `src/DotPython.StdLib` | Managed / pure-Python standard-library surface. |
| `src/DotPython.Cli` | `dotpython` command-line front end. |
| `src/DotPython.Build.Tasks` | Deterministic out-of-process module compiler and C# facade generator. |
| `src/DotPython.Sdk` | Additive MSBuild SDK props, targets, and package layout. |
| `benchmarks/DotPython.Benchmarks` | Managed front-end, compiler, and runtime performance baselines. |

## Development

```sh
just            # list available tasks
just format     # format C# and project files with the pinned CSharpier version
just lint       # check formatting + build Release with analyzers as errors
just run -- ... # run the CLI
```

Build settings (`Directory.Build.props`) enforce C# 14, nullable reference types,
`TreatWarningsAsErrors`, all analyzers enabled, and deterministic builds.

## Testing

```sh
dotnet test DotPython.sln
```

| Test project | Focus |
|---|---|
| `tests/DotPython.ParserTests` | Tokenizer and parser behavior. |
| `tests/DotPython.CompilerTests` | Binding and bytecode compilation. |
| `tests/DotPython.RuntimeTests` | Managed VM execution. |
| `tests/DotPython.InteropTests` | Static export contracts and Python-to-CLR type mapping. |
| `tests/DotPython.DifferentialTests` | Behavior compared against the CPython reference. |
| `tests/DotPython.PackageCompatibilityTests` | Package/language compatibility matrix. |
| `tests/DotPython.BuildIntegrationTests` | `.dpyproj` SDK packaging, ProjectReference, incremental, and runtime execution. |

## Benchmarking

Run a short managed-runtime sample in Release mode:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*RuntimeBenchmarks*' --job short
```

The benchmark project reports time and managed allocations separately for tokenization, parsing,
compilation, precompiled artifact execution, and end-to-end source execution. Treat results as
machine-specific observations; do not compare results collected on different hardware or runtime
configurations.

## License

Released under the [MIT License](LICENSE).
