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

    internal static SourceText CreateComparisonSpecializationSource(
        ComparisonOperandFamily operandFamily,
        OrderedComparisonOperation operation
    )
    {
        var ascendingOperands = operandFamily switch
        {
            ComparisonOperandFamily.WholeNumber => (Left: "10000", Right: "11000"),
            ComparisonOperandFamily.FloatingPoint => (Left: "10000.0", Right: "11000.0"),
            _ => throw new ArgumentOutOfRangeException(nameof(operandFamily)),
        };
        var operands = operation
            is OrderedComparisonOperation.LessThan
                or OrderedComparisonOperation.LessThanOrEqual
            ? ascendingOperands
            : (Left: ascendingOperands.Right, Right: ascendingOperands.Left);
        var sourceOperator = operation switch
        {
            OrderedComparisonOperation.LessThan => "<",
            OrderedComparisonOperation.LessThanOrEqual => "<=",
            OrderedComparisonOperation.GreaterThan => ">",
            OrderedComparisonOperation.GreaterThanOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return new SourceText(
            "def compare(left, right): return left "
                + sourceOperator
                + " right\n"
                + "def compare_values():\n"
                + "    current = 0\n"
                + "    value = False\n"
                + "    while current != 10000:\n"
                + "        value = compare("
                + operands.Left
                + ", "
                + operands.Right
                + ")\n"
                + "        current = current + 1\n"
                + "    return value\n",
            "comparison-specialization-benchmark.py"
        );
    }

    internal static SourceText CreateManagedCallDispatchSource(int argumentCount)
    {
        var (callee, inlineAssignment, call) = argumentCount switch
        {
            0 => ("def callee(): return None\n", "        value = None\n", "callee()"),
            1 => ("def callee(value): return value\n", "        value = value\n", "callee(value)"),
            _ => throw new ArgumentOutOfRangeException(nameof(argumentCount)),
        };
        return new SourceText(
            callee
                + "def inline_loop():\n"
                + "    current = 0\n"
                + "    value = None\n"
                + "    while current != 10000:\n"
                + inlineAssignment
                + "        current = current + 1\n"
                + "    return value\n"
                + "def call_loop():\n"
                + "    current = 0\n"
                + "    value = None\n"
                + "    while current != 10000:\n"
                + "        value = "
                + call
                + "\n"
                + "        current = current + 1\n"
                + "    return value\n",
            "managed-call-dispatch-benchmark.py"
        );
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
                RuntimeAllocationScenario.LargeIntegerLoop => "current = 10000\n"
                    + "while current < 11000:\n"
                    + "    current = current + 1\n",
                RuntimeAllocationScenario.LocalIntegerAddition => "def add_integers():\n"
                    + "    current = 10000\n"
                    + "    while current < 11000:\n"
                    + "        current = current + 1\n"
                    + "    return current\n"
                    + "add_integers()\n",
                RuntimeAllocationScenario.LocalFloatingPointAddition => "def add_floats():\n"
                    + "    current = 10000.0\n"
                    + "    while current < 11000.0:\n"
                    + "        current = current + 1.0\n"
                    + "    return current\n"
                    + "add_floats()\n",
                RuntimeAllocationScenario.IntegerLessThan => CreateLessThanCalls("10000", "11000"),
                RuntimeAllocationScenario.FloatingPointLessThan => CreateLessThanCalls(
                    "10000.0",
                    "11000.0"
                ),
                RuntimeAllocationScenario.TextLessThan => CreateLessThanCalls("'Dot'", "'Python'"),
                RuntimeAllocationScenario.FunctionCallsNoArguments => "global_value = 42\n"
                    + "def get_value(): return global_value\n"
                    + "current = 0\n"
                    + "while current < 1000:\n"
                    + "    value = get_value()\n"
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
                RuntimeAllocationScenario.MutableGlobalLookup => "global_value = 0\n"
                    + "current = 0\n"
                    + "while current < 1000:\n"
                    + "    value = global_value\n"
                    + "    global_value = global_value + 1\n"
                    + "    current = current + 1\n",
                RuntimeAllocationScenario.BuiltinLookup => "def lookup_builtin():\n"
                    + "    current = 0\n"
                    + "    while current < 1000:\n"
                    + "        value = print\n"
                    + "        current = current + 1\n"
                    + "lookup_builtin()\n",
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

    private static string CreateLessThanCalls(string left, string right) =>
        "def less_than(left, right): return left < right\n"
        + "def compare_values():\n"
        + "    current = 0\n"
        + "    while current != 1000:\n"
        + "        value = less_than("
        + left
        + ", "
        + right
        + ")\n"
        + "        current = current + 1\n"
        + "compare_values()\n";
}
