# DotPython benchmarks

This project measures the managed language pipeline without using CPython as an execution
backend. The initial suites separate tokenization, parsing, compilation, precompiled bytecode
execution, and source-to-execution costs.

`RuntimeAllocationBenchmarks` separates fixed VM startup, repeated constant loads, cached-range
and larger integer-loop execution, zero- and one-argument function frames/calls, and global
lookup. Global-lookup controls separate stable globals, reassigned globals, and builtin fallback.
Function-local whole-number and floating-point addition loops isolate adaptive arithmetic from
global lookup and retain a generic-path control. These workloads attribute allocation trends; they
are not intended to represent application throughput.

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
