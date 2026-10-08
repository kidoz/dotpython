using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DataclassesExecutionTests
{
    [Fact]
    public void DecoratorGeneratesInitReprAndEq()
    {
        var output = Run(
            """
            from dataclasses import dataclass, fields, is_dataclass

            @dataclass
            class Point:
                x: int
                y: int = 0

            p = Point(1)
            print(p)
            print(repr(p))
            print(p.x, p.y)
            print(p == Point(1, 0), p == Point(1, 2), p != Point(1, 2))
            print(p == (1, 0))
            print(Point.__name__, Point.__module__)
            print(sorted(Point.__dataclass_fields__))
            print(Point.__match_args__)
            print([f.name for f in fields(Point)])
            print(is_dataclass(Point), is_dataclass(Point(1)), is_dataclass(3))
            """
        );

        Assert.Equal(
            Lines(
                "Point(x=1, y=0)",
                "Point(x=1, y=0)",
                "1 0",
                "True False True",
                "False",
                "Point __main__",
                "['x', 'y']",
                "('x', 'y')",
                "['x', 'y']",
                "True True False"
            ),
            output
        );
    }

    [Fact]
    public void FieldOptionsDriveTheGeneratedMethods()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field, fields

            @dataclass
            class C:
                a: int
                b: int = field(default=2, init=False)
                c: list = field(default_factory=list)
                d: int = field(default=4, repr=False)
                e: int = field(default=5, compare=False)
                f: int = field(default=6, kw_only=True)
                g: dict = field(default_factory=dict, metadata={'unit': 'kg'})

            c = C(1)
            print(c)
            print(c.a, c.b, c.c, c.d, c.e, c.f, c.g)
            print(C(1, c=[9]) == C(1, c=[9]))
            print(C(1) == C(1, e=7))
            print([f.name for f in fields(C)])
            print(C.__dataclass_fields__['g'].metadata['unit'])
            print(C(1, f=8).f)
            print(C(1, c=[]) != C(1, c=[]))
            """
        );

        Assert.Equal(
            Lines(
                "C(a=1, b=2, c=[], e=5, f=6, g={})",
                "1 2 [] 4 5 6 {}",
                "True",
                "True",
                "['a', 'b', 'c', 'd', 'e', 'f', 'g']",
                "kg",
                "8",
                "False"
            ),
            output
        );
    }

    [Fact]
    public void ClassVarAndInitVarStayOutOfFields()
    {
        var output = Run(
            """
            import typing
            from dataclasses import dataclass, field, fields, InitVar

            @dataclass
            class WithVars:
                x: int
                cv: typing.ClassVar[int] = 7
                iv: 'typing.ClassVar[str]' = 'palette'

            print([f.name for f in fields(WithVars)])
            print(WithVars(1).cv, WithVars(1).iv)

            @dataclass
            class WithInit:
                a: int
                b: InitVar[int] = 10
                def __post_init__(self, b):
                    self.a += b

            print([f.name for f in fields(WithInit)])
            w = WithInit(1)
            print(w.a, w == WithInit(1))
            print(WithInit.__match_args__)
            print(WithInit(1, 2).a)
            """
        );

        Assert.Equal(Lines("['x']", "7 palette", "['a']", "11 True", "('a', 'b')", "3"), output);
    }

    [Fact]
    public void InheritanceOrdersBaseFieldsFirstAndOverridesInPlace()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field, fields

            @dataclass
            class Base:
                a: int
                b: str = 'base'

            @dataclass
            class Mid(Base):
                c: float = 1.5

            @dataclass
            class Leaf(Mid):
                a: int = 100
                d: bool = False

            print([f.name for f in fields(Leaf)])
            print(Leaf())
            print(repr(Leaf(1, 'b', 2.5, True)))
            print([f.name for f in fields(Base)])

            @dataclass
            class Sub(Base):
                b: str = 'sub'

            print(Sub(1), Sub(1, 'x'))
            """
        );

        Assert.Equal(
            Lines(
                "['a', 'b', 'c', 'd']",
                "Leaf(a=100, b='base', c=1.5, d=False)",
                "Leaf(a=1, b='b', c=2.5, d=True)",
                "['a', 'b']",
                "Sub(a=1, b='sub') Sub(a=1, b='x')"
            ),
            output
        );
    }

    [Fact]
    public void GeneratedInitBindsLikeCpythonAndReportsItsOwnErrors()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            @dataclass
            class P:
                x: int
                y: int = 0
                z: int = field(kw_only=True, default=5)

            print(P(1), P(1, 2), P(x=1, y=2, z=3), P(1, z=9))
            show('missing', lambda: P())
            show('too many', lambda: P(1, 2, 3))
            show('duplicate', lambda: P(1, 2, x=3))
            show('unexpected', lambda: P(1, w=4))

            @dataclass
            class Two:
                a: int
                b: int

            show('two missing', lambda: Two())
            show('one missing', lambda: Two(1))

            @dataclass
            class OnlyKw:
                a: int = field(kw_only=True)
                b: int = field(kw_only=True, default=2)

            show('kw missing', lambda: OnlyKw())
            print('kw given', OnlyKw(a=1))

            @dataclass
            class Empty:
                pass

            print(Empty())
            show('empty kw', lambda: Empty(z=1))
            """
        );

        Assert.Equal(
            Lines(
                "P(x=1, y=0, z=5) P(x=1, y=2, z=5) P(x=1, y=2, z=3) P(x=1, y=0, z=9)",
                "missing !! TypeError P.__init__() missing 1 required positional argument: 'x'",
                "too many !! TypeError P.__init__() takes from 2 to 3 positional arguments but 4 were given",
                "duplicate !! TypeError P.__init__() got multiple values for argument 'x'",
                "unexpected !! TypeError P.__init__() got an unexpected keyword argument 'w'",
                "two missing !! TypeError Two.__init__() missing 2 required positional arguments: 'a' and 'b'",
                "one missing !! TypeError Two.__init__() missing 1 required positional argument: 'b'",
                "kw missing !! TypeError OnlyKw.__init__() missing 1 required keyword-only argument: 'a'",
                "kw given OnlyKw(a=1, b=2)",
                "Empty()",
                "empty kw !! TypeError Empty.__init__() got an unexpected keyword argument 'z'"
            ),
            output
        );
    }

    [Fact]
    public void OrderingComesFromTheOrderFlag()
    {
        var output = Run(
            """
            from dataclasses import dataclass

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            @dataclass(order=True)
            class Ord:
                r: int
                s: int = 0

            print(Ord(1) < Ord(2), Ord(2) > Ord(1), Ord(1) <= Ord(1), Ord(1) >= Ord(1, 1))
            print(sorted([Ord(3), Ord(1), Ord(2)]))
            show('order vs int', lambda: Ord(1) < 3)

            @dataclass(eq=False)
            class NoEq:
                v: int

            show('no eq', lambda: NoEq(1) < NoEq(2))

            show('order needs eq', lambda: dataclass(order=True, eq=False)(type('Bad', (), {})))
            """
        );

        Assert.Equal(
            Lines(
                "True True True False",
                "[Ord(r=1, s=0), Ord(r=2, s=0), Ord(r=3, s=0)]",
                "order vs int !! TypeError '<' not supported between instances of 'Ord' and 'int'",
                "no eq !! TypeError '<' not supported between instances of 'NoEq' and 'NoEq'",
                "order needs eq !! ValueError eq must be true if order is true"
            ),
            output
        );
    }

    [Fact]
    public void HashingFollowsEqFrozenAndUnsafeHash()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            @dataclass
            class Eq:
                x: int

            show('eq default', lambda: hash(Eq(1)))
            print('hash is none', Eq.__hash__ is None, Eq(1) == Eq(1))

            @dataclass(eq=False)
            class NoEq:
                x: int

            n = NoEq(1)
            print('keeps object hash', NoEq.__hash__ is not None and hash(n) == hash(n))

            @dataclass(frozen=True)
            class Fr:
                x: int

            print('frozen', hash(Fr(1)) == hash(Fr(1)), Fr(1) == Fr(1), Fr(1) == Fr(2))

            @dataclass(unsafe_hash=True)
            class Un:
                x: int

            print('unsafe', hash(Un(2)) == hash(Un(2)), Un(2) == Un(2))

            @dataclass(unsafe_hash=True)
            class Sel:
                a: int = field(hash=False)
                b: int = field(hash=True)
                c: int = 0

            print('hash flags', hash(Sel(1, 2, 3)) == hash(Sel(9, 2, 3)))

            @dataclass(frozen=True)
            class FrList:
                xs: list

            show('unhashable field', lambda: hash(FrList([1])))
            """
        );

        Assert.Equal(
            Lines(
                "eq default !! TypeError unhashable type: 'Eq'",
                "hash is none True True",
                "keeps object hash True",
                "frozen True True False",
                "unsafe True True",
                "hash flags True",
                "unhashable field !! TypeError unhashable type: 'list'"
            ),
            output
        );
    }

    [Fact]
    public void FrozenInstancesRefuseMutation()
    {
        var output = Run(
            """
            from dataclasses import dataclass, FrozenInstanceError, replace

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            @dataclass(frozen=True)
            class Pt:
                x: int
                y: int = 0

            p = Pt(1)
            print(p, p.x, p.y)
            show('set', lambda: setattr(p, 'x', 5))
            show('new attr', lambda: setattr(p, 'z', 5))
            show('del', lambda: delattr(p, 'x'))
            print('replace', replace(p, y=9))
            print('is attribute error', issubclass(FrozenInstanceError, AttributeError))

            @dataclass(frozen=False)
            class Mut:
                x: int

            m = Mut(1)
            m.x = 2
            m.z = 3
            print(m, m.z)

            @dataclass(frozen=True)
            class Sub(Pt):
                pass

            show('subclass set', lambda: setattr(Sub(1), 'x', 5))
            show('subclass other', lambda: setattr(Sub(1), 'z', 5))
            """
        );

        Assert.Equal(
            Lines(
                "Pt(x=1, y=0) 1 0",
                "set !! FrozenInstanceError cannot assign to field 'x'",
                "new attr !! FrozenInstanceError cannot assign to field 'z'",
                "del !! FrozenInstanceError cannot delete field 'x'",
                "replace Pt(x=1, y=9)",
                "is attribute error True",
                "Mut(x=2) 3",
                "subclass set !! FrozenInstanceError cannot assign to field 'x'",
                "subclass other !! FrozenInstanceError cannot assign to field 'z'"
            ),
            output
        );
    }

    [Fact]
    public void FieldsAsdictAstupleAndReplaceRoundTrip()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field, fields, asdict, astuple, replace

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            @dataclass
            class Inner:
                z: int = 0

            @dataclass
            class Outer:
                a: int
                b: Inner
                c: list
                d: dict

            o = Outer(1, Inner(2), [1, Inner(3)], {'k': Inner(4)})
            print(asdict(o))
            print(astuple(o))
            print(asdict(o, dict_factory=lambda items: dict(sorted(items))))
            print(astuple(o, tuple_factory=list))
            print(replace(o, a=9))
            print(replace(o, a=9) == o)
            show('replace unknown', lambda: replace(o, nope=1))
            show('replace non dataclass', lambda: replace(3, a=1))
            show('asdict non dataclass', lambda: asdict(3))
            show('astuple non dataclass', lambda: astuple(3))
            show('fields non dataclass', lambda: fields(3))
            show('fields plain class', lambda: fields(type('Plain', (), {})))
            """
        );

        Assert.Equal(
            Lines(
                "{'a': 1, 'b': {'z': 2}, 'c': [1, {'z': 3}], 'd': {'k': {'z': 4}}}",
                "(1, (2,), [1, (3,)], {'k': (4,)})",
                "{'a': 1, 'b': {'z': 2}, 'c': [1, {'z': 3}], 'd': {'k': {'z': 4}}}",
                "[1, [2], [1, [3]], {'k': [4]}]",
                "Outer(a=9, b=Inner(z=2), c=[1, Inner(z=3)], d={'k': Inner(z=4)})",
                "False",
                "replace unknown !! TypeError Outer.__init__() got an unexpected keyword argument 'nope'",
                "replace non dataclass !! TypeError replace() should be called on dataclass instances",
                "asdict non dataclass !! TypeError asdict() should be called on dataclass instances",
                "astuple non dataclass !! TypeError astuple() should be called on dataclass instances",
                "fields non dataclass !! TypeError must be called with a dataclass type or instance",
                "fields plain class !! TypeError must be called with a dataclass type or instance"
            ),
            output
        );
    }

    [Fact]
    public void MakeDataclassBuildsClassesFromSpecs()
    {
        var output = Run(
            """
            import dataclasses
            from dataclasses import make_dataclass, fields, field

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            Simple = make_dataclass('Simple', ['x', 'y'])
            print(Simple.__name__, Simple.__match_args__, Simple(1, 2))

            Detailed = make_dataclass('Detailed', [('a', int), ('b', str, 'hi')])
            print(Detailed(1), Detailed(1, 'yo'))
            print([(f.name, f.type) for f in fields(Detailed)])

            Pair = make_dataclass('Pair', [['n', int], ['m', int, 4]])
            print(Pair(1))

            Namespaced = make_dataclass('Namespaced', [], namespace={'__doc__': 'mine', 'extra': 9})
            print(Namespaced.__doc__, Namespaced.extra)

            print(make_dataclass('Kw', ['a'], kw_only=True).__dataclass_params__)

            seen = {}

            def dec(cls, **kw):
                seen.update(kw)
                cls.marked = True
                return cls

            Marked = make_dataclass('Marked', [('v', int)], decorator=dec)
            print(Marked.marked, Marked.__name__, sorted(seen))

            Custom = make_dataclass('Custom', [('v', int)], decorator=dataclasses.dataclass)
            print(Custom(3))

            show('int item', lambda: make_dataclass('B', [5]))
            show('long tuple', lambda: make_dataclass('C', [('a', int, 1, 2)]))
            show('bad name', lambda: make_dataclass('E', ['x y']))
            show('non-str name', lambda: make_dataclass('F', [(5, int)]))
            show('keyword name', lambda: make_dataclass('G', ['class']))
            show('duplicate', lambda: make_dataclass('H', ['x', ('x', int)]))
            show('missing args', lambda: make_dataclass('I'))
            show('duplicate cls_name', lambda: make_dataclass('K', cls_name='L', fields=[]))
            show('bad first arg', lambda: make_dataclass(3, []))
            """
        );

        Assert.Equal(
            Lines(
                "Simple ('x', 'y') Simple(x=1, y=2)",
                "Detailed(a=1, b='hi') Detailed(a=1, b='yo')",
                "[('a', <class 'int'>), ('b', <class 'str'>)]",
                "Pair(n=1, m=4)",
                "mine 9",
                "_DataclassParams(init=True,repr=True,eq=True,order=False,unsafe_hash=False,frozen=False,match_args=True,kw_only=True,slots=False,weakref_slot=False)",
                "True Marked ['eq', 'frozen', 'init', 'kw_only', 'match_args', 'order', 'repr', 'slots', 'unsafe_hash', 'weakref_slot']",
                "Custom(v=3)",
                "int item !! TypeError object of type 'int' has no len()",
                "long tuple !! TypeError Invalid field: ('a', <class 'int'>, 1, 2)",
                "bad name !! TypeError Field names must be valid identifiers: 'x y'",
                "non-str name !! TypeError Field names must be valid identifiers: 5",
                "keyword name !! TypeError Field names must not be keywords: 'class'",
                "duplicate !! TypeError Field name duplicated: 'x'",
                "missing args !! TypeError make_dataclass() missing 1 required positional argument: 'fields'",
                "duplicate cls_name !! TypeError make_dataclass() got multiple values for argument 'cls_name'",
                "bad first arg !! TypeError type.__new__() argument 1 must be str, not int"
            ),
            output
        );
    }

    [Fact]
    public void SlotsAndWeakrefOptionsFollowTheDeclaredBoundary()
    {
        var output = Run(
            """
            from dataclasses import dataclass

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            show('weakref without slots', lambda: dataclass(type('W', (), {}), weakref_slot=True))
            show('slots', lambda: dataclass(type('S', (), {}), slots=True))
            show('slots with weakref', lambda: dataclass(type('T', (), {}), slots=True, weakref_slot=True))

            @dataclass(slots=False)
            class Plain:
                x: int

            print(Plain(1))
            """
        );

        Assert.Equal(
            Lines(
                "weakref without slots !! TypeError weakref_slot is True but slots is False",
                "slots !! NotImplementedError dataclass(slots=True) is outside this runtime slice: "
                    + "the decorator cannot re-create the class around a slot layout.",
                "slots with weakref !! NotImplementedError dataclass(slots=True) is outside this runtime slice: "
                    + "the decorator cannot re-create the class around a slot layout.",
                "Plain(x=1)"
            ),
            output
        );
    }

    [Fact]
    public void DecoratorContractAndErrorSurface()
    {
        var output = Run(
            """
            from dataclasses import dataclass, field, FrozenInstanceError

            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))

            show('non-class', lambda: dataclass(3))
            show('two positionals', lambda: dataclass(1, 2))
            show('unknown option', lambda: dataclass(type('C', (), {}), nope=True))
            show('keyword cls', lambda: dataclass(cls=type('C', (), {})))
            show('both default kinds', lambda: field(default=1, default_factory=list))
            show('field positional', lambda: field(1))
            show('bare field', lambda: field().repr)

            show('mutable default', lambda: dataclass(type('M', (), {'__annotations__': {'a': list}, 'a': []})))

            def build():
                @dataclass
                class Follows:
                    a: int = 1
                    b: int

            show('follows default', build)
            print('field class', field(default=1).__class__.__name__,
                  isinstance(field(default=1), field().__class__))
            print('frozen error', FrozenInstanceError.__name__, FrozenInstanceError.__bases__[0].__name__)
            """
        );

        Assert.Equal(
            Lines(
                "non-class !! AttributeError 'int' object has no attribute '__module__'",
                "two positionals !! TypeError dataclass() takes from 0 to 1 positional arguments but 2 were given",
                "unknown option !! TypeError dataclass() got an unexpected keyword argument 'nope'",
                "keyword cls !! TypeError dataclass() got some positional-only arguments passed as keyword arguments: 'cls'",
                "both default kinds !! ValueError cannot specify both default and default_factory",
                "field positional !! TypeError field() takes 0 positional arguments but 1 was given",
                "bare field -> True",
                "mutable default !! ValueError mutable default <class 'list'> for field a is not allowed: use default_factory",
                "follows default !! TypeError non-default argument 'b' follows default argument 'a'",
                "field class Field True",
                "frozen error FrozenInstanceError AttributeError"
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
