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
}
