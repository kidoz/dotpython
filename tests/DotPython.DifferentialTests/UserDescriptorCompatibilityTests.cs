using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UserDescriptorCompatibilityTests
{
    [Fact]
    public Task FirstMroMatchWinsBeforeDescriptorPrecedenceIsConsidered() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Data:
                def __get__(self, instance, owner):
                    return 'base data'
                def __set__(self, instance, value):
                    print('base setter', value)
            class NonData:
                def __get__(self, instance, owner):
                    return 'child nondata'
            class Base:
                value = Data()
            class Child(Base):
                value = NonData()
            item = Child()
            print(item.value)
            item.value = 'shadow'
            print(item.value)
            del Child.value
            print(item.value, item.__dict__['value'])
            item.value = 4
            print(item.__dict__['value'])
            """
        );

    [Fact]
    public Task DataDescriptorsOverrideInstanceStorageAndIgnoreSetterReturnValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    if instance is None:
                        return self
                    print('get', owner.__name__)
                    return instance._value
                def __set__(self, instance, value):
                    print('set', value)
                    instance._value = value
                    return 123
                def __delete__(self, instance):
                    print('delete')
                    del instance._value
                    return 456
            descriptor = Descriptor()
            class Example:
                value = descriptor
            item = Example()
            item.__dict__['value'] = 'shadow'
            item.value = 10
            print(item.value, item.__dict__['value'], Example.value is descriptor)
            item.value += 2
            print(item.value)
            del item.value
            print(item.__dict__['value'], hasattr(item, '_value'))
            """
        );

    [Fact]
    public Task NonDataDescriptorsCanBeShadowedAndInstanceValuesAreNotBound() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    print('get', instance is None, owner.__name__)
                    return 20
            descriptor = Descriptor()
            class Example:
                value = descriptor
            item = Example()
            print(item.value)
            item.value = 30
            print(item.value, Example.value)
            del item.value
            print(item.value)
            item.other = descriptor
            print(item.other is descriptor)
            try:
                del item.value
            except AttributeError:
                print('no instance attribute to delete')
            """
        );

    [Fact]
    public Task SetterAndDeleterWithoutGetterRetainOrdinaryReadPrecedence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class SetOnly:
                def __set__(self, instance, value):
                    print('set', value)
            class DeleteOnly:
                def __delete__(self, instance):
                    print('delete')
            setter = SetOnly()
            deleter = DeleteOnly()
            class Example:
                x = setter
                y = deleter
            item = Example()
            print(item.x is setter, item.y is deleter)
            item.__dict__['x'] = 1
            item.__dict__['y'] = 2
            print(item.x, item.y)
            item.x = 3
            del item.y
            print(item.x, item.y)
            try:
                del item.x
            except AttributeError:
                print('missing __delete__')
            try:
                item.y = 4
            except AttributeError:
                print('missing __set__')
            print(item.x, item.y)
            """
        );

    [Fact]
    public Task DeleteOnlyGetterIsADataDescriptorAndRejectsAssignment() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    return 42
                def __delete__(self, instance):
                    print('deleted')
            class Example:
                value = Descriptor()
            item = Example()
            item.__dict__['value'] = 9
            print(item.value)
            try:
                item.value = 10
            except AttributeError:
                print('assignment rejected')
            del item.value
            print(item.value, item.__dict__['value'])
            """
        );

    [Fact]
    public Task ClassAndSuperLookupsPassTheDynamicOwnerAndBypassInstanceStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    return instance is None, owner.__name__
            class Base:
                value = Descriptor()
            class Middle(Base):
                pass
            class Child(Middle):
                pass
            item = Child()
            item.value = 'shadow'
            print(Base.value, Child.value, item.value)
            print(super(Child, item).value, super(Middle, item).value)
            print(super(Child, Child).value)
            print(super(Child, item).value == (False, 'Child'))
            """
        );

    [Fact]
    public Task ClassAssignmentReplacesDescriptorWithoutCallingInstanceSetter() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    return 'descriptor'
                def __set__(self, instance, value):
                    print('unexpected setter')
                def __delete__(self, instance):
                    print('unexpected deleter')
            class Base:
                value = Descriptor()
            class Child(Base):
                pass
            item = Child()
            print(item.value)
            Child.value = 'child'
            print(item.value, Base.value)
            del Child.value
            print(item.value)
            Base.value = 'base'
            print(item.value)
            del Base.value
            print(hasattr(item, 'value'))
            """
        );

    [Fact]
    public Task DescriptorHooksComeFromItsTypeAndIgnoreItsInstanceAttributes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class DescriptorBase:
                def __get__(self, instance, owner):
                    return 'inherited getter'
                def __set__(self, instance, value):
                    print('inherited setter', value)
                def __delete__(self, instance):
                    print('inherited deleter')
            class Descriptor(DescriptorBase):
                def __getattribute__(self, name):
                    if name in ('__get__', '__set__', '__delete__'):
                        raise RuntimeError('ordinary hook lookup')
                    return object.__getattribute__(self, name)
            descriptor = Descriptor()
            descriptor.__dict__['__get__'] = lambda *args: 'instance getter'
            descriptor.__dict__['__set__'] = lambda *args: print('instance setter')
            descriptor.__dict__['__delete__'] = lambda *args: print('instance deleter')
            class Example:
                value = descriptor
            item = Example()
            print(item.value)
            item.value = 3
            del item.value
            class Plain:
                pass
            plain = Plain()
            plain.__get__ = lambda *args: 'not a descriptor'
            Example.other = plain
            print(item.other is plain, Example.other is plain)
            """
        );

    [Fact]
    public Task MutatingDescriptorTypeChangesDataPrecedenceForExistingInstances() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    return 'getter'
            descriptor = Descriptor()
            class Example:
                value = descriptor
            item = Example()
            item.value = 'shadow'
            print(item.value)
            def set_value(self, instance, value):
                print('setter', value)
            Descriptor.__set__ = set_value
            print(item.value)
            item.value = 8
            print(item.__dict__['value'])
            del Descriptor.__set__
            print(item.value)
            del item.value
            print(item.value)
            del Descriptor.__get__
            print(item.value is descriptor)
            Descriptor.__get__ = lambda self, instance, owner: 'replacement'
            print(item.value)
            """
        );

    [Fact]
    public Task AttributeErrorInvokesFallbackButOtherGetterExceptionsPropagate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            missing = AttributeError('descriptor missing')
            broken = ValueError('descriptor broken')
            class Descriptor:
                def __init__(self, error):
                    self.error = error
                def __get__(self, instance, owner):
                    raise self.error
            class Example:
                missing = Descriptor(missing)
                broken = Descriptor(broken)
                def __getattr__(self, name):
                    print('fallback', name)
                    return 'fallback value'
            item = Example()
            print(item.missing, getattr(item, 'missing', 'default'), hasattr(item, 'missing'))
            try:
                object.__getattribute__(item, 'missing')
            except AttributeError as error:
                print(error is missing)
            try:
                item.broken
            except ValueError as error:
                print(error is broken)
            class NoFallback:
                value = Descriptor(missing)
            print(getattr(NoFallback(), 'value', 'default'), hasattr(NoFallback(), 'value'))
            """
        );

    [Fact]
    public Task SetterAndDeleterFailuresPreserveExceptionIdentityAndInstanceStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = RuntimeError('descriptor failed')
            class Descriptor:
                def __set__(self, instance, value):
                    raise failure
                def __delete__(self, instance):
                    raise failure
            class Example:
                value = Descriptor()
            item = Example()
            item.__dict__['value'] = 'original'
            try:
                item.value = 'replacement'
            except RuntimeError as error:
                print(error is failure)
            try:
                del item.value
            except RuntimeError as error:
                print(error is failure)
            print(item.value)
            """
        );

    [Fact]
    public Task SpecialMethodLookupBindsDescriptorsAndIgnoresInstanceShadows() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Length:
                def __get__(self, instance, owner):
                    print('bind length', owner.__name__)
                    return lambda: 7
            class Addition:
                def __get__(self, instance, owner):
                    print('bind addition', owner.__name__)
                    return lambda other: other + 10
            class Example:
                __len__ = Length()
                __add__ = Addition()
            item = Example()
            item.__len__ = lambda: 99
            item.__add__ = lambda other: 99
            print(item.__len__(), len(item))
            print(item.__add__(2), item + 2)
            """
        );

    [Fact]
    public Task SpecialDescriptorErrorsDoNotInvokeOrdinaryAttributeFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            missing = AttributeError('special descriptor missing')
            class Descriptor:
                def __get__(self, instance, owner):
                    raise missing
            class Example:
                __len__ = Descriptor()
                def __getattr__(self, name):
                    print('fallback', name)
                    return lambda: 9
            item = Example()
            print(item.__len__())
            try:
                len(item)
            except AttributeError as error:
                print(error is missing)
            """
        );

    [Fact]
    public Task NoneDescriptorHooksArePresentAndRaiseTypeErrorWhenInvoked() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                __get__ = None
                __set__ = None
                __delete__ = None
            class Example:
                value = Descriptor()
            item = Example()
            item.__dict__['value'] = 'shadow'
            try:
                item.value
            except TypeError:
                print('getter is not callable')
            try:
                item.value = 1
            except TypeError:
                print('setter is not callable')
            try:
                del item.value
            except TypeError:
                print('deleter is not callable')
            print(item.__dict__['value'])
            """
        );

    [Fact]
    public Task GetterUsesRawTypeHookWhileSetterAndDeleterBindStaticMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                @staticmethod
                def __get__(*args):
                    print('get', len(args), type(args[0]).__name__)
                    return 12
                @staticmethod
                def __set__(*args):
                    print('set', len(args), args[1])
                @staticmethod
                def __delete__(*args):
                    print('delete', len(args))
            class Example:
                value = Descriptor()
            item = Example()
            print(item.value)
            item.value = 4
            del item.value
            class ClassMethodGetter:
                @classmethod
                def __get__(cls, instance, owner):
                    return 99
            Example.value = ClassMethodGetter()
            try:
                item.value
            except TypeError:
                print('raw classmethod is not callable')
            """
        );

    [Fact]
    public Task OverriddenAttributeAccessCanBypassOrDelegateToDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Descriptor:
                def __get__(self, instance, owner):
                    print('descriptor get')
                    return 15
                def __set__(self, instance, value):
                    print('descriptor set', value)
                def __delete__(self, instance):
                    print('descriptor delete')
            class Example:
                value = Descriptor()
                def __getattribute__(self, name):
                    if name == 'value':
                        return 'intercepted'
                    return object.__getattribute__(self, name)
                def __setattr__(self, name, value):
                    print('override set', name)
                    object.__setattr__(self, name, value)
                def __delattr__(self, name):
                    print('override delete', name)
                    object.__delattr__(self, name)
            item = Example()
            print(item.value, object.__getattribute__(item, 'value'))
            item.value = 6
            del item.value
            """
        );
}
