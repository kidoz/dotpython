# DotPython benchmarks

This project measures the managed language pipeline without using CPython as an execution
backend. The initial suites separate tokenization, parsing, compilation, precompiled bytecode
execution, and source-to-execution costs.

`RuntimeAllocationBenchmarks` separates fixed VM startup, repeated constant loads, cached-range
and larger integer-loop execution, zero- and one-argument function frames/calls, and global
lookup. Global-lookup controls separate stable globals, reassigned globals, and builtin fallback.
Function-local whole-number and floating-point addition loops isolate adaptive arithmetic from
global lookup. Repeated less-than calls isolate whole-number and floating-point comparison while a
text comparison retains the generic-path control; their driver loop uses `!=` to avoid another
less-than site. These workloads attribute allocation trends; they are not intended to represent
application throughput.

`ComparisonSpecializationBenchmarks` is the matched throughput experiment for adaptive ordered
comparison dispatch. Its generic and specialized methods execute identical 10,000-call numeric
loops for `<`, `<=`, `>`, and `>=` under the same BenchmarkDotNet job invocation. Setup warms one
comparison site with its numeric family and saturates the other with text observations, so
BenchmarkDotNet can report a direct baseline ratio from adjacent, equivalent benchmark cases. Run
it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ComparisonSpecializationBenchmarks*'
```

`ManagedCallDispatchBenchmarks` attributes the marginal cost of the current managed function-call
and frame path. It compares an inline loop with an otherwise equivalent loop that makes 10,000
stable managed calls, separately for zero- and one-argument functions. Both paths reuse canonical
`None`, perform the same loop control, and return the same value. Run it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedCallDispatchBenchmarks*'
```

`ManagedCallSpecializationBenchmarks` is the matched throughput experiment for guarded managed
call dispatch. Both methods execute the same 10,000-call loop after setup deliberately saturates
one call site as generic and specializes the other for its stable managed-function identity. Run
it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedCallSpecializationBenchmarks*'
```

`ManagedFramePathBenchmarks` bounds the residual cost of an already-specialized managed call. Its
matched functions receive the same target as a local and execute equivalent 10,000-iteration
loops, excluding repeated global-target lookup. The measured delta still includes the local target
load and identity guard as well as argument transfer, explicit frame entry/return, and callee
execution. Run it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedFramePathBenchmarks*'
```

`ManagedReturnPathBenchmarks` compares 10,000 calls to a bare `return` with the semantically
equivalent explicit `return None` path. It attributes redundant constant-load/return dispatch
without changing frame or call behavior. Run it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedReturnPathBenchmarks*'
```

The opt-in opcode-pair profiler records dynamically executed, statically adjacent instructions
within each logical frame. It uses a separate profiled VM loop, so ordinary execution does not pay
an instrumentation branch. Print the twelve most frequent pairs for the managed-frame workloads
with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --profile-opcode-pairs
```

`ManagedReturnContinuationBenchmarks` is the matched control for consuming `Call -> StoreLocal`
through a callee-owned return continuation. Both methods execute the same prepared code and
specialized call site; the baseline disables only the continuation while the candidate skips the
result-stack round trip and caller-side store dispatch. Run it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedReturnContinuationBenchmarks*'
```

`ManagedReturnLocalBenchmarks` is the matched control for compiling `LoadLocal -> ReturnValue`
as one `ReturnLocal` superinstruction. Both methods execute equivalent compiler output through
specialized call sites; setup verifies the baseline pair and optimized opcode before measurement.
Run it with:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*ManagedReturnLocalBenchmarks*'
```

Run a short local runtime sample from the repository root:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- \
  --filter '*RuntimeBenchmarks*' --job short
```

Run all benchmarks with BenchmarkDotNet's normal measurement policy:

```sh
dotnet run -c Release --project benchmarks/DotPython.Benchmarks -- --filter '*'
```

Use Release builds without an attached debugger. Record the operating system, architecture,
.NET SDK/runtime, CPU, power policy, and benchmark commit with any retained result. Compare
results only on equivalent environments.

The benchmark results are observations, not compatibility or release claims. Establish
environment noise before adding regression thresholds. End-to-end CLI process startup, module
loading, public facade calls, worker round trips, and bulk-buffer transfer require separate
benchmarks as those features mature.
