using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CollectionsAbcExecutionTests
{
    [Fact]
    public void CollectionsAbcSurface()
    {
        var output = Run(
            """
            import collections.abc as abc
            import collections
            from collections import Counter, OrderedDict, defaultdict, deque

            print("all", sorted(abc.__all__))
            print("mro", abc.Mapping.__mro__)
            print("mro2", abc.MutableSequence.__mro__)
            print("bases", abc.Mapping.__bases__, abc.Sequence.__bases__)
            print("names", abc.Mapping.__name__, abc.Mapping.__qualname__, abc.Mapping.__module__, repr(abc.Mapping))
            print("meta", type(abc.Mapping).__name__, abc.ABCMeta is type(abc.Mapping), abc.ABCMeta.__name__)
            print("abstract", sorted(abc.Mapping.__abstractmethods__), sorted(abc.MutableSequence.__abstractmethods__), sorted(abc.Iterator.__abstractmethods__), sorted(abc.ItemsView.__abstractmethods__))
            print("slots", abc.Collection.__slots__, abc.Iterable.__slots__)

            values = {
                'dict': {}, 'list': [], 'tuple': (), 'set': set(), 'frozenset': frozenset(),
                'str': 'a', 'bytes': b'a', 'bytearray': bytearray(b'a'), 'range': range(1),
                'memoryview': memoryview(b'a'), 'dict_keys': {}.keys(), 'dict_items': {}.items(),
                'dict_values': {}.values(), 'generator': (x for x in []), 'int': 1, 'bool': True,
                'none': None, 'type': int, 'function': (lambda: 0), 'builtin': len,
                'counter': Counter(), 'defaultdict': defaultdict(list), 'odict': OrderedDict(),
                'deque': deque(), 'slice': slice(1), 'map': map(len, []),
            }
            for name, value in values.items():
                checks = []
                for abcname in ('Iterable', 'Iterator', 'Sized', 'Container', 'Hashable', 'Collection',
                                'Reversible', 'Mapping', 'MutableMapping', 'Sequence', 'MutableSequence',
                                'Set', 'MutableSet', 'Callable', 'MappingView', 'KeysView',
                                'ItemsView', 'ValuesView', 'Generator', 'Awaitable', 'AsyncIterable', 'Buffer'):
                    if isinstance(value, getattr(abc, abcname)):
                        checks.append(abcname)
                print(name, "isinstance", ",".join(checks))
            print("issubclass", issubclass(dict, abc.Mapping), issubclass(dict, abc.MutableMapping), issubclass(dict, abc.Reversible), issubclass(list, abc.MutableSequence), issubclass(set, abc.MutableSet), issubclass(frozenset, abc.MutableSet))
            print("issubclass2", issubclass(str, abc.Sequence), issubclass(range, abc.MutableSequence), issubclass(tuple, abc.MutableSequence), issubclass(memoryview, abc.Sequence))
            print("issubclass3", issubclass(type({}.keys()), abc.KeysView), issubclass(type({}.keys()), abc.Set), issubclass(Counter, abc.MutableMapping), issubclass(type((x for x in [])), abc.Generator))
            print("issubclass4", issubclass(bool, abc.Hashable), issubclass(bool, abc.Sequence), issubclass(int, abc.Callable), issubclass(type, abc.Callable))


            class Duck:
                def __iter__(self): return iter([])
                def __len__(self): return 0
                def __getitem__(self, k): return None


            print("duck", isinstance(Duck(), abc.Collection), isinstance(Duck(), abc.Mapping), isinstance(Duck(), abc.Sequence), issubclass(Duck, abc.Iterable), issubclass(Duck, abc.Sequence))
            print("hook", abc.Iterable.__subclasshook__(Duck), abc.Sequence.__subclasshook__(Duck), abc.Hashable.__subclasshook__(Duck))
            print("register", issubclass(Duck, abc.Mapping), abc.Mapping.register(Duck) is Duck, isinstance(Duck(), abc.Mapping), issubclass(Duck, abc.Mapping), issubclass(Duck, abc.MutableMapping))


            class Empty:
                pass


            print("empty", [n for n in ('Iterable', 'Sized', 'Hashable', 'Callable', 'Container') if isinstance(Empty(), getattr(abc, n))])
            print("empty2", issubclass(Empty, abc.Hashable), issubclass(Empty, abc.Callable), issubclass(int, abc.Callable), isinstance(1, abc.Callable))


            class Concrete(abc.Sequence):
                def __getitem__(self, index):
                    return index

                def __len__(self):
                    return 3


            print("concrete", isinstance(Concrete(), abc.Sequence), issubclass(Concrete, abc.Sequence), Concrete.__mro__)


            def try_call(label, thunk):
                try:
                    thunk()
                    print(label, "=> ok")
                except Exception as error:
                    print(label, "!!", type(error).__name__, error)


            try_call("instantiate-mapping", lambda: abc.Mapping())
            try_call("instantiate-sequence", lambda: abc.Sequence())
            try_call("instantiate-iterable", lambda: abc.Iterable())
            try_call("instantiate-iterator", lambda: abc.Iterator())
            try_call("instantiate-mutable-sequence", lambda: abc.MutableSequence())
            try_call("instantiate-partial", lambda: type('P', (abc.Sequence,), {'__getitem__': lambda self, index: index})())
            try_call("instantiate-concrete", lambda: Concrete())
            print("module-names", collections.abc is abc, abc.Iterable.__name__)
            """
        );
        Assert.Equal(
            Lines(
                "all ['AsyncGenerator', 'AsyncIterable', 'AsyncIterator', 'Awaitable', 'Buffer', 'ByteString', 'Callable', 'Collection', 'Container', 'Coroutine', 'Generator', 'Hashable', 'ItemsView', 'Iterable', 'Iterator', 'KeysView', 'Mapping', 'MappingView', 'MutableMapping', 'MutableSequence', 'MutableSet', 'Reversible', 'Sequence', 'Set', 'Sized', 'ValuesView']",
                "mro (<class 'collections.abc.Mapping'>, <class 'collections.abc.Collection'>, <class 'collections.abc.Sized'>, <class 'collections.abc.Iterable'>, <class 'collections.abc.Container'>, <class 'object'>)",
                "mro2 (<class 'collections.abc.MutableSequence'>, <class 'collections.abc.Sequence'>, <class 'collections.abc.Reversible'>, <class 'collections.abc.Collection'>, <class 'collections.abc.Sized'>, <class 'collections.abc.Iterable'>, <class 'collections.abc.Container'>, <class 'object'>)",
                "bases (<class 'collections.abc.Collection'>,) (<class 'collections.abc.Reversible'>, <class 'collections.abc.Collection'>)",
                "names Mapping Mapping collections.abc <class 'collections.abc.Mapping'>",
                "meta ABCMeta True ABCMeta",
                "abstract ['__getitem__', '__iter__', '__len__'] ['__delitem__', '__getitem__', '__len__', '__setitem__', 'insert'] ['__next__'] []",
                "slots () ()",
                "dict isinstance Iterable,Sized,Container,Collection,Reversible,Mapping,MutableMapping",
                "list isinstance Iterable,Sized,Container,Collection,Reversible,Sequence,MutableSequence",
                "tuple isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,Sequence",
                "set isinstance Iterable,Sized,Container,Collection,Set,MutableSet",
                "frozenset isinstance Iterable,Sized,Container,Hashable,Collection,Set",
                "str isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,Sequence",
                "bytes isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,Sequence,Buffer",
                "bytearray isinstance Iterable,Sized,Container,Collection,Reversible,Sequence,MutableSequence,Buffer",
                "range isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,Sequence",
                "memoryview isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,Sequence,Buffer",
                "dict_keys isinstance Iterable,Sized,Container,Collection,Reversible,Set,MappingView,KeysView",
                "dict_items isinstance Iterable,Sized,Container,Collection,Reversible,Set,MappingView,ItemsView",
                "dict_values isinstance Iterable,Sized,Container,Hashable,Collection,Reversible,MappingView,ValuesView",
                "generator isinstance Iterable,Iterator,Hashable,Generator",
                "int isinstance Hashable",
                "bool isinstance Hashable",
                "none isinstance Hashable",
                "type isinstance Hashable,Callable",
                "function isinstance Hashable,Callable",
                "builtin isinstance Hashable,Callable",
                "counter isinstance Iterable,Sized,Container,Collection,Reversible,Mapping,MutableMapping",
                "defaultdict isinstance Iterable,Sized,Container,Collection,Reversible,Mapping,MutableMapping",
                "odict isinstance Iterable,Sized,Container,Collection,Reversible,Mapping,MutableMapping",
                "deque isinstance Iterable,Sized,Container,Collection,Reversible,Sequence,MutableSequence",
                "slice isinstance Hashable",
                "map isinstance Iterable,Iterator,Hashable",
                "issubclass True True True True True False",
                "issubclass2 True False False True",
                "issubclass3 True True True True",
                "issubclass4 True False False True",
                "duck False False False True False",
                "hook True NotImplemented True",
                "register False True True True False",
                "empty ['Hashable']",
                "empty2 True False False False",
                "concrete True True (<class '__main__.Concrete'>, <class 'collections.abc.Sequence'>, <class 'collections.abc.Reversible'>, <class 'collections.abc.Collection'>, <class 'collections.abc.Sized'>, <class 'collections.abc.Iterable'>, <class 'collections.abc.Container'>, <class 'object'>)",
                "instantiate-mapping !! TypeError Can't instantiate abstract class Mapping without an implementation for abstract methods '__getitem__', '__iter__', '__len__'",
                "instantiate-sequence !! TypeError Can't instantiate abstract class Sequence without an implementation for abstract methods '__getitem__', '__len__'",
                "instantiate-iterable !! TypeError Can't instantiate abstract class Iterable without an implementation for abstract method '__iter__'",
                "instantiate-iterator !! TypeError Can't instantiate abstract class Iterator without an implementation for abstract method '__next__'",
                "instantiate-mutable-sequence !! TypeError Can't instantiate abstract class MutableSequence without an implementation for abstract methods '__delitem__', '__getitem__', '__len__', '__setitem__', 'insert'",
                "instantiate-partial !! TypeError Can't instantiate abstract class P without an implementation for abstract method '__len__'",
                "instantiate-concrete => ok",
                "module-names True Iterable"
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
