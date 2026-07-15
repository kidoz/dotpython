using System.Numerics;
using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PreparedPythonCode
{
    private const byte AdaptiveWarmupThreshold = 8;
    private readonly AdaptiveNumericCache[] _binaryAddCaches;
    private readonly int[] _binaryAddCacheIndexes;
    private readonly AdaptiveCallCache[] _callCaches;
    private readonly int[] _callCacheIndexes;
    private readonly Dictionary<string, int> _closureCellIndexes;
    private readonly PythonValue?[] _constants;
    private readonly PreparedPythonCode?[] _functionCodes;
    private readonly GlobalLoadCache[] _globalLoadCaches;
    private readonly int[] _globalLoadCacheIndexes;
    private readonly int[] _localCellIndexes;
    private readonly AdaptiveNumericCache[] _orderedComparisonCaches;
    private readonly int[] _orderedComparisonCacheIndexes;

    private PreparedPythonCode(PythonCodeObject definition)
    {
        Definition = definition;
        _binaryAddCacheIndexes = new int[definition.Instructions.Count];
        _binaryAddCaches = new AdaptiveNumericCache[
            definition.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.BinaryAdd
            )
        ];
        _callCacheIndexes = new int[definition.Instructions.Count];
        _callCaches = new AdaptiveCallCache[
            definition.Instructions.Count(instruction =>
                instruction.OpCode is PythonOpCode.Call or PythonOpCode.CallLocal
            )
        ];
        _closureCellIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < definition.CellVariableNames.Count; index++)
        {
            _closureCellIndexes.Add(definition.CellVariableNames[index], index);
        }

        for (var index = 0; index < definition.FreeVariableNames.Count; index++)
        {
            _closureCellIndexes.Add(
                definition.FreeVariableNames[index],
                definition.CellVariableNames.Count + index
            );
        }

        _constants = new PythonValue?[definition.Constants.Count];
        _functionCodes = new PreparedPythonCode?[definition.Constants.Count];
        _globalLoadCacheIndexes = new int[definition.Instructions.Count];
        _globalLoadCaches = new GlobalLoadCache[
            definition.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.LoadName
            )
        ];
        _localCellIndexes = new int[definition.VariableNames.Count];
        Array.Fill(_localCellIndexes, -1);
        for (var index = 0; index < definition.CellVariableNames.Count; index++)
        {
            var localIndex = FindNameIndex(
                definition.VariableNames,
                definition.CellVariableNames[index]
            );
            if (localIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Cell variable '{definition.CellVariableNames[index]}' is not local."
                );
            }

            _localCellIndexes[localIndex] = index;
        }
        _orderedComparisonCacheIndexes = new int[definition.Instructions.Count];
        _orderedComparisonCaches = new AdaptiveNumericCache[
            definition.Instructions.Count(instruction => IsOrderedComparison(instruction.OpCode))
        ];

        var binaryAddCacheIndex = 0;
        var callCacheIndex = 0;
        var globalLoadCacheIndex = 0;
        var orderedComparisonCacheIndex = 0;
        for (var index = 0; index < definition.Instructions.Count; index++)
        {
            switch (definition.Instructions[index].OpCode)
            {
                case PythonOpCode.BinaryAdd:
                    _binaryAddCacheIndexes[index] = ++binaryAddCacheIndex;
                    break;
                case PythonOpCode.Call:
                case PythonOpCode.CallLocal:
                    _callCacheIndexes[index] = ++callCacheIndex;
                    break;
                case PythonOpCode.LoadName:
                    _globalLoadCacheIndexes[index] = ++globalLoadCacheIndex;
                    break;
                case PythonOpCode.CompareLessThan:
                case PythonOpCode.CompareLessThanOrEqual:
                case PythonOpCode.CompareGreaterThan:
                case PythonOpCode.CompareGreaterThanOrEqual:
                    _orderedComparisonCacheIndexes[index] = ++orderedComparisonCacheIndex;
                    break;
            }
        }

        for (var index = 0; index < definition.Constants.Count; index++)
        {
            var constant = definition.Constants[index];
            if (
                constant.Type == PythonConstantType.CodeObject
                && constant.Value is PythonCodeObject functionCode
            )
            {
                _functionCodes[index] = Create(functionCode);
            }
            else
            {
                _constants[index] = ConvertConstant(constant);
            }
        }
    }

    internal PythonCodeObject Definition { get; }

    internal static PreparedPythonCode Create(PythonCodeObject definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PreparedPythonCode(definition);
    }

    internal PythonValue GetConstant(int index)
    {
        if ((uint)index >= (uint)_constants.Length || _constants[index] is not { } value)
        {
            throw new InvalidOperationException("The prepared constant index is invalid.");
        }

        return value;
    }

    internal PreparedPythonCode GetFunctionCode(int index)
    {
        if ((uint)index >= (uint)_functionCodes.Length || _functionCodes[index] is not { } code)
        {
            throw new InvalidOperationException("The prepared function-code index is invalid.");
        }

        return code;
    }

    internal int GetLocalCellIndex(int localIndex)
    {
        if ((uint)localIndex >= (uint)_localCellIndexes.Length)
        {
            throw new InvalidOperationException("The prepared local index is invalid.");
        }

        return _localCellIndexes[localIndex];
    }

    internal int GetClosureCellIndex(string name) =>
        _closureCellIndexes.TryGetValue(name, out var index) ? index : -1;

    internal AdaptiveNumericCacheState GetBinaryAddCacheState(int instructionIndex)
    {
        return GetBinaryAddCache(instructionIndex).State;
    }

    internal void RecordBinaryAddObservation(
        int instructionIndex,
        AdaptiveNumericOperandKind operandKind
    ) => RecordNumericObservation(ref GetBinaryAddCache(instructionIndex), operandKind);

    internal AdaptiveCallCacheState GetCallCacheState(int instructionIndex)
    {
        return GetCallCache(instructionIndex).State;
    }

    internal bool TryGetCachedManagedCall(
        int instructionIndex,
        PythonValue target,
        out PythonFunctionValue function,
        out bool useEmptyFrame
    )
    {
        ref var cache = ref GetCallCache(instructionIndex);
        if (IsManagedCallCacheState(cache.State))
        {
            if (ReferenceEquals(cache.Target, target))
            {
                function = cache.Target!;
                useEmptyFrame = cache.State == AdaptiveCallCacheState.ManagedFunctionEmptyFrame;
                return true;
            }

            cache = default;
        }

        function = null!;
        useEmptyFrame = false;
        return false;
    }

    internal void RecordManagedCall(int instructionIndex, PythonFunctionValue function)
    {
        ref var cache = ref GetCallCache(instructionIndex);
        if (cache.State == AdaptiveCallCacheState.Generic)
        {
            return;
        }

        if (IsManagedCallCacheState(cache.State))
        {
            if (ReferenceEquals(cache.Target, function))
            {
                return;
            }

            cache = default;
        }

        if (!ReferenceEquals(cache.Candidate, function))
        {
            cache.Candidate = function;
            cache.WarmupCount = 1;
            return;
        }

        if (cache.WarmupCount < AdaptiveWarmupThreshold - 1)
        {
            cache.WarmupCount++;
            return;
        }

        cache = new AdaptiveCallCache
        {
            State = CanUseEmptyFrame(function)
                ? AdaptiveCallCacheState.ManagedFunctionEmptyFrame
                : AdaptiveCallCacheState.ManagedFunction,
            Target = function,
        };
    }

    internal void RecordBuiltinCall(int instructionIndex)
    {
        ref var cache = ref GetCallCache(instructionIndex);
        if (cache.State == AdaptiveCallCacheState.Generic)
        {
            return;
        }

        if (cache.State != AdaptiveCallCacheState.Adaptive || cache.Candidate is not null)
        {
            cache = default;
        }

        if (cache.WarmupCount < AdaptiveWarmupThreshold - 1)
        {
            cache.WarmupCount++;
            return;
        }

        cache = new AdaptiveCallCache { State = AdaptiveCallCacheState.Generic };
    }

    internal AdaptiveNumericCacheState GetOrderedComparisonCacheState(int instructionIndex)
    {
        return GetOrderedComparisonCache(instructionIndex).State;
    }

    internal void RecordOrderedComparisonObservation(
        int instructionIndex,
        AdaptiveNumericOperandKind operandKind
    ) => RecordNumericObservation(ref GetOrderedComparisonCache(instructionIndex), operandKind);

    internal GlobalLoadCacheState GetGlobalLoadCacheState(int instructionIndex)
    {
        return GetGlobalLoadCache(instructionIndex).State;
    }

    internal bool TryGetCachedName(
        int instructionIndex,
        PythonGlobalNamespace globals,
        out PythonValue value
    )
    {
        ref var cache = ref GetGlobalLoadCache(instructionIndex);
        if (ReferenceEquals(cache.Globals, globals) && cache.KeysVersion == globals.KeysVersion)
        {
            if (cache.State == GlobalLoadCacheState.Global)
            {
                value = cache.Slot!.Value;
                return true;
            }

            if (cache.State == GlobalLoadCacheState.Builtin)
            {
                value = cache.Value!;
                return true;
            }
        }

        value = null!;
        return false;
    }

    internal void RecordGlobalLoad(
        int instructionIndex,
        PythonGlobalNamespace globals,
        PythonGlobalSlot slot
    )
    {
        ref var cache = ref GetGlobalLoadCache(instructionIndex);
        if (cache.State != GlobalLoadCacheState.Adaptive)
        {
            cache = default;
        }

        if (cache.WarmupCount < AdaptiveWarmupThreshold - 1)
        {
            cache.WarmupCount++;
            return;
        }

        cache = new GlobalLoadCache
        {
            State = GlobalLoadCacheState.Global,
            Globals = globals,
            KeysVersion = globals.KeysVersion,
            Slot = slot,
        };
    }

    internal void RecordBuiltinLoad(
        int instructionIndex,
        PythonGlobalNamespace globals,
        PythonValue value
    )
    {
        ref var cache = ref GetGlobalLoadCache(instructionIndex);
        if (cache.State != GlobalLoadCacheState.Adaptive)
        {
            cache = default;
        }

        if (cache.WarmupCount < AdaptiveWarmupThreshold - 1)
        {
            cache.WarmupCount++;
            return;
        }

        cache = new GlobalLoadCache
        {
            State = GlobalLoadCacheState.Builtin,
            Globals = globals,
            KeysVersion = globals.KeysVersion,
            Value = value,
        };
    }

    private static PythonValue ConvertConstant(PythonConstant constant) =>
        constant.Type switch
        {
            PythonConstantType.NoneValue => PythonNoneValue.Instance,
            PythonConstantType.TruthValue => PythonTruthValue.FromBoolean((bool)constant.Value!),
            PythonConstantType.WholeNumber => PythonWholeNumberValue.Create(
                (BigInteger)constant.Value!
            ),
            PythonConstantType.FloatingPoint => new PythonFloatingPointValue(
                (double)constant.Value!
            ),
            PythonConstantType.ComplexNumber => new PythonComplexValue((Complex)constant.Value!),
            PythonConstantType.TextValue => new PythonTextValue((string)constant.Value!),
            PythonConstantType.ByteSequence => new PythonByteSequenceValue((byte[])constant.Value!),
            PythonConstantType.CodeObject => throw new InvalidOperationException(
                "Code-object constants must be prepared as function code."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(constant)),
        };

    private ref GlobalLoadCache GetGlobalLoadCache(int instructionIndex) =>
        ref _globalLoadCaches[_globalLoadCacheIndexes[instructionIndex] - 1];

    private ref AdaptiveCallCache GetCallCache(int instructionIndex) =>
        ref _callCaches[_callCacheIndexes[instructionIndex] - 1];

    private ref AdaptiveNumericCache GetBinaryAddCache(int instructionIndex) =>
        ref _binaryAddCaches[_binaryAddCacheIndexes[instructionIndex] - 1];

    private ref AdaptiveNumericCache GetOrderedComparisonCache(int instructionIndex) =>
        ref _orderedComparisonCaches[_orderedComparisonCacheIndexes[instructionIndex] - 1];

    private static bool IsOrderedComparison(PythonOpCode opCode) =>
        opCode
            is PythonOpCode.CompareLessThan
                or PythonOpCode.CompareLessThanOrEqual
                or PythonOpCode.CompareGreaterThan
                or PythonOpCode.CompareGreaterThanOrEqual;

    private static bool IsManagedCallCacheState(AdaptiveCallCacheState state) =>
        state
            is AdaptiveCallCacheState.ManagedFunction
                or AdaptiveCallCacheState.ManagedFunctionEmptyFrame;

    private static bool CanUseEmptyFrame(PythonFunctionValue function)
    {
        var definition = function.Code.Definition;
        return definition.ArgumentCount == 0
            && definition.VariableNames.Count == 0
            && definition.CellVariableNames.Count == 0
            && definition.FreeVariableNames.Count == 0
            && function.Closure.Length == 0;
    }

    private static int FindNameIndex(IReadOnlyList<string> names, string name)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void RecordNumericObservation(
        ref AdaptiveNumericCache cache,
        AdaptiveNumericOperandKind operandKind
    )
    {
        if (cache.State == AdaptiveNumericCacheState.Generic)
        {
            return;
        }

        var targetState = operandKind switch
        {
            AdaptiveNumericOperandKind.WholeNumber => AdaptiveNumericCacheState.WholeNumber,
            AdaptiveNumericOperandKind.FloatingPoint => AdaptiveNumericCacheState.FloatingPoint,
            _ => AdaptiveNumericCacheState.Generic,
        };

        if (cache.State != AdaptiveNumericCacheState.Adaptive)
        {
            if (cache.State == targetState)
            {
                return;
            }

            cache = default;
        }

        if (cache.Candidate != targetState)
        {
            cache.Candidate = targetState;
            cache.WarmupCount = 1;
            return;
        }

        if (cache.WarmupCount < AdaptiveWarmupThreshold - 1)
        {
            cache.WarmupCount++;
            return;
        }

        cache = new AdaptiveNumericCache { State = targetState };
    }

    private struct AdaptiveNumericCache
    {
        internal AdaptiveNumericCacheState State { get; init; }

        internal AdaptiveNumericCacheState Candidate { get; set; }

        internal byte WarmupCount { get; set; }
    }

    private struct AdaptiveCallCache
    {
        internal AdaptiveCallCacheState State { get; init; }

        internal PythonFunctionValue? Candidate { get; set; }

        internal PythonFunctionValue? Target { get; init; }

        internal byte WarmupCount { get; set; }
    }

    private struct GlobalLoadCache
    {
        internal PythonGlobalNamespace? Globals { get; init; }

        internal long KeysVersion { get; init; }

        internal PythonGlobalSlot? Slot { get; init; }

        internal PythonValue? Value { get; init; }

        internal GlobalLoadCacheState State { get; init; }

        internal byte WarmupCount { get; set; }
    }
}

internal enum AdaptiveCallCacheState : byte
{
    Adaptive,
    ManagedFunction,
    ManagedFunctionEmptyFrame,
    Generic,
}

internal enum AdaptiveNumericCacheState : byte
{
    Adaptive,
    WholeNumber,
    FloatingPoint,
    Generic,
}

internal enum AdaptiveNumericOperandKind : byte
{
    Other,
    WholeNumber,
    FloatingPoint,
}

internal enum GlobalLoadCacheState : byte
{
    Adaptive,
    Global,
    Builtin,
}
