using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SuperStorageCompatibilityTests
{
    [Fact]
    public Task SuperStorageSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class L(list):
                def __init__(self, values):
                    super().__init__(values)
                    self.tag = "t"

                def push(self, value):
                    super().append(value)

                def describe(self):
                    return super().__repr__()

                def total(self):
                    return super().__len__()

                @classmethod
                def build(cls, values):
                    instance = super().__new__(cls)
                    super(L, instance).extend(values)
                    return instance


            l = L([1, 2])
            l.push(3)
            print("list", l, l.total(), l.describe(), l.tag, type(l.build([9, 8])).__name__, l.build([9, 8]))


            class D(dict):
                def __init__(self, **kwargs):
                    super().__init__(**kwargs)

                def merged(self, other):
                    result = D()
                    super(D, result).update(other)
                    return result

                @property
                def size(self):
                    return super().__len__()


            d = D(a=1)
            print("dict", d, d.size, d.merged({"b": 2}), type(d.merged({"b": 2})).__name__)


            class P(tuple):
                def __new__(cls, *values):
                    return super().__new__(cls, values)

                def first(self):
                    return super().__getitem__(0)


            p = P(1, 2)
            print("tuple", p, type(p).__name__, p.first())


            class Tagged(list):
                def __setattr__(self, name, value):
                    super().__setattr__(name, value.strip())

                def __getattribute__(self, name):
                    return super().__getattribute__(name)


            t = Tagged([1])
            t.tag = "  x  "
            print("attrs", t.tag, t)


            def probe(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            probe("unbound-init", lambda: list.__init__(L([9]), [2, 3]))
            probe("unbound-append", lambda: list.append(L([1]), 5))
            probe("dict-init-merge", lambda: (lambda x: (dict.__init__(x, {"b": 2}), x)[1])(L([1]) if False else D(a=1)))
            probe("plain-dict-init", lambda: (lambda x: (x.__init__({"b": 2}), x)[1])({"a": 1}))
            probe("plain-dict-init-empty", lambda: (lambda x: (x.__init__(), x)[1])({"a": 1}))
            probe("list-init-replace", lambda: (lambda x: (x.__init__([2]), x)[1])([1]))
            probe("super-missing", lambda: super(L, L([1])).nope)
            probe("super-noargs", lambda: super())
            probe("super-type", lambda: type(super(L, L([1]))).__name__)
            probe("super-isinstance", lambda: isinstance(super(L, L([1])), super))
            probe("super-repr", lambda: repr(super(L, L([1]))))

            try:
                class Mix(dict, list):
                    pass
            except TypeError as error:
                print("conflict", "TypeError", error)


            class Base(list):
                def label(self):
                    return "base"


            class Middle(Base):
                def label(self):
                    return super().label() + "+middle"


            class Leaf(Middle):
                def label(self):
                    return super().label() + "+leaf"


            print("chain", Leaf([1]).label(), super(Middle, Leaf([2])).label())
            """
        );
}
