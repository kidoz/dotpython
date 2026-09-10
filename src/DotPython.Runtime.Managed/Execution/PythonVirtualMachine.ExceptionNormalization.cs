using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    (bool HasValue, PythonValue Value) IUserObjectDispatcher.StepUserIterator(
        PythonValue nextMethod,
        TextSpan span
    ) => StepUserIterator(nextMethod, span);

    (bool HasValue, PythonValue Value) IUserObjectDispatcher.ResumeGenerator(
        PythonGeneratorValue generator,
        PythonValue? sent,
        PythonExceptionValue? injected,
        TextSpan span
    ) => ResumeGenerator(generator, sent, injected, span);

    (bool HasValue, PythonValue Value) IUserObjectDispatcher.ThrowGenerator(
        PythonGeneratorValue generator,
        PythonValue exception,
        TextSpan span
    ) => ThrowGenerator(generator, exception, span);

    private (bool HasValue, PythonValue Value) ThrowGenerator(
        PythonGeneratorValue generator,
        PythonValue argument,
        TextSpan span
    )
    {
        // Forward the original argument before constructing it. The delegated
        // iterator owns normalization, and the outer generator is executing while
        // its delegate's throw method (including constructors) runs.
        var state = (GeneratorFrameState)generator.OwnedFrameState!;
        if (generator.State == PythonGeneratorState.Suspended && state.Delegation is { } iterator)
        {
            var closing = argument is PythonExceptionValue instance
                ? IsExceptionSubclass(instance, "GeneratorExit")
                : argument is PythonExceptionTypeValue or PythonManagedTypeValue
                    && IsSubclassOf(
                        argument,
                        PythonBuiltinTypes.GetExceptionType("GeneratorExit"),
                        span
                    );
            PythonValue? throwMethod = null;
            var delegateObject = iterator.Iterable
                is PythonUserIteratorSourceValue { OriginalIterator: { } original }
                ? original
                : iterator.Iterable;
            var asyncStep = !closing ? delegateObject as PythonAsyncGeneratorStepValue : null;
            try
            {
                if (asyncStep is null)
                    throwMethod = ManagedObjectProtocols.GetAttribute(
                        delegateObject,
                        closing ? "close" : "throw",
                        span
                    );
            }
            catch (PythonRuntimeException fault)
                when ((
                        fault.PythonExceptionTypeName
                        ?? PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code)
                    ) == "AttributeError"
                ) { }
            catch (PythonRaisedException raised)
                when (IsExceptionSubclass(raised.Value, "AttributeError")) { }
            if (throwMethod is not null || asyncStep is not null)
            {
                PythonExceptionValue? failure = null;
                PythonValue? returned = null;
                generator.State = PythonGeneratorState.Running;
                try
                {
                    (bool HasValue, PythonValue Value) advanced = asyncStep is not null
                        ? ThrowAsyncGeneratorStep(asyncStep, argument, span)
                        : (
                            true,
                            InvokeCallableNested(throwMethod!, closing ? [] : [argument], span)
                        );
                    if (!closing && advanced.HasValue)
                    {
                        generator.YieldedValue = advanced.Value;
                        return (true, advanced.Value);
                    }
                    returned = advanced.Value;
                }
                catch (PythonRaisedException raised)
                {
                    if (!closing && IsExceptionSubclass(raised.Value, "StopIteration"))
                        returned =
                            raised.Value.EffectiveArguments.Count > 0
                                ? raised.Value.EffectiveArguments[0]
                                : PythonNoneValue.Instance;
                    else
                        failure = raised.Value;
                }
                catch (PythonRuntimeException fault)
                    when (fault.PythonExceptionTypeName is not null
                        || PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code) is not null
                    )
                {
                    failure = ExceptionFromNormalizationFault(fault);
                }
                finally
                {
                    generator.State = PythonGeneratorState.Suspended;
                }
                if (failure is not null)
                    return ResumeGenerator(generator, null, failure, span);

                // Continue immediately after YieldFromStep with its completed
                // (return-value, false) stack result, retaining the saved iterator
                // for the compiler's normal delegation cleanup.
                if (!closing)
                {
                    generator.InstructionPointer = state.DelegationContinuation;
                    generator.SavedEvaluationStack.Add(returned!);
                    return ResumeGenerator(generator, PythonTruthValue.False, null, span);
                }
            }
        }

        var exception = NormalizeExceptionForThrow(argument, span);
        if (generator is { IsCoroutine: true, State: PythonGeneratorState.Completed })
            throw Fault("DPY4035", "cannot reuse already awaited coroutine", span, "RuntimeError");
        if (generator.State is PythonGeneratorState.Created or PythonGeneratorState.Completed)
        {
            generator.State = PythonGeneratorState.Completed;
            throw new PythonRaisedException(exception);
        }
        return ResumeGenerator(generator, null, exception, span);
    }

    private PythonExceptionValue NormalizeExceptionForThrow(PythonValue value, TextSpan span)
    {
        if (value is PythonExceptionValue instance)
            return instance;
        // Invalid arguments fail at the caller; only failures from constructing an
        // admitted exception class become the exception injected into the frame.
        if (
            value
            is not (
                PythonExceptionTypeValue
                or PythonManagedTypeValue { ExceptionBaseName: not null }
            )
        )
            throw Fault(
                "DPY4003",
                $"exceptions must be classes or instances deriving from BaseException, not {ManagedObjectProtocols.GetTypeName(value)}",
                span,
                "TypeError"
            );
        try
        {
            var constructed = InvokeCallableNested(value, [], span);
            var exception = RequireExceptionInstance(constructed, value, span);
            // CPython restores the normalized exception using the requested class.
            // A metaclass can return a different exception type, requiring a second
            // construction with that instance as the sole argument.
            if (!ReferenceEquals(PythonBuiltinTypes.GetRuntimeType(exception), value))
                exception = RequireExceptionInstance(
                    InvokeCallableNested(value, [exception], span),
                    value,
                    span
                );
            return exception;
        }
        catch (PythonRaisedException raised)
        {
            return raised.Value;
        }
        catch (PythonRuntimeException fault)
            when (fault.PythonExceptionTypeName is not null
                || PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code) is not null
            )
        {
            return ExceptionFromNormalizationFault(fault);
        }
        // Cancellation, instruction limits and untyped VM faults are host control
        // flow and must never become catchable injected Python exceptions.
    }

    private static PythonExceptionValue RequireExceptionInstance(
        PythonValue constructed,
        PythonValue type,
        TextSpan span
    ) =>
        constructed as PythonExceptionValue
        ?? throw Fault(
            "DPY4003",
            $"calling {type.ToRepresentationString()} should have returned an instance of BaseException, not {ManagedObjectProtocols.GetTypeName(constructed)}",
            span,
            "TypeError"
        );

    private static PythonExceptionValue ExceptionFromNormalizationFault(
        PythonRuntimeException fault
    ) =>
        new(
            fault.PythonExceptionTypeName
                ?? PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code)!,
            fault.Message
        );
}
