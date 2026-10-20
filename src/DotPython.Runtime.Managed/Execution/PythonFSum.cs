namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// `math.fsum`'s algorithm: the correctly-rounded sum of a run of doubles, by Shewchuk's
/// partials — the same algorithm CPython's implementation uses. The statistics module
/// leans on it for its float-only functions.
/// </summary>
internal static class PythonFSum
{
    /// <summary>The sum, or NaN when the run holds a NaN.</summary>
    internal static double Sum(IEnumerable<double> values)
    {
        var partials = new List<double>();
        var hasNan = false;
        var positiveInfinity = false;
        var negativeInfinity = false;
        foreach (var value in values)
        {
            if (double.IsNaN(value))
            {
                hasNan = true;
                continue;
            }
            if (double.IsPositiveInfinity(value))
            {
                positiveInfinity = true;
                continue;
            }
            if (double.IsNegativeInfinity(value))
            {
                negativeInfinity = true;
                continue;
            }
            Add(partials, value);
        }
        if (positiveInfinity && negativeInfinity)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "-inf + inf in fsum",
                default,
                "ValueError"
            );
        }
        if (hasNan)
        {
            return double.NaN;
        }
        if (positiveInfinity)
        {
            return double.PositiveInfinity;
        }
        if (negativeInfinity)
        {
            return double.NegativeInfinity;
        }
        var total = 0.0;
        foreach (var partial in partials)
        {
            total += partial;
        }
        return total;
    }

    /// <summary>Adds one value to the running exact partials.</summary>
    internal static void Add(List<double> partials, double value)
    {
        var x = value;
        var index = 0;
        for (var slot = 0; slot < partials.Count; slot++)
        {
            var y = partials[slot];
            if (Math.Abs(x) < Math.Abs(y))
            {
                (x, y) = (y, x);
            }
            var high = x + y;
            var low = y - (high - x);
            if (low != 0.0)
            {
                partials[index] = low;
                index++;
            }
            x = high;
        }
        partials.RemoveRange(index, partials.Count - index);
        partials.Add(x);
    }
}
