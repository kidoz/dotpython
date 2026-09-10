using Xunit;

namespace DotPython.DifferentialTests;

public sealed class GeneratorThrowNormalizationCompatibilityTests
{
    [Fact]
    public Task CloseDelegationRunsInnerCleanupBeforeOuterAndPropagatesFailures() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def inner(fail):
                try: yield 'ready'
                finally:
                    events.append('inner')
                    if fail: raise ValueError('close failed')
            def outer(fail):
                try: yield from inner(fail)
                except ValueError as error: events.append(str(error))
                finally: events.append('outer')
            for fail in (False, True):
                instance = outer(fail)
                print(next(instance), instance.close(), events)
                events.clear()
            """
        );

    [Fact]
    public Task AssignedGroupArgsRemainTheConstructorArgumentsDuringReconstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            group = ExceptionGroup('original', [ValueError('leaf')])
            group.args = ('replacement',)
            for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                try: rebuild(group)
                except TypeError as error: print(type(error).__name__)
            print(group.args, group.message, str(group))
            """
        );

    [Fact]
    public Task ExceptionArgsAssignmentMaterializesAtomicallyAndPreservesTupleIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = ValueError('original')
            replacement = ('replacement', 7)
            error.args = replacement
            print(error.args is replacement, error.args is error.args, str(error), repr(error))
            print(error.__dict__)
            values = ['a', 'b']
            error.args = values
            values.append('c')
            print(error.args, str(error))
            error.args = 'xy'
            print(error.args)
            def failing():
                yield 'partial'
                raise RuntimeError('iteration failed')
            saved = error.args
            for value in (failing(), None):
                try: error.args = value
                except Exception as failure:
                    print(type(failure).__name__, error.args is saved)
            try: del error.args
            except TypeError as failure: print(str(failure), error.args is saved)
            error.args = (value for value in (1, 2))
            print(error.args)
            error.args = ()
            print(error.args, str(error), repr(error))
            """
        );

    [Fact]
    public Task ExceptionArgsAssignmentPreservesIndependentBuiltinFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for error in (KeyError('old'), StopIteration('old'), SystemExit('old')):
                error.args = ('new',)
                print(type(error).__name__, error.args, str(error), repr(error))
                if isinstance(error, StopIteration): print(error.value)
                if isinstance(error, SystemExit): print(error.code)
            group = ExceptionGroup('old', [ValueError('leaf')])
            original = group.exceptions
            group.args = ('replacement',)
            print(group.args, group.message, group.exceptions is original, str(group))
            children = (ValueError('child'),)
            group = ExceptionGroup('tuple', children)
            group.args = ('changed',)
            print(group.exceptions is children, group.exceptions is group.exceptions)
            import copy
            import pickle
            group = ExceptionGroup('tuple', children)
            for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                rebuilt = rebuild(group)
                print(rebuilt.exceptions is rebuilt.exceptions, rebuilt.exceptions is rebuilt.args[1])
            """
        );

    [Fact]
    public Task YieldFromThrowResumesOuterWhenInnerCatchesAndReturns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    super().__init__('payload')
            def inner():
                try: yield 'ready'
                except Error as error:
                    events.append(error.args)
                    return 'returned'
            def outer():
                result = yield from inner()
                yield ('outer', result)
            generator = outer()
            print(next(generator), generator.throw(Error), events)
            generator.close()
            """
        );

    [Fact]
    public Task YieldFromConstructorFailurePropagatesToOuterHandler() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('constructor failed')
            class Error(Exception):
                def __init__(self): raise failure
            def inner():
                try: yield 'ready'
                finally: print('inner finally')
            def outer():
                try: yield from inner()
                except ValueError as error: yield ('caught', error is failure)
                yield 'after'
            generator = outer()
            print(next(generator), generator.throw(Error))
            print(next(generator))
            generator.close()
            """
        );

    [Fact]
    public Task YieldFromMarksOuterRunningBeforeConstructingInjectedException() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Error(Exception):
                def __init__(self):
                    try: next(generator)
                    except ValueError as error: print(str(error))
            def inner():
                try: yield 'ready'
                except Error: yield 'caught'
            def outer(): yield from inner()
            generator = outer()
            print(next(generator))
            print(generator.throw(Error))
            generator.close()
            """
        );

    [Fact]
    public Task YieldFromCustomIteratorReceivesRawExceptionClass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self): events.append('construct')
            class Iterator:
                def __iter__(self): return self
                def __next__(self): return 'ready'
                def throw(self, value):
                    events.append(value is Error)
                    return 'handled'
                def close(self): events.append('close')
            def outer(): yield from Iterator()
            generator = outer()
            print(next(generator), generator.throw(Error), events)
            generator.close()
            print(events)
            """
        );

    [Fact]
    public Task YieldFromGeneratorExitUsesDelegateCloseInsteadOfThrow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Exit(GeneratorExit):
                def __init__(self): events.append('construct')
            class Iterator:
                def __iter__(self): return self
                def __next__(self): return 'ready'
                def throw(self, value): events.append('throw')
                def close(self): events.append('close')
            def outer():
                try: yield from Iterator()
                except GeneratorExit as error: yield type(error).__name__
            for target in (GeneratorExit, Exit):
                events.clear()
                generator = outer()
                print(next(generator), generator.throw(target), events)
                generator.close()
            """
        );

    [Fact]
    public Task MetaclassConstructorResultsAreValidatedAndOtherExceptionsAreRenormalized() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __call__(cls, *args):
                    events.append(tuple(type(value).__name__ for value in args))
                    return result
            class Error(Exception, metaclass=Meta): pass
            def generator():
                try: yield 'ready'
                except BaseException as error:
                    yield (type(error).__name__, error is result, str(error))
            for result in (42, ValueError('other')):
                events.clear()
                instance = generator()
                print(next(instance), instance.throw(Error), events)
                instance.close()
            """
        );

    [Fact]
    public Task NonNoneInitializerResultsProduceAnInjectedTypeError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    return 42
            def generator():
                try: yield 'ready'
                except TypeError as error: yield str(error)
                yield 'after'
            instance = generator()
            print(next(instance), instance.throw(Error), events)
            print(next(instance))
            instance.close()
            """
        );

    [Fact]
    public Task AsyncThrowAwaitablesRejectReuseAfterAValueOrErrorWithoutRetainingOwnership() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            async def generator():
                try: yield 'ready'
                except ValueError: yield 'caught'
                yield 'after'
            async def drive():
                for target in (ValueError, TypeError, 42):
                    instance = generator()
                    print(await anext(instance))
                    step = instance.athrow(target)
                    for index in range(2):
                        try: print('value', await step)
                        except BaseException as error: print(type(error).__name__, str(error))
                    try: print('next', await anext(instance))
                    except StopAsyncIteration: print('closed')
                    await instance.aclose()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task ThrowConstructsExceptionClassesBeforeInjectingTheirArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    super().__init__('constructed', 7)
            def generator():
                try: yield 'ready'
                except Error as error:
                    events.append('caught')
                    yield (type(error) is Error, error.args)
            instance = generator()
            print(next(instance), events)
            print(instance.throw(Error), events)
            instance.close()
            """
        );

    [Fact]
    public Task ConstructorFailuresAreInjectedAndCanBeCaughtInsideSuspendedGenerators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    raise failure
            def generator():
                try: yield 'ready'
                except BaseException as error: yield (error is failure, type(error).__name__)
                finally: events.append('cleanup')
            for failure in (ValueError('failed'), StopIteration('stopped')):
                instance = generator()
                print(next(instance), instance.throw(Error))
                instance.close()
            print(events)
            """
        );

    [Fact]
    public Task RequiredConstructorArgumentsProduceCatchableInjectedTypeErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def generator():
                try: yield 'ready'
                except TypeError as error: yield str(error)
            class NeedsArgument(Exception):
                def __init__(self, value): super().__init__(value)
            for target in (NeedsArgument, ExceptionGroup):
                instance = generator()
                print(next(instance), instance.throw(target))
                instance.close()
            """
        );

    [Fact]
    public Task ExistingExceptionInstancesAreInjectedWithoutCallingTheirConstructorAgain() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self, value):
                    events.append(value)
                    super().__init__(value)
            original = Error('original')
            def forbidden(self): raise ValueError('constructor replayed')
            Error.__init__ = forbidden
            def generator():
                try: yield 'ready'
                except Error as error: yield (error is original, error.args)
            instance = generator()
            print(next(instance), instance.throw(original), events)
            instance.close()
            """
        );

    [Fact]
    public Task InvalidThrowArgumentsDoNotEnterOrResumeTheGenerator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Ordinary:
                def __init__(self): events.append('ordinary constructor')
            def generator():
                events.append('entered')
                try: yield 'first'
                except BaseException: yield 'injected'
                yield 'second'
            instance = generator()
            for invalid in (42, Ordinary):
                try: instance.throw(invalid)
                except TypeError as error: print(str(error), events)
            print(next(instance))
            try: instance.throw('invalid')
            except TypeError as error: print(str(error), events)
            print(next(instance))
            instance.close()
            """
        );

    [Fact]
    public Task FreshAndClosedGeneratorsStillNormalizeExceptionClasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('constructor failed')
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    raise failure
            def generator():
                events.append('entered')
                yield 1
            for closed in (False, True):
                instance = generator()
                if closed: instance.close()
                try: instance.throw(Error)
                except ValueError as error: print(error is failure)
                print(next(instance, 'closed'))
            print(events)
            """
        );

    [Fact]
    public Task RunningGeneratorChecksStateAfterConstructorSideEffectsAndValidation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Good(Exception):
                def __init__(self): events.append('good constructor')
            class Failing(Exception):
                def __init__(self):
                    events.append('failing constructor')
                    raise TypeError('constructor failed')
            def generator():
                for target in (Good, Failing, 42):
                    events.clear()
                    try: instance.throw(target)
                    except BaseException as error: print(type(error).__name__, str(error), events)
                yield 'ready'
            instance = generator()
            print(next(instance))
            instance.close()
            """
        );

    [Fact]
    public Task YieldFromDelegatesAConstructedExceptionExactlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    super().__init__('value')
            def inner():
                try: yield 'ready'
                except Error as error: yield error.args
            def outer(): yield from inner()
            instance = outer()
            print(next(instance), instance.throw(Error), events)
            instance.close()
            """
        );

    [Fact]
    public Task CoroutineThrowInjectsConstructorFailuresAndNormalizesBeforeReuseChecks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('constructor failed')
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    raise failure
            class Gate:
                def __await__(self): yield 'ready'
            async def coroutine():
                try: await Gate()
                except ValueError as error: return error is failure
            instance = coroutine()
            print(instance.send(None))
            try: instance.throw(Error)
            except StopIteration as stopped: print(stopped.value)
            for target in (Error, 42):
                try: instance.throw(target)
                except BaseException as error: print(type(error).__name__, str(error))
            print(events)
            """
        );

    [Fact]
    public Task AsyncThrowDefersConstructionAndUsesClassMutationBeforeAwait() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self):
                    events.append('original constructor')
                    super().__init__('original')
            def replacement(self):
                events.append('replacement constructor')
                self.args = ('replacement',)
            async def generator():
                try: yield 'ready'
                except Error as error: yield error.args
            async def drive():
                instance = generator()
                print(await anext(instance))
                step = instance.athrow(Error)
                print('created', events)
                Error.__init__ = replacement
                print(await step, events)
                await instance.aclose()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task AsyncThrowInjectsConstructorFailureAndPreservesExistingInstanceIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('constructor failed')
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    raise failure
            async def generator():
                try: yield 'ready'
                except BaseException as error: yield error is failure
            async def drive():
                for target in (Error, failure):
                    instance = generator()
                    print(await anext(instance))
                    step = instance.athrow(target)
                    print('created', events)
                    print(await step, events)
                    await instance.aclose()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task AsyncThrowIntoFreshGeneratorNormalizesOnlyWhenAwaited() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('constructor failed')
            class Error(Exception):
                def __init__(self):
                    events.append('construct')
                    raise failure
            async def generator():
                events.append('entered')
                yield 1
            async def drive():
                instance = generator()
                step = instance.athrow(Error)
                print(events)
                try: await step
                except ValueError as error: print(error is failure, events)
                try: await anext(instance)
                except StopAsyncIteration: print('closed')
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task ClosedAsyncGeneratorsSkipThrowValidationAndConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self): events.append('construct')
            async def generator(): yield 1
            async def drive():
                instance = generator()
                pending = instance.athrow(Error)
                await instance.aclose()
                print(await pending, events)
                for target in (Error, 42):
                    step = instance.athrow(target)
                    print('created', events)
                    print(await step, events)
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task InvalidAsyncThrowArgumentsAreLazyAndDoNotResumeSuspendedGenerators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            async def generator():
                try: yield 'first'
                except BaseException: yield 'injected'
                yield 'second'
            async def drive():
                instance = generator()
                print(await anext(instance))
                step = instance.athrow(42)
                print('created')
                try: await step
                except TypeError as error: print(str(error))
                print(await anext(instance))
                await instance.aclose()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task RunningAsyncGeneratorsRejectThrowBeforeValidationOrConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self): events.append('construct')
            async def generator():
                for target in (Error, 42):
                    try: await instance.athrow(target)
                    except RuntimeError as error: print(str(error), events)
                yield 'ready'
            async def drive():
                print(await anext(instance))
                await instance.aclose()
            instance = generator()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );
}
