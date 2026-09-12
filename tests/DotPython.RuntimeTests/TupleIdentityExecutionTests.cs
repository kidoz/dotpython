using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TupleIdentityExecutionTests
{
    [Fact]
    public void FullSlicesPreserveTupleAndPublishedHashWithoutAnExecution()
    {
        var tuple = new PythonTupleValue([Number(1), Number(2), Number(3)]);
        var hash = ManagedObjectProtocols.ComputePythonHash(tuple);
        PythonSliceValue[] slices =
        [
            new(None, None, None),
            new(Number(0), Number(3), Number(1)),
            new(Number(-3), Number(100), PythonTruthValue.True),
            new(Number(-(BigInteger.One << 100)), Number(BigInteger.One << 100), None),
        ];
        foreach (var slice in slices)
        {
            var result = ManagedObjectProtocols.GetItem(tuple, slice);
            Assert.Same(tuple, result);
            Assert.Equal(hash, new BigInteger(tuple.CachedHash));
        }
    }

    [Fact]
    public void ChangedSlicesOwnFreshCachesAndReuseElementReferences()
    {
        var tuple = new PythonTupleValue([Number(1), Number(2), Number(3)]);
        ManagedObjectProtocols.ComputePythonHash(tuple);
        var sliced = Assert.IsType<PythonTupleValue>(
            ManagedObjectProtocols.GetItem(tuple, new PythonSliceValue(Number(1), None, None))
        );
        Assert.NotSame(tuple, sliced);
        Assert.Equal(-1, sliced.CachedHash);
        Assert.Equal(2, sliced.Elements.Length);
        Assert.Same(tuple.Elements[1], sliced.Elements[0]);
        var singleton = new PythonTupleValue([Number(1)]);
        Assert.NotSame(
            singleton,
            ManagedObjectProtocols.GetItem(singleton, new PythonSliceValue(None, None, Number(-1)))
        );
        var empty = new PythonTupleValue([]);
        Assert.Same(
            empty,
            ManagedObjectProtocols.GetItem(empty, new PythonSliceValue(None, None, Number(-1)))
        );
        Assert.Throws<PythonRuntimeException>(() =>
            ManagedObjectProtocols.GetItem(empty, new PythonSliceValue(None, None, Number(0)))
        );
    }

    [Fact]
    public void ExtremeSliceStridesDoNotWrapAfterTheirLastValidIndex()
    {
        var positive = new PythonSliceValue(Number(1), None, Number(int.MaxValue));
        var negative = new PythonSliceValue(Number(1), None, Number(int.MinValue));
        var tuple = new PythonTupleValue([Number(0), Number(1), Number(2)]);
        var list = new PythonListValue([Number(0), Number(1), Number(2)]);
        foreach (var slice in new[] { positive, negative })
        {
            Assert.Equal(
                1,
                Assert.Single(ManagedObjectProtocols.EnumerateSliceIndices(slice, 3, default))
            );
            Assert.Same(
                tuple.Elements[1],
                Assert.Single(
                    Assert
                        .IsType<PythonTupleValue>(ManagedObjectProtocols.GetItem(tuple, slice))
                        .Elements
                )
            );
            Assert.Same(
                list.Elements[1],
                Assert.Single(
                    Assert
                        .IsType<PythonListValue>(ManagedObjectProtocols.GetItem(list, slice))
                        .Elements
                )
            );
            Assert.Equal(
                "b",
                Assert
                    .IsType<PythonTextValue>(
                        ManagedObjectProtocols.GetItem(new PythonTextValue("abc"), slice)
                    )
                    .Value
            );
        }
    }

    [Fact]
    public void TupleNoOpOperatorsPreserveIdentityWhileListsCopyAndCountsStillValidate()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            value = tuple([1, 2, 3])
            cached = hash(value)
            for result in (value + (), () + value, value * 1, 1 * value, value * True, True * value):
                print(result is value, hash(result) == cached)
            empty = tuple([])
            print(empty * 0 is empty, empty * -1 is empty, empty * 9223372036854775807 is empty)
            for count in (2 ** 100, -(2 ** 100)):
                try: empty * count
                except OverflowError: print('overflow')
            values = [1, 2, 3]
            print(values[:] is values, (values + []) is values, (values * 1) is values)
            """,
            "tuple_identity.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "True True",
                "True True",
                "True True",
                "True True",
                "True True",
                "True True",
                "True True True",
                "overflow",
                "overflow",
                "False False False",
                ""
            ),
            output.ToString()
        );
    }

    private static PythonValue None => PythonNoneValue.Instance;

    private static PythonWholeNumberValue Number(BigInteger value) =>
        PythonWholeNumberValue.Create(value);
}
