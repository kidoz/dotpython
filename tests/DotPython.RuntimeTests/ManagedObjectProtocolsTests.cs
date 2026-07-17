using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedObjectProtocolsTests
{
    [Fact]
    public void Call_BindsTypeMethodsAndDescriptors()
    {
        var type = new PythonManagedTypeValue("Version");
        type.Attributes["normalized"] = new PythonDescriptorValue(
            "normalized",
            target => new PythonTextValue(
                Assert.IsType<string>(Assert.IsType<PythonManagedObjectValue>(target).Payload)
            )
        );
        type.Attributes["compare"] = new PythonProtocolFunctionValue(
            "compare",
            (target, arguments) =>
            {
                var instance = Assert.IsType<PythonManagedObjectValue>(target);
                var other = Assert.IsType<PythonTextValue>(Assert.Single(arguments));
                return PythonWholeNumberValue.Create(
                    string.CompareOrdinal(Assert.IsType<string>(instance.Payload), other.Value)
                );
            }
        );
        var instance = new PythonManagedObjectValue(type, "1.2.3");

        var normalized = ManagedObjectProtocols.GetAttribute(instance, "normalized");
        var compare = ManagedObjectProtocols.GetAttribute(instance, "compare");
        var result = ManagedObjectProtocols.Call(compare, [new PythonTextValue("2.0")]);

        Assert.Equal("1.2.3", Assert.IsType<PythonTextValue>(normalized).Value);
        Assert.True(Assert.IsType<PythonWholeNumberValue>(result).Value < BigInteger.Zero);
        Assert.Equal("Version", ManagedObjectProtocols.GetTypeName(instance));
    }

    [Fact]
    public void SetAttribute_UsesWritableDescriptorBeforeInstanceDictionary()
    {
        PythonValue stored = PythonNoneValue.Instance;
        var type = new PythonManagedTypeValue("Box");
        type.Attributes["value"] = new PythonDescriptorValue(
            "value",
            _ => stored,
            (_, value) => stored = value
        );
        var instance = new PythonManagedObjectValue(type);

        ManagedObjectProtocols.SetAttribute(instance, "value", PythonWholeNumberValue.Create(42));

        Assert.Equal(
            new BigInteger(42),
            Assert
                .IsType<PythonWholeNumberValue>(
                    ManagedObjectProtocols.GetAttribute(instance, "value")
                )
                .Value
        );
        Assert.Empty(instance.Attributes);
    }

    [Fact]
    public void GetAttribute_UsesPythonDataAndNonDataDescriptorPrecedence()
    {
        var type = new PythonManagedTypeValue("Version");
        type.Attributes["data"] = new PythonDescriptorValue(
            "data",
            _ => new PythonTextValue("descriptor")
        );
        type.Attributes["method"] = new PythonProtocolFunctionValue(
            "method",
            (_, _) => PythonNoneValue.Instance
        );
        var instance = new PythonManagedObjectValue(type);
        instance.Attributes["data"] = new PythonTextValue("instance");
        instance.Attributes["method"] = new PythonTextValue("shadowed");

        Assert.Equal(
            "descriptor",
            Assert
                .IsType<PythonTextValue>(ManagedObjectProtocols.GetAttribute(instance, "data"))
                .Value
        );
        Assert.Equal(
            "shadowed",
            Assert
                .IsType<PythonTextValue>(ManagedObjectProtocols.GetAttribute(instance, "method"))
                .Value
        );
    }

    [Fact]
    public void Collections_ProvideSequenceMappingAndIteratorProtocols()
    {
        var list = new PythonListValue([
            PythonWholeNumberValue.Create(1),
            PythonWholeNumberValue.Create(2),
        ]);
        var tuple = new PythonTupleValue([
            new PythonTextValue("alpha"),
            new PythonTextValue("beta"),
        ]);
        var dictionary = ManagedObjectProtocols.CreateDictionary();
        ManagedObjectProtocols.SetItem(
            dictionary,
            new PythonTextValue("key"),
            PythonWholeNumberValue.Create(3)
        );

        ManagedObjectProtocols.SetItem(
            list,
            PythonWholeNumberValue.Create(-1),
            PythonWholeNumberValue.Create(4)
        );
        var iterator = ManagedObjectProtocols.GetIterator(tuple);

        Assert.Equal(2, ManagedObjectProtocols.GetLength(list));
        Assert.Equal(
            new BigInteger(4),
            Assert
                .IsType<PythonWholeNumberValue>(
                    ManagedObjectProtocols.GetItem(list, PythonWholeNumberValue.Create(1))
                )
                .Value
        );
        Assert.Equal(
            new BigInteger(3),
            Assert
                .IsType<PythonWholeNumberValue>(
                    ManagedObjectProtocols.GetItem(dictionary, new PythonTextValue("key"))
                )
                .Value
        );
        Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out var first));
        Assert.Equal("alpha", Assert.IsType<PythonTextValue>(first).Value);
        Assert.True(ManagedObjectProtocols.Contains(tuple, new PythonTextValue("beta")));
    }

    [Fact]
    public void ScalarProtocols_PreserveBytesUnicodeNumericAndHashBehavior()
    {
        var text = new PythonTextValue("Aβ");
        var bytes = new PythonByteSequenceValue([0, 127, 255]);
        var one = PythonWholeNumberValue.Create(1);

        Assert.Equal(
            "Aβ",
            System.Text.Encoding.UTF8.GetString(ManagedObjectProtocols.GetUtf8(text))
        );
        Assert.Equal([0, 127, 255], ManagedObjectProtocols.GetBytes(bytes));
        Assert.Same(
            PythonTruthValue.True,
            ManagedObjectProtocols.RichCompare(
                one,
                PythonTruthValue.True,
                PythonRichComparison.Equal
            )
        );
        Assert.Equal(
            ManagedObjectProtocols.GetPythonHash(one),
            ManagedObjectProtocols.GetPythonHash(PythonTruthValue.True)
        );
        Assert.False(ManagedObjectProtocols.IsTrue(ManagedObjectProtocols.CreateList(0)));
        Assert.Same(
            PythonTruthValue.False,
            ManagedObjectProtocols.RichCompare(
                new PythonFloatingPointValue(double.NaN),
                new PythonFloatingPointValue(double.NaN),
                PythonRichComparison.LessThanOrEqual
            )
        );
    }

    [Fact]
    public void ModuleAndTypeProtocols_CreateAndMutateManagedValues()
    {
        var module = ManagedObjectProtocols.CreateModule("anyver._anyver");
        var type = new PythonManagedTypeValue(
            "Version",
            construct: arguments => new PythonManagedObjectValue(
                Assert.IsType<PythonManagedTypeValue>(
                    ManagedObjectProtocols.GetAttribute(module, "Version")
                ),
                Assert.IsType<PythonTextValue>(Assert.Single(arguments)).Value
            )
        );
        ManagedObjectProtocols.SetAttribute(module, "Version", type);

        var result = ManagedObjectProtocols.Call(
            ManagedObjectProtocols.GetAttribute(module, "Version"),
            [new PythonTextValue("1.2.3")]
        );

        Assert.Equal(
            "1.2.3",
            Assert.IsType<string>(Assert.IsType<PythonManagedObjectValue>(result).Payload)
        );
    }

    [Fact]
    public void MutationAndIteration_RejectUnhashableAndInvalidatedDictionaryState()
    {
        var dictionary = ManagedObjectProtocols.CreateDictionary();
        Assert.Throws<PythonRuntimeException>(() =>
            ManagedObjectProtocols.SetItem(
                dictionary,
                ManagedObjectProtocols.CreateList(0),
                PythonNoneValue.Instance
            )
        );
        ManagedObjectProtocols.SetItem(
            dictionary,
            new PythonTextValue("first"),
            PythonNoneValue.Instance
        );
        var iterator = ManagedObjectProtocols.GetIterator(dictionary);
        ManagedObjectProtocols.SetItem(
            dictionary,
            new PythonTextValue("second"),
            PythonNoneValue.Instance
        );

        var exception = Assert.Throws<PythonRuntimeException>(() =>
            ManagedObjectProtocols.TryGetNext(iterator, out _)
        );
        Assert.Equal("DPY4016", exception.Code);
    }
}
