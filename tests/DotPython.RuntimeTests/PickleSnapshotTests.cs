using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PickleSnapshotTests
{
    [Fact]
    public void Dumps_CapturesMutableGraphAndEachLoadIsIndependent()
    {
        var pickle = CreatePickleModule();
        var list = new PythonListValue([PythonWholeNumberValue.Create(1)]);
        var instance = new PythonManagedObjectValue(new PythonManagedTypeValue("Box"));
        instance.Attributes["items"] = list;
        var graph = new PythonListValue([instance, list, list]);

        var payload = Call(pickle, "dumps", graph);
        list.Elements.Add(PythonWholeNumberValue.Create(2));
        instance.Attributes["items"] = PythonNoneValue.Instance;
        var first = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        var restoredList = Assert.IsType<PythonListValue>(first.Elements[1]);
        Assert.Single(restoredList.Elements);
        Assert.Same(restoredList, first.Elements[2]);
        Assert.Same(
            restoredList,
            Assert.IsType<PythonManagedObjectValue>(first.Elements[0]).Attributes["items"]
        );

        restoredList.Elements.Clear();
        var second = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        Assert.NotSame(first, second);
        Assert.Single(Assert.IsType<PythonListValue>(second.Elements[1]).Elements);
        Assert.Same(second.Elements[1], second.Elements[2]);
    }

    [Fact]
    public void Dumps_PreservesSelfCyclesAndTupleListCycleIdentity()
    {
        var pickle = CreatePickleModule();
        var list = new PythonListValue([]);
        var tuple = new PythonTupleValue([list]);
        list.Elements.Add(tuple);
        list.Elements.Add(list);

        var payload = Call(pickle, "dumps", tuple);
        list.Elements.Clear();
        var restored = Assert.IsType<PythonTupleValue>(Call(pickle, "loads", payload));
        var restoredList = Assert.IsType<PythonListValue>(restored.Elements[0]);
        Assert.Same(restored, restoredList.Elements[0]);
        Assert.Same(restoredList, restoredList.Elements[1]);
    }

    [Fact]
    public void Loads_RejectsOtherModuleTokensEvenWithMatchingSequenceNumbers()
    {
        var first = CreatePickleModule();
        var second = CreatePickleModule();
        var firstPayload = Assert.IsType<PythonByteSequenceValue>(
            Call(first, "dumps", new PythonTextValue("first"))
        );
        var secondPayload = Assert.IsType<PythonByteSequenceValue>(
            Call(second, "dumps", new PythonTextValue("second"))
        );

        Assert.False(firstPayload.Value.AsSpan().SequenceEqual(secondPayload.Value));
        var error = Assert.Throws<PythonRuntimeException>(() =>
            Call(second, "loads", firstPayload)
        );
        Assert.Equal("ValueError", error.PythonExceptionTypeName);
        Assert.Equal(
            "first",
            Assert.IsType<PythonTextValue>(Call(first, "loads", firstPayload)).Value
        );
        Assert.Equal(
            "second",
            Assert.IsType<PythonTextValue>(Call(second, "loads", secondPayload)).Value
        );
    }

    private static PythonGlobalNamespace CreatePickleModule()
    {
        var definitions = new Dictionary<
            string,
            DotPython.Runtime.Managed.Execution.PythonModuleDefinition
        >(StringComparer.Ordinal);
        PythonStandardModules.AddTo(definitions, []);
        var globals = new PythonGlobalNamespace();
        definitions["pickle"].Initialize!(globals);
        return globals;
    }

    private static PythonValue Call(PythonGlobalNamespace module, string name, PythonValue argument)
    {
        Assert.True(module.TryGetValue(name, out var function));
        return ManagedObjectProtocols.Call(function, [argument]);
    }
}
