using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UnicodeDecodeErrorCompatibilityTests
{
    [Fact]
    public Task FiveArgumentConstructionPreservesArgumentAndFieldIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            encoding = 'custom-codec'
            source = bytes((65, 255))
            reason = 'bad input'
            error = UnicodeDecodeError(encoding, source, 1, 2, reason)
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason)
            print(error.args[0] is encoding, error.args[1] is source, error.args[4] is reason)
            print(error.encoding is encoding, error.object is source, error.reason is reason)
            print(str(error), repr(error))
            """
        );

    [Fact]
    public Task GeneratedDecodingErrorsExposeCanonicalFieldsAndCopiedSourceIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, numbers in (('UTF8', [255]), ('UTF8', [65, 226, 130]), ('ascii', [65, 255]), ('utf16', [254, 255, 216, 0, 0, 65])):
                source = bytes(numbers)
                try: source.decode(encoding)
                except UnicodeDecodeError as error:
                    print(error.args, error.encoding, error.start, error.end, error.reason)
                    print(error.object == source, error.object is source, error.object is error.args[1])
            """
        );

    [Fact]
    public Task ConstructorArityAndKeywordErrorsUseBuiltinMessages() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((), ('x',), ('x', b'a', 0, 1), ('x', b'a', 0, 1, 'reason', 9)):
                try: UnicodeDecodeError(*args)
                except TypeError as error: print(str(error))
            try: UnicodeDecodeError('x', b'a', 0, 1, reason='bad')
            except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ConstructorValidatesStringsIndicesAndByteObjectsInReferenceOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((None, b'a', 0, 1, 'bad'), ('x', 'a', 0, 1, 'bad'), ('x', [65], 0, 1, 'bad'), ('x', b'a', 1.5, 1, 'bad'), ('x', b'a', 0, None, 'bad'), ('x', b'a', 0, 1, None), ('x', 'a', 1.5, 1, None)):
                try: UnicodeDecodeError(*args)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ConstructorConvertsStartThenEndButRetainsOriginalIndexArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            start = Index('start', 0)
            end = Index('end', 1)
            error = UnicodeDecodeError('x', b'a', start, end, 'bad')
            print(error.start, error.end, error.args[2] is start, error.args[3] is end, events)
            print(str(error))
            """
        );

    [Fact]
    public Task ConstructorChecksNativeIndexWidthAndPreservesCallbackExceptions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            problem = LookupError('index failed')
            class Index:
                def __index__(self): events.append('index'); raise problem
            try: UnicodeDecodeError('x', b'a', Index(), 1, 'bad')
            except LookupError as error: print(error is problem, str(error), events)
            for value in (-(2 ** 63) - 1, 2 ** 63):
                try: UnicodeDecodeError('x', b'a', value, 1, 'bad')
                except OverflowError as error: print(str(error))
            """
        );

    [Fact]
    public Task FormattingUsesRawIndicesAndOnlyValidSingletonRangesShowAByte() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in (b'', bytes([65, 255])):
                for start, end in ((0, 1), (1, 2), (-1, 0), (0, 0), (2, 3), (-10, 100), (5, 2), (-(2 ** 63), -(2 ** 63)), (2 ** 63 - 1, 2 ** 63 - 1)):
                    error = UnicodeDecodeError('x', source, start, end, 'bad')
                    print(error.start, error.end, str(error))
            """
        );

    [Fact]
    public Task ChangingFieldsDoesNotChangeArgsAndChangingArgsDoesNotChangeFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('old', b'ab', 0, 1, 'old reason')
            original = error.args
            error.encoding = 'new'
            error.object = bytes([255])
            error.start = 0
            error.end = 1
            error.reason = 'new reason'
            print(error.args is original, error.args, str(error))
            error.args = ('unrelated',)
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason, str(error))
            """
        );

    [Fact]
    public Task ObjectFieldsAcceptArbitraryValuesAndFormattingValidatesTheByteObject() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (None, 'not bytes', [65], 1):
                error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
                error.object = value
                print(error.object is value)
                try: print(str(error))
                except TypeError as problem: print(str(problem))
            error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
            error.encoding = None
            error.reason = 17
            print(str(error))
            """
        );

    [Fact]
    public Task DeletingObjectFieldsDiffersFromExplicitNoneAndNumericFieldsCannotBeDeleted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('encoding', 'object', 'reason'):
                error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
                delattr(error, name)
                print(name, getattr(error, name), str(error), error.args)
            for name in ('start', 'end'):
                error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
                try: delattr(error, name)
                except TypeError as problem: print(str(problem), error.start, error.end)
            """
        );

    [Fact]
    public Task NumericMemberAssignmentsRequireIntegersWithoutCallingIndex() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 1
            error = UnicodeDecodeError('x', b'ab', 0, 1, 'bad')
            for name in ('start', 'end'):
                for value in (Index(), None, 1.5, 2 ** 100, True, -(2 ** 63)):
                    try: setattr(error, name, value); print(getattr(error, name))
                    except (TypeError, OverflowError) as problem: print(str(problem))
            print(events)
            """
        );

    [Fact]
    public Task InstanceDictionaryEntriesCannotShadowStructuredDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
            for name in ('encoding', 'object', 'start', 'end', 'reason'):
                error.__dict__[name] = 'shadow'
            print(error.encoding, error.object, error.start, error.end, error.reason)
            print(error.__dict__)
            del error.reason
            print(error.reason, error.__dict__['reason'])
            """
        );

    [Fact]
    public Task FailedReinitializationUpdatesArgsButKeepsPreviouslyInitializedFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('old', b'a', 0, 1, 'old reason')
            for args in (('short',), ('new', b'b', 0, 1, None), ('new', 'not bytes', 0, 1, 'new reason'), ('new', b'b', 1.5, 1, 'new reason')):
                try: error.__init__(*args)
                except TypeError as problem: print(str(problem))
                print(error.args, error.encoding, error.object, error.start, error.end, error.reason)
            """
        );

    [Fact]
    public Task ReinitializationCallbacksObserveNewArgsAndCanMutateFieldsBeforeFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('old', b'a', 0, 1, 'old reason')
            events = []
            problem = LookupError('reinit failed')
            class Index:
                def __index__(self):
                    events.append(error.args[0])
                    error.reason = 'callback reason'
                    raise problem
            try: error.__init__('new', b'b', Index(), 1, 'new reason')
            except LookupError as caught: print(caught is problem, str(caught), events)
            print(error.args[0], error.encoding, error.object, error.start, error.end, error.reason)
            """
        );

    [Fact]
    public Task BaseExceptionInitializationChangesOnlyArgsAndSuccessfulReinitializationReplacesFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('old', b'a', 0, 1, 'old reason')
            print(BaseException.__init__(error, 'base'))
            print(error.args, error.encoding, error.object, str(error))
            print(UnicodeDecodeError.__init__(error, 'new', b'bc', 1, 2, 'new reason'))
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason, str(error))
            """
        );

    [Fact]
    public Task AllocationWithoutInitializationExposesEmptyStructuredStateAndPreservesArgs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((), ('allocated',), ('x', b'a', 0, 1, 'bad')):
                error = UnicodeDecodeError.__new__(UnicodeDecodeError, *args)
                print(error.args, error.encoding, error.object, error.start, error.end, error.reason, repr(str(error)))
            error = BaseException.__new__(UnicodeDecodeError, 'base allocated')
            print(error.args, error.object, repr(str(error)))
            """
        );

    [Fact]
    public Task SubclassesCanInitializeStructuredFieldsAndCustomizeStringConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(UnicodeDecodeError):
                def __init__(self, encoding, source, start, end, reason):
                    events.append('init')
                    super().__init__(encoding, source, start, end, reason)
                    self.extra = 7
                def __str__(self): return 'custom: ' + super().__str__()
            error = Error('x', b'a', 0, 1, 'bad')
            print(type(error).__name__, error.args, error.encoding, error.extra, str(error), events)
            """
        );

    [Fact]
    public Task FormattingConvertsReasonThenEncodingAndRereadsTheMutatedObjectAndBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            error = UnicodeDecodeError('x', b'ab', 0, 1, 'bad')
            class Reason:
                def __str__(self): events.append('reason'); error.object = b'cd'; error.start = 1; error.end = 2; return 'changed reason'
            class Encoding:
                def __str__(self): events.append('encoding'); error.object = bytes([65, 255]); return 'changed encoding'
            error.reason = Reason()
            error.encoding = Encoding()
            print(str(error), events)
            """
        );

    [Fact]
    public Task FormattingCallbackErrorsPreserveIdentityAndTheirMutationEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('format failed')
            events = []
            error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
            class Reason:
                def __str__(self): events.append('reason'); error.start = 7; raise problem
            class Encoding:
                def __str__(self): events.append('encoding'); return 'new'
            error.reason = Reason()
            error.encoding = Encoding()
            try: str(error)
            except LookupError as caught: print(caught is problem, str(caught), error.start, events)
            """
        );

    [Fact]
    public Task ShallowCopyReconstructsFieldsFromArgsAndCopiesTheInstanceDictionary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            error = UnicodeDecodeError('x', b'ab', 0, 1, 'bad')
            error.start = 1
            error.reason = 'mutated'
            error.extra = [7]
            cloned = copy.copy(error)
            print(cloned is error, cloned.args, cloned.start, cloned.reason, str(cloned))
            print(cloned.extra is error.extra, cloned.__dict__ is error.__dict__)
            """
        );

    [Fact]
    public Task DeepCopyPreservesSharedGraphReferencesAndCopiesTheInstanceState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            error = UnicodeDecodeError('x', b'ab', 0, 1, 'bad')
            error.extra = [7]
            error.reason = 'mutated'
            result = copy.deepcopy([error, error])
            cloned = result[0]
            print(cloned is result[1], cloned is error, cloned.extra == error.extra, cloned.extra is error.extra)
            print(cloned.args, cloned.reason, str(cloned))
            """
        );

    [Fact]
    public Task PickleReconstructsStructuredFieldsFromArgsAndRestoresExtraAttributes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import pickle
            error = UnicodeDecodeError('x', bytes([65, 255]), 1, 2, 'bad')
            error.reason = 'mutated'
            error.extra = [7]
            restored = pickle.loads(pickle.dumps(error))
            print(type(restored).__name__, restored.args, restored.encoding, restored.object, restored.start, restored.end, restored.reason)
            print(restored.extra, str(restored))
            """
        );

    [Fact]
    public Task DirectMemberDescriptorsReadWriteAndDeleteTheirStructuredStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('x', b'ab', 0, 1, 'bad')
            for name, value in (('encoding', 'new'), ('object', bytes([255])), ('reason', 17), ('start', True), ('end', 2)):
                descriptor = getattr(UnicodeDecodeError, name)
                print(descriptor.__get__(None, UnicodeDecodeError) is descriptor)
                print(descriptor.__get__(error), descriptor.__set__(error, value), descriptor.__get__(error, type(error)))
                if name in ('start', 'end'):
                    try: descriptor.__delete__(error)
                    except TypeError as problem: print(str(problem))
                else:
                    print(descriptor.__delete__(error), descriptor.__get__(error))
            print(error.args)
            """
        );

    [Fact]
    public Task MemberDescriptorCallsValidateTheirReceiverAndArgumentCounts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
            for name in ('start', 'reason'):
                descriptor = getattr(UnicodeDecodeError, name)
                for call in (lambda: descriptor.__get__(), lambda: descriptor.__get__(None), lambda: descriptor.__get__(error, type(error), None), lambda: descriptor.__set__(error), lambda: descriptor.__set__(error, 1, 2), lambda: descriptor.__delete__(), lambda: descriptor.__delete__(error, 1), lambda: descriptor.__get__(object()), lambda: descriptor.__set__(object(), 1), lambda: descriptor.__delete__(object())):
                    try: call()
                    except TypeError as problem: print(str(problem))
            """
        );

    [Fact]
    public Task SubclassClassAttributesShadowMembersWhileDirectDescriptorsKeepIndependentState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Child(UnicodeDecodeError):
                start = 99
            error = Child('x', b'ab', 0, 1, 'bad')
            descriptor = UnicodeDecodeError.start
            print(error.start, descriptor.__get__(error), error.__dict__)
            error.start = 7
            print(error.start, descriptor.__get__(error), error.__dict__)
            print(descriptor.__set__(error, 1))
            print(error.start, descriptor.__get__(error), str(error))
            del error.start
            print(error.start, descriptor.__get__(error), error.__dict__)
            """
        );

    [Fact]
    public Task InheritedMembersAndSubclassPropertiesFollowNormalDescriptorPrecedence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Plain(UnicodeDecodeError): pass
            class Child(Plain):
                @property
                def reason(self): events.append('get'); return self.custom
                @reason.setter
                def reason(self, value): events.append('set'); self.custom = value
            plain = Plain('x', b'a', 0, 1, 'bad')
            print(Plain.start is UnicodeDecodeError.start, Plain.start.__get__(plain))
            error = Child('x', b'a', 0, 1, 'underlying')
            error.reason = 'property'
            print(error.reason, UnicodeDecodeError.reason.__get__(error), events)
            error.__dict__['reason'] = 'shadow'
            print(error.reason, UnicodeDecodeError.reason.__get__(error), str(error), events)
            print(UnicodeDecodeError.reason.__set__(error, 'changed underlying'))
            print(error.reason, UnicodeDecodeError.reason.__get__(error), str(error), events)
            """
        );
}
