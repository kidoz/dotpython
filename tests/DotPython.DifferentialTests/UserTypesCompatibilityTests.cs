using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UserTypesCompatibilityTests
{
    [Fact]
    public Task UserTypeSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import collections
            from collections import ChainMap, UserDict, UserList, UserString
            import collections.abc as abc
            import copy

            print("catalogue", all(hasattr(collections, name) for name in collections.__all__))
            print("bases", UserDict.__bases__, UserList.__bases__, UserString.__bases__, ChainMap.__bases__)
            print("abstracts", UserDict.__abstractmethods__, UserList.__abstractmethods__, UserString.__abstractmethods__, ChainMap.__abstractmethods__)
            print("isinstance", isinstance(UserDict(), abc.MutableMapping), isinstance(UserList(), abc.MutableSequence), isinstance(UserString("a"), abc.Sequence), isinstance(ChainMap(), abc.MutableMapping))
            print("hash", UserDict.__hash__ is None, UserList.__hash__ is None, UserString.__hash__ is None, ChainMap.__hash__ is None)

            d = UserDict({"a": 1}, b=2)
            print("udict", sorted(d.data.items()), len(d), list(d), "a" in d, d.get("z"), d.get("z", 9), repr(d))
            d["c"] = 3
            del d["a"]
            print("udict-mutate", d.data, sorted(d.items()), d.pop("c"), d.pop("zz", 7), d.setdefault("s", 1), sorted(d.data))
            d.update({"u": 1}, v=2)
            print("udict-update", sorted(d.data.items()), sorted(d.keys()), sorted(d.values()), len(d.items()))
            print("udict-compare", UserDict({"a": 1}) == {"a": 1}, UserDict({"a": 1}) == UserDict({"a": 1}), UserDict({"a": 1}) == {"b": 2})
            print("udict-or", (UserDict({"a": 1}) | {"b": 2}).data, ({"b": 2} | UserDict({"a": 1})).data, type(UserDict({"a": 1}) | UserDict({"b": 2})).__name__)
            try:
                UserDict({"a": 1}) | [1]
            except TypeError as error:
                print("udict-or-other", error)
            merged = UserDict({"a": 1})
            merged |= {"b": 2}
            merged |= UserDict({"c": 3})
            print("udict-ior", sorted(merged.data.items()), type(merged).__name__)
            print("udict-copy", UserDict({"a": 1}).copy().__class__.__name__, copy.copy(UserDict({"a": [1]})).data)
            print("udict-fromkeys", UserDict.fromkeys("ab").data, UserDict.fromkeys(["a"], 5).data, type(UserDict.fromkeys("a")).__name__)
            try:
                UserDict({"a": 1})["z"]
            except KeyError as error:
                print("udict-keyerror", repr(error), error.args)


            class Table(UserDict):
                def __missing__(self, key):
                    return "missing:" + key


            print("udict-missing", Table({"a": 1})["z"], Table({"a": 1})["a"], "z" in Table({"a": 1}), Table({"a": 1}).get("z"))
            print("udict-sub", type(Table({"a": 1}).copy()).__name__, Table.fromkeys("a").data, (Table({"a": 1}) | {"b": 2}).__class__.__name__)

            items = UserList([1, 2, 3])
            print("ulist", items, items.data, len(items), 2 in items, 9 in items, items[0], items[-1], items[1:], type(items[1:]).__name__)
            print("ulist-init", UserList(UserList([1, 2])).data, UserList((1, 2)).data, UserList("ab").data, UserList(range(3)).data, UserList(None).data)
            print("ulist-compare", items == [1, 2, 3], items == UserList([1, 2, 3]), items == (1, 2, 3), items < [1, 2, 4], items > [1, 2], items <= [1, 2, 3], items >= [1], items != [1])
            print("ulist-add", (items + [4]).data, (items + UserList([4])).data, ([0] + items).data, (UserList([0]) + items).data, (items + (4,)).data, (items + "ab").data)
            print("ulist-add-type", type(items + [4]).__name__, type([0] + items).__name__, (items * 2).data, (2 * items).data, type(items * 2).__name__)
            growing = UserList([1])
            growing += [2]
            growing += UserList([3])
            growing *= 2
            print("ulist-inplace", growing.data, type(growing).__name__)
            print("ulist-empty", repr(UserList()), bool(UserList()), bool(UserList([0])), str(UserList([1])))
            print("ulist-cast", UserList([1])._UserList__cast(UserList([2])), UserList([1])._UserList__cast([2]))
            print("ulist-reversed", list(reversed(UserList([1, 2, 3]))))
            numbers = UserList([1, 2, 3, 2])
            print("ulist-methods", numbers.pop(), numbers.pop(0), numbers.data, numbers.count(2), numbers.index(2))
            numbers.remove(2)
            numbers.reverse()
            numbers.extend([9])
            numbers.extend(UserList([8]))
            numbers.sort()
            numbers.sort(reverse=True)
            print("ulist-methods2", numbers.data)
            numbers.clear()
            print("ulist-clear", numbers.data, len(numbers))
            print("ulist-copy", UserList([1, 2]).copy().data, copy.copy(UserList([1, 2])).data, type(UserList([1, 2]).copy()).__name__)
            print("ulist-slice", UserList([1, 2, 3])[0:2].data, UserList([1, 2, 3])[5:].data)
            try:
                hash(UserList())
            except TypeError as error:
                print("ulist-unhashable", error)


            class Numbers(UserList):
                pass


            print("ulist-sub", Numbers([1, 2])[0:1].__class__.__name__, (Numbers([1]) + [2]).__class__.__name__, Numbers([1]).copy().__class__.__name__, (Numbers([1]) * 2).__class__.__name__)

            text = UserString("hello")
            print("ustr", text, text.data, repr(text), len(text), "e" in text, "z" in text, text[0], text[1:], type(text[1:]).__name__)
            print("ustr-init", UserString(UserString("ab")).data, UserString(5).data, str(UserString(5)), type(UserString(5)).__name__)
            print("ustr-compare", text == "hello", text == UserString("hello"), text == "other", text < "z", text > "a", text <= "hello", text >= "h", text == 5, "hello" == text)
            print("ustr-scope", UserString("he") in text, "he" in text, UserString("he").__eq__("he"))
            print("ustr-add", (text + "!").data, (text + UserString("!")).data, (text + 5).data, ("x" + text).data, (UserString("x") + text).data, (5 + text).data)
            print("ustr-mul", (UserString("ab") * 2).data, (2 * UserString("ab")).data)
            print("ustr-mod", (UserString("%s-%d") % ("a", 1)).data, repr("%s" % text), type("%s" % text).__name__)
            print("ustr-hash", hash(UserString("ab")) == hash("ab"), hash(UserString("ab")) == hash(UserString("ab")))
            print("ustr-convert", len(UserString("")), bool(UserString("")), bool(UserString("a")), str(UserString(5)), repr(UserString(5)), int(UserString("42")), float(UserString("1.5")), complex(UserString("1j")))
            print("ustr-methods", UserString("aBc").upper().data, UserString("aBc").lower().data, UserString(" a ").strip().data, UserString("a,b").split(","), UserString("a").center(3, "-").data)
            print("ustr-methods2", UserString("ab").find("b"), UserString("ab").index("b"), UserString("ab").count("a"), UserString("ab").replace("a", "z").data)
            print("ustr-methods3", UserString("ab").startswith("a"), UserString("ab").endswith("b"), UserString("ab").join(["x"]), UserString("ab").encode())
            print("ustr-methods4", UserString("ab").removeprefix("a").data, UserString("ab").removesuffix("b").data, UserString("ab").casefold().data, UserString("ab").isalpha(), UserString("ab").zfill(4).data)
            print("ustr-maketrans", type(UserString.maketrans).__name__, UserString.maketrans("a", "b"), UserString("ab").maketrans("a", "b"))
            print("ustr-format", UserString("{}").format(5), UserString("{x}").format_map({"x": 1}))
            print("ustr-newargs", text.__getnewargs__(), copy.copy(text).data, type(copy.copy(text)).__name__)
            try:
                UserString("ab").index("z")
            except ValueError as error:
                print("ustr-index", error)

            chain = ChainMap()
            print("chain-empty", chain.maps, len(chain), bool(chain), repr(chain), list(chain), "a" in chain, chain.get("a"), chain.get("a", 9))
            chain = ChainMap({"a": 1}, {"b": 2, "a": 9})
            print("chain", chain.maps, chain["a"], chain["b"], len(chain), list(chain), sorted(chain), "a" in chain, "z" in chain, bool(chain))
            print("chain-repr", repr(ChainMap({"a": 1}, {"b": 2})), ChainMap({"a": 1}) == {"a": 1}, ChainMap({"a": 1}) == ChainMap({"a": 1}))
            try:
                chain["z"]
            except KeyError as error:
                print("chain-missing", repr(error), error.args)


            class Layered(ChainMap):
                def __missing__(self, key):
                    return "gone:" + key


            print("chain-sub-missing", Layered({"a": 1})["z"], ChainMap({"a": 1})["a"], type(Layered({"a": 1}).new_child()).__name__, type(Layered({"a": 1}).parents).__name__)
            chain["x"] = 9
            del chain["a"]
            print("chain-mutate", chain.maps)
            try:
                del chain["zz"]
            except KeyError as error:
                print("chain-del-miss", repr(error), error.args)
            print("chain-pop", chain.pop("x"), chain.pop("zz", 1))
            try:
                chain.pop("zz")
            except KeyError as error:
                print("chain-pop-miss", repr(error), error.args)
            print("chain-popitem", ChainMap({"a": 1}).popitem())
            try:
                ChainMap().popitem()
            except KeyError as error:
                print("chain-popitem-empty", repr(error), error.args)
            cleared = ChainMap({"a": 1}, {"b": 2})
            cleared.clear()
            print("chain-clear", cleared.maps, len(cleared))
            print("chain-child", ChainMap({"a": 1}).new_child().maps, ChainMap({"a": 1}).new_child({"b": 2}).maps, ChainMap({"a": 1}).new_child(c=3).maps, ChainMap({"a": 1}).new_child({"b": 2}, c=3).maps)
            print("chain-parents", ChainMap({"a": 1}, {"b": 2}).parents.maps, type(ChainMap({"a": 1}).parents).__name__)
            print("chain-copy", ChainMap({"a": [1]}, {"b": 2}).copy().maps, copy.copy(ChainMap({"a": 1})).maps, ChainMap.__copy__ is ChainMap.copy)
            print("chain-fromkeys", ChainMap.fromkeys("ab").maps, ChainMap.fromkeys(["a"], 5).maps)
            print("chain-or", (ChainMap({"a": 1}, {"b": 2}) | {"c": 3}).maps, (ChainMap({"a": 1}) | UserDict({"b": 2})).maps, ({"z": 0} | ChainMap({"a": 1}, {"a": 9, "b": 2})).maps)
            try:
                ChainMap({"a": 1}) | [1]
            except TypeError as error:
                print("chain-or-other", error)
            growing_chain = ChainMap({"a": 1})
            growing_chain |= {"b": 2}
            print("chain-ior", growing_chain.maps, list(ChainMap({"a": 1, "b": 2}, {"c": 3})), list(ChainMap({}, {"a": 1})), len(ChainMap({"a": 1}, {"a": 2})))
            print("chain-views", sorted(ChainMap({"a": 1}, {"b": 2}).keys()), sorted(ChainMap({"a": 1}, {"b": 2}).items()), sorted(ChainMap({"a": 1}, {"b": 2}).values()))
            """
        );
}
