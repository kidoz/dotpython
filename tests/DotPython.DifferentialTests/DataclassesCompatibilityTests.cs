using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `dataclasses` surface that is implemented, checked against CPython. Every case
/// catches what it exercises, so the reference exits 0; the module is compared through
/// printed values only — generated methods are native values, so their reprs (which
/// would carry an address in CPython) are never printed.
/// </summary>
public sealed class DataclassesCompatibilityTests
{
    [Fact]
    public Task DecoratorGeneratesInitReprAndEq() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from dataclasses import dataclass, fields, is_dataclass

            @dataclass
            class Point:
                x: int
                y: int = 0

            p = Point(1)
            print(p)
            print(repr(p))
            print(str(p))
            print(p.x, p.y)
            print(p == Point(1, 0), p == Point(1, 2), p != Point(1, 2))
            print(p == (1, 0))
            print(Point.__name__, Point.__module__)
            print(sorted(Point.__dataclass_fields__))
            print(Point.__dataclass_params__)
            print(Point.__match_args__)
            print([f.name for f in fields(Point)])
            print(is_dataclass(Point), is_dataclass(Point(1)), is_dataclass(3))
            print(Point.__doc__)
            """
        );

    [Fact]
    public Task FieldOptionsDriveTheGeneratedMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            """
        );

    [Fact]
    public Task ClassVarAndInitVarStayOutOfFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            from dataclasses import dataclass, fields, is_dataclass

            @dataclass
            class WithVars:
                x: int
                cv: typing.ClassVar[int] = 7
                iv: 'typing.ClassVar[str]' = 'palette'

            print([f.name for f in fields(WithVars)])
            print(WithVars(1).cv, WithVars(1).iv)

            from dataclasses import InitVar

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
            """
        );

    [Fact]
    public Task InheritanceOrdersBaseFieldsFirstAndOverridesInPlace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from dataclasses import dataclass, fields

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

            print([(f.name, f.type) for f in fields(Leaf)])
            print(Leaf())
            print(repr(Leaf(1, 'b', 2.5, True)))
            print([f.name for f in fields(Base)])

            @dataclass
            class Sub(Base):
                b: str = 'sub'

            print(Sub(1), Sub(1, 'x'))
            """
        );

    [Fact]
    public Task GeneratedInitBindsLikeCpythonAndReportsItsOwnErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            show('keyword only', lambda: P(1, y=2, z=3, w=4))

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
            show('kw given', lambda: OnlyKw(a=1))

            @dataclass
            class Empty:
                pass

            print(Empty())
            show('empty kw', lambda: Empty(z=1))
            show('empty pos', lambda: Empty(1))
            """
        );

    [Fact]
    public Task OrderingMethodsFollowOrderTrue() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            print(Ord(1) <= Ord(1), Ord(1) >= Ord(1))
            print(sorted([Ord(3), Ord(1), Ord(2)]))
            print(Ord(1) == Ord(1), Ord(1) == Ord(1, 1))
            show('order vs int', lambda: Ord(1) < 3)

            @dataclass(eq=False)
            class NoEq:
                v: int

            show('no eq', lambda: NoEq(1) < NoEq(2))

            def build():
                @dataclass(order=True, eq=False)
                class Bad:
                    v: int

            show('order needs eq', build)
            """
        );

    [Fact]
    public Task HashingFollowsEqFrozenAndFieldHash() =>
        CompatibilityOracle.AssertMatchesAsync(
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

            @dataclass(eq=False)
            class NoEq:
                x: int

            n = NoEq(1)
            print('keeps object hash', NoEq.__hash__ is not None and hash(n) == hash(n))

            @dataclass(frozen=True)
            class Fr:
                x: int

            print('frozen hashes equal', hash(Fr(1)) == hash(Fr(1)))
            print('frozen eq', Fr(1) == Fr(1), Fr(1) == Fr(2))
            print('frozen hash derived', Fr(1).__hash__() == Fr(1).__hash__())

            @dataclass(unsafe_hash=True)
            class Un:
                x: int

            print('unsafe hashes equal', hash(Un(2)) == hash(Un(2)))

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
            show('set of dataclass', lambda: {Sel(1, 2)})
            """
        );

    [Fact]
    public Task FrozenInstancesRefuseMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            print('frozen error is exception', issubclass(FrozenInstanceError, AttributeError))

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

    [Fact]
    public Task FieldsAsdictAstupleAndReplaceRoundTrip() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            show('replace plain instance', lambda: replace(type('Plain', (), {})(), x=1))
            """
        );

    [Fact]
    public Task MakeDataclassBuildsClassesFromSpecs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import dataclasses
            from dataclasses import make_dataclass, fields, field


            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))


            Simple = make_dataclass('Simple', ['x', 'y'])
            print(Simple.__name__, Simple.__match_args__)
            s = Simple(1, 2)
            print(s)
            print([f.name for f in fields(Simple)])

            Detailed = make_dataclass('Detailed', [('a', int), ('b', str, 'hi')])
            print(Detailed(1), Detailed(1, 'yo'))
            print([(f.name, f.type) for f in fields(Detailed)])

            Pair = make_dataclass('Pair', [['n', int], ['m', int, 4]])
            print(Pair(1))

            Namespaced = make_dataclass('Namespaced', [], namespace={'__doc__': 'mine', 'extra': 9})
            print(Namespaced.__doc__, Namespaced.extra)

            print(make_dataclass('N', [('x', int)], namespace={'__annotations__': {'x': str}})(1).__class__.__name__)
            print(make_dataclass('Kw', ['a'], kw_only=True).__dataclass_params__)
            Ord = make_dataclass('Ord', [('v', int)], order=True)
            print(Ord(1) < Ord(2), Ord(2) > Ord(1), Ord(1) <= Ord(1))

            seen = {}

            def dec(cls, **kw):
                seen.update(kw)
                cls.marked = True
                return cls

            Marked = make_dataclass('Marked', [('v', int)], decorator=dec)
            print(Marked.marked, sorted(seen))
            print(Marked.__name__, hasattr(Marked, '__dataclass_fields__'))

            Custom = make_dataclass('Custom', [('v', int)], decorator=dataclasses.dataclass)
            print(Custom(3))

            show('int item', lambda: make_dataclass('B', [5]))
            show('long tuple', lambda: make_dataclass('C', [('a', int, 1, 2)]))
            show('empty tuple', lambda: make_dataclass('D', [()]))
            show('bad name', lambda: make_dataclass('E', ['x y']))
            show('non-str name', lambda: make_dataclass('F', [(5, int)]))
            show('keyword name', lambda: make_dataclass('G', ['class']))
            show('duplicate', lambda: make_dataclass('H', ['x', ('x', int)]))
            show('missing args', lambda: make_dataclass('I'))
            show('too many args', lambda: make_dataclass('J', [], (), 1))
            show('duplicate cls_name', lambda: make_dataclass('K', cls_name='L', fields=[]))
            show('bad first arg', lambda: make_dataclass(3, []))
            """
        );

    [Fact]
    public Task DecoratorContractAndErrorSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from dataclasses import dataclass, field, replace, fields, FrozenInstanceError


            def show(label, fn):
                try:
                    print(label, '->', repr(fn()))
                except Exception as e:
                    print(label, '!!', type(e).__name__, str(e))


            show('non-class', lambda: dataclass(3))
            show('two positionals', lambda: dataclass(1, 2))
            show('unknown option', lambda: dataclass(type('C', (), {}), nope=True))
            show('mutable default', lambda: dataclass(type('M', (), {'__annotations__': {'a': list}, 'a': []})))
            show('both default kinds', lambda: field(default=1, default_factory=list))
            show('field positional', lambda: field(1))
            show('dataclass keyword cls', lambda: dataclass(cls=type('C', (), {})))
            show('weakref without slots', lambda: dataclass(type('W', (), {}), weakref_slot=True))

            def build():
                @dataclass
                class Follows:
                    a: int = 1
                    b: int

            show('follows default', build)

            @dataclass
            class Base:
                x: int

            print(replace(Base(1), x=2))
            show('replace initvar', lambda: replace(Base(1), y=2))
            print('field class', field(default=1).__class__.__name__, isinstance(field(default=1), field().__class__))
            print('exceptions', FrozenInstanceError.__name__, FrozenInstanceError.__bases__[0].__name__)
            """
        );

    [Fact]
    public Task FieldsMetadataAndDocCarryCpythonValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from dataclasses import dataclass, field, fields

            @dataclass
            class C:
                x: int = field(metadata={'help': 'the x', 'tags': (1, 2)})

            f = C.__dataclass_fields__['x']
            print(f.name, f.type, f.init, f.repr, f.compare, f.hash, f.kw_only)
            print(dict(f.metadata), type(f.metadata).__name__)
            print([(g.name, g.default == field().default) for g in fields(C)])
            print(C(1))
            """
        );
}
