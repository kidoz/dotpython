# DotPython per-session DI sample

This sample demonstrates stateful Python code registered as a normal scoped C# service:

- `SessionCounter.dpyproj` declares `PerSession` state.
- repeated resolutions and calls in one DI scope share Python module globals;
- another DI scope receives independent module globals;
- the scopes are logical state boundaries, not security boundaries.

Run it from the repository root:

```sh
dotnet run --project samples/DotPython.PerSession/Consumer/Consumer.csproj
```

The expected output is `1,2,1`.
