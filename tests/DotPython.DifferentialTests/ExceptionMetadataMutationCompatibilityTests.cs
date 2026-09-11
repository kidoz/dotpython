using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExceptionMetadataMutationCompatibilityTests
{
    [Fact]
    public Task CauseAndContextAssignmentsPreserveIdentityAndCauseEnablesSuppression() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class CustomError(Exception): pass
            error = CustomError('source')
            cause = ValueError('cause')
            context = CustomError('context')
            print(error.__cause__, error.__context__, error.__suppress_context__)
            error.__context__ = context
            print(error.__context__ is context, error.__suppress_context__)
            error.__cause__ = cause
            print(error.__cause__ is cause, error.__suppress_context__)
            error.__suppress_context__ = False
            error.__cause__ = None
            print(error.__cause__, error.__context__ is context, error.__suppress_context__)
            error.__suppress_context__ = False
            error.__context__ = None
            print(error.__context__, error.__suppress_context__, error.__dict__)
            error.__context__ = error
            error.__cause__ = error
            print(error.__context__ is error, error.__cause__ is error)
            """
        );

    [Fact]
    public Task InvalidMetadataAssignmentsAndDeletionsPreserveExistingSlotValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = Exception('source')
            cause = ValueError('cause')
            context = TypeError('context')
            error.__cause__ = cause
            error.__context__ = context
            error.__suppress_context__ = False
            for name in ('__cause__', '__context__'):
                for value in (17, Exception, 'invalid'):
                    try: setattr(error, name, value)
                    except TypeError as failure: print(str(failure))
            for value in (None, 0, 1, 'invalid'):
                try: error.__suppress_context__ = value
                except TypeError as failure: print(str(failure))
            for name in ('__cause__', '__context__', '__suppress_context__'):
                try: delattr(error, name)
                except TypeError as failure: print(str(failure))
            print(error.__cause__ is cause, error.__context__ is context, error.__suppress_context__)
            """
        );

    [Fact]
    public Task InstanceDictionaryEntriesCannotShadowReservedExceptionSlots() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = Exception('actual args')
            namespace = error.__dict__
            namespace.update({'__cause__': 'shadow cause', '__context__': 'shadow context',
                              '__suppress_context__': 'shadow suppression', 'args': 'shadow args'})
            print(error.__cause__, error.__context__, error.__suppress_context__, error.args)
            cause = ValueError('actual cause')
            error.__cause__ = cause
            error.args = ('replacement',)
            print(error.__cause__ is cause, error.__suppress_context__, error.args)
            print(namespace['__cause__'], namespace['__context__'], namespace['__suppress_context__'], namespace['args'])
            error.__dict__ = {'__cause__': 'replacement shadow'}
            print(error.__cause__ is cause, error.__context__, error.__suppress_context__, error.args)
            """
        );

    [Fact]
    public Task AddNoteCreatesAndMutatesTheSameListAndCanRecreateDeletedNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = Exception('source')
            print(hasattr(error, '__notes__'), error.add_note('first'))
            notes = error.__notes__
            print(error.add_note('second'), error.__notes__ is notes, notes)
            print(error.__dict__['__notes__'] is notes)
            notes.append(17)
            error.add_note('third')
            print(notes)
            replacement = ['replacement']
            error.__notes__ = replacement
            error.add_note('after replacement')
            print(error.__notes__ is replacement, replacement)
            del error.__notes__
            print(hasattr(error, '__notes__'))
            error.add_note('recreated')
            print(error.__notes__, error.__notes__ is replacement)
            """
        );

    [Fact]
    public Task AddNoteValidatesTheNoteBeforeExistingNotesAndPreservesInvalidState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            error = Exception('source')
            for note in (None, 17, [], ValueError('invalid')):
                try: error.add_note(note)
                except TypeError as failure: print(str(failure), hasattr(error, '__notes__'))
            for notes in (None, 17, 'invalid', ()):
                error.__notes__ = notes
                try: error.add_note('valid note')
                except TypeError as failure: print(str(failure), error.__notes__ is notes)
                try: error.add_note(17)
                except TypeError as failure: print(str(failure), error.__notes__ is notes)
            """
        );
}
