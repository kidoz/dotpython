using System.Numerics;
using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PreparedPythonCode
{
    private const byte GlobalLoadWarmupThreshold = 8;
    private readonly PythonValue?[] _constants;
    private readonly PreparedPythonCode?[] _functionCodes;
    private readonly GlobalLoadCache[] _globalLoadCaches;
    private readonly int[] _globalLoadCacheIndexes;

    private PreparedPythonCode(PythonCodeObject definition)
    {
        Definition = definition;
        _constants = new PythonValue?[definition.Constants.Count];
        _functionCodes = new PreparedPythonCode?[definition.Constants.Count];
        _globalLoadCacheIndexes = new int[definition.Instructions.Count];
        _globalLoadCaches = new GlobalLoadCache[
            definition.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.LoadName
            )
        ];

        var cacheIndex = 0;
        for (var index = 0; index < definition.Instructions.Count; index++)
        {
            if (definition.Instructions[index].OpCode == PythonOpCode.LoadName)
            {
                _globalLoadCacheIndexes[index] = ++cacheIndex;
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

        if (cache.WarmupCount < GlobalLoadWarmupThreshold - 1)
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

        if (cache.WarmupCount < GlobalLoadWarmupThreshold - 1)
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

internal enum GlobalLoadCacheState : byte
{
    Adaptive,
    Global,
    Builtin,
}
