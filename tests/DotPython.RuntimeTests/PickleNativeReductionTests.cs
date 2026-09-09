using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PickleNativeReductionTests
{
    [Fact]
    public void Dumps_DefersNativeFactoryAndFreezesReductionArguments()
    {
        var pickle = CreateModule("pickle");
        var state = new PythonListValue([PythonWholeNumberValue.Create(1)]);
        var protocol = new ReductionProtocol(state);
        var native = new PythonExternalObjectValue(protocol);
        var payload = Call(pickle, "dumps", new PythonListValue([native, native, state]));

        Assert.Equal(1, protocol.ReductionCalls);
        Assert.Equal(0, protocol.FactoryCalls);
        state.Elements.Add(PythonWholeNumberValue.Create(2));

        var first = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        var restored = Assert.IsType<PythonManagedObjectValue>(first.Elements[0]);
        var restoredState = Assert.IsType<PythonListValue>(restored.Attributes["state"]);
        Assert.Single(restoredState.Elements);
        Assert.Same(first.Elements[0], first.Elements[1]);
        Assert.Same(restoredState, first.Elements[2]);
        Assert.Equal(1, protocol.ReductionCalls);
        Assert.Equal(1, protocol.FactoryCalls);

        restoredState.Elements.Clear();
        var second = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        Assert.NotSame(first.Elements[0], second.Elements[0]);
        Assert.Single(Assert.IsType<PythonListValue>(second.Elements[2]).Elements);
        Assert.Equal(1, protocol.ReductionCalls);
        Assert.Equal(2, protocol.FactoryCalls);
    }

    [Fact]
    public void Deepcopy_StillReducesAndReconstructsImmediately()
    {
        var copy = CreateModule("copy");
        var state = new PythonListValue([PythonWholeNumberValue.Create(1)]);
        var protocol = new ReductionProtocol(state);

        var restored = Assert.IsType<PythonManagedObjectValue>(
            Call(copy, "deepcopy", new PythonExternalObjectValue(protocol))
        );

        Assert.Equal(1, protocol.ReductionCalls);
        Assert.Equal(1, protocol.FactoryCalls);
        Assert.NotSame(state, restored.Attributes["state"]);
    }

    [Fact]
    public void Loads_RejectsRecursiveNativeConstructorArgumentsWithoutCallingFactory()
    {
        var pickle = CreateModule("pickle");
        var state = new PythonListValue([]);
        var protocol = new ReductionProtocol(state);
        var native = new PythonExternalObjectValue(protocol);
        state.Elements.Add(native);
        var payload = Call(pickle, "dumps", native);

        var error = Assert.Throws<PythonRuntimeException>(() => Call(pickle, "loads", payload));

        Assert.Equal("ValueError", error.PythonExceptionTypeName);
        Assert.Equal(1, protocol.ReductionCalls);
        Assert.Equal(0, protocol.FactoryCalls);
    }

    private static PythonGlobalNamespace CreateModule(string name)
    {
        var definitions = new Dictionary<
            string,
            DotPython.Runtime.Managed.Execution.PythonModuleDefinition
        >(StringComparer.Ordinal);
        PythonStandardModules.AddTo(definitions, []);
        var globals = new PythonGlobalNamespace();
        definitions[name].Initialize!(globals);
        return globals;
    }

    private static PythonValue Call(PythonGlobalNamespace module, string name, PythonValue argument)
    {
        Assert.True(module.TryGetValue(name, out var function));
        return ManagedObjectProtocols.Call(function, [argument]);
    }

    private sealed class ReductionProtocol(PythonListValue state) : PythonExternalObjectProtocol
    {
        internal int ReductionCalls { get; private set; }

        internal int FactoryCalls { get; private set; }

        public PythonValue GetAttribute(string name, TextSpan span)
        {
            Assert.Equal("__reduce__", name);
            return new PythonBuiltinFunctionValue(
                "__reduce__",
                (_, _) =>
                {
                    ReductionCalls++;
                    return new PythonTupleValue([
                        new PythonBuiltinFunctionValue(
                            "restore",
                            (arguments, _) =>
                            {
                                FactoryCalls++;
                                var instance = new PythonManagedObjectValue(
                                    new PythonManagedTypeValue("Restored")
                                );
                                instance.Attributes["state"] = Assert.Single(arguments);
                                return instance;
                            }
                        ),
                        new PythonTupleValue([state]),
                    ]);
                }
            );
        }

        public PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
            throw new NotSupportedException();

        public PythonValue CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw new NotSupportedException();

        public PythonValue GetItem(PythonValue index, TextSpan span) =>
            throw new NotSupportedException();

        public long GetHash(TextSpan span) => throw new NotSupportedException();

        public int GetLength(TextSpan span) => throw new NotSupportedException();

        public PythonTruthValue RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        ) => throw new NotSupportedException();

        public bool IsInstanceOf(PythonValue value, TextSpan span) =>
            throw new NotSupportedException();

        public string ToDisplayString() => "native fixture";

        public string ToRepresentationString() => ToDisplayString();
    }
}
