using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExceptionGroupTypeMetadataCompatibilityTests
{
    [Fact]
    public Task ContextManagerReceivesTheCanonicalRenamedExceptionGroupType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            canonical = ExceptionGroup
            group = ExceptionGroup('message', [ValueError('value')])
            ExceptionGroup.__name__ = 'Renamed'
            class Manager:
                def __enter__(self): return self
                def __exit__(self, exception_type, exception, traceback):
                    print(exception_type is canonical, exception is group)
                    print(exception_type is type(exception), exception_type.__name__)
                    return True
            with Manager(): raise group
            print('continued')
            """
        );

    [Fact]
    public Task CanonicalAndChildConstructorsValidateArityAndBaseExceptionLeaves() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Child(ExceptionGroup): pass
            for target in (ExceptionGroup, Child):
                for arguments in ((), ('message',)):
                    try: target(*arguments)
                    except TypeError as error: print(str(error))
                try: raise target
                except TypeError as error: print('raise', str(error))
                try: target('message', [SystemExit()])
                except TypeError as error: print(str(error))
            ExceptionGroup.__name__ = 'Renamed'
            Child.__name__ = 'RenamedChild'
            for target in (ExceptionGroup, Child):
                try: target('message', [KeyboardInterrupt()])
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task GroupArgumentsRetainTheOriginalSequenceWhileExceptionsAreASnapshot() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Child(ExceptionGroup): pass
            for target in (ExceptionGroup, Child):
                for leaves in ([ValueError('value')], (ValueError('value'),)):
                    group = target('message', leaves)
                    print(group.args[1] is leaves, group.exceptions[0] is leaves[0])
                    print(type(group.exceptions).__name__, len(group.exceptions))
                    if isinstance(leaves, list):
                        leaves.append(TypeError('extra'))
                        print(len(group.args[1]), len(group.exceptions), repr(group), str(group))
            """
        );

    [Fact]
    public Task ExceptStarProducesTheCanonicalTypeAfterExceptionGroupIsRenamed() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            canonical = ExceptionGroup
            ExceptionGroup.__name__ = 'Renamed'
            group = ExceptionGroup('message', [ValueError('value'), TypeError('type')])
            try: raise group
            except* ValueError as matched:
                print(type(matched) is canonical, repr(matched))
            except* TypeError as matched:
                print(type(matched) is canonical, repr(matched))
            """
        );

    [Fact]
    public Task ExceptionGroupStartsWithHeapTypeMetadataAndTheExpectedTwoBases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(type(ExceptionGroup) is type)
            print(ExceptionGroup.__name__, ExceptionGroup.__qualname__, ExceptionGroup.__module__)
            print(ExceptionGroup.__doc__ is None, ExceptionGroup.__dict__['__doc__'] is None)
            print(ExceptionGroup.__dict__['__module__'])
            print('__name__' in ExceptionGroup.__dict__, '__qualname__' in ExceptionGroup.__dict__)
            print(ExceptionGroup.__bases__ == (BaseExceptionGroup, Exception))
            print(ExceptionGroup.__base__ is BaseExceptionGroup)
            print(ExceptionGroup.__mro__ == (ExceptionGroup, BaseExceptionGroup, Exception, BaseException, object))
            print(type.__dict__['__name__'].__get__(ExceptionGroup))
            """
        );

    [Fact]
    public Task CustomAttributesUseTheLiveClassNamespaceAndExistingInstances() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            group = ExceptionGroup('message', [ValueError('value')])
            namespace = ExceptionGroup.__dict__
            value = []
            ExceptionGroup.custom = value
            print(namespace['custom'] is value, group.custom is value)
            value.append(7)
            print(group.custom)
            ExceptionGroup.custom = 'replacement'
            print(namespace['custom'], group.custom)
            del ExceptionGroup.custom
            print('custom' in namespace, hasattr(group, 'custom'))
            try: BaseExceptionGroup.custom = 7
            except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task RenamingExceptionGroupUpdatesExistingInstanceRepresentationAndKeepsIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            canonical = ExceptionGroup
            group = ExceptionGroup('message', [ValueError('value')])
            ExceptionGroup.__name__ = 'Renamed'
            ExceptionGroup.__qualname__ = 'Scope.Qualified'
            print(repr(ExceptionGroup), repr(group), str(group))
            print(type(group) is canonical, isinstance(group, canonical))
            ExceptionGroup.__module__ = 'custom.module'
            print(repr(ExceptionGroup), repr(group))
            print(type(BaseExceptionGroup('promoted', [ValueError()])) is canonical)
            class Ordinary: pass
            Ordinary.__module__ = 'builtins'
            Ordinary.__name__ = 'Short'
            Ordinary.__qualname__ = 'Scope.Qualified'
            print(repr(Ordinary))
            Ordinary.__module__ = None
            print(repr(Ordinary))
            Ordinary.__module__ = ''
            print(repr(Ordinary))
            """
        );

    [Fact]
    public Task ModuleAndDocAcceptArbitraryValuesWhileNamesRetainValidation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            namespace = ExceptionGroup.__dict__
            documentation = []
            ExceptionGroup.__module__ = None
            ExceptionGroup.__doc__ = documentation
            print(namespace['__module__'] is None, namespace['__doc__'] is documentation)
            print(ExceptionGroup.__doc__ is documentation, repr(ExceptionGroup))
            for name in ('__name__', '__qualname__'):
                try: setattr(ExceptionGroup, name, 42)
                except TypeError as error: print(str(error))
            try: ExceptionGroup.__name__ = 'bad\x00name'
            except ValueError as error: print(str(error))
            ExceptionGroup.__qualname__ = 'valid\x00qualname'
            print(repr(ExceptionGroup.__qualname__))
            print('__name__' in namespace, '__qualname__' in namespace)
            """
        );

    [Fact]
    public Task MetadataDeletionAndReadonlyAttributesRemainRestricted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('__name__', '__qualname__', '__module__', '__doc__', '__bases__'):
                try: delattr(ExceptionGroup, name)
                except TypeError as error: print(str(error))
            for name in ('__dict__', '__mro__', '__base__'):
                try: setattr(ExceptionGroup, name, ())
                except AttributeError as error: print(str(error))
            print(ExceptionGroup.__name__, ExceptionGroup.__doc__ is None)
            """
        );

    [Fact]
    public Task SameBasesAssignmentPreservesTupleIdentityAndReordersParentRegistration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sibling(BaseExceptionGroup): pass
            class Child(ExceptionGroup): pass
            selected = (ExceptionGroup, Sibling)
            print([child.__name__ for child in BaseExceptionGroup.__subclasses__() if child in selected])
            original = ExceptionGroup.__bases__
            ExceptionGroup.__bases__ = original
            print(ExceptionGroup.__bases__ is original)
            print([child.__name__ for child in BaseExceptionGroup.__subclasses__() if child in selected])
            print(ExceptionGroup.__subclasses__() == [Child])
            """
        );

    [Fact]
    public Task RemovingExceptionBaseChangesExistingInstanceChecksAndCatching() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            group = ExceptionGroup('message', [ValueError('value')])
            ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            print(type(group) is ExceptionGroup, isinstance(group, Exception))
            print(issubclass(ExceptionGroup, Exception), isinstance(group, BaseExceptionGroup))
            print(ExceptionGroup in Exception.__subclasses__(), ExceptionGroup in BaseExceptionGroup.__subclasses__())
            try: raise group
            except Exception: print('Exception caught')
            except BaseException as error: print('BaseException caught', error is group)
            """
        );

    [Fact]
    public Task CanonicalConstructorPolicyAndPromotionSurviveRenameAndBaseRemoval() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            canonical = ExceptionGroup
            ExceptionGroup.__name__ = 'Renamed'
            ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            print(type(ExceptionGroup('ordinary', [ValueError()])) is canonical)
            print(type(BaseExceptionGroup('promoted', [ValueError()])) is canonical)
            print(type(BaseExceptionGroup('base', [SystemExit()])) is BaseExceptionGroup)
            try: ExceptionGroup('rejected', [SystemExit()])
            except TypeError as error: print(str(error))
            class Child(ExceptionGroup): pass
            child = Child('allowed', [SystemExit()])
            print(type(child) is Child, isinstance(child, Exception))
            """
        );

    [Fact]
    public Task ReorderedBasesRetainTheGroupLayoutAndCanRestoreTheOriginalTuple() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            original = ExceptionGroup.__bases__
            group = ExceptionGroup('message', [ValueError('value')])
            descriptor = type.__dict__['__bases__']
            reordered = (Exception, BaseExceptionGroup)
            descriptor.__set__(ExceptionGroup, reordered)
            print(ExceptionGroup.__bases__ is reordered, ExceptionGroup.__base__ is BaseExceptionGroup)
            print(ExceptionGroup.__mro__ == (ExceptionGroup, Exception, BaseExceptionGroup, BaseException, object))
            print(str(group), repr(group), isinstance(group, Exception))
            descriptor.__set__(ExceptionGroup, original)
            print(ExceptionGroup.__bases__ is original)
            print(ExceptionGroup.__mro__ == (ExceptionGroup, BaseExceptionGroup, Exception, BaseException, object))
            """
        );

    [Fact]
    public Task InvalidLayoutsAndCyclesPreserveExceptionGroupMetadata() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class HeapGroup(BaseExceptionGroup): pass
            original_bases = ExceptionGroup.__bases__
            original_mro = ExceptionGroup.__mro__
            for bases in ((Exception,), (BaseException,), (object,), (HeapGroup,), (ExceptionGroup,), ()):
                try: ExceptionGroup.__bases__ = bases
                except TypeError as error: print(str(error))
                print(ExceptionGroup.__bases__ is original_bases, ExceptionGroup.__mro__ is original_mro)
            """
        );

    [Fact]
    public Task BaseMutationUpdatesDescendantBranchesAndExistingInstances() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def mro(cls):
                    events.append(cls.__name__)
                    return type.mro(cls)
            class Left(ExceptionGroup, metaclass=Meta): pass
            class Right(ExceptionGroup, metaclass=Meta): pass
            class Grandchild(Left): pass
            group = Grandchild('message', [ValueError()])
            events.clear()
            ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            print(events)
            print(isinstance(group, Exception), issubclass(Grandchild, Exception))
            print(Grandchild.__mro__ == (Grandchild, Left, ExceptionGroup, BaseExceptionGroup, BaseException, object))
            print(ExceptionGroup.__subclasses__() == [Left, Right])
            """
        );

    [Fact]
    public Task DescendantCallbackFailureRestoresTheCanonicalGroupHierarchy() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            failure = ValueError('mro failed')
            class Meta(type):
                def mro(cls):
                    if active: raise failure
                    return type.mro(cls)
            class Child(ExceptionGroup, metaclass=Meta): pass
            original_bases = ExceptionGroup.__bases__
            original_mro = ExceptionGroup.__mro__
            original_child = Child.__mro__
            group = Child('message', [ValueError()])
            active = True
            try: ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            except ValueError as error: print(error is failure)
            print(ExceptionGroup.__bases__ is original_bases, ExceptionGroup.__mro__ is original_mro)
            print(Child.__mro__ is original_child, isinstance(group, Exception))
            print(ExceptionGroup in Exception.__subclasses__(), ExceptionGroup.__subclasses__() == [Child])
            """
        );

    [Fact]
    public Task ReplacingExceptionWithValueErrorChangesGroupExceptionMatching() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            group = ExceptionGroup('message', [TypeError('value')])
            ExceptionGroup.__bases__ = (BaseExceptionGroup, ValueError)
            print(isinstance(group, ValueError), issubclass(ExceptionGroup, ValueError))
            print(ExceptionGroup in ValueError.__subclasses__(), ExceptionGroup in Exception.__subclasses__())
            try: raise group
            except ValueError as error: print(error is group)
            print(group.message, group.exceptions[0].args)
            """
        );
}
