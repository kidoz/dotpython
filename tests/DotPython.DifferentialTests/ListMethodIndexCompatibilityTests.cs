using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ListMethodIndexCompatibilityTests
{
    [Fact]
    public Task PopNormalizesAfterIndexAppendsAndConvertsExactlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-3, -2, -1, 0, 2):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); return offset
                print(values.pop(Index()), values, events)
            """
        );

    [Fact]
    public Task PopConvertsBeforeCheckingWhetherAnInitiallyEmptyListHasItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, 0, 1):
                values = []
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.extend([10, 20]); return offset
                print(values.pop(Index()), values, events)
            """
        );

    [Fact]
    public Task PopReportsEmptyAfterTheIndexCallbackClearsTheList() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, 0, 2 ** 63 - 1):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.clear(); return offset
                try: values.pop(Index())
                except IndexError as error: print(str(error), values, events)
                values.append(42)
                print(values.pop(), values)
            """
        );

    [Fact]
    public Task PopReadsReplacementStorageAndRejectsIndicesInvalidatedByShrinking() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, 0, 1, 2):
                values = [10, 20, 30]
                class Index:
                    def __index__(self): values.clear(); values.extend([70, 80]); return offset
                try: print(values.pop(Index()))
                except IndexError as error: print(str(error))
                print(values)
            """
        );

    [Fact]
    public Task InsertNormalizesAndClampsAfterIndexAppends() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-100, -3, -1, 0, 2, 100):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); return offset
                print(values.insert(Index(), 99), values, events)
            """
        );

    [Fact]
    public Task InsertUsesTheContentsRemainingAfterTheIndexCallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for replacement in ([], [70], [70, 80, 90]):
                for offset in (-1, 1):
                    values = [10, 20]
                    class Index:
                        def __index__(self): values.clear(); values.extend(replacement); return offset
                    print(values.insert(Index(), 99), values)
            """
        );

    [Fact]
    public Task InsertClampsRepresentableNativeIndicesWithoutNarrowingToManagedInt() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            for offset in (-(2 ** 63), -(2 ** 31) - 1, -3, -1, 0, 2, 2 ** 31, 2 ** 63 - 1):
                for index in (offset, Index(offset)):
                    for initial in ([], [10, 20]):
                        values = initial.copy()
                        print(values.insert(index, 99), values)
            """
        );

    [Fact]
    public Task PopDistinguishesEmptyListsFromRepresentableOutOfRangeIndices() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            for offset in (-(2 ** 63), -(2 ** 31), -3, 2, 2 ** 31, 2 ** 63 - 1):
                for index in (offset, Index(offset)):
                    for initial in ([], [10, 20]):
                        values = initial.copy()
                        try: values.pop(index)
                        except IndexError as error: print(str(error), values)
            """
        );

    [Fact]
    public Task BothMethodsRejectNativeWidthOverflowAfterConversionBeforeListChecks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): events.append('index'); values.clear(); return self.value
            for method in ('pop', 'insert'):
                for offset in (-(2 ** 63) - 1, 2 ** 63, -(2 ** 100), 2 ** 100):
                    for index in (offset, Index(offset)):
                        for initial in ([], [10, 20]):
                            values = initial.copy()
                            events = []
                            try:
                                if method == 'pop': values.pop(index)
                                else: values.insert(index, 99)
                            except OverflowError as error: print(method, str(error), values, events)
            """
        );

    [Fact]
    public Task InvalidIndexReturnValuesPreserveCallbackMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for method in ('pop', 'insert'):
                for result in (None, '1', 1.5, NotImplemented):
                    values = []
                    events = []
                    class Index:
                        def __index__(self): events.append('index'); values.append(30); return result
                    try:
                        if method == 'pop': values.pop(Index())
                        else: values.insert(Index(), 99)
                    except TypeError as error: print(method, str(error), values, events)
            """
        );

    [Fact]
    public Task MissingAndDisabledIndexMethodsRetainTheirDistinctTypeErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Missing: pass
            class Disabled: __index__ = None
            for method in ('pop', 'insert'):
                for index in (None, 1.5, '1', Missing(), Disabled()):
                    for initial in ([], [10, 20]):
                        values = initial.copy()
                        try:
                            if method == 'pop': values.pop(index)
                            else: values.insert(index, 99)
                        except TypeError as error: print(method, str(error), values)
            """
        );

    [Fact]
    public Task CallbackExceptionsPreserveIdentityAndMutationEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('index failed')
            for method in ('pop', 'insert'):
                values = []
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); raise problem
                try:
                    if method == 'pop': values.pop(Index())
                    else: values.insert(Index(), 99)
                except LookupError as error: print(method, error is problem, str(error), values, events)
            """
        );

    [Fact]
    public Task DirectIntegersBooleansAndDefaultPopKeepTheirBuiltinBehavior() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for index in (False, True, -1, 0, 1):
                values = [10, 20, 30]
                print(values.pop(index), values)
                print(values.insert(index, 99), values)
            values = [10, 20]
            print(values.pop(), values.pop(), values)
            try: values.pop()
            except IndexError as error: print(str(error))
            """
        );

    [Fact]
    public Task ClassIndexLookupIgnoresInstanceShadowsAndTracksReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Base:
                def __index__(self): events.append('base'); values.append(30); return -1
            class Index(Base): pass
            index = Index()
            index.__index__ = lambda: 0
            print(values.pop(index), values, events)
            def replacement(self): events.append('replacement'); values.append(40); return -1
            Base.__index__ = replacement
            print(values.insert(index, 99), values, events)
            print(values.pop(index), values, events)
            """
        );

    [Fact]
    public Task SavedMethodsRetainTheirReceiverAcrossNameRebinding() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            original = values
            pop = values.pop
            insert = values.insert
            values = [70, 80]
            events = []
            class Index:
                def __index__(self): events.append('index'); original.append(30); return -1
            print(pop(Index()), original, values, events)
            print(insert(Index(), 99), original, values, events)
            """
        );

    [Fact]
    public Task NestedMethodConversionsFinishBeforeOuterNormalization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for method in ('pop', 'insert'):
                values = [10, 20]
                events = []
                class Inner:
                    def __index__(self): events.append('inner'); values.append(30); return -1
                class Outer:
                    def __index__(self):
                        events.append('outer')
                        values.insert(Inner(), 40)
                        events.append(values.pop(Inner()))
                        return -1
                if method == 'pop': print(values.pop(Outer()))
                else: print(values.insert(Outer(), 99))
                print(method, values, events)
            """
        );

    [Fact]
    public Task ArgumentsAreEvaluatedLeftToRightBeforeIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Index:
                def __index__(self): events.append('index'); values.append(40); return -1
            def target(): events.append('target'); return values
            def key(): events.append('key'); values.append(30); return Index()
            def item(): events.append('item'); values.append(35); return 99
            print(target().insert(key(), item()), values, events)
            events.clear()
            print(target().pop(key()), values, events)
            """
        );
}
