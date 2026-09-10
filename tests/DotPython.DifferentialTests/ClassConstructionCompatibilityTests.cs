using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ClassConstructionCompatibilityTests
{
    [Fact]
    public Task CallableMetaclassReturningBuiltinTypeStillRequiresPropagatingClassCell() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def factory(name, bases, namespace): return int
            try:
                class Result(metaclass=factory):
                    def method(self): return __class__
            except RuntimeError:
                print('missing class cell', 'Result' in globals())
            class Plain(metaclass=factory): pass
            print(Plain is int)
            """
        );

    [Fact]
    public Task UserFunctionCustomAttributesShareIdentityAndSupportDeletion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def function(): return 7
            alias = function
            value = [1]
            function.custom = value
            print(alias.custom is value, vars(function) is function.__dict__)
            alias.__dict__['custom'] = [2]
            print(function.custom, function() == 7)
            del alias.custom
            print(hasattr(function, 'custom'), function.__dict__)
            try:
                del function.custom
            except AttributeError as error:
                print(str(error))
            function.__prepare__ = lambda name, bases: {}
            print(alias.__prepare__ is function.__dict__['__prepare__'])
            """
        );

    [Fact]
    public Task UserFunctionDictionariesCanBeReplacedAndRetainArbitraryDictionaryKeys() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def function(): pass
            original = function.__dict__
            original['before'] = 1
            replacement = {'after': 2, 3: 'nonstring', '__name__': 'dictionary value'}
            function.__dict__ = replacement
            print(vars(function) is replacement, function.after, hasattr(function, 'before'))
            print(function.__dict__[3], function.__name__)
            original['after'] = 99
            print(function.after)
            try:
                function.__dict__ = []
            except TypeError as error:
                print(str(error))
            try:
                del function.__dict__
            except TypeError as error:
                print(str(error))
            print(function.__dict__ is replacement)
            """
        );

    [Fact]
    public Task ClassPrologueReadsModuleNameFromPreparedNamespaceBeforeWritingMetadata() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            marker = object()
            class Namespace:
                def __init__(self): self.data = {'__name__': marker}
                def __getitem__(self, key):
                    if key == '__name__': events.append('module lookup')
                    return self.data[key]
                def __setitem__(self, key, value):
                    if key in ('__module__', '__qualname__'): events.append(key)
                    self.data[key] = value
            namespace = Namespace()
            class Factory:
                def __prepare__(self, name, bases): return namespace
                def __call__(self, name, bases, prepared):
                    print(prepared is namespace, prepared.data['__module__'] is marker)
                    print(events)
                    return 42
            class Result(metaclass=Factory()): pass
            print(Result)
            """
        );

    [Fact]
    public Task ActualClassBasesSkipExpansionButSuccessfulAliasHooksAlwaysRecordOriginalBases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base:
                @staticmethod
                def __mro_entries__(bases):
                    raise RuntimeError('actual class bases must not expand')
            class Child(Base): pass
            print(Child.__bases__ == (Base,), '__orig_bases__' in Child.__dict__)
            captured = []
            class Alias:
                def __mro_entries__(self, bases):
                    captured.append(bases)
                    return bases
            alias = Alias()
            def factory(name, bases, namespace):
                print(bases == (alias,), namespace['__orig_bases__'] is captured[0])
                return 42
            class Result(alias, metaclass=factory): pass
            print(Result)
            """
        );

    [Fact]
    public Task ExplicitGlobalsBypassMappingWhileNonlocalLoadsRetainNamespacePriority() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            global_value = 'global'
            events = []
            class Namespace:
                def __init__(self):
                    self.data = {'global_value': 'mapping global', 'captured': 'mapping cell'}
                def __getitem__(self, key):
                    if key in ('global_value', 'captured'): events.append(('get', key))
                    return self.data[key]
                def __setitem__(self, key, value):
                    if key in ('global_value', 'captured'): events.append(('set', key))
                    self.data[key] = value
                def __delitem__(self, key):
                    if key in ('global_value', 'captured'): events.append(('delete', key))
                    del self.data[key]
            namespace = Namespace()
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return namespace
                def __new__(meta, name, bases, prepared):
                    return super().__new__(meta, name, bases, prepared.data)
            def outer():
                captured = 'closure'
                class Example(metaclass=Meta):
                    global global_value
                    nonlocal captured
                    before = global_value, captured
                    global_value = 'updated global'
                    captured = 'updated cell'
                    after = global_value, captured
                    del global_value
                    del captured
                    try: global_value
                    except NameError: print('global deleted')
                    try: captured
                    except NameError: print('cell deleted')
                return Example
            Example = outer()
            print(Example.before, Example.after, events)
            print(namespace.data['global_value'], namespace.data['captured'])
            print('global_value' in globals())
            """
        );

    [Fact]
    public Task MetaclassCanCopyCustomPreparedNamespaceUsingMappingKeys() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key): return self.data[key]
                def __setitem__(self, key, value): self.data[key] = value
                def keys(self):
                    events.append('keys')
                    return self.data.keys()
                def __iter__(self):
                    raise RuntimeError('must copy using mapping keys')
            namespace = Namespace()
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return namespace
                def __new__(meta, name, bases, prepared):
                    return super().__new__(meta, name, bases, dict(prepared))
            class Example(metaclass=Meta):
                value = 7
                def owner(self): return __class__
            print(events, Example.value, Example().owner() is Example)
            namespace.data['value'] = 9
            print(Example.value)
            """
        );

    [Fact]
    public Task PreparedMappingKeepsIdentityAndReceivesClassLoadsStoresAndDeletes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Namespace:
                def __init__(self): self.data = {'seed': 10}
                def __getitem__(self, key):
                    if key in ('seed', 'value'): events.append(('get', key))
                    return self.data[key]
                def __setitem__(self, key, value):
                    if key == 'value': events.append(('set', key, value))
                    self.data[key] = value
                def __delitem__(self, key):
                    events.append(('delete', key))
                    del self.data[key]
            namespace = Namespace()
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return namespace
                def __new__(meta, name, bases, prepared):
                    print(prepared is namespace)
                    return super().__new__(meta, name, bases, prepared.data)
            class Example(metaclass=Meta):
                print(locals() is namespace, vars() is namespace)
                value = seed + 2
                saved = value
                del value
            print(events, Example.saved, hasattr(Example, 'value'))
            namespace.data['saved'] = 99
            print(Example.saved)
            """
        );

    [Fact]
    public Task PreparedMappingMissesFallBackToGlobalsBuiltinsAndClosureCells() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            global_value = 7
            class Missing(KeyError): pass
            class Namespace:
                def __init__(self, shadow): self.data = shadow
                def __getitem__(self, key):
                    if key in self.data: return self.data[key]
                    raise Missing(key)
                def __setitem__(self, key, value): self.data[key] = value
            def outer(shadow):
                captured = 'closure'
                namespace = Namespace(shadow)
                class Meta(type):
                    @classmethod
                    def __prepare__(meta, name, bases): return namespace
                    def __new__(meta, name, bases, prepared):
                        return super().__new__(meta, name, bases, prepared.data)
                class Example(metaclass=Meta):
                    result = captured
                    combined = global_value + len([1, 2])
                    def read(self): return captured
                return Example
            First = outer({'captured': 'mapping'})
            Second = outer({})
            print(First.result, Second.result, First.combined, Second.combined)
            print(First().read(), Second().read())
            """
        );

    [Fact]
    public Task OnlyKeyErrorFromNamespaceLoadsAllowsFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            target = 'global fallback'
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key):
                    if key == 'target': raise failure
                    return self.data[key]
                def __setitem__(self, key, value): self.data[key] = value
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return Namespace()
            for failure in (ValueError('load'), AttributeError('load')):
                try:
                    class Example(metaclass=Meta): value = target
                except Exception as error:
                    print(type(error).__name__, error is failure, 'Example' in globals())
            print('continued')
            """
        );

    [Fact]
    public Task NamespaceStoreFailuresKeepIdentityWhileDeleteFailuresBecomeNameError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('namespace failure')
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key): return self.data[key]
                def __setitem__(self, key, value):
                    if key == 'blocked': raise failure
                    self.data[key] = value
                def __delitem__(self, key): raise failure
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return Namespace()
            try:
                class Store(metaclass=Meta): blocked = 1
            except ValueError as error:
                print(error is failure)
            try:
                class Delete(metaclass=Meta): del absent
            except NameError as error:
                print(str(error), error.__context__ is None)
            print('Store' in globals(), 'Delete' in globals())
            """
        );

    [Fact]
    public Task FunctionMetaclassesWithoutPrepareReceiveADictionaryAndMayReturnValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def factory(name, bases, namespace, flag):
                events.append((name, bases, type(namespace).__name__, namespace['value'], flag))
                return 40
            def decorate(value):
                events.append(('decorator', value))
                return value + 2
            @decorate
            class Example(metaclass=factory, flag=7):
                value = 3
                def owner(self): return __class__
            print(Example, events)
            """
        );

    [Fact]
    public Task FunctionMetaclassPrepareAttributeReceivesKeywordsAndExactNamespace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            namespace = {'seed': 5}
            events = []
            def factory(name, bases, prepared, flag):
                events.append(('call', prepared is namespace, flag))
                return prepared['value']
            def prepare(name, bases, flag):
                events.append(('prepare', name, bases, flag))
                return namespace
            factory.__prepare__ = prepare
            class Example(metaclass=factory, flag=9): value = seed + 1
            print(Example, events)
            """
        );

    [Fact]
    public Task CallableInstanceCanSupplyPrepareThroughOrdinaryAttributeLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            namespace = {'seed': 10}
            class Factory:
                def __getattr__(self, name):
                    if name == '__prepare__':
                        events.append('lookup prepare')
                        return self.prepare
                    raise AttributeError(name)
                def prepare(self, name, bases, flag):
                    events.append(('prepare', flag))
                    return namespace
                def __call__(self, name, bases, prepared, flag):
                    events.append(('call', prepared is namespace, flag))
                    return prepared['value']
            class Example(metaclass=Factory(), flag=4): value = seed + 2
            print(Example, events)
            """
        );

    [Fact]
    public Task MetaclassPrepareLookupErrorsPropagateAndPreventBodyExecution() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('prepare lookup')
            events = []
            class Factory:
                def __getattribute__(self, name):
                    if name == '__prepare__': raise failure
                    return object.__getattribute__(self, name)
                def __call__(self, name, bases, namespace):
                    events.append('call')
                    return 1
            try:
                class Example(metaclass=Factory()): events.append('body')
            except ValueError as error:
                print(error is failure)
            print(events, 'Example' in globals())
            """
        );

    [Fact]
    public Task ClassSyntaxInvokesTheMetaclassesOwnMetaclassCallHook() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class MetaMeta(type):
                def __call__(cls, name, bases, namespace, **kwargs):
                    events.append(('metameta call', cls.__name__, name))
                    result = super().__call__(name, bases, namespace, **kwargs)
                    result.tag = 'observed'
                    return result
            class Meta(type, metaclass=MetaMeta):
                def __new__(meta, name, bases, namespace):
                    events.append(('new', name))
                    return super().__new__(meta, name, bases, namespace)
            class Example(metaclass=Meta): pass
            print(events, Example.tag, type(Example) is Meta, type(Meta) is MetaMeta)
            """
        );

    [Fact]
    public Task BaseExpansionSharesOriginalTupleAndRunsBeforeMetaclassPreparation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            original = []
            class A: pass
            class B: pass
            class C: pass
            class Alias:
                def __init__(self, label, expanded):
                    self.label = label
                    self.expanded = expanded
                def __mro_entries__(self, bases):
                    original.append(bases)
                    events.append(('expand', self.label))
                    return self.expanded
            first = Alias('first', (A, B))
            empty = Alias('empty', ())
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append(('prepare', tuple(base.__name__ for base in bases)))
                    return {}
            class Example(first, empty, C, metaclass=Meta): pass
            print(events)
            print(original[0] is original[1], Example.__orig_bases__ is original[0])
            print(Example.__orig_bases__ == (first, empty, C), Example.__bases__ == (A, B, C))
            """
        );

    [Fact]
    public Task MroEntryLookupUsesInstanceAttributesAndDoesNotReexpandReturnedBases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base: pass
            class Alias: pass
            alias = Alias()
            alias.__mro_entries__ = lambda bases: (Base,)
            class Example(alias): pass
            print(Example.__bases__ == (Base,), Example.__orig_bases__[0] is alias)
            nested = Alias()
            nested.__mro_entries__ = lambda bases: events.append('unexpected nested expansion') or (Base,)
            first = Alias()
            first.__mro_entries__ = lambda bases: (nested,)
            def factory(name, bases, namespace): return bases
            class Result(first, metaclass=factory): pass
            print(Result == (nested,), events)
            """
        );

    [Fact]
    public Task OriginalBasesAreWrittenAfterClassCellAndOverrideBodyAssignment() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key): return self.data[key]
                def __setitem__(self, key, value):
                    if key in ('__classcell__', '__orig_bases__'): events.append(key)
                    self.data[key] = value
            class Base: pass
            class Alias:
                def __mro_entries__(self, bases): return (Base,)
            alias = Alias()
            namespace = Namespace()
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return namespace
                def __new__(meta, name, bases, prepared):
                    events.append('new')
                    return super().__new__(meta, name, bases, prepared.data)
            class Example(alias, metaclass=Meta):
                __orig_bases__ = 'body value'
                def owner(self): return __class__
            print(events, Example.__orig_bases__ == (alias,), Example().owner() is Example)
            class Ordinary(Base): __orig_bases__ = 'preserved'
            print(Ordinary.__orig_bases__)
            """
        );

    [Fact]
    public Task DynamicTypeRejectsMroEntryResolutionAndDoesNotInvokeTheHook() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base: pass
            class Alias:
                def __mro_entries__(self, bases):
                    events.append('expanded')
                    return (Base,)
            alias = Alias()
            try:
                type('Dynamic', (alias,), {})
            except TypeError as error:
                print(str(error))
            print(events)
            class Example(alias): pass
            print(events, Example.__bases__ == (Base,))
            """
        );

    [Fact]
    public Task InvalidBaseExpansionAndHookErrorsLeaveClassNamesUnbound() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('expansion failed')
            class Alias:
                def __mro_entries__(self, bases):
                    if fail: raise failure
                    return []
            for fail in (False, True):
                try:
                    class Example(Alias()): events.append('body')
                except Exception as error:
                    print(type(error).__name__, error is failure)
            print(events, 'Example' in globals())
            class Recovered: pass
            print(Recovered.__name__)
            """
        );

    [Fact]
    public Task FailedOriginalBasesStorePreventsMetaclassCallAfterBodyExecution() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('original bases store')
            events = []
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key): return self.data[key]
                def __setitem__(self, key, value):
                    if key == '__orig_bases__': raise failure
                    self.data[key] = value
            class Alias:
                def __mro_entries__(self, bases): return ()
            class Factory:
                def __prepare__(self, name, bases): return Namespace()
                def __call__(self, name, bases, namespace):
                    events.append('unexpected call')
                    return 42
            try:
                class Example(Alias(), metaclass=Factory()): events.append('body')
            except ValueError as error:
                print(error is failure)
            print(events, 'Example' in globals())
            """
        );
}
