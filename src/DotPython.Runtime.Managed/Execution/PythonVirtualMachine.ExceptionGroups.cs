using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    PythonValue IUserObjectDispatcher.InvokeExceptionGroupMethod(
        string name,
        PythonExceptionValue group,
        PythonValue argument,
        TextSpan span
    )
    {
        if (name == "derive")
        {
            if (!IsExceptionGroupSequence(argument))
                throw Fault(
                    "DPY4003",
                    "second argument (exceptions) must be a sequence",
                    span,
                    "TypeError"
                );
            return CreateExceptionGroupValue([new PythonTextValue(group.Message), argument]);
        }
        var predicate = !PythonTypeProtocols.IsType(argument) && Callable([argument], span).Value;
        if (!predicate && !IsExceptionGroupTypeSelector(argument))
            throw Fault(
                "DPY4003",
                "expected an exception type, a tuple of exception types, or a callable (other than a class)",
                span,
                "TypeError"
            );
        var result = SplitExceptionGroup(
            group,
            exception =>
                predicate
                    ? IsTruthy(InvokeCallableNested(argument, [exception], span))
                    : MatchesExceptionType(exception, argument, span),
            name == "split",
            span
        );
        return name == "split"
            ? new PythonTupleValue([
                (PythonValue?)result.Matched ?? PythonNoneValue.Instance,
                (PythonValue?)result.Remainder ?? PythonNoneValue.Instance,
            ])
            : (PythonValue?)result.Matched ?? PythonNoneValue.Instance;
    }

    private static bool IsExceptionGroupTypeSelector(PythonValue value) =>
        value is PythonExceptionTypeValue or PythonManagedTypeValue { ExceptionBaseName: not null }
        || value is PythonTupleValue tuple
            && tuple.Elements.All(item =>
                item
                    is PythonExceptionTypeValue
                        or PythonManagedTypeValue { ExceptionBaseName: not null }
            );

    private static bool IsExceptionGroupSequence(PythonValue value) =>
        value
            is PythonListValue
                or PythonTupleValue
                or PythonTextValue
                or PythonByteSequenceValue
                or PythonRangeValue
        || value is PythonManagedObjectValue instance
            && UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__");

    private (PythonExceptionValue? Matched, PythonExceptionValue? Remainder) SplitExceptionGroup(
        PythonExceptionValue exception,
        Func<PythonExceptionValue, bool> matches,
        bool includeRest,
        TextSpan span,
        int depth = 0
    )
    {
        if (depth >= MaximumNestedCallDepth)
            throw Fault(
                "DPY4009",
                "maximum recursion depth exceeded in exceptiongroup_split_recursive",
                span,
                "RecursionError"
            );
        CheckProtocolWork(span);
        if (matches(exception))
            return (exception, null);
        if (exception.GroupExceptions is null)
            return (null, includeRest ? exception : null);
        var matched = new List<PythonExceptionValue>();
        var rest = new List<PythonExceptionValue>();
        foreach (var child in exception.GroupExceptions)
        {
            var split = SplitExceptionGroup(child, matches, includeRest, span, depth + 1);
            if (split.Matched is not null)
                matched.Add(split.Matched);
            if (split.Remainder is not null)
                rest.Add(split.Remainder);
        }
        return (
            matched.Count == 0 ? null : DeriveExceptionGroup(exception, matched, span),
            rest.Count == 0 ? null : DeriveExceptionGroup(exception, rest, span)
        );
    }

    private PythonExceptionValue DeriveExceptionGroup(
        PythonExceptionValue source,
        List<PythonExceptionValue> children,
        TextSpan span
    )
    {
        var method = ManagedObjectProtocols.GetAttribute(source, "derive", span);
        var value = InvokeCallableNested(
            method,
            [new PythonListValue([.. children.Cast<PythonValue>()])],
            span
        );
        if (value is not PythonExceptionValue { GroupExceptions: not null } result)
            throw Fault(
                "DPY4003",
                "derive must return an instance of BaseExceptionGroup",
                span,
                "TypeError"
            );
        result.TracebackIdentity = source.TracebackIdentity;
        result.Cause = source.Cause;
        result.Context = source.Context;
        result.SuppressContext = true;
        PythonValue? notes = null;
        try
        {
            notes = ManagedObjectProtocols.GetAttribute(source, "__notes__", span);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError")) { }
        if (notes is not null && IsExceptionGroupSequence(notes))
            ManagedObjectProtocols.SetAttribute(
                result,
                "__notes__",
                new PythonListValue(
                    ManagedObjectProtocols.MaterializeValues(notes, span, _userIterationDispatcher)
                ),
                span
            );
        return result;
    }

    private static void RestoreExceptStarContext(
        ref PythonFrame frame,
        PythonExceptStarStateValue state
    )
    {
        frame.ActiveExceptions.Pop();
        frame.ActiveExceptions.Push(state.Original);
    }

    private static void ValidateExceptStarHandler(PythonValue handler, TextSpan span)
    {
        if (handler is PythonTupleValue tuple)
        {
            foreach (var item in tuple.Elements)
            {
                if (item is PythonTupleValue)
                    throw Fault(
                        "DPY4003",
                        "catching classes that do not inherit from BaseException is not allowed",
                        span,
                        "TypeError"
                    );
                ValidateExceptStarHandler(item, span);
            }
            return;
        }
        if (
            handler
            is not (
                PythonExceptionTypeValue
                or PythonManagedTypeValue { ExceptionBaseName: not null }
            )
        )
            throw Fault(
                "DPY4003",
                "catching classes that do not inherit from BaseException is not allowed",
                span,
                "TypeError"
            );
        if (IsSubclassOf(handler, PythonBuiltinTypes.GetExceptionType("BaseExceptionGroup"), span))
            throw Fault(
                "DPY4003",
                "catching ExceptionGroup with except* is not allowed. Use except instead.",
                span,
                "TypeError"
            );
    }

    private PythonExceptionValue? RecombineExceptionGroup(
        PythonExceptStarStateValue state,
        TextSpan span
    )
    {
        if (state.Rest is null && state.Raised.Count == 0)
            return null;
        var original = state.Original.Value;
        if (original.GroupExceptions is null)
            return state.Raised.Count == 0 ? state.Rest : state.Raised[0];
        var fresh = new List<PythonExceptionValue>();
        var leaves = new HashSet<PythonExceptionValue>(ReferenceEqualityComparer.Instance);
        foreach (var raised in state.Raised)
        {
            if (
                ReferenceEquals(raised.TracebackIdentity, original.TracebackIdentity)
                && ReferenceEquals(raised.Cause, original.Cause)
                && ReferenceEquals(raised.Context, original.Context)
            )
                CollectExceptionLeaves(raised, leaves, span);
            else
                fresh.Add(raised);
        }
        if (state.Rest is not null)
            CollectExceptionLeaves(state.Rest, leaves, span);
        var projected =
            leaves.Count == 0
                ? null
                : SplitExceptionGroup(
                    original,
                    exception => exception.GroupExceptions is null && leaves.Contains(exception),
                    false,
                    span
                ).Matched;
        if (projected is not null)
            fresh.Add(projected);
        if (fresh.Count == 0)
            return null;
        if (fresh.Count == 1)
            return fresh[0];
        return CreateExceptionGroupValue([
            new PythonTextValue(string.Empty),
            new PythonListValue([.. fresh.Cast<PythonValue>()]),
        ]);
    }

    private void CollectExceptionLeaves(
        PythonExceptionValue exception,
        HashSet<PythonExceptionValue> leaves,
        TextSpan span
    )
    {
        var pending = new Stack<PythonExceptionValue>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            CheckProtocolWork(span);
            if (current.GroupExceptions is null)
                leaves.Add(current);
            else
                foreach (var child in current.GroupExceptions)
                    pending.Push(child);
        }
    }
}
