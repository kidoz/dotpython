using Xunit;

namespace DotPython.DifferentialTests;

public sealed class MappingProxyCompatibilityTests
{
    [Fact]
    public Task ProxyHashDelegatesWithoutTruncatingCustomMappingHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            class Mapping:
                def __getitem__(self, key): return 1
                def __hash__(self): return self.result
            mapping = Mapping()
            proxy = MappingProxyType(mapping)
            for result in (2**40 + 7, 2**80 + 11, -1):
                mapping.result = result
                print(hash(mapping), hash(proxy), hash(MappingProxyType(proxy)))
            """
        );

    [Fact]
    public Task CustomMappingComparisonsPreserveReturnedObjectsAndReflectedOperators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            equal = ['equal']
            unequal = ['unequal']
            less = ['less']
            greater = ['greater']
            class Mapping:
                def __getitem__(self, key): return 1
                def __eq__(self, other): return equal
                def __ne__(self, other): return unequal
                def __lt__(self, other): return less
                def __gt__(self, other): return greater
            backing = Mapping()
            proxy = MappingProxyType(backing)
            other = MappingProxyType(Mapping())
            print((proxy == other) is equal, (proxy != other) is unequal)
            print((proxy < other) is less, (proxy > other) is greater)
            print((proxy == 0) is equal, (0 == proxy) is equal)
            print((proxy != 0) is unequal, (0 != proxy) is unequal)
            print((proxy < 0) is less, (0 < proxy) is greater)
            print((proxy > 0) is greater, (0 > proxy) is less)
            print(proxy is backing, proxy is other)
            """
        );

    [Fact]
    public Task ProxyTruthinessUsesMappingLengthRatherThanItsBooleanHook() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            class Mapping:
                def __getitem__(self, key): return 1
                def __len__(self):
                    print('length called')
                    return self.size
                def __bool__(self):
                    print('boolean called')
                    return True
            mapping = Mapping()
            mapping.size = 0
            proxy = MappingProxyType(mapping)
            print(bool(mapping), bool(proxy))
            mapping.size = 2
            print(bool(proxy))
            class NoLength:
                def __getitem__(self, key): return 1
                def __bool__(self):
                    print('unexpected boolean call')
                    return True
            try:
                bool(MappingProxyType(NoLength()))
            except TypeError as error:
                print(str(error))
            """
        );

    [Fact]
    public Task LiveViewIteratorsReadUpdatedValuesButRejectSizeChanges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {'a': 1, 'b': 2}
            proxy = MappingProxyType(backing)
            values = iter(proxy.values())
            items = iter(proxy.items())
            print(next(values), next(items))
            backing['b'] = 20
            print(next(values), next(items))
            for reverse in (False, True):
                backing = {'a': 1, 'b': 2}
                view = MappingProxyType(backing).items()
                iterator = reversed(view) if reverse else iter(view)
                print(next(iterator))
                backing['c'] = 3
                for attempt in range(2):
                    try:
                        next(iterator)
                    except RuntimeError:
                        print('size mutation rejected')
            """
        );

    [Fact]
    public Task ExhaustedMappingAndViewIteratorsStayExhaustedAfterMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for reverse in (False, True):
                for kind in ('mapping', 'keys', 'values', 'items'):
                    backing = {'a': 1}
                    proxy = MappingProxyType(backing)
                    iterable = proxy if kind == 'mapping' else getattr(proxy, kind)()
                    iterator = reversed(iterable) if reverse else iter(iterable)
                    print(reverse, kind, next(iterator), next(iterator, 'end'))
                    backing['b'] = 2
                    print(next(iterator, 'still exhausted'))
                    del backing['a']
                    print(next(iterator, 'still exhausted'))
            """
        );

    [Fact]
    public Task RecursiveLiveViewRepresentationsTerminate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for kind in ('keys', 'values', 'items'):
                backing = {}
                proxy = MappingProxyType(backing)
                view = getattr(proxy, kind)()
                backing['self'] = view
                print(view)
                print(view)
            """
        );

    [Fact]
    public Task ClassDictionaryProxiesAreLiveAndContainOnlyOwnBindings() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base:
                inherited = 1
            class Child(Base):
                own = 2
            proxy = Child.__dict__
            other = vars(Child)
            size = len(proxy)
            print(type(proxy).__name__, proxy is other, proxy == other)
            print(proxy['own'], 'inherited' in proxy, Child.inherited)
            Child.own = 3
            Child.added = 4
            print(proxy['own'], other['added'], len(proxy) - size)
            del Child.own
            print('own' in proxy, len(proxy) - size)
            Base.later = 5
            print('later' in proxy, Child.later)
            """
        );

    [Fact]
    public Task ClassNamespaceReadsDoNotBindDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    print('descriptor called')
                    return 8
            descriptor = Descriptor()
            class Example:
                value = descriptor
                def method(self): return 9
            proxy = vars(Example)
            print(proxy['value'] is descriptor, proxy.get('value') is descriptor)
            print(proxy['method'] is Example.method)
            print(Example.value)
            snapshot = proxy.copy()
            Example.value = 10
            print(snapshot['value'] is descriptor, proxy['value'])
            """
        );

    [Fact]
    public Task ClassMetadataChangesRespectNamespaceStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                pass
            proxy = Example.__dict__
            print(proxy['__module__'], '__name__' in proxy, '__qualname__' in proxy)
            Example.__name__ = 'Renamed'
            Example.__module__ = 'custom_module'
            print(Example.__name__, proxy['__module__'], '__name__' in proxy)
            print(vars(Example)['__module__'])
            """
        );

    [Fact]
    public Task PublicMappingProxyTypePreservesLiveBackingAndTypeIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {'first': 1}
            proxy = MappingProxyType(backing)
            nested = MappingProxyType(proxy)
            keyword = MappingProxyType(mapping=backing)
            class Example:
                pass
            print(type(proxy) is MappingProxyType, type(Example.__dict__) is MappingProxyType)
            print(isinstance(proxy, MappingProxyType), MappingProxyType.__name__)
            print(proxy is nested, proxy is keyword, proxy == nested == keyword)
            backing['second'] = 2
            print(proxy['second'], nested['second'], keyword['second'])
            print(str(proxy), repr(proxy))
            """
        );

    [Fact]
    public Task MappingReadsPreserveKeyTypesDefaultsLengthAndTruthiness() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {}
            proxy = MappingProxyType(backing)
            print(len(proxy), bool(proxy), proxy.get('missing'), proxy.get('missing', 12))
            backing[3] = 'integer'
            backing[(1, 2)] = 'tuple'
            backing['value'] = None
            print(len(proxy), bool(proxy), 3 in proxy, '3' in proxy)
            print(proxy[3], proxy[(1, 2)], proxy.get('value', 'default'))
            print(list(proxy))
            try:
                proxy['missing']
            except KeyError as error:
                print(error.args)
            try:
                proxy[[]]
            except TypeError:
                print('unhashable key')
            """
        );

    [Fact]
    public Task MappingViewsRemainLiveAndKeepDictionaryOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {'a': 1, 'b': 2}
            proxy = MappingProxyType(backing)
            keys = proxy.keys()
            values = proxy.values()
            items = proxy.items()
            print(type(keys).__name__, type(values).__name__, type(items).__name__)
            print(list(keys), list(values), list(items))
            backing['a'] = 3
            del backing['b']
            backing['c'] = 4
            print(list(keys), list(values), list(items))
            print(list(reversed(proxy)), list(reversed(keys)), list(reversed(items)))
            print('c' in keys, ('a', 3) in items, 4 in values)
            """
        );

    [Fact]
    public Task ClassDictionaryViewsObserveLaterClassAssignments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                first = 1
            proxy = Example.__dict__
            keys = proxy.keys()
            items = proxy.items()
            Example.second = 2
            Example.first = 3
            print([key for key in keys if key in ('first', 'second')])
            print([item for item in items if item[0] in ('first', 'second')])
            del Example.first
            print('first' in keys, ('second', 2) in items)
            """
        );

    [Fact]
    public Task CopyIsShallowAndDoesNotShareDictionaryMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            shared = [1]
            backing = {'shared': shared, 'value': 2}
            proxy = MappingProxyType(backing)
            copied = proxy.copy()
            print(type(copied).__name__, copied == backing, copied is backing)
            copied['new'] = 3
            copied['value'] = 4
            backing['value'] = 5
            shared.append(6)
            print('new' in proxy, copied['value'], proxy['value'])
            print(copied['shared'] is proxy['shared'], copied['shared'])
            """
        );

    [Fact]
    public Task EqualityAndUnionUseBackingDictionarySemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {'a': 1, 'b': 2}
            proxy = MappingProxyType(backing)
            other = MappingProxyType({'b': 9, 'c': 3})
            print(proxy == backing, backing == proxy, proxy == MappingProxyType(backing.copy()))
            print(proxy != other, proxy == [], proxy != [])
            print(proxy | {'b': 8, 'd': 4})
            print({'a': 0, 'd': 4} | proxy)
            print(proxy | other, type(proxy | other).__name__)
            print(backing, list(other))
            """
        );

    [Fact]
    public Task MutationOperationsAreRejectedWithoutChangingBackingStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            backing = {'value': 1}
            proxy = MappingProxyType(backing)
            original = proxy
            try:
                proxy['value'] = 2
            except TypeError:
                print('assignment rejected')
            try:
                del proxy['value']
            except TypeError:
                print('deletion rejected')
            try:
                proxy |= {'added': 3}
            except TypeError:
                print('in-place union rejected')
            print(proxy is original, backing)
            for name in ('clear', 'update', 'pop', 'popitem', 'setdefault'):
                print(name, hasattr(proxy, name))
            try:
                proxy.extra = 4
            except AttributeError:
                print('attribute assignment rejected')
            try:
                hash(proxy)
            except TypeError:
                print('dictionary-backed proxy is unhashable')
            """
        );

    [Fact]
    public Task ClassDictionaryCannotBeReplacedDeletedOrMutatedThroughItsProxy() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                value = 1
            proxy = Example.__dict__
            try:
                proxy['value'] = 2
            except TypeError:
                print('proxy write rejected')
            try:
                Example.__dict__ = {}
            except AttributeError:
                print('namespace replacement rejected')
            try:
                del Example.__dict__
            except AttributeError:
                print('namespace deletion rejected')
            print(proxy['value'], Example.value)
            Example.value = 3
            print(proxy['value'])
            """
        );

    [Fact]
    public Task MappingProxyConstructorValidatesArityAndNonMappingInputs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for value in (None, 1, [], ()):
                try:
                    MappingProxyType(value)
                except TypeError:
                    print('rejected', type(value).__name__)
            try:
                MappingProxyType()
            except TypeError:
                print('missing mapping')
            try:
                MappingProxyType({}, {})
            except TypeError:
                print('extra mapping')
            try:
                MappingProxyType(mapping={}, extra=1)
            except TypeError:
                print('unexpected keyword')
            """
        );

    [Fact]
    public Task MappingComparisonOnlyUnwrapsTheProxyWhoseMethodIsCalled() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            class Mapping:
                def __getitem__(self, key): return key
                def __eq__(self, other): return type(other).__name__
                def __lt__(self, other): return type(other).__name__
            mapping = Mapping()
            proxy = MappingProxyType(mapping)
            print(proxy == mapping, mapping == proxy, proxy == proxy)
            print(proxy < mapping, mapping < proxy, proxy < proxy)
            """
        );
}
