using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SlotStorageExecutionTests
{
    [Fact]
    public void SlotDeclarationsRejectUndeclaredAttributes()
    {
        var output = Run(
            """
            class C:
                __slots__ = ()
            instance = C()
            try:
                instance.x = 1
            except AttributeError as error:
                print("AttributeError:", error)
            """
        );

        Assert.Equal(
            $"AttributeError: 'C' object has no attribute 'x' and no __dict__ for setting new attributes{Environment.NewLine}",
            output
        );
    }

    [Fact]
    public void DeclaredMembersRoundTripAndDelete()
    {
        var output = Run(
            """
            class C:
                __slots__ = ('x', '__y')
            instance = C()
            instance.x = 1
            instance._C__y = 2
            print(instance.x, instance._C__y)
            del instance.x
            print(hasattr(instance, 'x'))
            try:
                instance.x
            except AttributeError as error:
                print('AttributeError:', error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "1 2",
                "False",
                "AttributeError: 'C' object has no attribute 'x'",
                ""
            ),
            output
        );
    }

    [Fact]
    public void BasesAndSubclassesCombineTheirSlotsAndDictionaries()
    {
        var output = Run(
            """
            class Plain:
                pass
            class Slotted(Plain):
                __slots__ = ('y',)
            class Strict(object):
                __slots__ = ()
            class StrictChild(Strict):
                __slots__ = ('z',)
            class DictSlots(object):
                __slots__ = ('w', '__dict__')

            def attempt(label, instance, name):
                try:
                    setattr(instance, name, 1)
                    print(label, 'accepts', name)
                except AttributeError:
                    print(label, 'rejects', name)

            attempt('plain', Plain(), 'free')
            attempt('slotted', Slotted(), 'free')
            attempt('strict', Strict(), 'free')
            attempt('strict child', StrictChild(), 'z')
            attempt('strict child', StrictChild(), 'free')
            attempt('dict slots', DictSlots(), 'free')
            print('slotted dict:', hasattr(Slotted(), '__dict__'))
            print('strict dict:', hasattr(Strict(), '__dict__'))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "plain accepts free",
                "slotted accepts free",
                "strict rejects free",
                "strict child accepts z",
                "strict child rejects free",
                "dict slots accepts free",
                "slotted dict: True",
                "strict dict: False",
                ""
            ),
            output
        );
    }

    [Fact]
    public void SlotDeclarationFormsAreReadTheWayCpythonReadsThem()
    {
        // A bare string is one member, not one member per character.
        var output = Run(
            """
            class Single:
                __slots__ = 'xy'
            single = Single()
            single.xy = 1
            print(single.xy)
            try:
                single.x = 1
            except AttributeError:
                print('rejects x')

            class Pair:
                __slots__ = ['ab', 'c']
            pair = Pair()
            pair.ab = 1
            pair.c = 2
            print(pair.ab, pair.c)

            class Mapping:
                __slots__ = {'m': 'doc'}
            mapping = Mapping()
            mapping.m = 3
            print(mapping.m)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "1", "rejects x", "1 2", "3", ""), output);
    }

    [Fact]
    public void SlotClassesHideTheInstanceDictionary()
    {
        var output = Run(
            """
            class C:
                __slots__ = ('x',)
            instance = C()
            for label, action in (
                ('read', lambda: instance.__dict__),
                ('write', lambda: setattr(instance, '__dict__', {})),
            ):
                try:
                    action()
                    print(label, 'allowed')
                except AttributeError as error:
                    print(label, 'AttributeError:', error)
            try:
                vars(instance)
            except TypeError as error:
                print('TypeError:', error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "read AttributeError: 'C' object has no attribute '__dict__'",
                "write AttributeError: 'C' object has no attribute '__dict__' and no __dict__ for setting new attributes",
                "TypeError: vars() argument must have __dict__ attribute",
                ""
            ),
            output
        );
    }

    [Theory]
    [InlineData("__slots__ = (1,)", "DPY4029")]
    [InlineData("__slots__ = 5", "DPY4015")]
    [InlineData("__slots__ = ('not an identifier',)", "DPY4029")]
    [InlineData("__slots__ = ('x',)\n    x = 1", "DPY4029")]
    public void InvalidSlotDeclarationsAreRejected(string declaration, string code)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            $"class C:\n    {declaration}\n",
            "slot_storage_declaration.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "slot_storage_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
