using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ListIndexMutationCompatibilityTests
{
    [Fact]
    public Task NegativeReadsUseTheLengthAfterIndexAppends() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, -2, -3, 2):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); return offset
                print(values[Index()], values, events)
            """
        );

    [Fact]
    public Task NegativeAssignmentsUseTheLengthAfterIndexAppends() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, -2, -3, 2):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); return offset
                values[Index()] = 99
                print(values, events)
            """
        );

    [Fact]
    public Task NegativeDeletionsUseTheLengthAfterIndexAppends() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for offset in (-1, -2, -3, 2):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.append(30); return offset
                del values[Index()]
                print(values, events)
            """
        );

    [Fact]
    public Task ClearingDuringConversionRaisesCatchableOperationSpecificIndexErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in ('get', 'set', 'delete'):
                for offset in (0, -1):
                    values = [10, 20]
                    events = []
                    class Index:
                        def __index__(self): events.append('index'); values.clear(); return offset
                    try:
                        if operation == 'get': print(values[Index()])
                        elif operation == 'set': values[Index()] = 99
                        else: del values[Index()]
                    except IndexError as error: print(operation, str(error), values, events)
                    values.append(42)
                    print(values[0])
            """
        );

    [Fact]
    public Task ReplacingContentsDuringConversionUsesTheReplacementLengthAndStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in ('get', 'set', 'delete'):
                values = [10, 20, 30, 40]
                class Index:
                    def __index__(self): values.clear(); values.extend([70, 80]); return -1
                if operation == 'get': print(values[Index()])
                elif operation == 'set': values[Index()] = 99
                else: del values[Index()]
                print(operation, values)
            """
        );

    [Fact]
    public Task ShrinkingDuringConversionRejectsPreviouslyValidPositiveIndices() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in ('get', 'set', 'delete'):
                values = [10, 20, 30]
                class Index:
                    def __index__(self): del values[1:]; return 2
                try:
                    if operation == 'get': print(values[Index()])
                    elif operation == 'set': values[Index()] = 99
                    else: del values[Index()]
                except IndexError as error: print(operation, str(error), values)
            """
        );

    [Fact]
    public Task InvalidIndexResultsPreserveCallbackMutationsWithoutApplyingTheOperation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in ('get', 'set', 'delete'):
                for result in (None, '0', 0.5, NotImplemented):
                    values = [10, 20]
                    events = []
                    class Index:
                        def __index__(self): events.append('index'); values.append(30); return result
                    try:
                        if operation == 'get': print(values[Index()])
                        elif operation == 'set': values[Index()] = 99
                        else: del values[Index()]
                    except TypeError as error: print(operation, str(error), values, events)
            """
        );

    [Fact]
    public Task IndexCallbackExceptionsPreserveIdentityAndMutationEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('index failed')
            for operation in ('get', 'set', 'delete'):
                values = [10, 20]
                events = []
                class Index:
                    def __index__(self): events.append('index'); values.clear(); raise problem
                try:
                    if operation == 'get': print(values[Index()])
                    elif operation == 'set': values[Index()] = 99
                    else: del values[Index()]
                except LookupError as error: print(operation, error is problem, str(error), values, events)
            """
        );

    [Fact]
    public Task OversizedIndexResultsReportTheOriginalIndexTypeAfterConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, result): self.result = result
                def __index__(self): events.append('index'); values.append(30); return self.result
            for operation in ('get', 'set', 'delete'):
                for result in (-(2 ** 63) - 1, 2 ** 63, -(2 ** 100), 2 ** 100):
                    for index in (result, Index(result)):
                        values = [10, 20]
                        events = []
                        try:
                            if operation == 'get': print(values[index])
                            elif operation == 'set': values[index] = 99
                            else: del values[index]
                        except IndexError as error: print(operation, str(error), values, events)
            """
        );

    [Fact]
    public Task RepresentableOutOfRangeIndicesUseOperationSpecificErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, result): self.result = result
                def __index__(self): return self.result
            for operation in ('get', 'set', 'delete'):
                for result in (-(2 ** 63), -(2 ** 31), -3, 2, 2 ** 31, 2 ** 63 - 1):
                    for index in (result, Index(result)):
                        values = [10, 20]
                        try:
                            if operation == 'get': print(values[index])
                            elif operation == 'set': values[index] = 99
                            else: del values[index]
                        except IndexError as error: print(operation, str(error), values)
            """
        );

    [Fact]
    public Task MissingAndDisabledIndexMethodsRetainTheirDistinctErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Missing: pass
            class Disabled: __index__ = None
            for operation in ('get', 'set', 'delete'):
                for index in (None, 0.5, '0', Missing(), Disabled()):
                    values = [10, 20]
                    try:
                        if operation == 'get': print(values[index])
                        elif operation == 'set': values[index] = 99
                        else: del values[index]
                    except TypeError as error: print(operation, str(error), values)
            """
        );

    [Fact]
    public Task IndexLookupUsesTheClassAndObservesClassMethodReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Base:
                def __index__(self): events.append('base'); values.append(30); return -1
            class Index(Base): pass
            index = Index()
            index.__index__ = lambda: 0
            print(values[index], values, events)
            def replacement(self): events.append('replacement'); values.append(40); return -1
            Base.__index__ = replacement
            values[index] = 99
            del values[index]
            print(values, events)
            """
        );

    [Fact]
    public Task AssignmentEvaluatesRightHandSideBeforeTargetAndIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Index:
                def __index__(self): events.append('index'); values.append(40); return -1
            def replacement(): events.append('rhs'); values.append(30); return 99
            def target(): events.append('target'); return values
            def key(): events.append('key'); return Index()
            target()[key()] = replacement()
            print(values, events)
            """
        );

    [Fact]
    public Task AugmentedAssignmentConvertsTheSameIndexForBothReadAndWrite() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Index:
                def __init__(self): self.calls = 0
                def __index__(self):
                    self.calls += 1
                    events.append(self.calls)
                    values.append(self.calls * 30)
                    return -1
            index = Index()
            def target(): events.append('target'); return values
            def key(): events.append('key'); return index
            def rhs(): events.append('rhs'); values.append(40); return 5
            target()[key()] += rhs()
            print(values, index.calls, events)
            """
        );

    [Fact]
    public Task AugmentedAssignmentWriteFailurePreservesReadAndRightHandSideEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Index:
                def __init__(self): self.calls = 0
                def __index__(self):
                    self.calls += 1
                    events.append(self.calls)
                    if self.calls == 2: values.clear()
                    return -1
            index = Index()
            def rhs(): events.append('rhs'); values.append(30); return 5
            try: values[index] += rhs()
            except IndexError as error: print(str(error), values, index.calls, events)
            """
        );

    [Fact]
    public Task NestedIndexOperationsFinishBeforeOuterNormalization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in ('get', 'set', 'delete'):
                values = [10, 20]
                events = []
                class Inner:
                    def __index__(self): events.append('inner'); values.append(30); return -1
                class Outer:
                    def __index__(self):
                        events.append('outer')
                        values[Inner()] = 40
                        values.append(50)
                        return -1
                if operation == 'get': print(values[Outer()])
                elif operation == 'set': values[Outer()] = 99
                else: del values[Outer()]
                print(operation, values, events)
            """
        );
}
