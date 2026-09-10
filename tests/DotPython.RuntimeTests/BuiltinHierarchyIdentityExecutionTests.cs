using System.Runtime.CompilerServices;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BuiltinHierarchyIdentityExecutionTests
{
    [Fact]
    public void StoredBuiltinTuplesSurviveSubsequentExecutions()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var setup = engine.Execute(
            "bases = int.__bases__\nmro = ExceptionGroup.__mro__",
            "before.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var result = engine.Execute(
            """
            class NewType: pass
            print(int.__bases__ is bases, ExceptionGroup.__mro__ is mro)
            fresh = type.mro(ExceptionGroup)
            fresh.clear()
            print(ExceptionGroup.__mro__ is mro, len(mro))
            """,
            "after.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            $"True True{Environment.NewLine}True 5{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void ConcurrentFirstReadsPublishOneTuplePerTypeIdentity()
    {
        var type = NewBuiltin();
        var bases = new PythonTupleValue[64];
        var mros = new PythonTupleValue[64];
        Parallel.For(
            0,
            bases.Length,
            index =>
            {
                bases[index] = PythonBuiltinTypes.GetBases(type);
                mros[index] = PythonBuiltinTypes.GetMro(type);
            }
        );
        Assert.All(bases, value => Assert.Same(bases[0], value));
        Assert.All(mros, value => Assert.Same(mros[0], value));
        Assert.Same(type, mros[0].Elements[0]);
        Assert.Same(PythonBuiltinFunctions.Object, mros[0].Elements[1]);
        Assert.NotSame(bases[0], PythonBuiltinTypes.GetBases(NewBuiltin()));
        Assert.NotSame(mros[0], PythonBuiltinTypes.GetMro(NewBuiltin()));
        Assert.Same(
            PythonBuiltinTypes.GetBases(PythonBuiltinFunctions.Object),
            PythonBuiltinTypes.GetBases(PythonBuiltinFunctions.ObjectType)
        );
        Assert.Same(
            PythonBuiltinTypes.GetMro(PythonBuiltinFunctions.Object),
            PythonBuiltinTypes.GetMro(PythonBuiltinFunctions.ObjectType)
        );
    }

    [Fact]
    public void CachedMroSelfReferenceDoesNotKeepAnUnreachableBuiltinAlive()
    {
        var reference = CreateWeakBuiltin();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
    }

    private static PythonBuiltinTypeValue NewBuiltin() =>
        new("Transient", static (_, _) => PythonNoneValue.Instance);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakBuiltin()
    {
        var type = NewBuiltin();
        _ = PythonBuiltinTypes.GetBases(type);
        _ = PythonBuiltinTypes.GetMro(type);
        return new WeakReference(type);
    }
}
