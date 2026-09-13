using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UnicodeEncodeErrorCompatibilityTests
{
    [Fact]
    public Task ConstructorArityAndKeywordErrorsUseBuiltinMessages() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((), ('x',), ('x', 'a', 0, 1), ('x', 'a', 0, 1, 'reason', 9)):
                try: UnicodeEncodeError(*args)
                except TypeError as error: print(str(error))
            try: UnicodeEncodeError('x', 'a', 0, 1, reason='bad')
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
            error = UnicodeEncodeError('x', 'a', start, end, 'bad')
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
            try: UnicodeEncodeError('x', 'a', Index(), 1, 'bad')
            except LookupError as error: print(error is problem, str(error), events)
            for value in (-(2 ** 63) - 1, 2 ** 63):
                try: UnicodeEncodeError('x', 'a', value, 1, 'bad')
                except OverflowError as error: print(str(error))
            """
        );

    [Fact]
    public Task FormattingUsesRawIndicesAndOnlyValidSingletonRangesShowAUnicodeCharacter() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('', 'A😀'):
                for start, end in ((0, 1), (1, 2), (-1, 0), (0, 0), (2, 3), (-10, 100), (5, 2), (-(2 ** 63), -(2 ** 63)), (2 ** 63 - 1, 2 ** 63 - 1)):
                    error = UnicodeEncodeError('x', source, start, end, 'bad')
                    print(error.start, error.end, str(error))
            """
        );

    [Fact]
    public Task ChangingFieldsDoesNotChangeArgsAndChangingArgsDoesNotChangeFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeEncodeError('old', 'ab', 0, 1, 'old reason')
            original = error.args
            error.encoding = 'new'
            error.object = 'é'
            error.start = 0
            error.end = 1
            error.reason = 'new reason'
            print(error.args is original, error.args, str(error))
            error.args = ('unrelated',)
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason, str(error))
            """
        );

    [Fact]
    public Task DeletingObjectFieldsDiffersFromExplicitNoneAndNumericFieldsCannotBeDeleted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('encoding', 'object', 'reason'):
                error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
                delattr(error, name)
                print(name, getattr(error, name), str(error), error.args)
            for name in ('start', 'end'):
                error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
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
            error = UnicodeEncodeError('x', 'ab', 0, 1, 'bad')
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
            error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
            for name in ('encoding', 'object', 'start', 'end', 'reason'):
                error.__dict__[name] = 'shadow'
            print(error.encoding, error.object, error.start, error.end, error.reason)
            print(error.__dict__)
            del error.reason
            print(error.reason, error.__dict__['reason'])
            """
        );

    [Fact]
    public Task ReinitializationCallbacksObserveNewArgsAndCanMutateFieldsBeforeFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeEncodeError('old', 'a', 0, 1, 'old reason')
            events = []
            problem = LookupError('reinit failed')
            class Index:
                def __index__(self):
                    events.append(error.args[0])
                    error.reason = 'callback reason'
                    raise problem
            try: error.__init__('new', 'b', Index(), 1, 'new reason')
            except LookupError as caught: print(caught is problem, str(caught), events)
            print(error.args[0], error.encoding, error.object, error.start, error.end, error.reason)
            """
        );

    [Fact]
    public Task BaseExceptionInitializationChangesOnlyArgsAndSuccessfulReinitializationReplacesFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeEncodeError('old', 'a', 0, 1, 'old reason')
            print(BaseException.__init__(error, 'base'))
            print(error.args, error.encoding, error.object, str(error))
            print(UnicodeEncodeError.__init__(error, 'new', 'bc', 1, 2, 'new reason'))
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason, str(error))
            """
        );

    [Fact]
    public Task AllocationWithoutInitializationExposesEmptyStructuredStateAndPreservesArgs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((), ('allocated',), ('x', 'a', 0, 1, 'bad')):
                error = UnicodeEncodeError.__new__(UnicodeEncodeError, *args)
                print(error.args, error.encoding, error.object, error.start, error.end, error.reason, repr(str(error)))
            error = BaseException.__new__(UnicodeEncodeError, 'base allocated')
            print(error.args, error.object, repr(str(error)))
            """
        );

    [Fact]
    public Task SubclassesCanInitializeStructuredFieldsAndCustomizeStringConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(UnicodeEncodeError):
                def __init__(self, encoding, source, start, end, reason):
                    events.append('init')
                    super().__init__(encoding, source, start, end, reason)
                    self.extra = 7
                def __str__(self): return 'custom: ' + super().__str__()
            error = Error('x', 'a', 0, 1, 'bad')
            print(type(error).__name__, error.args, error.encoding, error.extra, str(error), events)
            """
        );

    [Fact]
    public Task FormattingConvertsReasonThenEncodingAndRereadsTheMutatedObjectAndBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            error = UnicodeEncodeError('x', 'ab', 0, 1, 'bad')
            class Reason:
                def __str__(self): events.append('reason'); error.object = 'cd'; error.start = 1; error.end = 2; return 'changed reason'
            class Encoding:
                def __str__(self): events.append('encoding'); error.object = 'A😀'; return 'changed encoding'
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
            error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
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
            error = UnicodeEncodeError('x', 'ab', 0, 1, 'bad')
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
            error = UnicodeEncodeError('x', 'ab', 0, 1, 'bad')
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
            error = UnicodeEncodeError('x', 'A😀', 1, 2, 'bad')
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
            error = UnicodeEncodeError('x', 'ab', 0, 1, 'bad')
            for name, value in (('encoding', 'new'), ('object', 'é'), ('reason', 17), ('start', True), ('end', 2)):
                descriptor = getattr(UnicodeEncodeError, name)
                print(descriptor.__get__(None, UnicodeEncodeError) is descriptor)
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
    public Task FiveArgumentConstructionPreservesTextAndArgumentIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            encoding = 'custom-codec'
            source = 'Aé😀'
            reason = 'bad input'
            error = UnicodeEncodeError(encoding, source, 1, 2, reason)
            print(error.args, error.encoding, error.object, error.start, error.end, error.reason)
            print(error.args[0] is encoding, error.args[1] is source, error.args[4] is reason)
            print(error.encoding is encoding, error.object is source, error.reason is reason)
            print(str(error), repr(error))
            """
        );

    [Fact]
    public Task TextObjectValidationOccursBeforeIndexCallbacksAndReasonValidation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 0
            for source in (None, b'a', [65], 1):
                events.clear()
                try: UnicodeEncodeError('x', source, Index(), Index(), None)
                except TypeError as error: print(str(error), events)
            for encoding, reason in ((None, 'bad'), ('x', None), (1, 'bad')):
                events.clear()
                try: UnicodeEncodeError(encoding, 'a', Index(), Index(), reason)
                except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task FailedReinitializationChangesArgsWhilePreservingThePreviousStructuredFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = UnicodeEncodeError('old', 'ab', 0, 1, 'old reason')
            for args in (('short',), ('new', 'cd', 0, 1, None), ('new', b'bytes', 0, 1, 'new reason'), ('new', 'cd', 1.5, 1, 'new reason')):
                try: error.__init__(*args)
                except TypeError as problem: print(str(problem))
                print(error.args, error.encoding, error.object, error.start, error.end, error.reason)
            """
        );

    [Fact]
    public Task MutableObjectFieldsAreValidatedAsTextAfterFormattingCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (None, b'bytes', [65], 1):
                error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
                error.object = value
                print(error.object is value)
                try: print(str(error))
                except TypeError as problem: print(str(problem))
            error = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
            error.encoding = None
            error.reason = 17
            print(str(error))
            """
        );

    [Fact]
    public Task UnicodeCharacterFormattingUsesCodePointPositionsAndEscapesByCharacterWidth() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aé€😀Z'
            for position in range(5):
                error = UnicodeEncodeError('x', source, position, position + 1, 'bad')
                print(str(error))
            for start, end in ((2, 4), (3, 5), (5, 6)):
                print(str(UnicodeEncodeError('x', source, start, end, 'bad')))
            print(str(UnicodeEncodeError('x', chr(0xd800), 0, 1, 'bad')))
            """
        );

    [Fact]
    public Task EncodeAndDecodeDescriptorsRejectEachOthersExceptionReceivers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            encode = UnicodeEncodeError('x', 'a', 0, 1, 'bad')
            decode = UnicodeDecodeError('x', b'a', 0, 1, 'bad')
            for owner, wrong in ((UnicodeEncodeError, decode), (UnicodeDecodeError, encode)):
                for name in ('encoding', 'object', 'start', 'end', 'reason'):
                    descriptor = getattr(owner, name)
                    for call in (lambda: descriptor.__get__(wrong), lambda: descriptor.__set__(wrong, 1), lambda: descriptor.__delete__(wrong)):
                        try: call()
                        except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task AsciiAndLatinOneGeneratedErrorsRetainSourceIdentityAndInvalidRuns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, sources in (('US-ASCII', ('é', 'Aé€😀Z', 'A😀Bé')), ('ISO-8859-1', ('€', 'A€😀Z', 'A😀B€'))):
                for source in sources:
                    try: source.encode(encoding)
                    except UnicodeEncodeError as error:
                        print(error.encoding, error.start, error.end, error.reason, str(error))
                        print(error.object is source, error.args[1] is source, len(error.args))
            """
        );

    [Fact]
    public Task Utf8AndUtf16GeneratedErrorsReportSurrogateRunsInCodePointCoordinates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, source in (('ascii', 'AZ'), ('latin1', 'Aé'), ('UTF_8', 'A😀'), ('utf16', 'A😀'), ('utf-16-le', 'A😀'), ('utf-16-be', 'A😀')):
                print(encoding, list(source.encode(encoding)))
            for encoding in ('UTF_8', 'utf16', 'utf-16-le', 'utf-16-be'):
                for source in (chr(0xd800), 'A😀' + chr(0xdc00) + 'Z', 'A' + chr(0xd800) + chr(0xd801) + 'Z'):
                    try: source.encode(encoding)
                    except UnicodeEncodeError as error:
                        print(error.encoding, error.start, error.end, error.reason, str(error))
                        print(error.object is source, error.args[1] is source, len(error.args))
            """
        );
}
