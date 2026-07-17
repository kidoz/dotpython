# DotPython

[![Language: C#](https://img.shields.io/badge/language-C%23%2014-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A managed implementation of Python for **.NET 10 / C# 14**. DotPython owns the full
execution pipeline — tokenizer, PEG parser, AST, symbol binder, bytecode compiler, and a
managed stack VM. The default runtime runs Python **without loading or hosting CPython**;
an optional, explicitly selected CPython worker provider is planned for unchanged native packages.

DotPython is usable as a command-line application, as an embedded scripting/runtime service
inside a .NET solution, and as an early SDK-style `.dpyproj` project language whose compiled
libraries can be referenced from C# and other managed languages.

> **Status:** early, active development. Today the CLI executes a growing managed subset of the
> language (literals, names, assignment, arithmetic, calls, control flow, functions, explicit
> exceptions, and managed package imports). The
> compiler also emits deterministic `.dpyc` module artifacts, the interop layer statically
> compiles an initial `.pyi` subset into typed CLR export contracts, and the prototype
> `DotPython.Sdk` generates typed C# facades for single-module projects.

## Compatibility contract

- Targets the **Python 3.14** language surface through an explicit compatibility profile.
- CPython is a **differential reference** for managed execution and is never an implicit fallback.
- CPython bytecode and C-extension binaries are **unsupported by the managed runtime today**.
- An optional CPython worker provider is accepted architecture but is not yet implemented or
  qualified.
- The managed interpreter is the semantic reference; any future JIT tier must fall back to it.
- Host/.NET access is **capability based**; arbitrary assembly loading and reflection are off by default.

The native-extension direction is layered: managed `abi3` and HPy work begins in workers, while
unchanged version-specific packages use an explicitly selected CPython worker provider. None of
these executable native capabilities is implemented or qualified yet.

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
dotpython wheel inspect x.whl # classify a wheel without loading native code
dotpython -V | --version   # print implementation and target language version
dotpython -h | --help      # print usage
```

Exit codes follow familiar conventions: `0` success, `1` execution/diagnostic error,
`2` usage error, `130` cancelled. Interactive (REPL) mode is not yet implemented.

`wheel inspect` reports canonical filename and embedded tags, platform/libc/architecture,
free-threaded ABI selection, SHA-256, native archive entries, imported symbols when their ELF,
Mach-O, or PE tables are readable, and actionable incompatibility diagnostics. Classification
never loads or executes a library and does not change the managed runtime's CPython ABI support.

## Managed modules and packages

Embedded callers can provide an immutable catalog of source modules and packages. A package is a
catalog entry that has registered dot-separated child modules. Each engine owns its module objects
and initialization cache; imports execute in the calling VM and therefore share its cancellation
token, output, and instruction limit.

```csharp
var modules = new Dictionary<string, SourceText>
{
    ["helpers"] = new("from . import values", "helpers/__init__.py"),
    ["helpers.values"] = new("answer = 42", "helpers/values.py"),
};
var engine = new ManagedPythonEngine(modules);
engine.Execute("import helpers.values; print(helpers.values.answer)", "main.py", output);
```

The current slice supports dotted absolute imports, explicit packages, relative `from` imports,
aliases, parenthesized import lists, submodule fallback, and module attribute reads. Catalog size,
source size, module-name length, and active import depth are bounded. Every dotted child requires an
explicit parent-package source entry.

An engine can instead take an ordered set of package roots. Startup discovers regular packages
from `__init__.py`, source modules, validated DotPython `.dpyc` artifacts, and top-level
`*.dist-info/METADATA` records. The snapshot is immutable after construction; the first configured
root wins across roots, while ambiguous source/artifact identities within one root are rejected.

```csharp
var engine = new ManagedPythonEngine(
    new ManagedModuleDiscoveryOptions
    {
        SearchPaths = ["/opt/my-application/python"],
    }
);
engine.Execute("import helpers.values; print(helpers.values.answer)", "main.py", output);
```

The CLI applies the same discovery contract to the script directory, or to the current directory
for `-c` and standard-input execution. A minimal runtime-owned `importlib.metadata.version()` reads
the startup metadata snapshot, including normalized distribution names. Discovery has fixed entry,
depth, per-file, and aggregate payload limits, uses strict UTF-8, and does not traverse symbolic
links or reparse points.

Namespace packages, wheel/zip imports, wildcard imports, reload, and import hooks are not
implemented. Native `.so` and `.pyd` files are recognized but never loaded; importing one produces
the actionable `DPY4027` unsupported-native-extension diagnostic. The managed runtime currently
reports an empty executable native-extension capability list.

## Managed exceptions

The managed compiler and VM support explicit `raise`, including bare re-raise and
`raise ... from ...`, plus `try` / `except` / `else` / `finally`. Handlers can match the
implemented built-in exception hierarchy, tuples of exception types, or a final bare clause, and
can bind an exception with `as`. Exception propagation crosses managed function and module frames;
uncaught explicit exceptions produce `DPY4031` at the original raise span.

`finally` executes for normal completion, returns, explicit exceptions, existing runtime faults,
cancellation, and the normal instruction limit. Deferred cleanup has its own fixed instruction
budget so cancellation or a resource-limit failure cannot open an unbounded cleanup path.

Language-level VM operation failures are converted into catchable built-in exceptions such as
`TypeError`, `NameError`, `ZeroDivisionError`, `LookupError`, `AttributeError`, and `ImportError`.
The VM keeps a private raised-error indicator, separate from the exception currently handled by an
`except` block, so callback bridges can save and restore propagating error state. If a converted
operation remains uncaught or is re-raised, its original `DPY4xxx` code, message, and source span
remain the host-facing diagnostic.

Cancellation, instruction limits, exception-block limits, deferred-cleanup limits, and VM
invariant failures are host/runtime control signals and cannot be swallowed by Python handlers.
User-defined exception classes, exception groups and `except*`, `sys.exception()`, public traceback
objects, and exact deletion of an `except ... as` target remain follow-up work. The internal error
indicator does not implement or enable CPython's native error ABI.

## Runtime ownership and shutdown

`ManagedPythonModuleRuntime` serializes module loading and invocation onto one dedicated owning
thread. Normal work admission is limited to 1,024 pending calls. Runtime-owned logical resource
leases are limited to 4,096 registrations; explicit disposal schedules an exactly-once release on
the owner thread, while a managed finalizer can only enqueue the same release and never executes it
directly.

Runtime disposal rejects new work, drains work that was already admitted, releases remaining
resources in reverse registration order, clears runtime module state, and then stops the owning
thread. Cancellation remains cooperative after a call begins. A synchronous host callback can
re-enter the same runtime inline on its owning thread, with active and explicit cancellation linked
and nesting limited to 64 calls. Cross-runtime entry from an owning thread, execution during
resource/finalization cleanup, and asynchronous callback suspension are rejected to avoid hidden
queue waits and deadlocks.

This lifecycle foundation does not load native code or change the compatibility contract. No
CPython ABI, HPy, Anyver, or NumPy execution support is enabled.

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
just parser-generate # regenerate the checked-in parser from the pinned PEG subset
just parser-check    # verify deterministic parser regeneration has no drift
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
