using System.Globalization;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Benchmarks;

internal static class BenchmarkPrograms
{
    internal static SourceText CreateFrontEndSource(int functionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(functionCount);

        var source = new StringBuilder(functionCount * 160);
        for (var index = 0; index < functionCount; index++)
        {
            source
                .Append("def calculate_")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append("(value):\n")
                .Append("    result = value + 1\n")
                .Append("    while result < 100:\n")
                .Append("        result = result + 3\n")
                .Append("    if result > 100:\n")
                .Append("        return result\n")
                .Append("    return value\n");
        }

        return new SourceText(source.ToString(), "front-end-benchmark.py");
    }

    internal static SourceText CreateRuntimeSource(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        var source =
            "def calculate(limit):\n"
            + "    total = 0\n"
            + "    current = 0\n"
            + "    while current < limit:\n"
            + "        total = total + current * 2\n"
            + "        current = current + 1\n"
            + "    return total\n"
            + "result = calculate("
            + iterations.ToString(CultureInfo.InvariantCulture)
            + ")\n";

        return new SourceText(source, "runtime-benchmark.py");
    }

    internal static SourceText CreateAllocationSource(RuntimeAllocationScenario scenario) =>
        new(
            scenario switch
            {
                RuntimeAllocationScenario.Empty => string.Empty,
                RuntimeAllocationScenario.Constants => CreateConstantLoads(),
                RuntimeAllocationScenario.IntegerLoop => "current = 0\n"
                    + "while current < 1000:\n"
                    + "    current = current + 1\n",
                RuntimeAllocationScenario.FunctionCalls =>
                    "def increment(value): return value + 1\n"
                        + "current = 0\n"
                        + "while current < 1000:\n"
                        + "    current = increment(current)\n",
                RuntimeAllocationScenario.GlobalLookup => "global_value = 42\n"
                    + "current = 0\n"
                    + "while current < 1000:\n"
                    + "    value = global_value\n"
                    + "    current = current + 1\n",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            },
            "allocation-benchmark.py"
        );

    private static string CreateConstantLoads()
    {
        var source = new StringBuilder(1_100);
        for (var index = 0; index < 100; index++)
        {
            source.Append("value = 42\n");
        }

        return source.ToString();
    }
}
