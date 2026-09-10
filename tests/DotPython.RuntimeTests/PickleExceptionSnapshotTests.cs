using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PickleExceptionSnapshotTests
{
    [Fact]
    public void Dumps_CapturesExceptionArgsStateAndCyclesWithClassMetadata()
    {
        var pickle = CreatePickleModule();
        var type = new PythonManagedTypeValue("Custom", exceptionBaseName: "ValueError");
        var values = new PythonListValue([PythonWholeNumberValue.Create(1)]);
        var exception = new PythonExceptionValue("Custom", "original")
        {
            ManagedType = type,
            Arguments = [values],
            Cause = new PythonExceptionValue("ValueError", "cause"),
            Context = new PythonExceptionValue("ValueError", "context"),
            SuppressContext = true,
        };
        exception.Attributes["values"] = values;
        exception.Attributes["self"] = exception;
        var payload = Call(pickle, "dumps", new PythonListValue([exception, exception, values]));
        values.Elements.Clear();

        var first = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        var restored = Assert.IsType<PythonExceptionValue>(first.Elements[0]);
        Assert.NotSame(exception, restored);
        Assert.Same(type, restored.ManagedType);
        Assert.Same(restored, first.Elements[1]);
        Assert.Same(restored, restored.Attributes["self"]);
        Assert.Same(first.Elements[2], restored.EffectiveArguments[0]);
        Assert.Same(first.Elements[2], restored.Attributes["values"]);
        Assert.Null(restored.Cause);
        Assert.Null(restored.Context);
        Assert.False(restored.SuppressContext);
        Assert.Single(Assert.IsType<PythonListValue>(first.Elements[2]).Elements);

        Assert.IsType<PythonListValue>(first.Elements[2]).Elements.Clear();
        var second = Assert.IsType<PythonListValue>(Call(pickle, "loads", payload));
        Assert.NotSame(first.Elements[0], second.Elements[0]);
        Assert.Single(Assert.IsType<PythonListValue>(second.Elements[2]).Elements);
    }

    [Fact]
    public void Loads_ReusesExceptionReconstructedThroughMutableConstructorArgument()
    {
        var pickle = CreatePickleModule();
        var values = new PythonListValue([]);
        var exception = new PythonExceptionValue("ValueError", "cycle") { Arguments = [values] };
        values.Elements.Add(exception);
        var payload = Call(pickle, "dumps", exception);

        var restored = Assert.IsType<PythonExceptionValue>(Call(pickle, "loads", payload));

        Assert.Same(
            restored,
            Assert.IsType<PythonListValue>(restored.EffectiveArguments[0]).Elements[0]
        );
    }

    [Fact]
    public void Dumps_RejectsConstructorCyclesWithoutMutableMemoBoundary()
    {
        var pickle = CreatePickleModule();
        var exception = new PythonExceptionValue("ValueError", "cycle");
        exception.Arguments = [exception];

        var error = Assert.Throws<PythonRuntimeException>(() => Call(pickle, "dumps", exception));

        Assert.Equal("RecursionError", error.PythonExceptionTypeName);
    }

    [Fact]
    public void Dumps_NormalizesRuntimeExceptionGroupArguments()
    {
        var pickle = CreatePickleModule();
        var leaf = new PythonExceptionValue("ValueError", "leaf");
        var group = new PythonExceptionValue("ExceptionGroup", "group")
        {
            GroupExceptions = [leaf, leaf],
        };

        var restored = Assert.IsType<PythonExceptionValue>(
            Call(pickle, "loads", Call(pickle, "dumps", group))
        );

        Assert.Equal(2, restored.EffectiveArguments.Count);
        Assert.Equal(2, restored.GroupExceptions!.Count);
        Assert.NotSame(leaf, restored.GroupExceptions[0]);
        Assert.Same(restored.GroupExceptions[0], restored.GroupExceptions[1]);
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
