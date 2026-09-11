using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExceptionInitializerDispatchCompatibilityTests
{
    [Fact]
    public Task RaisingConstructsCauseBeforeRenormalizingForeignResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('replacement')
            class Meta(type):
                def __call__(cls, *args):
                    events.append((cls.__name__, len(args)))
                    return failure
            class Error(Exception, metaclass=Meta): pass
            try: raise Error from Error
            except ValueError as error: print(events, error is failure, error.__cause__ is failure)
            """
        );

    [Fact]
    public Task BaseInitializationDoesNotOverwriteBuiltinValueOrCode() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            end = StopIteration('old')
            exit = SystemExit('old')
            for value in (end, exit): BaseException.__init__(value, 'base')
            print(end.args, end.value, exit.args, exit.code)
            StopIteration.__init__(end)
            SystemExit.__init__(exit)
            print(end.args, end.value, exit.args, exit.code)
            """
        );

    [Fact]
    public Task InitializerBindingSupportsStaticClassAndUserDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class StaticError(Exception):
                @staticmethod
                def __init__(value): events.append(('static', value))
            class ClassError(Exception):
                @classmethod
                def __init__(cls, value): events.append((cls.__name__, value))
            class Initializer:
                def __get__(self, instance, owner):
                    events.append(('bind', instance is not None, owner.__name__))
                    def initialize(value): instance.args = ('changed', value)
                    return initialize
            class DescriptorError(Exception):
                __init__ = Initializer()
            print(StaticError(1).args, ClassError(2).args, DescriptorError(3).args)
            print(events)
            """
        );

    [Fact]
    public Task RaisingUsesMetaclassCallAndExplicitBaseInitializationPreservesSpecializedFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('replacement')
            class Meta(type):
                def __call__(cls, *args):
                    events.append('call')
                    return failure
            class Error(Exception, metaclass=Meta): pass
            try: raise Error
            except ValueError as error: print(error is failure, events)
            class End(StopIteration):
                def __init__(self, value): super().__init__(value)
            class Exit(SystemExit):
                def __init__(self, *args): super().__init__(*args)
            end = End('initial')
            exit = Exit('one', 'two')
            print(end.args, end.value, exit.args, exit.code)
            BaseException.__init__(end, 'args only')
            BaseException.__init__(exit, 'args only')
            print(end.args, end.value, exit.args, exit.code)
            try: end.__init__(value='named')
            except TypeError as error: print(type(error).__name__)
            print(end.value)
            """
        );
}
