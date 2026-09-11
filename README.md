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
> `DotPython.Sdk` generates typed C# facades for single-module projects. A versioned worker
> boundary now loads and executes one pinned Stable-ABI conformance fixture through an experimental,
> hash-pinned symbol allowlist. This is not general native-extension support.

## Compatibility contract

- Targets the **Python 3.14** language surface through an explicit compatibility profile.
- Compiled artifacts must target Python 3.14. Artifacts stamped 3.15 must be rebuilt for this
  release; accepting a version stamp does not select separate language semantics.
- CPython is a **differential reference** for managed execution and is never an implicit fallback.
- CPython bytecode and arbitrary C-extension binaries are **unsupported by the managed runtime
  today**. One internal Stable-ABI fixture is executable only through its explicit worker option.
- A provider-neutral worker protocol and managed worker host are implemented. The optional CPython
  provider itself is not implemented or qualified.
- The managed interpreter is the semantic reference; any future JIT tier must fall back to it.
- Host/.NET access is **capability based**; arbitrary assembly loading and reflection are off by default.

The native-extension direction is layered: managed `abi3` and HPy work begins in workers, while
unchanged version-specific packages use an explicitly selected CPython worker provider. The first
managed `abi3` experiment is intentionally fixture-specific; no package or universal ABI
compatibility is qualified yet.

## Requirements

- [.NET SDK **10.0.301**](https://dotnet.microsoft.com/download) or later (pinned in `global.json`).
- Rust 1.85 or later with Cargo, rustfmt, and Clippy for the experimental Stable-ABI native build.
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
dotpython lint src/        # statically lint Python sources without executing them
dotpython -V | --version   # print implementation and target language version
dotpython -h | --help      # print usage
```

Exit codes follow familiar conventions: `0` success, `1` execution/diagnostic error,
`2` usage error, `130` cancelled. Interactive (REPL) mode is not yet implemented.

`wheel inspect` reports canonical filename and embedded tags, platform/libc/architecture,
free-threaded ABI selection, SHA-256, native archive entries, imported symbols when their ELF,
Mach-O, or PE tables are readable, and actionable incompatibility diagnostics. Classification
never loads or executes a library and does not change the managed runtime's CPython ABI support.

### Python linting

`DotPython.Lint` is an embeddable, managed linter for DotPython's current Python 3.14
parser profile. It shares the tokenizer and AST with the compiler and has no runtime
or external-tool dependency. It checks source without executing it. It does not yet
claim full Python syntax coverage, type checking, or automatic fixes.

```sh
dotnet run --project src/DotPython.Cli -- lint scripts/
dotnet run --project src/DotPython.Cli -- lint --select DPYL001,DPYL002 script.py
dotnet run --project src/DotPython.Cli -- lint --output-format json --stdin-filename buffer.py -
```

| Rule | Warning |
|---|---|
| `DPYL001` | Bare `except:` catches every exception, including interruption and exit signals. |
| `DPYL002` | A list, dictionary, or set literal used as a function/lambda default is shared between calls. |
| `DPYL003` | An asserted tuple containing a non-starred element is always truthy. |
| `DPYL004` | An imported binding has no reference in a visible lexical scope. |
| `DPYL005` | A name read has no visible lexical definition, builtin, or declared host global. |

Rules also visit nested definitions and expressions. Mutable-default checks cover
literal displays; constructor calls, comprehensions, and nested mutable objects
inside immutable defaults are outside this initial rule. Tuple assertions consisting
only of starred elements are not reported, since the resulting tuple may be empty.

Unused-import analysis distinguishes module, function, lambda, comprehension, and
class scopes; a same-spelled parameter or local does not use an outer import.
Defaults, decorators, annotations (including quoted forward references), and f/t-string
interpolations count as references. `global` and `nonlocal` redirect references to
their owning scopes. Attribute names such as `obj.os` do not count as uses of an
unrelated `os` import.

`DPYL004` preserves explicit re-exports (`from helpers import Item as Item`), names in
literal module `__all__` lists/tuples and their concatenations, `+=`, `append`, or
`extend` operations, and `__future__` imports. It conservatively skips imports inside
try statements, imports exposed as class attributes, and scopes potentially observed
through direct `exec`, `eval`, `globals`, `locals`, or `vars` calls. Unknown or escaped
`__all__` values preserve module imports. Ordinary conditional imports are checked;
a reference in either branch keeps all imports of the same lexical binding.

This analysis does not track which assignment reaches a read. Repeated imports or
assignments to a used binding are retained, as are unaliased dotted imports sharing a
used package name. Quoted annotations are inspected for name references without type
inference. If execution binding rejects the file, the semantic lint rules are skipped;
the existing compiler remains responsible for its required diagnostics. No import is
automatically removed; intentional side-effect imports can use a `DPYL004` suppression.

`DPYL005` checks ordinary reads and unquoted annotation names using the same semantic
model. It recognizes the [Python 3.14 builtin vocabulary](https://docs.python.org/3.14/library/functions.html),
including exceptions and common site helpers, and conventional module metadata names.
This catalogue does not guarantee that every builtin is implemented by the managed
runtime. Attributes and quoted annotation contents are not checked for undefined names.
The rule is lexical: assignments anywhere in the owning scope count as definitions,
so it does not detect reads before assignment, conditional initialization, or deleted
bindings. Invalid binding skips both semantic rules.

Dynamic namespaces receive conservative treatment: a direct `exec`, `eval`, `globals`,
`locals`, or `vars` call anywhere in the file skips `DPYL005` for the entire file,
including nested functions. Aliases and arbitrary namespace mutations are not inferred.
This can hide real typos, including when one of those function names is shadowed.
Unknown `__all__` contents alone do not disable undefined-name checks.

Declare globals supplied by a .NET host with `PythonLintOptions.KnownGlobals`, CLI
`--known-globals host,context`, or SDK `DotPythonLintKnownGlobals`. Names must be exact
Python identifiers accepted by the current parser; matching is case-sensitive and uses
mangled lookup names for private class identifiers. The CLI/SDK accept comma-separated
names, and repeated CLI options use the last value. These settings declare name
availability for linting; the embedding host must still supply the runtime values.

All five rules are enabled by default. `--select` replaces that set; `--ignore`
removes rules from it. Both accept comma-separated exact identifiers and reject
unknown identifiers. Repeated options use the last value. An empty CLI `--select ""`
disables lint rules. Parser diagnostics remain enabled, and a file with parser errors
does not receive lint warnings from its partial syntax tree.

Suppress individual warnings using a comment on the diagnostic's starting line:

```python
def collect(items=[]):  # dotpython: ignore[DPYL002] shared state is intentional
    return items
```

Multiple IDs can appear inside the brackets, separated by commas. A trailing
explanation is allowed. Directives inside string contents have no effect; malformed
directives and unknown suppression IDs are ignored. Suppressions never hide parser
errors. Multiline defaults and assertions use the line where the reported expression
starts. Configuration files, file-wide suppressions, and Ruff `noqa` directives are
not supported by this initial native linter.

Supply explicit files or directories; directory discovery includes `.py` and `.pyi`
files recursively, skipping `.git`, `.venv`, `venv`, `__pycache__`, `bin`, `obj`, and
symbolic-link entries. Explicit file paths bypass discovery filters. Use `--` before
paths starting with `-`. Stdin (`-`) must be the only input. Inputs are deduplicated
by full path and ordered ordinally; diagnostics are ordered by source offset and code.

Findings go to stdout; usage, I/O, and cancellation messages go to stderr. Lint exit
codes are `0` clean, `1` findings (including parser errors), `2` usage or input failure,
and `130` cancellation. JSON output is an array with `file`, `code`, `severity`,
`message`, `line`, `column`, `endLine`, and `endColumn`; positions are one-based,
columns count UTF-16 code units, and the end position is exclusive. A clean JSON run
returns `[]`. File decoding uses the CLI's supported Python source encodings.

Embedded callers can reference `DotPython.Lint` directly:

```csharp
var result = PythonLinter.Analyze(
    new SourceText("result = host.calculate(context)", "example.py"),
    new PythonLintOptions { KnownGlobals = ["host", "context"] },
    cancellationToken);
```

The API uses the `DotPython.Lint` and `DotPython.Language.Text` namespaces. Results
contain the original source and ordered diagnostics. Cancellation is cooperative
before/after parsing and during AST traversal; parsing and synchronous input reads
are not preemptible.

Source tooling can also call `PythonSymbolBinder.Analyze(parseResult.Module,
cancellationToken)` from `DotPython.Compiler.Binding`. The optional semantic model
exposes lexical symbols, per-occurrence references/declarations with source spans,
imports, export metadata, and binding diagnostics. Class-body fallback lookups are
explicit, and unresolved globals/builtins have a null symbol. `IsQuotedAnnotation`
distinguishes conservative string-content references from actual annotation name nodes.
`LookupName` exposes private-name mangling even when a reference has no symbol. Check the model's
diagnostics before consuming it. Normal compilation does not build this model.

SDK projects can opt into the same rules:

```xml
<PropertyGroup>
  <DotPythonLintEnabled>true</DotPythonLintEnabled>
  <DotPythonLintWarningsAsErrors>true</DotPythonLintWarningsAsErrors>
  <DotPythonLintSelect>DPYL001,DPYL002,DPYL003,DPYL004,DPYL005</DotPythonLintSelect>
  <DotPythonLintKnownGlobals>host,context</DotPythonLintKnownGlobals>
  <DotPythonLintIgnore>DPYL001</DotPythonLintIgnore>
</PropertyGroup>
```

Linting is disabled by default in the SDK; when enabled, findings are warnings unless
`DotPythonLintWarningsAsErrors` is true. An absent or empty SDK selection enables all
rules. Checks run on every enabled build, including when module generation is up to
date, so configuration changes take effect immediately. Required compiler and export
contract diagnostics retain their existing behavior.

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

Discovery captures bounded source bytes and decodes a module when it is imported. An unrelated
file with invalid source encoding therefore does not prevent startup. Script files and discovered
modules support UTF-8 (including a BOM), ASCII, and Latin-1 encoding declarations, with Python
newline normalization. Other source codecs are not yet supported.

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

An internal managed-only native-boundary simulator now exercises this lifecycle without loading a
library. Logical handles carry runtime identity, generation, and handle identity; borrowed, new,
stolen, immortal, and nullable reference transitions are explicit. New references are backed by
the existing scheduler-owned leases, so explicit disposal and forced-GC cleanup release on the
owning thread and shutdown preserves reverse registration order. The simulator also keeps a
separate native-style raised-error indicator and saves/restores it around bounded re-entrant
callbacks.

This lifecycle foundation does not load native code or change the compatibility contract. No
CPython ABI, HPy, Anyver, or NumPy execution support is enabled.

## Worker protocol foundation

`DotPython.Protocol`, `DotPython.Worker`, and `DotPython.Worker.Host` establish the worker-first
boundary required by the native-extension roadmap. The host and child negotiate protocol,
provider/runtime/environment identity, feature flags, message/output/session/concurrency limits,
and an explicit worker generation before accepting work. Four-byte little-endian length prefixes
bound source-generated JSON envelopes carrying correlation IDs, deadlines, cancellation, and
structured faults.

Worker protocol v3.0 reserves the retired experimental Stable-ABI request/response message IDs.
Qualified native modules execute only through ordinary managed source imports and the generic
native-object path; there is no direct module load/invoke/release RPC or worker module handle API.

The parent starts an explicitly configured executable without a shell, clears inherited environment
variables, and passes only allowlisted values and package roots. Sessions and logical object handles
are scoped to provider, worker identity, generation, and session. Clean disposal closes sessions and
drains the child; recycling, malformed protocol, crashes, and hard timeout invalidate the generation.
Hard timeout sends cooperative cancellation, waits a bounded grace period, and terminates the
process tree before a replacement is admitted.

The first transport uses redirected standard streams as a private framed channel. Protocol output
never shares the Python standard-output payload, which is returned inside a bounded response. This
contains crashes and enables hard termination, but it is not by itself a complete native-code
sandbox; platform OS resource and network enforcement remain separate work. No provider is selected
implicitly.

## Managed Stable-ABI experiments

`DotPython.Runtime.Native` loads checked-in conformance fixtures or exact pinned native entries
through an explicitly configured worker. The parent supplies a bounded catalog of absolute module
and manifest paths plus their SHA-256 values; every entry shares one pinned bridge identity. Before
execution, the worker freezes the catalog, rejects duplicate module names and artifact paths, and
validates bounded regular files, platform and architecture, the versioned manifest, `PyInit_*`, and
every required bridge and module export. Imports select entries by qualified module name and exact
path. Native addresses remain inside the dedicated native-owner lane.

The Anyver manifest records the immutable wheel and native-entry hashes and exactly 90 imported
Stable-ABI symbols. `inspect-anyver-wheel.sh` rejects wheel, entry-point, or import-surface drift.
Bridge ABI v6 exposes only the generic opaque-object surface for qualified modules: attribute
discovery, positional calls, primitive and sequence conversion, attribute/item access, display and
representation conversion, length, rich comparison, and explicit owner-thread release. The legacy
scalar module query/call helpers have been removed. The
worker can therefore import the unchanged Anyver Python wrapper
from the staged wheel and use its native exports without Anyver-specific native exports, worker
messages, or managed call methods. Package identity and process-pinned lifetime are manifest data;
the runtime path is package-neutral. Two independent conformance modules verify catalog routing and
failure isolation in one worker. The Anyver acceptance path covers comparisons, sorting, `Version`
construction/display/representation/properties/indexing, length, rich comparison, and dictionary
round trips. PyO3 heap-type and intern caches
are retained for the worker lifetime; transient module objects are released on logical-module
disposal and all remaining native state is reclaimed by worker termination.

The macOS ARM64 native CI lane downloads the exact PyPI wheel, verifies its pinned SHA-256 and
native symbol surface, and treats every skipped worker test as a failure. For local acceptance, set
`DOTPYTHON_ANYVER_WHEEL` to the pinned wheel when building `DotPython.WorkerTests`; the native
harness additionally accepts its prepared module through `ANYVER_MODULE`. The checked-in
`anyver-compatibility.json` records 1 successful unchanged-package import, 16 passing public checks,
and links `anyver-upstream-qualification.json`, which attempts every one of the 325 upstream pytest
node IDs through a managed pytest shim: all 325 pass. Native CI checks out the pinned source
revision, verifies the unchanged test-file hash, repeats pytest collection with pinned tool
versions, and submits the unchanged suite to the isolated worker. The supporting os.path surface is
capability-scoped to registered module search roots and pickle provides in-process round-trip
tokens rather than the CPython wire format; both restrictions are documented per feature. No
skipped or failed test is promoted to a pass. This remains a package-, artifact-, and platform-specific partial result;
`SupportsCpythonAbi` remains false, and this does not claim NumPy, HPy, or general `abi3`
compatibility.

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

A C# project can use an ordinary project reference and register the generated interface as a typed
service:

```xml
<ProjectReference Include="../PricingRules/PricingRules.dpyproj" />
```

```csharp
await using var services = new ServiceCollection()
    .AddDotPythonManaged()
    .AddDotPythonModule(PricingModule.Registration)
    .BuildServiceProvider();

var rules = services.GetRequiredService<IPricingModule>();
BigInteger total = await rules.AddAsync(left, right, cancellationToken);
```

The generated `PricingModule.LoadAsync(runtime, cancellationToken)` API remains available for
tools and applications that prefer explicit low-level ownership.

Generic Host applications can load and validate required per-runtime modules during startup:

```csharp
builder.Services.AddDotPythonManaged().AddDotPythonModule(
    PricingModule.Registration,
    options =>
    {
        options.WarmUpOnHostStart = true;
        options.MaximumInitializationAttempts = 3;
    });
```

Initialization uses one shared task per module. The default is one attempt; an explicitly configured
maximum (up to ten, including the first attempt) retries structured `DotPythonException` load
failures sequentially inside that task. A final failure remains sticky for the provider lifetime,
and canceling one caller does not cancel initialization needed by other callers.

Generic Host warm-up logs event IDs `6000` (starting), `6001` (succeeded), and `6002` (failed).
The `DotPython.Hosting` meter publishes these counters with module-name and state-policy tags:

- `dotpython.module.initialization.attempts`
- `dotpython.module.initialization.failures`
- `dotpython.module.warmup.successes`
- `dotpython.module.warmup.failures`

Modules compiled with `<DotPythonModuleStatePolicy>PerSession</DotPythonModuleStatePolicy>` are
registered as scoped services. Each DI scope receives distinct module globals while sharing the
host-owned managed runtime and scheduler. These logical sessions are state scopes, not security
boundaries.

Small applications that do not use DI can keep the same explicit lifetime ownership with one
object:

```csharp
await using var python = DotPythonHost.CreateManaged();
var pricing = python.GetModule(PricingModule.Registration);
BigInteger total = await pricing.CalculateTotalAsync(left, right, cancellationToken);
```

The SDK is not published yet. The build-integration suite packs the SDK and runtime package graph
into an isolated local feed. It proves both C# `ProjectReference` and package-only consumption,
embedded-resource execution, incremental reuse, and clean rebuild equivalence. The initial SDK
accepts one synchronous, positional, scalar-only module.

A runnable source-tree example is available in
[`samples/DotPython.ProjectReference`](samples/DotPython.ProjectReference/README.md). It places the
`.dpyproj` Python library and its C# consumer in this solution and runs with:

```sh
dotnet run --project samples/DotPython.ProjectReference/Consumer/Consumer.csproj
```

The scoped-state example at [`samples/DotPython.PerSession`](samples/DotPython.PerSession/README.md)
shows a stateful generated module shared within one DI scope and isolated across scopes:

```sh
dotnet run --project samples/DotPython.PerSession/Consumer/Consumer.csproj
```

## Project layout

| Project | Purpose |
|---|---|
| `src/DotPython.Language` | Tokenizer, AST, syntax, source text, and diagnostics. |
| `src/DotPython.ParserGenerator` | PEG grammar and generated parser. |
| `src/DotPython.Lint` | Static Python lint rules, selection, suppressions, and embeddable analysis. |
| `src/DotPython.Compiler` | Symbol/scope binding and DotPython bytecode compilation. |
| `src/DotPython.Runtime.Managed` | Managed stack VM, object model, and execution engine. |
| `src/DotPython.Abstractions` | Backend-independent public API surface. |
| `src/DotPython.Hosting` | Embedded hosting, runtime/session lifecycle, and DI. |
| `src/DotPython.Interop` | Static `.pyi` contracts, value conversion, and the capability-limited .NET bridge. |
| `src/DotPython.StdLib` | Managed / pure-Python standard-library surface. |
| `src/DotPython.Cli` | `dotpython` command-line front end. |
| `src/DotPython.Build.Tasks` | Deterministic out-of-process module compiler and C# facade generator. |
| `src/DotPython.Sdk` | Additive MSBuild SDK props, targets, and package layout. |
| `src/DotPython.Protocol` | Versioned, bounded worker envelopes, framing, handshake, and faults. |
| `src/DotPython.Runtime.Native` | Experimental manifest-bound Stable-ABI fixture loader and logical module boundary. |
| `src/DotPython.Worker` | Worker policy, process lifecycle, sessions, recycling, and logical handles. |
| `src/DotPython.Worker.Host` | Executable managed worker host and test-only failure injection. |
| `native/dotpython-abi3` | Minimal Stable-ABI bridge, pinned fixture, manifest generator, and native harness. |
| `benchmarks/DotPython.Benchmarks` | Managed front-end, compiler, and runtime performance baselines. |
| `samples/DotPython.ProjectReference` | Runnable `.dpyproj` library referenced and called from C#. |
| `samples/DotPython.PerSession` | Runnable stateful `.dpyproj` registered as a scoped C# service. |

## Development

```sh
just            # list available tasks
just format     # format C#, project files, and native Rust sources
just native-format # format only the native Stable-ABI workspace with rustfmt
just native-lint   # check native formatting and run Clippy
just native-test   # build, verify, test, and stage native artifacts
just parser-generate # regenerate the checked-in parser from the pinned PEG subset
just parser-check    # verify deterministic parser regeneration has no drift
just lint       # check managed/native formatting and analyzers, then build Release
just run -- ... # run the CLI
```

Cargo is the authoritative native build graph:

```sh
cargo fmt --manifest-path native/dotpython-abi3/Cargo.toml --all -- --check
cargo clippy --manifest-path native/dotpython-abi3/Cargo.toml --workspace --all-targets -- -D warnings
cargo test --manifest-path native/dotpython-abi3/Cargo.toml --workspace
native/dotpython-abi3/build.sh build/native-abi3
```

The checked-in `build.sh` is the authoritative verification and explicit-staging wrapper used by
MSBuild. AddressSanitizer and UndefinedBehaviorSanitizer require a separate nightly Rust workflow;
the stable workflow relies on Rust checks, Clippy, symbol-boundary checks, and harness leak
accounting.

Build settings (`Directory.Build.props`) enforce C# 14, nullable reference types,
`TreatWarningsAsErrors`, all analyzers enabled, and deterministic builds.

## Testing

```sh
dotnet test DotPython.sln
```

| Test project | Focus |
|---|---|
| `tests/DotPython.ParserTests` | Tokenizer and parser behavior. |
| `tests/DotPython.LintTests` | Lint rules, source spans, suppressions, selection, and cancellation. |
| `tests/DotPython.CompilerTests` | Binding and bytecode compilation. |
| `tests/DotPython.RuntimeTests` | Managed VM execution. |
| `tests/DotPython.InteropTests` | Static export contracts and Python-to-CLR type mapping. |
| `tests/DotPython.DifferentialTests` | Behavior compared against the CPython reference. |
| `tests/DotPython.PackageCompatibilityTests` | Package/language compatibility matrix. |
| `tests/DotPython.BuildIntegrationTests` | `.dpyproj` SDK packaging, ProjectReference, incremental, and runtime execution. |
| `tests/DotPython.WorkerTests` | Framing, handshake, process lifecycle, limits, cancellation, crash, timeout, and recycling. |

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
