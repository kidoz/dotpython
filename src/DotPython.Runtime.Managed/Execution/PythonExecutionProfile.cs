using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonExecutionProfile
{
    private const int OpCodeCount = (int)PythonOpCode.DeleteAttribute + 1;
    private readonly long[] _instructionCounts = new long[OpCodeCount];
    private readonly long[] _pairCounts = new long[OpCodeCount * OpCodeCount];
    private ProfilePosition[] _previousPositions = new ProfilePosition[4];

    internal void Record(
        int frameDepth,
        PreparedPythonCode code,
        int instructionIndex,
        PythonOpCode opCode
    )
    {
        if (frameDepth >= _previousPositions.Length)
        {
            Array.Resize(ref _previousPositions, checked(_previousPositions.Length * 2));
        }

        _instructionCounts[(int)opCode]++;
        ref var previous = ref _previousPositions[frameDepth];
        if (ReferenceEquals(previous.Code, code) && instructionIndex == previous.Index + 1)
        {
            _pairCounts[GetPairIndex(previous.OpCode, opCode)]++;
        }

        previous = new ProfilePosition(code, instructionIndex, opCode);
    }

    internal long GetInstructionCount(PythonOpCode opCode) => _instructionCounts[(int)opCode];

    internal long GetPairCount(PythonOpCode first, PythonOpCode second) =>
        _pairCounts[GetPairIndex(first, second)];

    internal IEnumerable<PythonInstructionPairCount> GetPairs() =>
        from first in Enum.GetValues<PythonOpCode>()
        from second in Enum.GetValues<PythonOpCode>()
        let count = GetPairCount(first, second)
        where count != 0
        orderby count descending, first, second
        select new PythonInstructionPairCount(first, second, count);

    private static int GetPairIndex(PythonOpCode first, PythonOpCode second) =>
        checked((int)first * OpCodeCount + (int)second);

    private readonly record struct ProfilePosition(
        PreparedPythonCode? Code,
        int Index,
        PythonOpCode OpCode
    );
}

internal readonly record struct PythonInstructionPairCount(
    PythonOpCode First,
    PythonOpCode Second,
    long Count
);
