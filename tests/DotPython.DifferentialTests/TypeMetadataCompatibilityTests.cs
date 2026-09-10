using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TypeMetadataCompatibilityTests
{
    [Fact]
    public Task TypeDictionaryNewEntryCanConstructAClassDirectly() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            raw = type.__dict__['__new__']
            Created = raw(type, 'Created', (Base,), {'value': 7})
            print(callable(raw), Created.value, issubclass(Created, Base), type(Created) is type)
            print(type.__new__ is raw)
            """
        );

    [Fact]
    public Task TypeDictionaryExposesStableDataDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__name__', '__qualname__', '__module__', '__doc__',
                         '__dict__', '__bases__', '__base__', '__mro__'):
                descriptor = type.__dict__[name]
                print(name, type(descriptor).__name__, descriptor is type.__dict__[name])
                print(hasattr(descriptor, '__get__'), hasattr(descriptor, '__set__'),
                      hasattr(descriptor, '__delete__'))
            """
        );

    [Fact]
    public Task DirectGetReadsManagedMetadataAndIgnoresExplicitOwner() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            class Example(Base):
                __doc__ = 'example documentation'
                value = 7
            for name in ('__name__', '__qualname__', '__module__', '__doc__'):
                descriptor = type.__dict__[name]
                print(descriptor.__get__(Example), descriptor.__get__(Example, int))
            print(type.__dict__['__bases__'].__get__(Example) == (Base,))
            print(type.__dict__['__base__'].__get__(Example) is Base)
            print(type.__dict__['__mro__'].__get__(Example) == (Example, Base, object))
            mapping = type.__dict__['__dict__'].__get__(Example)
            print(mapping['value'], mapping == Example.__dict__)
            Example.value = 8
            print(mapping['value'])
            """
        );

    [Fact]
    public Task DescriptorGetWithNoneReturnsSelfOnlyWhenOwnerIsPresent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__name__', '__qualname__', '__module__', '__doc__',
                         '__dict__', '__bases__', '__base__', '__mro__'):
                descriptor = type.__dict__[name]
                print(descriptor.__get__(None, type) is descriptor)
                print(descriptor.__get__(None, int) is descriptor)
                for arguments in ((None,), (None, None)):
                    try:
                        descriptor.__get__(*arguments)
                    except TypeError as error:
                        print(str(error))
            """
        );

    [Fact]
    public Task DirectDescriptorOperationsValidateTheReceiverBeforeTheValue() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__name__', '__qualname__', '__module__', '__doc__',
                         '__dict__', '__bases__', '__base__', '__mro__'):
                descriptor = type.__dict__[name]
                for operation in ('get', 'set', 'delete'):
                    try:
                        if operation == 'get': descriptor.__get__(object(), type)
                        elif operation == 'set': descriptor.__set__(object(), 42)
                        else: descriptor.__delete__(object())
                    except TypeError as error:
                        print(name, operation, str(error))
            """
        );

    [Fact]
    public Task DirectDescriptorsReadBuiltinMetadata() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for target in (object, type, int, list, Exception):
                for name in ('__name__', '__qualname__', '__module__', '__doc__',
                             '__bases__', '__base__', '__mro__'):
                    print(target.__name__, name,
                          type.__dict__[name].__get__(target) == getattr(target, name))
                mapping = type.__dict__['__dict__'].__get__(target)
                print(type(mapping).__name__, mapping == target.__dict__)
            """
        );

    [Fact]
    public Task TypeMetadataOverridesClassNamespaceCollisions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                __name__ = 'namespace name'
                __qualname__ = 'Namespace.Qualified'
                __bases__ = 'namespace bases'
                __base__ = 'namespace base'
                __mro__ = 'namespace mro'
                __dict__ = 'namespace dictionary'
            print(Example.__name__, Example.__qualname__)
            print(Example.__bases__ == (object,), Example.__base__ is object)
            print(Example.__mro__ == (Example, object))
            for name in ('__name__', '__bases__', '__base__', '__mro__', '__dict__'):
                print(name, Example.__dict__[name])
            print('__qualname__' in Example.__dict__)
            """
        );

    [Fact]
    public Task LeadingMixinNonDataAttributesCanHideBuiltinMetadataDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin:
                __name__ = 'mixin name'
                __bases__ = 'mixin bases'
                __base__ = 'mixin base'
                __mro__ = 'mixin mro'
            class Before(Mixin, type): pass
            class After(type, Mixin): pass
            for meta in (Before, After):
                class Example(metaclass=meta):
                    __name__ = 'namespace name'
                    __bases__ = 'namespace bases'
                    __base__ = 'namespace base'
                    __mro__ = 'namespace mro'
                print(meta.__name__, Example.__name__)
                print(Example.__bases__, Example.__base__, Example.__mro__)
                print(type.__dict__['__name__'].__get__(Example))
                print(type.__dict__['__bases__'].__get__(Example) == (object,))
            """
        );

    [Fact]
    public Task LeadingMixinDataDescriptorWinsAndTrailingDescriptorLoses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin:
                @property
                def __name__(cls): return 'mixin override'
                @__name__.setter
                def __name__(cls, value): cls.assigned = value
            class Before(Mixin, type): pass
            class After(type, Mixin): pass
            for meta in (Before, After):
                class Example(metaclass=meta): pass
                print(meta.__name__, Example.__name__)
                Example.__name__ = 'Renamed'
                print(Example.__name__, getattr(Example, 'assigned', None))
                print(type.__dict__['__name__'].__get__(Example))
            """
        );

    [Fact]
    public Task DirectMetadataMutationBypassesMetaclassProperties() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                @property
                def __name__(cls): return 'override'
                @__name__.setter
                def __name__(cls, value): print('property set', value)
            class Example(metaclass=Meta): pass
            descriptor = type.__dict__['__name__']
            Example.__name__ = 'Property'
            descriptor.__set__(Example, 'Direct')
            print(Example.__name__, descriptor.__get__(Example))
            print('__name__' in Example.__dict__)
            """
        );

    [Fact]
    public Task NameAndQualnameWritesValidateStringsWithoutChangingNamespace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                __name__ = 'namespace name'
            for name in ('__name__', '__qualname__'):
                descriptor = type.__dict__[name]
                for value in (42, None, []):
                    try: descriptor.__set__(Example, value)
                    except TypeError as error: print(str(error))
                descriptor.__set__(Example, 'Changed')
                print(getattr(Example, name))
                descriptor.__set__(Example, 'Example')
            print(Example.__dict__['__name__'], '__qualname__' in Example.__dict__)
            """
        );

    [Fact]
    public Task NameRejectsNullCharactersWhileQualnameAcceptsThem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            try: Example.__name__ = 'bad\x00name'
            except ValueError as error: print(str(error))
            print(Example.__name__)
            Example.__qualname__ = 'valid\x00qualname'
            print(repr(Example.__qualname__))
            Example.__name__ = ''
            Example.__qualname__ = ''
            print(repr(Example.__name__), repr(Example.__qualname__))
            """
        );

    [Fact]
    public Task ModuleAndDocAcceptArbitraryValuesAndUpdateTheLiveNamespace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            namespace = Example.__dict__
            for name in ('__module__', '__doc__'):
                value = []
                type.__dict__[name].__set__(Example, value)
                print(getattr(Example, name) is value, namespace[name] is value)
                value.append(7)
                print(getattr(Example, name))
                setattr(Example, name, None)
                print(getattr(Example, name) is None, namespace[name] is None)
            """
        );

    [Fact]
    public Task DocDescriptorBindsOnlyTheClassOwnDocumentationValue() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Documentation:
                def __get__(self, instance, owner):
                    events.append((instance is None, owner.__name__))
                    return 'computed documentation'
            value = Documentation()
            class Base:
                __doc__ = value
            class Child(Base): pass
            print(Base.__doc__, type.__dict__['__doc__'].__get__(Base))
            print(Child.__doc__ is None, Base.__dict__['__doc__'] is value)
            print(events)
            """
        );

    [Fact]
    public Task WritableMetadataDescriptorsStillForbidDeletion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            for name in ('__name__', '__qualname__', '__module__', '__doc__', '__bases__'):
                for direct in (False, True):
                    try:
                        if direct: type.__dict__[name].__delete__(Example)
                        else: delattr(Example, name)
                    except TypeError as error: print(str(error))
            print(Example.__name__, Example.__bases__ == (object,))
            """
        );

    [Fact]
    public Task DictionaryMroAndBaseDescriptorsRejectMutationAndDeletion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            for name in ('__dict__', '__mro__', '__base__'):
                descriptor = type.__dict__[name]
                for direct in (False, True):
                    try:
                        if direct: descriptor.__set__(Example, ())
                        else: setattr(Example, name, ())
                    except AttributeError as error: print(str(error))
                    try:
                        if direct: descriptor.__delete__(Example)
                        else: delattr(Example, name)
                    except AttributeError as error: print(str(error))
            print(Example.__mro__ == (Example, object), Example.__base__ is object)
            """
        );

    [Fact]
    public Task ImmutableBuiltinValidationPrecedesWritableMetadataChecks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__name__', '__qualname__', '__module__', '__doc__', '__bases__'):
                descriptor = type.__dict__[name]
                for direct in (False, True):
                    try:
                        if direct: descriptor.__set__(int, [])
                        else: setattr(int, name, [])
                    except TypeError as error: print(str(error))
                    try:
                        if direct: descriptor.__delete__(int)
                        else: delattr(int, name)
                    except TypeError as error: print(str(error))
            print(int.__name__, int.__bases__ == (object,))
            """
        );

    [Fact]
    public Task DirectReadonlyDescriptorsHaveDifferentErrorsFromBuiltinAttributeWrites() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__dict__', '__mro__', '__base__'):
                descriptor = type.__dict__[name]
                try: descriptor.__set__(int, ())
                except AttributeError as error: print(str(error))
                try: descriptor.__delete__(int)
                except AttributeError as error: print(str(error))
                try: setattr(int, name, ())
                except TypeError as error: print(str(error))
                try: delattr(int, name)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task BasesDescriptorRejectsNonTuplesWithoutChangingInheritance() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            class Example(Base): pass
            descriptor = type.__dict__['__bases__']
            for value in ([], None, 42):
                try: descriptor.__set__(Example, value)
                except TypeError as error: print(str(error))
            print(Example.__bases__ == (Base,), Example.__base__ is Base)
            print(Example.__mro__ == (Example, Base, object))
            """
        );
}
