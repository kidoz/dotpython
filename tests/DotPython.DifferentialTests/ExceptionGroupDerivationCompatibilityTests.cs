using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExceptionGroupDerivationCompatibilityTests
{
    [Fact]
    public Task ExceptStarFailurePathsRestoreActiveExceptionStateAndPreserveOuterHandlers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class FailingGroup(ExceptionGroup):
                def derive(self, exceptions): raise RuntimeError('derive failed')
            def exercise(mode):
                group_type = FailingGroup if mode == 'derive' else ExceptionGroup
                root = group_type('root', [ValueError('matched'), TypeError('remaining')])
                selector = int if mode == 'invalid' else ValueError
                try:
                    try: raise root
                    except* selector:
                        if mode == 'body': raise RuntimeError('handler failed')
                except BaseException as error: print(mode, type(error).__name__)
            for mode in ('derive', 'invalid', 'remainder', 'body'):
                exercise(mode)
                try: raise ValueError('later')
                except ValueError as error: print('later context cleared', error.__context__ is None)
                try: raise
                except RuntimeError as error: print(str(error), error.__context__ is None)
                outer = KeyError('outer')
                try: raise outer
                except KeyError:
                    exercise(mode)
                    try: raise
                    except KeyError as error: print('outer preserved', error is outer)
            """
        );

    [Fact]
    public Task ExceptStarRejectsNestedTuplesAndValidatesHandlersAfterTheRemainderIsExhausted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for condition in (((ValueError,),), int, (ValueError, int)):
                try:
                    try: raise ExceptionGroup('root', [ValueError('value')])
                    except* condition: print('unexpected invalid handler')
                except TypeError as error: print(str(error))
                try:
                    try: raise ExceptionGroup('root', [ValueError('value')])
                    except* ValueError: print('handled all leaves')
                    except* condition: print('unexpected exhausted handler')
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ExplicitlyRaisingMatchedGroupsPreservesContextWithoutCreatingSelfContext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for context in (None, KeyError('original context')):
                for partial in (False, True):
                    leaves = [ValueError('value')]
                    if partial: leaves.append(TypeError('remaining'))
                    root = ExceptionGroup('root', leaves)
                    root.__context__ = context
                    try:
                        try: raise root
                        except* ValueError as matched:
                            saved = matched
                            print('before', matched.__context__ is context)
                            raise matched
                    except BaseException as result:
                        print('after', partial, saved.__context__ is context, saved.__context__ is saved)
                        if partial: print(result.exceptions[0] is saved)
                        else: print(result is saved)
            """
        );

    [Fact]
    public Task ExceptStarRecombinesBareReraisesButKeepsExplicitAndNewRaisesSeparate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(error):
                if isinstance(error, BaseExceptionGroup):
                    return (type(error).__name__, error.message, tuple(shape(child) for child in error.exceptions))
                return (type(error).__name__, error.args)
            class Group(ExceptionGroup):
                def derive(self, exceptions): return Group(self.message, exceptions)
            for mode in ('bare', 'explicit', 'new'):
                root = Group('root', [Group('inner', [ValueError('v'), TypeError('t')]), KeyError('k')])
                try:
                    try: raise root
                    except* ValueError as matched:
                        if mode == 'explicit': raise matched
                        if mode == 'new': raise RuntimeError('new')
                        raise
                except BaseException as result: print(mode, shape(result))
            """
        );

    [Fact]
    public Task ExceptStarReraiseClassificationUsesCauseAndContextButNotNotesOrSuppression() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(error):
                if isinstance(error, BaseExceptionGroup):
                    return (type(error).__name__, error.message, tuple(shape(child) for child in error.exceptions))
                return (type(error).__name__, error.args)
            class Group(ExceptionGroup):
                def derive(self, exceptions): return Group(self.message, exceptions)
            for mode in ('add note', 'replace notes', 'cause', 'context', 'suppression'):
                root = Group('root', [Group('inner', [ValueError('v'), TypeError('t')]), KeyError('k')])
                root.add_note('original')
                try:
                    try: raise root
                    except* ValueError as matched:
                        if mode == 'add note': matched.add_note('added')
                        if mode == 'replace notes': matched.__notes__ = ['replacement']
                        if mode == 'cause': matched.__cause__ = RuntimeError('changed')
                        if mode == 'context': matched.__context__ = RuntimeError('changed')
                        if mode == 'suppression': matched.__suppress_context__ = False
                        raise
                except BaseException as result:
                    print(mode, shape(result), getattr(result, '__notes__', None))
            """
        );

    [Fact]
    public Task ExceptStarProjectsUnmatchedBranchesAndRetainsNestedSubclassGroups() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(error):
                if isinstance(error, BaseExceptionGroup):
                    return (type(error).__name__, error.message, tuple(shape(child) for child in error.exceptions))
                return (type(error).__name__, error.args)
            class Group(ExceptionGroup):
                def derive(self, exceptions): return Group(self.message, exceptions)
            for handler in (ValueError, ZeroDivisionError):
                root = Group('root', [Group('inner', [ValueError('v'), TypeError('t')]), KeyError('k')])
                try:
                    try: raise root
                    except* handler as matched: print('handled', shape(matched))
                except BaseException as result: print('remaining', shape(result))
            """
        );

    [Fact]
    public Task BuiltinDeriveBypassesSubclassConstructionAndSelectsTheCanonicalGroupType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Group(ExceptionGroup):
                def __new__(cls, message, exceptions):
                    events.append('new')
                    return super().__new__(cls, message, exceptions)
            source = Group('source', [ValueError('old')])
            events.clear()
            ordinary = (TypeError('new'),)
            result = source.derive(ordinary)
            print(type(result) is ExceptionGroup, result.message, result.exceptions is ordinary, events)
            fatal = source.derive([KeyboardInterrupt('fatal')])
            print(type(fatal) is BaseExceptionGroup, fatal.message, events)
            promoted = fatal.derive(ordinary)
            print(type(promoted) is ExceptionGroup, promoted.exceptions is ordinary)
            """
        );

    [Fact]
    public Task SplitAndSubgroupPreserveNestedStructureAndLeafIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = ValueError('value')
            other = TypeError('type')
            key = KeyError('key')
            nested = ExceptionGroup('nested', [value, other])
            root = ExceptionGroup('root', [nested, key])
            matched, rest = root.split((ValueError, KeyError))
            print(matched.message, len(matched.exceptions), matched.exceptions[0].message)
            print(matched.exceptions[0].exceptions[0] is value, matched.exceptions[1] is key)
            print(rest.exceptions[0].exceptions[0] is other)
            print(root.subgroup(LookupError).exceptions[0] is key)
            print(root.subgroup(ZeroDivisionError), root.split(()))
            """
        );

    [Fact]
    public Task WholeGroupMatchesReturnOriginalObjectsWithoutDerivingOrVisitingChildren() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Group(ExceptionGroup):
                def derive(self, exceptions):
                    events.append('derive')
                    return super().derive(exceptions)
            root = Group('root', [ValueError('value'), TypeError('other')])
            def condition(error):
                events.append(error is root)
                return True
            print(root.subgroup(condition) is root, events)
            events.clear()
            matched, rest = root.split(condition)
            print(matched is root, rest, events)
            events.clear()
            print(root.subgroup(Exception) is root, root.split(BaseException)[0] is root, events)
            """
        );

    [Fact]
    public Task CallableConditionsRunDepthFirstAndUsePythonTruthiness() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Decision:
                def __init__(self, value): self.value = value
                def __bool__(self):
                    events.append(('bool', self.value))
                    return self.value
            class Predicate:
                def __call__(self, error):
                    label = error.message if isinstance(error, BaseExceptionGroup) else str(error)
                    events.append(label)
                    return Decision(isinstance(error, ValueError))
            root = ExceptionGroup('root', [ExceptionGroup('inner', [ValueError('v'), TypeError('t')]), KeyError('k')])
            matched, rest = root.split(Predicate())
            print(events)
            print(matched.exceptions[0].exceptions[0].args, len(rest.exceptions))
            """
        );

    [Fact]
    public Task ConditionsRejectNonExceptionClassesAndInvalidTuplesBeforeTraversal() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            root = ExceptionGroup('root', [ValueError('v')])
            for condition in (17, int, (ValueError, int), (ValueError, 17), ((ValueError,),)):
                for method in (root.split, root.subgroup):
                    try: method(condition)
                    except TypeError as error: print(str(error))
            print(root.subgroup(()), root.subgroup((TypeError, ValueError)).exceptions[0] is root.exceptions[0])
            """
        );

    [Fact]
    public Task PredicateFailuresPreserveIdentityAndStopBeforeFurtherLeavesOrDerivation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = RuntimeError('predicate failed')
            class Group(ExceptionGroup):
                def derive(self, exceptions):
                    events.append('derive')
                    return super().derive(exceptions)
            root = Group('root', [ValueError('v'), TypeError('t')])
            def predicate(error):
                events.append(type(error).__name__)
                if isinstance(error, ValueError): raise failure
                return False
            try: root.split(predicate)
            except RuntimeError as error: print(error is failure, events)
            """
        );

    [Fact]
    public Task CustomDeriveReceivesListsInDepthFirstMatchThenRemainderOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Group(ExceptionGroup):
                def derive(self, exceptions):
                    events.append((self.message, type(exceptions).__name__, tuple(type(error).__name__ for error in exceptions)))
                    result = Group(self.message, exceptions)
                    result.marker = self.marker
                    return result
            inner = Group('inner', [ValueError('v'), TypeError('t')])
            inner.marker = 'inner marker'
            root = Group('root', [inner, KeyError('k')])
            root.marker = 'root marker'
            matched, rest = root.split(ValueError)
            print(events)
            print(type(matched) is Group, matched.marker, matched.exceptions[0].marker)
            events.clear()
            root.subgroup(ValueError)
            print(events)
            """
        );

    [Fact]
    public Task ExceptStarUsesCustomDerivationForMatchingAndRemainingBranches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Group(ExceptionGroup):
                def derive(self, exceptions):
                    events.append(tuple(type(error).__name__ for error in exceptions))
                    return Group(self.message, exceptions)
            root = Group('root', [ValueError('v'), TypeError('t')])
            try: raise root
            except* ValueError as matched: print(type(matched) is Group, matched.message, events)
            except* TypeError as remaining: print(type(remaining) is Group, remaining.message, events)
            """
        );

    [Fact]
    public Task InvalidCustomDeriveResultsAreRejectedBeforeReturningFilteredGroups() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Group(ExceptionGroup):
                def derive(self, exceptions): return result
            root = Group('root', [ValueError('v'), TypeError('t')])
            for result in (17, None, ValueError('not group')):
                for method in (root.split, root.subgroup):
                    try: method(ValueError)
                    except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task DeriveFailuresPropagateFromPublicMethodsAndExceptStarWithTheirIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = RuntimeError('derive failed')
            class Group(ExceptionGroup):
                def derive(self, exceptions): raise failure
            root = Group('root', [ValueError('v'), TypeError('t')])
            for method in (root.split, root.subgroup):
                try: method(ValueError)
                except RuntimeError as error: print(error is failure)
            try:
                try: raise root
                except* ValueError: print('unexpected handler')
            except RuntimeError as error: print(error is failure)
            """
        );

    [Fact]
    public Task FilteringCopiesExceptionMetadataAndNotesButBuiltinDeriveDoesNot() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            root = ExceptionGroup('root', [ValueError('v'), TypeError('t')])
            cause = ValueError('cause')
            context = TypeError('context')
            root.__cause__ = cause
            root.__context__ = context
            root.__suppress_context__ = False
            root.add_note('first')
            root.extra = 'custom'
            direct = root.derive([root.exceptions[0]])
            print(direct.__cause__, direct.__context__, direct.__suppress_context__, hasattr(direct, '__notes__'), hasattr(direct, 'extra'))
            matched, rest = root.split(ValueError)
            for result in (matched, rest):
                print(result.__cause__ is cause, result.__context__ is context, result.__suppress_context__)
                print(result.__notes__, result.__notes__ is root.__notes__, hasattr(result, 'extra'))
            matched.__notes__.append('matched')
            print(root.__notes__, rest.__notes__)
            """
        );

    [Fact]
    public Task FilteringOverwritesMetadataOnCustomDerivedGroups() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Group(ExceptionGroup):
                def derive(self, exceptions):
                    result = ExceptionGroup('custom', exceptions)
                    result.__cause__ = RuntimeError('discarded cause')
                    result.__context__ = RuntimeError('discarded context')
                    result.add_note('discarded note')
                    result.extra = 'preserved'
                    return result
            root = Group('root', [ValueError('v'), TypeError('t')])
            root.add_note('source note')
            result = root.subgroup(ValueError)
            print(result.message, result.extra, result.__cause__, result.__context__, result.__suppress_context__)
            print(result.__notes__, result.__notes__ is root.__notes__)
            """
        );

    [Fact]
    public Task BuiltinDeriveValidatesSequenceAdmissionAndEachException() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            root = ExceptionGroup('root', [ValueError('v')])
            for exceptions in ([], (), [17], (value for value in [ValueError('v')])):
                try: root.derive(exceptions)
                except (TypeError, ValueError) as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task FilteringCopiesSequenceNotesAndIgnoresNonSequenceNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for notes in (None, 17, 'ab', ('a', 'b'), ['a']):
                root = ExceptionGroup('root', [ValueError('v'), TypeError('t')])
                root.__notes__ = notes
                result = root.subgroup(ValueError)
                print(getattr(result, '__notes__', 'missing'), result.__suppress_context__)
            """
        );

    [Fact]
    public Task InstanceDeriveOverridesAreResolvedDuringFiltering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            root = ExceptionGroup('root', [ValueError('v'), TypeError('t')])
            def derive(exceptions):
                events.append(type(exceptions).__name__)
                return ExceptionGroup('instance override', exceptions)
            root.derive = derive
            result = root.subgroup(ValueError)
            print(result.message, events)
            del root.derive
            print(root.subgroup(ValueError).message)
            """
        );
}
