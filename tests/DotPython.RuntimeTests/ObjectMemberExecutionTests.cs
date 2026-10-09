using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ObjectMemberExecutionTests
{
    [Fact]
    public void ObjectFormattingAttributeAndState()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.__format__([1, 2], ''), dict.__format__({1: 2}, ''), tuple.__format__((1,), ''))
            print([1, 2].__format__(''), {1: 2}.__format__(''), b'ab'.__format__(''), bytearray(b'ab').__format__(''))
            print(str.__format__('a', '>3'), int.__format__(5, 'x'), float.__format__(1.5, '.2f'), True.__format__(''))
            probe(lambda: [1].__format__('>10'))
            probe(lambda: {1: 2}.__format__('d'))
            probe(lambda: [1].__format__(5))
            probe(lambda: [1].__format__())
            probe(lambda: list.__format__(1))
            print(repr(list.__format__), repr(str.__format__), type(list.__format__).__name__)
            probe(lambda: list.__getattribute__([1], 5))
            probe(lambda: list.__getattribute__([1], 'a', 2))
            probe(lambda: list.__getattribute__())
            probe(lambda: list.__getattribute__([1], 'nope'))
            probe(lambda: [1].__getattribute__('nope'))
            print(list.__getstate__([1]), (5).__getstate__(), str.__getstate__('a'), set.__getstate__({1}))
            probe(lambda: list.__getstate__([1], 2))
            probe(lambda: list.__getstate__())
            print(repr(list.__getstate__), type(list.__getstate__).__name__)
            print(tuple.__init__((1,)), tuple.__init__((1,), (2,)), int.__init__(5))
            print(repr(tuple.__init__), type(tuple.__init__).__name__)
            """
        );

        Assert.Equal(
            Lines(
                "[1, 2] {1: 2} (1,)",
                "[1, 2] {1: 2} b'ab' bytearray(b'ab')",
                "  a 5 1.50 True",
                "TypeError unsupported format string passed to list.__format__",
                "TypeError unsupported format string passed to dict.__format__",
                "TypeError __format__() argument must be str, not int",
                "TypeError object.__format__() takes exactly one argument (0 given)",
                "TypeError object.__format__() takes exactly one argument (0 given)",
                "<method '__format__' of 'object' objects> <method '__format__' of 'str' objects> method_descriptor",
                "TypeError attribute name must be string, not 'int'",
                "TypeError expected 1 argument, got 2",
                "TypeError descriptor '__getattribute__' of 'object' object needs an argument",
                "AttributeError 'list' object has no attribute 'nope'",
                "AttributeError 'list' object has no attribute 'nope'",
                "None None None None",
                "TypeError object.__getstate__() takes no arguments (1 given)",
                "TypeError unbound method object.__getstate__() needs an argument",
                "<method '__getstate__' of 'object' objects> method_descriptor",
                "None None None",
                "<slot wrapper '__init__' of 'object' objects> wrapper_descriptor"
            ),
            output
        );
    }

    [Fact]
    public void TypeConstructorEntryPoints()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.__new__(list), dict.__new__(dict), int.__new__(int), str.__new__(str))
            print(list.__new__(list, [1, 2]), tuple.__new__(tuple, (1,)))
            print(frozenset.__new__(frozenset), bytearray.__new__(bytearray), set.__new__(set))
            probe(lambda: list.__new__())
            probe(lambda: list.__new__(int))
            probe(lambda: list.__new__(5))
            probe(lambda: dict.__new__(set))
            probe(lambda: list.__new__(cls=list))
            probe(lambda: list.__new__(list, [1], 2))
            print(list.__class_getitem__(int), tuple.__class_getitem__(int), dict.__class_getitem__((str, int)))
            print(set.__class_getitem__(int), frozenset.__class_getitem__(int))
            print(list.__class_getitem__((int, str)))
            probe(lambda: list.__class_getitem__())
            probe(lambda: list.__class_getitem__(int, str))
            probe(lambda: str.__class_getitem__)
            probe(lambda: int.__class_getitem__)
            print(callable(list.__new__), callable(list.__class_getitem__), type(list.__new__).__name__)
            """
        );

        Assert.Equal(
            Lines(
                "[] {} 0 ",
                "[] (1,)",
                "frozenset() bytearray(b'') set()",
                "TypeError list.__new__(): not enough arguments",
                "TypeError list.__new__(int): int is not a subtype of list",
                "TypeError list.__new__(X): X is not a type object (int)",
                "TypeError dict.__new__(set): set is not a subtype of dict",
                "TypeError list.__new__(): not enough arguments",
                "[]",
                "list[int] tuple[int] dict[str, int]",
                "set[int] frozenset[int]",
                "list[int, str]",
                "TypeError list.__class_getitem__() takes exactly one argument (0 given)",
                "TypeError list.__class_getitem__() takes exactly one argument (2 given)",
                "AttributeError type object 'str' has no attribute '__class_getitem__'",
                "AttributeError type object 'int' has no attribute '__class_getitem__'",
                "True True builtin_function_or_method"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
