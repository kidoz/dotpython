using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BytesConstructionCompatibilityTests
{
    [Fact]
    public Task BytesHookTakesPriorityOverCountAndIterationAndPreservesItsResult() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            result = bytes((97, 98))
            class Source:
                def __bytes__(self): events.append('bytes'); return result
                def __index__(self): events.append('index'); return 3
                def __iter__(self): events.append('iter'); return iter([99])
            print(bytes(Source()) is result, events)
            """
        );

    [Fact]
    public Task BytesHookLookupUsesTheClassAndTracksMethodReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __bytes__(self): events.append('base'); return b'a'
            class Source(Base): pass
            source = Source()
            source.__bytes__ = lambda: b'z'
            print(bytes(source), events)
            def replacement(self): events.append('replacement'); return b'b'
            Base.__bytes__ = replacement
            print(bytes(source), events)
            """
        );

    [Fact]
    public Task BytesHookDescriptorsBindBeforeCallingAndDisabledHooksDoNotFallBack() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __get__(self, instance, owner):
                    events.append(instance is source)
                    return lambda: b'ab'
            class Source:
                __bytes__ = Descriptor()
            source = Source()
            print(bytes(source), events)
            class Disabled:
                __bytes__ = None
                def __index__(self): events.append('index'); return 1
                def __iter__(self): events.append('iter'); return iter([1])
            try: bytes(Disabled())
            except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task InvalidBytesHookResultsRaiseWithoutTryingOtherProtocols() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __bytes__(self): events.append('bytes'); return result
                def __index__(self): events.append('index'); return 1
            for result in (None, 'abc', 3, [1], NotImplemented):
                events.clear()
                try: bytes(Source())
                except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task BytesHookExceptionsPreserveIdentityIncludingTypeError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            for problem in (LookupError('hook failed'), TypeError('hook type failed')):
                class Source:
                    def __bytes__(self): events.append('bytes'); raise problem
                    def __iter__(self): events.append('iter'); return iter([1])
                events.clear()
                try: bytes(Source())
                except Exception as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task CountConversionPrecedesIterationAndAcceptsBooleansAndBoundedCounts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __init__(self, count): self.count = count
                def __index__(self): events.append('index'); return self.count
                def __iter__(self): events.append('iter'); return iter([99])
            for count in (0, 1, 3, 5000):
                events.clear()
                value = bytes(Source(count))
                print(len(value), sum(value), events)
            print(list(bytes(False)), list(bytes(True)))
            """
        );

    [Fact]
    public Task NegativeAndOversizedCountsRaiseBeforeTryingIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __init__(self, count): self.count = count
                def __index__(self): events.append('index'); return self.count
                def __iter__(self): events.append('iter'); return iter([99])
            for count in (-1, -(2 ** 63), -(2 ** 63) - 1, 2 ** 63, 2 ** 100):
                for source in (count, Source(count)):
                    events.clear()
                    try: bytes(source)
                    except (ValueError, OverflowError) as error: print(type(error).__name__, str(error), events)
            """
        );

    [Fact]
    public Task TypeErrorsFromCountConversionFallBackToIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __index__(self):
                    events.append('index')
                    if result == 'raise': raise TypeError('index failed')
                    return result
                def __iter__(self): events.append('iter'); return iter([65, 66])
            for result in (None, 1.5, 'raise'):
                events.clear()
                print(bytes(Source()), events)
            class Disabled:
                __index__ = None
                def __iter__(self): events.append('iter'); return iter([67])
            events.clear()
            print(bytes(Disabled()), events)
            """
        );

    [Fact]
    public Task NonTypeCountErrorsPreserveIdentityAndStopBeforeIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            for problem in (LookupError('index failed'), OverflowError('overflow failed')):
                class Source:
                    def __index__(self): events.append('index'); raise problem
                    def __iter__(self): events.append('iter'); return iter([1])
                events.clear()
                try: bytes(Source())
                except Exception as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task ListElementConversionObservesAppendsDeletionAndReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('append', 'clear', 'delete', 'replace'):
                events = []
                class Item:
                    def __index__(self):
                        events.append('index')
                        if action == 'append': source.extend([68, 69])
                        elif action == 'clear': source.clear()
                        elif action == 'delete': del source[0]
                        else: source.clear(); source.extend([70, 71, 72])
                        return 65
                source = [Item(), 66, 67]
                print(action, list(bytes(source)), len(source), events)
            """
        );

    [Fact]
    public Task TupleElementConversionKeepsTheOriginalFixedItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __index__(self): events.append('index'); original.clear(); original.extend([70, 71]); return 65
            original = [Item(), 66, 67]
            source = tuple(original)
            print(list(bytes(source)), original, events)
            """
        );

    [Fact]
    public Task GenericConstructionInterleavesAdvanceAndElementConversionAfterSourceHint() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value): self.value = value
                def __index__(self): events.append(('index', self.value)); return self.value
            class Cursor:
                def __init__(self): self.position = 0
                def __iter__(self): events.append('cursor iter'); return self
                def __length_hint__(self): events.append('cursor hint'); return 100
                def __next__(self):
                    events.append(('next', self.position))
                    if self.position == 3: raise StopIteration
                    self.position += 1
                    return Item(self.position + 64)
            class Source:
                def __iter__(self): events.append('source iter'); return Cursor()
                def __length_hint__(self): events.append('source hint'); return 1
            print(bytes(Source()), events)
            """
        );

    [Fact]
    public Task InvalidHintsFailAfterIteratorCreationAndBeforeAnyAdvance() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): events.append('iter'); return self
                def __next__(self): events.append('next'); raise StopIteration
                def __length_hint__(self): events.append('hint'); return hint
            for hint in (-1, 1.5, None, 2 ** 100):
                events.clear()
                try: bytes(Source())
                except (TypeError, ValueError, OverflowError) as error: print(type(error).__name__, str(error), events)
            """
        );

    [Fact]
    public Task IteratorAcquisitionTypeErrorsAreReplacedButOtherErrorsKeepTheirIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            for problem in (TypeError('iter type failed'), LookupError('iter failed')):
                class Source:
                    def __iter__(self): events.append('iter'); raise problem
                    def __length_hint__(self): events.append('hint'); return 1
                events.clear()
                try: bytes(Source())
                except Exception as error: print(error is problem, str(error), events)
            class Missing: pass
            for source in (None, 1.5, Missing()):
                try: bytes(source)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task OutOfRangeElementsUseRangeErrorsEvenForArbitraryIntegers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Item:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            for value in (-1, 256, -(2 ** 100), 2 ** 100):
                for item in (value, Item(value)):
                    for source in ([item], (item,), iter([item])):
                        try: bytes(source)
                        except ValueError as error: print(str(error))
            """
        );

    [Fact]
    public Task NonintegerElementErrorsDoNotFallBackToElementIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Item:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
                def __iter__(self): raise RuntimeError('must not iterate element')
            class Disabled: __index__ = None
            for item in (None, 1.5, '1', Item(None), Item(1.5), Item('1'), Disabled()):
                for source in ([item], (item,), iter([item])):
                    try: bytes(source)
                    except TypeError as error: print(str(error))
            print(list(bytes([False, True, 128, 255])))
            """
        );

    [Fact]
    public Task BadElementsLeaveTheIteratorPositionImmediatelyAfterThatElement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def source():
                for value in (65, 256, 66): events.append(value); yield value
            iterator = source()
            try: bytes(iterator)
            except ValueError as error: print(str(error), events)
            print(next(iterator), events)
            """
        );

    [Fact]
    public Task ElementExceptionsPreserveIdentityAndListMutationsWithoutPublishingBytes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('element failed')
            events = []
            class Item:
                def __index__(self): events.append('index'); source.append(67); raise problem
            source = [65, Item(), 66]
            target = 'original'
            try: target = bytes(source)
            except LookupError as error: print(error is problem, str(error), target, len(source), events)
            """
        );

    [Fact]
    public Task SourceLengthPrecedesHintAndTypeErrorLengthFallsBackToHint() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): events.append('iter'); return iter([65])
                def __len__(self):
                    events.append('len')
                    if failing: raise TypeError('len type failed')
                    return 1
                def __length_hint__(self): events.append('hint'); return 1
            for failing in (False, True):
                events.clear()
                print(bytes(Source()), events)
            """
        );

    [Fact]
    public Task WriterCapacityDeterminesSingletonCachingForGenericAndShrinkingListSources() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            cached = bytes([65])
            empty = bytes()
            class Source:
                def __init__(self, hint, content): self.hint = hint; self.content = content
                def __iter__(self): return iter(self.content)
                def __length_hint__(self): return self.hint
            for hint in (0, 64, 512, 513):
                result = bytes(Source(hint, [65]))
                print(hint, result == cached, result is cached, bytes(Source(hint, [])) is empty)
            for size in (1, 512, 513):
                class Item:
                    def __index__(self): source.clear(); return 65
                source = [Item()] + [66] * (size - 1)
                result = bytes(source)
                print(size, result == cached, result is cached)
            result = bytes((65,))
            print(result == cached, result is cached, bytes(result) is result)
            """
        );

    [Fact]
    public Task KeywordConstructionPreservesSourceEncodingErrorsAndOmittedArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(bytes(source=[65, 66]))
            print(bytes(source='ab', encoding='ascii'))
            print(bytes('ab', encoding='ascii', errors='strict'))
            print(bytes(source=3) == bytes(3))
            for call in (lambda: bytes(encoding='ascii'), lambda: bytes(errors='strict'), lambda: bytes('ab', errors='strict'), lambda: bytes([65], encoding='ascii'), lambda: bytes(source=None), lambda: bytes(None, errors='strict')):
                try: call()
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task EncodingAndErrorValidationPrecedesConversionAndReportsDuplicateArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __bytes__(self): events.append('bytes'); return b'ab'
            for call in (lambda: bytes(Source(), encoding=None), lambda: bytes(Source(), errors=None), lambda: bytes(Source(), encoding=1), lambda: bytes(Source(), errors=1), lambda: bytes('ab', encoding='ascii' + chr(0)), lambda: bytes('ab', errors='strict' + chr(0)), lambda: bytes('ab', source='ab'), lambda: bytes('ab', 'ascii', encoding='ascii'), lambda: bytes('ab', 'ascii', 'strict', errors='strict'), lambda: bytes(unknown=1)):
                try: call()
                except (TypeError, ValueError) as error: print(str(error), events)
            """
        );
}
