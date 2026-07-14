# DotPython

[![Language: C#](https://img.shields.io/badge/language-C%23%2014-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A managed implementation of Python for **.NET 10 / C# 14**. DotPython owns the full
execution pipeline — tokenizer, PEG parser, AST, symbol binder, bytecode compiler, and a
managed stack VM — and runs Python **without loading or hosting CPython**.

DotPython is usable as a command-line application, as an embedded scripting/runtime service
inside a .NET solution, and (per the roadmap) as an SDK-style `.dpyproj` project language whose
compiled libraries can be referenced from C# and other managed languages.

> **Status:** early, active development. Today the CLI executes a growing managed subset of the
> language (literals, names, assignment, arithmetic, calls, control flow, and functions). The
> compiler also emits deterministic `.dpyc` module artifacts, and the interop layer statically
> compiles an initial `.pyi` subset into typed CLR export contracts.

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
DotPython does not load assemblies or evaluate annotations while parsing contracts. Contracts can
be persisted as deterministic, versioned JSON for the future build SDK and facade generator.

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

## License

Released under the [MIT License](LICENSE).
