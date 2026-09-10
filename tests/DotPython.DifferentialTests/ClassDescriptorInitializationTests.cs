using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ClassDescriptorInitializationTests
{
    [Fact]
    public Task HooksRunInDefinitionOrderBeforeDecoratorsWithClassCellsReady() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __set_name__(self, owner, name):
                    events.append((owner.__name__, name, owner().owner() is owner))
                    return 123
            shared = Descriptor()
            def decorate(cls):
                events.append(('decorated', cls.__name__))
                return cls
            @decorate
            class Owner:
                first = shared
                second = shared
                def owner(self): return __class__
            class Child(Owner):
                pass
            Owner.later = Descriptor()
            print(events)
            """
        );

    [Fact]
    public Task HooksUseOriginalNamespaceSnapshotDespiteReplacementAndDeletion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __init__(self, label): self.label = label
                def __set_name__(self, owner, name):
                    events.append((self.label, name))
                    if name == 'first':
                        owner.second = Descriptor('replacement')
                        del owner.third
                        owner.extra = Descriptor('added')
            class Owner:
                first = Descriptor('first')
                second = Descriptor('second')
                third = Descriptor('third')
            print(events)
            print(Owner.second.label, hasattr(Owner, 'third'), Owner.extra.label)
            """
        );

    [Fact]
    public Task SpecialLookupIgnoresInstanceHooksAndBindsInheritedStaticAndClassMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __set_name__(self, owner, name): events.append(('inherited', name))
            class Inherited(Base): pass
            inherited = Inherited()
            inherited.__set_name__ = lambda owner, name: events.append(('shadow', name))
            class Missing:
                def __getattr__(self, name):
                    events.append(('getattr', name))
                    return lambda owner, name: None
            class Static:
                @staticmethod
                def __set_name__(owner, name): events.append(('static', name))
            class Class:
                @classmethod
                def __set_name__(cls, owner, name): events.append((cls.__name__, name))
            class Owner:
                first = inherited
                second = Missing()
                third = Static()
                fourth = Class()
            print(events)
            """
        );

    [Fact]
    public Task HookFailuresKeepTheOriginalExceptionCauseAndAppendNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            original = ValueError('boom')
            original.__notes__ = ['prior']
            class Descriptor:
                def __set_name__(self, owner, name):
                    events.append(name)
                    raise original from KeyError('cause')
            try:
                class Owner:
                    first = Descriptor()
                    second = Descriptor()
            except ValueError as error:
                print(error is original, str(error), type(error.__cause__).__name__)
                print(error.__notes__)
            print(events, 'Owner' in globals())
            print('continued')
            """
        );

    [Fact]
    public Task NonCallableHooksReceiveFailureNotesAndInvalidNotesPreserveContext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Disabled:
                __set_name__ = None
            try:
                class Owner:
                    value = Disabled()
            except TypeError as error:
                print(type(error).__name__, error.__notes__)
            original = ValueError('boom')
            original.__notes__ = 'invalid'
            class Invalid:
                def __set_name__(self, owner, name): raise original
            try:
                class Other:
                    value = Invalid()
            except TypeError as error:
                print(str(error), error.__context__ is original)
            """
        );

    [Fact]
    public Task DescriptorHookLookupErrorsAreNotAnnotated() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            original = ValueError('lookup')
            class Hook:
                def __get__(self, instance, owner): raise original
            class Descriptor:
                __set_name__ = Hook()
            try:
                class Owner:
                    value = Descriptor()
            except ValueError as error:
                print(error is original, str(error), hasattr(error, '__notes__'))
            print('continued')
            """
        );
}
