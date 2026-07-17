using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonVirtualMachine
{
    private const int MaximumExceptionBlockDepth = 1024;
    private const int MaximumDeferredCleanupInstructions = 4096;

    [ThreadStatic]
    private static HashSet<PythonValuePair>? _activeEqualityPairs;

    private static readonly PythonCell[] NoCells = [];
    private static readonly IReadOnlyDictionary<string, string?> ExceptionBaseNames =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["BaseException"] = null,
            ["Exception"] = "BaseException",
            ["ArithmeticError"] = "Exception",
            ["LookupError"] = "Exception",
            ["RuntimeError"] = "Exception",
            ["RecursionError"] = "RuntimeError",
            ["TypeError"] = "Exception",
            ["ValueError"] = "Exception",
            ["NameError"] = "Exception",
            ["UnboundLocalError"] = "NameError",
            ["AttributeError"] = "Exception",
            ["ImportError"] = "Exception",
            ["ModuleNotFoundError"] = "ImportError",
            ["SyntaxError"] = "Exception",
            ["IndexError"] = "LookupError",
            ["KeyError"] = "LookupError",
            ["OverflowError"] = "ArithmeticError",
            ["ZeroDivisionError"] = "ArithmeticError",
        };
    private readonly Dictionary<string, PythonValue> _builtins;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _enableReturnLocalContinuation;
    private readonly PythonErrorIndicator _errorIndicator = new();
    private readonly Stack<PythonValue> _evaluationStack = [];
    private readonly PythonGlobalNamespace _globals;
    private readonly long _instructionLimit;
    private readonly PythonModuleRegistry _modules;
    private readonly TextWriter _output;
    private PythonFrame[] _frames = new PythonFrame[4];
    private int _frameCount;
    private int _deferredControlFlowCount;
    private int _deferredCleanupInstructions;
    private long _instructionsExecuted;
    private PythonValue?[] _locals = [];
    private int _localsCount;
    private PythonValue _result = PythonNoneValue.Instance;

    private ref PythonFrame CurrentFrame => ref _frames[_frameCount - 1];

    internal PythonVirtualMachine(
        PythonGlobalNamespace globals,
        PythonModuleRegistry modules,
        TextWriter output,
        long instructionLimit,
        bool enableReturnLocalContinuation,
        CancellationToken cancellationToken
    )
    {
        _globals = globals;
        _modules = modules;
        _output = output;
        _instructionLimit = instructionLimit;
        _enableReturnLocalContinuation = enableReturnLocalContinuation;
        _cancellationToken = cancellationToken;
        _builtins = new Dictionary<string, PythonValue>(StringComparer.Ordinal)
        {
            ["print"] = new PythonBuiltinFunctionValue("print", Print),
        };
        foreach (var name in ExceptionBaseNames.Keys)
        {
            _builtins.Add(name, new PythonExceptionTypeValue(name));
        }
    }

    internal PythonValue Execute(PreparedPythonCode code)
    {
        ArgumentNullException.ThrowIfNull(code);

        PushFrame(code, _globals, 0, 0, CreateCells(code, [], new TextSpan(0, 0)));
        return Run();
    }

    internal PythonValue Invoke(string functionName, IReadOnlyList<PythonValue> arguments)
    {
        PushNamedFunctionFrame(functionName, arguments);
        return Run();
    }

    internal PythonValue InvokeProfiled(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        PythonExecutionProfile profile
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        PushNamedFunctionFrame(functionName, arguments);
        return RunProfiled(profile);
    }

    private PythonValue Run() => RunCore(null);

    private PythonValue RunProfiled(PythonExecutionProfile profile) => RunCore(profile);

    private PythonValue RunCore(PythonExecutionProfile? profile)
    {
        _result = PythonNoneValue.Instance;
        try
        {
            while (_frameCount != 0)
            {
                try
                {
                    if (_deferredControlFlowCount == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (_deferredCleanupInstructions++ >= MaximumDeferredCleanupInstructions)
                    {
                        throw Fault(
                            "DPY4032",
                            $"Deferred cleanup exceeded the {MaximumDeferredCleanupInstructions} instruction limit.",
                            GetCurrentSpan(CurrentFrame)
                        );
                    }
                    ref var frame = ref CurrentFrame;
                    if (frame.InstructionPointer >= frame.Code.Definition.Instructions.Count)
                    {
                        ReturnFromFrame(PythonNoneValue.Instance);
                        continue;
                    }

                    var instructionIndex = frame.InstructionPointer;
                    var instruction = frame.Code.Definition.Instructions[instructionIndex];
                    frame.InstructionPointer++;
                    if (
                        _deferredControlFlowCount == 0
                        && _instructionsExecuted++ >= _instructionLimit
                    )
                    {
                        throw Fault(
                            "DPY4001",
                            "The managed instruction limit was exceeded.",
                            instruction.Span
                        );
                    }

                    profile?.Record(
                        _frameCount - 1,
                        frame.Code,
                        instructionIndex,
                        instruction.OpCode
                    );
                    ExecuteInstruction(ref frame, instruction, instructionIndex);
                }
                catch (Exception exception) when (IsManagedControlFlowException(exception))
                {
                    var dispatchException = PrepareExceptionalControlFlow(exception);
                    if (!HandleExceptionalControlFlow(dispatchException))
                    {
                        System
                            .Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                                dispatchException
                            )
                            .Throw();
                    }
                }
            }

            return _result;
        }
        finally
        {
            FailActiveModuleInitializations();
            Array.Clear(_frames, 0, _frameCount);
            _frameCount = 0;
            _evaluationStack.Clear();
            Array.Clear(_locals, 0, _localsCount);
            _localsCount = 0;
            _deferredControlFlowCount = 0;
            _deferredCleanupInstructions = 0;
            _errorIndicator.Clear();
        }
    }

    private void PushNamedFunctionFrame(string functionName, IReadOnlyList<PythonValue> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!_globals.TryGetValue(functionName, out var value))
        {
            throw Fault("DPY4002", $"Name '{functionName}' is not defined.", new TextSpan(0, 0));
        }

        if (value is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", $"Export '{functionName}' is not callable.", new TextSpan(0, 0));
        }

        PushFunctionFrame(function, arguments, new TextSpan(0, 0));
    }

    private void ExecuteInstruction(
        ref PythonFrame frame,
        PythonInstruction instruction,
        int instructionIndex
    )
    {
        switch (instruction.OpCode)
        {
            case PythonOpCode.LoadConstant:
                _evaluationStack.Push(frame.Code.GetConstant(instruction.Operand));
                break;
            case PythonOpCode.LoadName:
                _evaluationStack.Push(
                    LoadName(
                        frame.Code,
                        instructionIndex,
                        frame.Code.Definition.Names[instruction.Operand],
                        instruction.Span
                    )
                );
                break;
            case PythonOpCode.StoreName:
                frame.Globals.SetValue(
                    frame.Code.Definition.Names[instruction.Operand],
                    Pop(instruction.Span)
                );
                break;
            case PythonOpCode.LoadLocal:
                _evaluationStack.Push(LoadLocal(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.StoreLocal:
                StoreLocal(instruction.Operand, Pop(instruction.Span), instruction.Span);
                break;
            case PythonOpCode.LoadCell:
                _evaluationStack.Push(LoadCell(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.StoreCell:
                StoreCell(instruction.Operand, Pop(instruction.Span), instruction.Span);
                break;
            case PythonOpCode.ImportName:
                ImportModule(ref frame, instruction);
                break;
            case PythonOpCode.ImportFrom:
                ImportFrom(frame.Code.Definition.Names[instruction.Operand], instruction.Span);
                break;
            case PythonOpCode.LoadAttribute:
                LoadAttribute(frame.Code.Definition.Names[instruction.Operand], instruction.Span);
                break;
            case PythonOpCode.PopTop:
                Pop(instruction.Span);
                break;
            case PythonOpCode.CopyTop:
                _evaluationStack.Push(Peek(instruction.Span));
                break;
            case PythonOpCode.RotateTwo:
                RotateTwo(instruction.Span);
                break;
            case PythonOpCode.RotateThree:
                RotateThree(instruction.Span);
                break;
            case PythonOpCode.UnaryPositive:
            case PythonOpCode.UnaryNegative:
            case PythonOpCode.UnaryInvert:
            case PythonOpCode.UnaryNot:
                _evaluationStack.Push(
                    ApplyUnary(instruction.OpCode, Pop(instruction.Span), instruction.Span)
                );
                break;
            case PythonOpCode.BinaryAdd:
                ApplyBinaryAdd(frame.Code, instructionIndex, instruction.Span);
                break;
            case PythonOpCode.BinarySubtract:
            case PythonOpCode.BinaryMultiply:
            case PythonOpCode.BinaryTrueDivide:
            case PythonOpCode.BinaryFloorDivide:
            case PythonOpCode.BinaryModulo:
            case PythonOpCode.BinaryPower:
                ApplyBinary(instruction);
                break;
            case PythonOpCode.CompareEqual:
            case PythonOpCode.CompareNotEqual:
                ApplyComparison(instruction);
                break;
            case PythonOpCode.CompareLessThan:
            case PythonOpCode.CompareLessThanOrEqual:
            case PythonOpCode.CompareGreaterThan:
            case PythonOpCode.CompareGreaterThanOrEqual:
                ApplyOrderedComparison(frame.Code, instructionIndex, instruction);
                break;
            case PythonOpCode.Jump:
                frame.InstructionPointer = GetJumpTarget(
                    instruction,
                    frame.Code.Definition.Instructions.Count
                );
                break;
            case PythonOpCode.JumpIfFalse:
                if (!IsTruthy(Pop(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }

                break;
            case PythonOpCode.JumpIfFalseOrPop:
                if (!IsTruthy(Peek(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }
                else
                {
                    Pop(instruction.Span);
                }

                break;
            case PythonOpCode.JumpIfTrueOrPop:
                if (IsTruthy(Peek(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }
                else
                {
                    Pop(instruction.Span);
                }

                break;
            case PythonOpCode.MakeFunction:
                MakeFunction(instruction);
                break;
            case PythonOpCode.Call:
                ApplyCall(frame.Code, instructionIndex, instruction);
                break;
            case PythonOpCode.CallLocal:
                ApplyLocalCall(
                    frame.Code,
                    instructionIndex,
                    LoadLocal(instruction.Operand, instruction.Span),
                    instruction.Span
                );
                break;
            case PythonOpCode.BuildList:
                BuildCollection(instruction.Operand, buildTuple: false, instruction.Span);
                break;
            case PythonOpCode.BuildTuple:
                BuildCollection(instruction.Operand, buildTuple: true, instruction.Span);
                break;
            case PythonOpCode.BuildDictionary:
                BuildDictionary(instruction.Operand, instruction.Span);
                break;
            case PythonOpCode.LoadSubscript:
                LoadSubscript(instruction.Span);
                break;
            case PythonOpCode.StoreSubscript:
                StoreSubscript(instruction.Span);
                break;
            case PythonOpCode.GetIterator:
                GetIterator(instruction.Span);
                break;
            case PythonOpCode.ForIter:
                ForIter(ref frame, instruction);
                break;
            case PythonOpCode.SetupExcept:
                PushExceptionBlock(ref frame, PythonExceptionBlockKind.Except, instruction);
                break;
            case PythonOpCode.SetupFinally:
                PushExceptionBlock(ref frame, PythonExceptionBlockKind.Finally, instruction);
                break;
            case PythonOpCode.PopExceptionBlock:
                PopExceptionBlock(ref frame, instruction.Span);
                break;
            case PythonOpCode.EnterFinally:
                PushPendingFinally(
                    ref frame,
                    new PythonPendingFinally(null, null, frame.ExceptionBlocks.Count)
                );
                break;
            case PythonOpCode.EndFinally:
                EndFinally();
                break;
            case PythonOpCode.LoadException:
                _evaluationStack.Push(GetActiveException(instruction.Span).Value);
                break;
            case PythonOpCode.MatchException:
                MatchException(instruction.Span);
                break;
            case PythonOpCode.ClearException:
                ClearException(instruction.Span);
                break;
            case PythonOpCode.Raise:
                Raise(instruction.Operand, instruction.Span);
                break;
            case PythonOpCode.ReturnValue:
                ReturnFromFrame(Pop(instruction.Span));
                break;
            case PythonOpCode.ReturnLocal:
                ReturnFromFrame(LoadLocal(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.ReturnNone:
                ReturnFromFrame(PythonNoneValue.Instance);
                break;
            default:
                throw Fault("DPY4007", "Unknown DotPython instruction.", instruction.Span);
        }
    }

    private static bool IsManagedControlFlowException(Exception exception) =>
        exception is PythonRaisedException or PythonRuntimeException or OperationCanceledException;

    private Exception PrepareExceptionalControlFlow(Exception exception)
    {
        if (
            exception is not PythonRuntimeException fault
            || !_errorIndicator.TrySetFromRuntimeFault(fault)
        )
        {
            return exception;
        }

        return _errorIndicator.GetRaisedException()
            ?? throw new InvalidOperationException(
                "The managed Python error indicator did not retain the translated runtime fault."
            );
    }

    private bool HandleExceptionalControlFlow(Exception exception)
    {
        while (_frameCount != 0)
        {
            ref var frame = ref CurrentFrame;
            if (exception is PythonRaisedException raised)
            {
                if (raised.PreserveTracebackOnNextDispatch)
                {
                    raised.PreserveTracebackOnNextDispatch = false;
                }
                else
                {
                    raised.AddTracebackFrame(frame.Code.Definition.Name, GetCurrentSpan(frame));
                }
            }
            while (true)
            {
                var protectedBlockDepth =
                    frame.PendingFinalies.Count == 0
                        ? 0
                        : frame.PendingFinalies.Peek().OuterExceptionBlockDepth;
                while (frame.ExceptionBlocks.Count > protectedBlockDepth)
                {
                    var block = frame.ExceptionBlocks[^1];
                    frame.ExceptionBlocks.RemoveAt(frame.ExceptionBlocks.Count - 1);
                    ClearEvaluationStack(block.EvaluationStackDepth);
                    if (block.Kind == PythonExceptionBlockKind.Except)
                    {
                        if (exception is not PythonRaisedException catchable)
                        {
                            continue;
                        }

                        frame.ActiveExceptions.Push(catchable);
                        frame.InstructionPointer = block.HandlerTarget;
                        return true;
                    }

                    PushPendingFinally(
                        ref frame,
                        new PythonPendingFinally(exception, null, frame.ExceptionBlocks.Count)
                    );
                    frame.InstructionPointer = block.HandlerTarget;
                    return true;
                }

                if (frame.PendingFinalies.Count == 0)
                {
                    break;
                }

                var interrupted = PopPendingFinally(ref frame);
                if (
                    exception is PythonRaisedException replacement
                    && interrupted.Exception is PythonRaisedException context
                    && replacement.Value.Cause is null
                    && replacement.Value.Context is null
                )
                {
                    replacement.Value.Context = context.Value;
                }
            }

            UnwindCurrentFrameForException();
        }

        return false;
    }

    private static TextSpan GetCurrentSpan(PythonFrame frame)
    {
        var instructions = frame.Code.Definition.Instructions;
        if (instructions.Count == 0)
        {
            return new TextSpan(0, 0);
        }

        var index = Math.Clamp(frame.InstructionPointer - 1, 0, instructions.Count - 1);
        return instructions[index].Span;
    }

    private void PushExceptionBlock(
        ref PythonFrame frame,
        PythonExceptionBlockKind kind,
        PythonInstruction instruction
    )
    {
        if (frame.ExceptionBlocks.Count >= MaximumExceptionBlockDepth)
        {
            throw Fault(
                "DPY4030",
                $"Managed exception handling exceeded the {MaximumExceptionBlockDepth} block limit.",
                instruction.Span
            );
        }

        var handlerTarget = GetJumpTarget(instruction, frame.Code.Definition.Instructions.Count);
        if (handlerTarget == frame.Code.Definition.Instructions.Count)
        {
            throw Fault(
                "DPY4007",
                "The managed exception-handler target is invalid.",
                instruction.Span
            );
        }

        frame.ExceptionBlocks.Add(
            new PythonExceptionBlock(kind, handlerTarget, _evaluationStack.Count)
        );
    }

    private static void PopExceptionBlock(ref PythonFrame frame, TextSpan span)
    {
        if (frame.ExceptionBlocks.Count == 0)
        {
            throw Fault("DPY4007", "The managed exception block stack is empty.", span);
        }

        frame.ExceptionBlocks.RemoveAt(frame.ExceptionBlocks.Count - 1);
    }

    private void EndFinally()
    {
        ref var frame = ref CurrentFrame;
        if (frame.PendingFinalies.Count == 0)
        {
            throw Fault(
                "DPY4007",
                "The managed finally state is unavailable.",
                GetCurrentSpan(frame)
            );
        }

        var pending = PopPendingFinally(ref frame);
        if (pending.Exception is not null)
        {
            if (pending.Exception is PythonRaisedException raised)
            {
                raised.PreserveTracebackOnNextDispatch = true;
            }

            System
                .Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pending.Exception)
                .Throw();
        }

        if (pending.ReturnValue is not null)
        {
            ReturnFromFrame(pending.ReturnValue);
        }
    }

    private void PushPendingFinally(ref PythonFrame frame, PythonPendingFinally pending)
    {
        if (pending.Exception is not null)
        {
            if (_deferredControlFlowCount == 0)
            {
                _deferredCleanupInstructions = 0;
            }

            _deferredControlFlowCount++;
        }

        frame.PendingFinalies.Push(pending);
    }

    private PythonPendingFinally PopPendingFinally(ref PythonFrame frame)
    {
        var pending = frame.PendingFinalies.Pop();
        if (pending.Exception is not null)
        {
            _deferredControlFlowCount--;
        }

        return pending;
    }

    private PythonRaisedException GetActiveException(TextSpan span)
    {
        if (CurrentFrame.ActiveExceptions.Count == 0)
        {
            throw CreateRaisedException(
                new PythonExceptionValue("RuntimeError", "No active exception to reraise.")
            );
        }

        return CurrentFrame.ActiveExceptions.Peek();
    }

    private void ClearException(TextSpan span)
    {
        if (CurrentFrame.ActiveExceptions.Count == 0)
        {
            throw Fault("DPY4007", "The active exception stack is empty.", span);
        }

        CurrentFrame.ActiveExceptions.Pop();
    }

    private void MatchException(TextSpan span)
    {
        var handlerType = Pop(span);
        var exception = Pop(span);
        if (exception is not PythonExceptionValue exceptionValue)
        {
            throw Fault("DPY4007", "The active exception value is invalid.", span);
        }

        _evaluationStack.Push(
            PythonTruthValue.FromBoolean(MatchesExceptionType(exceptionValue, handlerType, span))
        );
    }

    private static bool MatchesExceptionType(
        PythonExceptionValue exception,
        PythonValue handlerType,
        TextSpan span
    )
    {
        if (handlerType is PythonExceptionTypeValue type)
        {
            return IsExceptionSubclass(exception.TypeName, type.Name);
        }

        if (handlerType is PythonTupleValue tuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (MatchesExceptionType(exception, element, span))
                {
                    return true;
                }
            }

            return false;
        }

        throw CreateRaisedException(
            new PythonExceptionValue(
                "TypeError",
                "Catching classes that do not inherit from BaseException is not allowed."
            )
        );
    }

    private static bool IsExceptionSubclass(string candidate, string expected)
    {
        for (string? current = candidate; current is not null; )
        {
            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return true;
            }

            current = ExceptionBaseNames.GetValueOrDefault(current);
        }

        return false;
    }

    private void Raise(int argumentCount, TextSpan span)
    {
        if (argumentCount == 0)
        {
            var active = GetActiveException(span);
            active.PreserveTracebackOnNextDispatch = true;
            throw active;
        }

        PythonValue? cause = null;
        if (argumentCount == 2)
        {
            cause = Pop(span);
        }

        var exception = CreateExceptionValue(Pop(span), span);
        if (argumentCount == 2)
        {
            if (cause is PythonNoneValue)
            {
                exception.SuppressContext = true;
            }
            else
            {
                exception.Cause = CreateExceptionValue(cause!, span);
                exception.SuppressContext = true;
            }
        }
        else if (CurrentFrame.ActiveExceptions.TryPeek(out var active))
        {
            exception.Context = active.Value;
        }

        throw CreateRaisedException(exception);
    }

    private static PythonExceptionValue CreateExceptionValue(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonExceptionValue exception => exception,
            PythonExceptionTypeValue type => new PythonExceptionValue(type.Name, string.Empty),
            _ => throw CreateRaisedException(
                new PythonExceptionValue("TypeError", "Exceptions must derive from BaseException.")
            ),
        };

    private static PythonRaisedException CreateRaisedException(PythonExceptionValue value) =>
        new(value);

    private void ClearEvaluationStack(int targetDepth)
    {
        if (targetDepth < CurrentFrame.EvaluationStackBase || targetDepth > _evaluationStack.Count)
        {
            throw Fault(
                "DPY4007",
                "The managed exception block has an invalid evaluation-stack depth.",
                GetCurrentSpan(CurrentFrame)
            );
        }

        while (_evaluationStack.Count > targetDepth)
        {
            _evaluationStack.Pop();
        }
    }

    private void UnwindCurrentFrameForException()
    {
        var evaluationStackBase = CurrentFrame.EvaluationStackBase;
        var localsBase = CurrentFrame.LocalsBase;
        var localsCount = CurrentFrame.LocalsCount;
        var initializingModule = CurrentFrame.InitializingModule;
        while (CurrentFrame.PendingFinalies.Count != 0)
        {
            PopPendingFinally(ref CurrentFrame);
        }

        ClearEvaluationStack(evaluationStackBase);
        Array.Clear(_locals, localsBase, localsCount);
        _localsCount = localsBase;
        _frames[--_frameCount] = default;

        if (initializingModule is not null)
        {
            _modules.Fail(initializingModule);
            if (
                _frameCount != 0
                && _evaluationStack.Count > CurrentFrame.EvaluationStackBase
                && ReferenceEquals(_evaluationStack.Peek(), initializingModule)
            )
            {
                _evaluationStack.Pop();
            }
        }
    }

    private void BuildCollection(int elementCount, bool buildTuple, TextSpan span)
    {
        if (
            elementCount < 0
            || elementCount > _evaluationStack.Count - CurrentFrame.EvaluationStackBase
        )
        {
            throw Fault("DPY4007", "Invalid collection element count.", span);
        }

        var elements = new PythonValue[elementCount];
        for (var index = elementCount - 1; index >= 0; index--)
        {
            elements[index] = Pop(span);
        }

        _evaluationStack.Push(
            buildTuple
                ? new PythonTupleValue(elements)
                : new PythonListValue(new List<PythonValue>(elements))
        );
    }

    private void ImportModule(ref PythonFrame frame, PythonInstruction instruction)
    {
        var name = frame.Code.Definition.Names[instruction.Operand];
        var currentPackage =
            frame.Globals.TryGetValue("__package__", out var packageValue)
            && packageValue is PythonTextValue packageText
                ? packageText.Value
                : string.Empty;
        var import = _modules.Resolve(name, currentPackage, instruction.Span);
        PushModuleImport(import, instruction.Span);
    }

    private void ImportFrom(string name, TextSpan span)
    {
        var target = Pop(span);
        if (target is not PythonModuleValue module)
        {
            throw Fault("DPY4023", "This value does not expose managed attributes.", span);
        }

        if (module.Globals.TryGetValue(name, out var value))
        {
            _evaluationStack.Push(value);
            return;
        }

        var childName = module.Name + "." + name;
        if (!_modules.ContainsAbsolute(childName))
        {
            throw Fault(
                "DPY4022",
                $"Module '{module.Name}' has no attribute '{name}'.",
                span,
                "ImportError"
            );
        }

        PushModuleImport(_modules.ResolveAbsolute(childName, span), span);
    }

    private void PushModuleImport(PythonModuleImport import, TextSpan span)
    {
        _evaluationStack.Push(import.Module);
        if (import.InitializationCode is null)
        {
            return;
        }

        var cells = CreateCells(import.InitializationCode, [], span);
        PushFrame(
            import.InitializationCode,
            import.Module.Globals,
            _localsCount,
            0,
            cells,
            initializingModule: import.Module
        );
    }

    private void LoadAttribute(string name, TextSpan span)
    {
        var target = Pop(span);
        _evaluationStack.Push(ManagedObjectProtocols.GetAttribute(target, name, span));
    }

    private void BuildDictionary(int itemCount, TextSpan span)
    {
        if (
            itemCount < 0
            || itemCount > (_evaluationStack.Count - CurrentFrame.EvaluationStackBase) / 2
        )
        {
            throw Fault("DPY4007", "Invalid dictionary item count.", span);
        }

        var keys = new PythonValue[itemCount];
        var values = new PythonValue[itemCount];
        for (var index = itemCount - 1; index >= 0; index--)
        {
            values[index] = Pop(span);
            keys[index] = Pop(span);
        }

        var dictionary = new PythonDictionaryValue([]);
        for (var index = 0; index < itemCount; index++)
        {
            SetDictionaryItem(dictionary, keys[index], values[index], span);
        }

        _evaluationStack.Push(dictionary);
    }

    private void LoadSubscript(TextSpan span)
    {
        var index = Pop(span);
        var target = Pop(span);
        _evaluationStack.Push(ManagedObjectProtocols.GetItem(target, index, span));
    }

    private void StoreSubscript(TextSpan span)
    {
        var index = Pop(span);
        var target = Pop(span);
        var value = Pop(span);

        ManagedObjectProtocols.SetItem(target, index, value, span);
    }

    private void GetIterator(TextSpan span)
    {
        var iterable = Pop(span);
        _evaluationStack.Push(ManagedObjectProtocols.GetIterator(iterable, span));
    }

    private void ForIter(ref PythonFrame frame, PythonInstruction instruction)
    {
        if (Peek(instruction.Span) is not PythonIteratorValue iterator)
        {
            throw Fault("DPY4007", "The for-loop iterator is invalid.", instruction.Span);
        }

        if (ManagedObjectProtocols.TryGetNext(iterator, out var value, instruction.Span))
        {
            _evaluationStack.Push(value);
            return;
        }

        Pop(instruction.Span);
        frame.InstructionPointer = GetJumpTarget(
            instruction,
            frame.Code.Definition.Instructions.Count
        );
    }

    private static void SetDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value,
        TextSpan span
    )
    {
        if (!IsHashable(key))
        {
            throw Fault("DPY4014", "The dictionary key is not hashable.", span);
        }

        if (TryFindDictionaryItem(dictionary, key, out var item))
        {
            item.Value = value;
            return;
        }

        dictionary.Items.Add(new PythonDictionaryItemValue(key, value));
        dictionary.SizeVersion++;
    }

    private static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonDictionaryItemValue item
    )
    {
        foreach (var candidate in dictionary.Items)
        {
            if (ReferenceEquals(candidate.Key, key) || AreEqual(candidate.Key, key))
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private static bool IsHashable(PythonValue value) =>
        value switch
        {
            PythonListValue or PythonDictionaryValue => false,
            PythonTupleValue tuple => tuple.Elements.All(IsHashable),
            _ => true,
        };

    private PythonValue LoadName(
        PreparedPythonCode code,
        int instructionIndex,
        string name,
        TextSpan span
    )
    {
        var globals = CurrentFrame.Globals;
        if (code.TryGetCachedName(instructionIndex, globals, out var value))
        {
            return value;
        }

        if (globals.TryGetSlot(name, out var slot))
        {
            code.RecordGlobalLoad(instructionIndex, globals, slot);
            return slot.Value;
        }

        if (_builtins.TryGetValue(name, out value))
        {
            code.RecordBuiltinLoad(instructionIndex, globals, value);
            return value;
        }

        throw Fault("DPY4002", $"Name '{name}' is not defined.", span);
    }

    private PythonValue LoadLocal(int index, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.LocalsCount)
        {
            throw Fault("DPY4007", "The DotPython local index is invalid.", span);
        }

        var value = _locals[frame.LocalsBase + index];
        if (value is not null)
        {
            return value;
        }

        var name = frame.Code.Definition.VariableNames[index];
        throw Fault("DPY4008", $"Local variable '{name}' was referenced before assignment.", span);
    }

    private void StoreLocal(int index, PythonValue value, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.LocalsCount)
        {
            throw Fault("DPY4007", "The DotPython local index is invalid.", span);
        }

        _locals[frame.LocalsBase + index] = value;
    }

    private PythonValue LoadCell(int index, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.Cells.Length)
        {
            throw Fault("DPY4007", "The DotPython closure-cell index is invalid.", span);
        }

        var value = frame.Cells[index].Value;
        if (value is not null)
        {
            return value;
        }

        var definition = frame.Code.Definition;
        if (index < definition.CellVariableNames.Count)
        {
            throw Fault(
                "DPY4008",
                $"Local variable '{definition.CellVariableNames[index]}' was referenced before assignment.",
                span
            );
        }

        var freeVariableIndex = index - definition.CellVariableNames.Count;
        throw Fault(
            "DPY4010",
            $"Free variable '{definition.FreeVariableNames[freeVariableIndex]}' was referenced before assignment in an enclosing scope.",
            span
        );
    }

    private void StoreCell(int index, PythonValue value, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.Cells.Length)
        {
            throw Fault("DPY4007", "The DotPython closure-cell index is invalid.", span);
        }

        frame.Cells[index].Value = value;
    }

    private void ApplyBinary(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyBinary(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyBinaryAdd(PreparedPythonCode code, int instructionIndex, TextSpan span)
    {
        var right = Pop(span);
        var left = Pop(span);
        var cacheState = code.GetBinaryAddCacheState(instructionIndex);
        if (
            cacheState == AdaptiveNumericCacheState.WholeNumber
            && left is PythonWholeNumberValue leftWholeNumber
            && right is PythonWholeNumberValue rightWholeNumber
        )
        {
            _evaluationStack.Push(
                PythonWholeNumberValue.Create(leftWholeNumber.Value + rightWholeNumber.Value)
            );
            return;
        }

        if (
            cacheState == AdaptiveNumericCacheState.FloatingPoint
            && left is PythonFloatingPointValue leftFloatingPoint
            && right is PythonFloatingPointValue rightFloatingPoint
        )
        {
            _evaluationStack.Push(
                new PythonFloatingPointValue(leftFloatingPoint.Value + rightFloatingPoint.Value)
            );
            return;
        }

        var operandKind = GetAdaptiveNumericOperandKind(left, right);
        if (
            cacheState
            is AdaptiveNumericCacheState.WholeNumber
                or AdaptiveNumericCacheState.FloatingPoint
        )
        {
            code.RecordBinaryAddObservation(instructionIndex, operandKind);
        }

        var result = ApplyBinary(PythonOpCode.BinaryAdd, left, right, span);
        if (cacheState == AdaptiveNumericCacheState.Adaptive)
        {
            code.RecordBinaryAddObservation(instructionIndex, operandKind);
        }

        _evaluationStack.Push(result);
    }

    private void ApplyOrderedComparison(
        PreparedPythonCode code,
        int instructionIndex,
        PythonInstruction instruction
    )
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        var cacheState = code.GetOrderedComparisonCacheState(instructionIndex);
        if (
            cacheState == AdaptiveNumericCacheState.WholeNumber
            && left is PythonWholeNumberValue leftWholeNumber
            && right is PythonWholeNumberValue rightWholeNumber
        )
        {
            _evaluationStack.Push(
                PythonTruthValue.FromBoolean(
                    ApplyWholeNumberComparison(
                        instruction.OpCode,
                        leftWholeNumber.Value,
                        rightWholeNumber.Value
                    )
                )
            );
            return;
        }

        if (
            cacheState == AdaptiveNumericCacheState.FloatingPoint
            && left is PythonFloatingPointValue leftFloatingPoint
            && right is PythonFloatingPointValue rightFloatingPoint
        )
        {
            _evaluationStack.Push(
                PythonTruthValue.FromBoolean(
                    ApplyFloatingPointComparison(
                        instruction.OpCode,
                        leftFloatingPoint.Value,
                        rightFloatingPoint.Value
                    )
                )
            );
            return;
        }

        var operandKind = GetAdaptiveNumericOperandKind(left, right);
        if (
            cacheState
            is AdaptiveNumericCacheState.WholeNumber
                or AdaptiveNumericCacheState.FloatingPoint
        )
        {
            code.RecordOrderedComparisonObservation(instructionIndex, operandKind);
        }

        var result = ApplyComparison(instruction.OpCode, left, right, instruction.Span);
        if (cacheState == AdaptiveNumericCacheState.Adaptive)
        {
            code.RecordOrderedComparisonObservation(instructionIndex, operandKind);
        }

        _evaluationStack.Push(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyWholeNumberComparison(
        PythonOpCode opCode,
        BigInteger left,
        BigInteger right
    ) =>
        opCode switch
        {
            PythonOpCode.CompareLessThan => left < right,
            PythonOpCode.CompareLessThanOrEqual => left <= right,
            PythonOpCode.CompareGreaterThan => left > right,
            PythonOpCode.CompareGreaterThanOrEqual => left >= right,
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyFloatingPointComparison(
        PythonOpCode opCode,
        double left,
        double right
    ) =>
        opCode switch
        {
            PythonOpCode.CompareLessThan => left < right,
            PythonOpCode.CompareLessThanOrEqual => left <= right,
            PythonOpCode.CompareGreaterThan => left > right,
            PythonOpCode.CompareGreaterThanOrEqual => left >= right,
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

    private static AdaptiveNumericOperandKind GetAdaptiveNumericOperandKind(
        PythonValue left,
        PythonValue right
    ) =>
        (left, right) switch
        {
            (PythonWholeNumberValue, PythonWholeNumberValue) =>
                AdaptiveNumericOperandKind.WholeNumber,
            (PythonFloatingPointValue, PythonFloatingPointValue) =>
                AdaptiveNumericOperandKind.FloatingPoint,
            _ => AdaptiveNumericOperandKind.Other,
        };

    private void ApplyComparison(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyComparison(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyCall(
        PreparedPythonCode code,
        int instructionIndex,
        PythonInstruction instruction
    )
    {
        var target = Peek(instruction.Operand, instruction.Span);
        if (
            code.TryGetCachedManagedCall(
                instructionIndex,
                target,
                out var cachedFunction,
                out var useEmptyFrame
            )
        )
        {
            if (useEmptyFrame)
            {
                PushEmptyFunctionFrame(cachedFunction, instruction.Span);
            }
            else
            {
                PushFunctionFrameUnchecked(cachedFunction, instruction.Operand, instruction.Span);
            }

            return;
        }

        if (target is PythonBuiltinFunctionValue builtin)
        {
            var arguments = PopArguments(instruction.Operand, instruction.Span);
            Pop(instruction.Span);
            _evaluationStack.Push(builtin.Invoke(arguments, instruction.Span));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is PythonExceptionTypeValue exceptionType)
        {
            var arguments = PopArguments(instruction.Operand, instruction.Span);
            Pop(instruction.Span);
            _evaluationStack.Push(CreateExceptionValue(exceptionType, arguments));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", instruction.Span);
        }

        ValidateArgumentCount(function, instruction.Operand, instruction.Span);
        code.RecordManagedCall(instructionIndex, function);
        PushFunctionFrameUnchecked(function, instruction.Operand, instruction.Span);
    }

    private void ApplyLocalCall(
        PreparedPythonCode code,
        int instructionIndex,
        PythonValue target,
        TextSpan span
    )
    {
        if (
            code.TryGetCachedManagedCall(
                instructionIndex,
                target,
                out var cachedFunction,
                out var useEmptyFrame
            )
        )
        {
            if (useEmptyFrame)
            {
                PushEmptyFunctionFrame(cachedFunction, span, popTarget: false);
            }
            else
            {
                PushFunctionFrameUnchecked(cachedFunction, 0, span, popTarget: false);
            }

            return;
        }

        if (target is PythonBuiltinFunctionValue builtin)
        {
            _evaluationStack.Push(builtin.Invoke(Array.Empty<PythonValue>(), span));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is PythonExceptionTypeValue exceptionType)
        {
            _evaluationStack.Push(CreateExceptionValue(exceptionType, Array.Empty<PythonValue>()));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", span);
        }

        ValidateArgumentCount(function, 0, span);
        code.RecordManagedCall(instructionIndex, function);
        PushFunctionFrameUnchecked(function, 0, span, popTarget: false);
    }

    private void PushEmptyFunctionFrame(
        PythonFunctionValue function,
        TextSpan span,
        bool popTarget = true
    )
    {
        var hasReturnLocalContinuation = CaptureReturnLocalContinuation();
        if (popTarget)
        {
            Pop(span);
        }

        PushFrame(
            function.Code,
            function.Globals,
            _localsCount,
            0,
            NoCells,
            hasReturnLocalContinuation
        );
    }

    private PythonValue[] PopArguments(int argumentCount, TextSpan span)
    {
        var arguments = new PythonValue[argumentCount];
        for (var index = arguments.Length - 1; index >= 0; index--)
        {
            arguments[index] = Pop(span);
        }

        return arguments;
    }

    private static PythonExceptionValue CreateExceptionValue(
        PythonExceptionTypeValue type,
        PythonValue[] arguments
    )
    {
        var message = arguments.Length switch
        {
            0 => string.Empty,
            1 => arguments[0].ToDisplayString(),
            _ => new PythonTupleValue(arguments).ToDisplayString(),
        };
        return new PythonExceptionValue(type.Name, message);
    }

    private void PushFunctionFrame(PythonFunctionValue function, int argumentCount, TextSpan span)
    {
        ValidateArgumentCount(function, argumentCount, span);
        PushFunctionFrameUnchecked(function, argumentCount, span);
    }

    private void PushFunctionFrameUnchecked(
        PythonFunctionValue function,
        int argumentCount,
        TextSpan span,
        bool popTarget = true
    )
    {
        var hasReturnLocalContinuation = CaptureReturnLocalContinuation();
        var localsBase = ReserveLocals(function.Code.Definition.VariableNames.Count);
        var cells = CreateCells(function.Code, function.Closure, span);
        for (var index = argumentCount - 1; index >= 0; index--)
        {
            StoreArgument(function.Code, cells, localsBase, index, Pop(span));
        }

        if (popTarget)
        {
            Pop(span);
        }

        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count,
            cells,
            hasReturnLocalContinuation
        );
    }

    private void PushFunctionFrame(
        PythonFunctionValue function,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        ValidateArgumentCount(function, arguments.Count, span);

        var localsBase = ReserveLocals(function.Code.Definition.VariableNames.Count);
        var cells = CreateCells(function.Code, function.Closure, span);
        for (var index = 0; index < arguments.Count; index++)
        {
            StoreArgument(function.Code, cells, localsBase, index, arguments[index]);
        }

        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count,
            cells
        );
    }

    private void StoreArgument(
        PreparedPythonCode code,
        PythonCell[] cells,
        int localsBase,
        int argumentIndex,
        PythonValue value
    )
    {
        var cellIndex = code.GetLocalCellIndex(argumentIndex);
        if (cellIndex >= 0)
        {
            cells[cellIndex].Value = value;
        }
        else
        {
            _locals[localsBase + argumentIndex] = value;
        }
    }

    private int ReserveLocals(int count)
    {
        var localsBase = _localsCount;
        var requiredCapacity = checked(localsBase + count);
        if (requiredCapacity > _locals.Length)
        {
            var doubledCapacity = _locals.Length == 0 ? 4 : checked(_locals.Length * 2);
            Array.Resize(ref _locals, Math.Max(requiredCapacity, doubledCapacity));
        }

        _localsCount = requiredCapacity;
        return localsBase;
    }

    private static void ValidateArgumentCount(
        PythonFunctionValue function,
        int argumentCount,
        TextSpan span
    )
    {
        if (argumentCount == function.Code.Definition.ArgumentCount)
        {
            return;
        }

        throw Fault(
            "DPY4009",
            $"Function '{function.Name}' expected {function.Code.Definition.ArgumentCount} positional "
                + $"argument(s), but received {argumentCount}.",
            span
        );
    }

    private void PushFrame(
        PreparedPythonCode code,
        PythonGlobalNamespace globals,
        int localsBase,
        int localsCount,
        PythonCell[] cells,
        bool hasReturnLocalContinuation = false,
        PythonModuleValue? initializingModule = null
    )
    {
        if (_frameCount == _frames.Length)
        {
            Array.Resize(ref _frames, checked(_frameCount * 2));
        }

        _frames[_frameCount++] = new PythonFrame(
            code,
            globals,
            localsBase,
            localsCount,
            cells,
            _evaluationStack.Count,
            hasReturnLocalContinuation,
            initializingModule
        );
    }

    private bool CaptureReturnLocalContinuation()
    {
        if (!_enableReturnLocalContinuation)
        {
            return false;
        }

        ref var caller = ref CurrentFrame;
        var instructionIndex = caller.InstructionPointer;
        if ((uint)instructionIndex >= (uint)caller.Code.Definition.Instructions.Count)
        {
            return false;
        }

        var instruction = caller.Code.Definition.Instructions[instructionIndex];
        if (instruction.OpCode != PythonOpCode.StoreLocal)
        {
            return false;
        }

        caller.InstructionPointer++;
        return true;
    }

    private void MakeFunction(PythonInstruction instruction)
    {
        PreparedPythonCode code;
        try
        {
            code = CurrentFrame.Code.GetFunctionCode(instruction.Operand);
        }
        catch (InvalidOperationException)
        {
            throw Fault("DPY4007", "The function code object is invalid.", instruction.Span);
        }

        var closure = new PythonCell[code.Definition.FreeVariableNames.Count];
        for (var index = 0; index < closure.Length; index++)
        {
            var name = code.Definition.FreeVariableNames[index];
            var cellIndex = CurrentFrame.Code.GetClosureCellIndex(name);
            if ((uint)cellIndex >= (uint)CurrentFrame.Cells.Length)
            {
                throw Fault(
                    "DPY4007",
                    $"Closure variable '{name}' cannot be resolved in the enclosing frame.",
                    instruction.Span
                );
            }

            closure[index] = CurrentFrame.Cells[cellIndex];
        }

        _evaluationStack.Push(
            new PythonFunctionValue(code.Definition.Name, code, CurrentFrame.Globals, closure)
        );
    }

    private static PythonCell[] CreateCells(
        PreparedPythonCode code,
        PythonCell[] closure,
        TextSpan span
    )
    {
        var definition = code.Definition;
        if (closure.Length != definition.FreeVariableNames.Count)
        {
            throw Fault("DPY4007", "The function closure does not match its code object.", span);
        }

        if (definition.CellVariableNames.Count == 0 && closure.Length == 0)
        {
            return NoCells;
        }

        var cells = new PythonCell[
            definition.CellVariableNames.Count + definition.FreeVariableNames.Count
        ];
        for (var index = 0; index < definition.CellVariableNames.Count; index++)
        {
            cells[index] = new PythonCell();
        }

        for (var index = 0; index < closure.Length; index++)
        {
            cells[definition.CellVariableNames.Count + index] = closure[index];
        }

        return cells;
    }

    private void ReturnFromFrame(PythonValue value)
    {
        ref var frame = ref CurrentFrame;
        while (true)
        {
            var protectedBlockDepth =
                frame.PendingFinalies.Count == 0
                    ? 0
                    : frame.PendingFinalies.Peek().OuterExceptionBlockDepth;
            while (frame.ExceptionBlocks.Count > protectedBlockDepth)
            {
                var block = frame.ExceptionBlocks[^1];
                frame.ExceptionBlocks.RemoveAt(frame.ExceptionBlocks.Count - 1);
                if (block.Kind != PythonExceptionBlockKind.Finally)
                {
                    continue;
                }

                ClearEvaluationStack(block.EvaluationStackDepth);
                PushPendingFinally(
                    ref frame,
                    new PythonPendingFinally(null, value, frame.ExceptionBlocks.Count)
                );
                frame.InstructionPointer = block.HandlerTarget;
                return;
            }

            if (frame.PendingFinalies.Count == 0)
            {
                break;
            }

            PopPendingFinally(ref frame);
        }

        var evaluationStackBase = CurrentFrame.EvaluationStackBase;
        var localsBase = CurrentFrame.LocalsBase;
        var localsCount = CurrentFrame.LocalsCount;
        var hasReturnLocalContinuation = CurrentFrame.HasReturnLocalContinuation;
        var initializingModule = CurrentFrame.InitializingModule;
        while (_evaluationStack.Count > evaluationStackBase)
        {
            _evaluationStack.Pop();
        }

        Array.Clear(_locals, localsBase, localsCount);
        _localsCount = localsBase;
        _frames[--_frameCount] = default;
        if (initializingModule is not null)
        {
            _modules.Complete(initializingModule);
            return;
        }

        if (_frameCount != 0)
        {
            if (hasReturnLocalContinuation)
            {
                ref var caller = ref CurrentFrame;
                var instructionIndex = caller.InstructionPointer - 1;
                var instruction = caller.Code.Definition.Instructions[instructionIndex];
                if (instruction.OpCode != PythonOpCode.StoreLocal)
                {
                    throw Fault(
                        "DPY4007",
                        "The managed return continuation is invalid.",
                        instruction.Span
                    );
                }

                _cancellationToken.ThrowIfCancellationRequested();
                if (_instructionsExecuted++ >= _instructionLimit)
                {
                    throw Fault(
                        "DPY4001",
                        "The managed instruction limit was exceeded.",
                        instruction.Span
                    );
                }

                StoreLocal(instruction.Operand, value, instruction.Span);
            }
            else
            {
                _evaluationStack.Push(value);
            }
        }
        else
        {
            _result = value;
        }
    }

    private PythonNoneValue Print(IReadOnlyList<PythonValue> arguments, TextSpan _)
    {
        _output.WriteLine(string.Join(" ", arguments.Select(value => value.ToDisplayString())));
        return PythonNoneValue.Instance;
    }

    private void FailActiveModuleInitializations()
    {
        for (var index = 0; index < _frameCount; index++)
        {
            if (_frames[index].InitializingModule is { } module)
            {
                _modules.Fail(module);
            }
        }
    }

    private PythonValue Pop(TextSpan span)
    {
        if (_evaluationStack.Count > CurrentFrame.EvaluationStackBase)
        {
            return _evaluationStack.Pop();
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private PythonValue Peek(TextSpan span)
    {
        if (_evaluationStack.Count > CurrentFrame.EvaluationStackBase)
        {
            return _evaluationStack.Peek();
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private PythonValue Peek(int depth, TextSpan span)
    {
        if (depth < 0 || _evaluationStack.Count - depth <= CurrentFrame.EvaluationStackBase)
        {
            throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
        }

        var index = 0;
        foreach (var value in _evaluationStack)
        {
            if (index++ == depth)
            {
                return value;
            }
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private void RotateTwo(TextSpan span)
    {
        var top = Pop(span);
        var second = Pop(span);
        _evaluationStack.Push(top);
        _evaluationStack.Push(second);
    }

    private void RotateThree(TextSpan span)
    {
        var top = Pop(span);
        var second = Pop(span);
        var third = Pop(span);
        _evaluationStack.Push(top);
        _evaluationStack.Push(third);
        _evaluationStack.Push(second);
    }

    private static PythonValue ApplyUnary(PythonOpCode opCode, PythonValue operand, TextSpan span)
    {
        if (opCode == PythonOpCode.UnaryNot)
        {
            return PythonTruthValue.FromBoolean(!IsTruthy(operand));
        }

        operand = PromoteTruthValue(operand);
        return (opCode, operand) switch
        {
            (PythonOpCode.UnaryPositive, PythonWholeNumberValue value) => value,
            (PythonOpCode.UnaryPositive, PythonFloatingPointValue value) => value,
            (PythonOpCode.UnaryPositive, PythonComplexValue value) => value,
            (PythonOpCode.UnaryNegative, PythonWholeNumberValue value) =>
                PythonWholeNumberValue.Create(-value.Value),
            (PythonOpCode.UnaryNegative, PythonFloatingPointValue value) =>
                new PythonFloatingPointValue(-value.Value),
            (PythonOpCode.UnaryNegative, PythonComplexValue value) => new PythonComplexValue(
                -value.Value
            ),
            (PythonOpCode.UnaryInvert, PythonWholeNumberValue value) =>
                PythonWholeNumberValue.Create(~value.Value),
            _ => throw Fault("DPY4005", "Unsupported operand for unary operator.", span),
        };
    }

    private static PythonTruthValue ApplyComparison(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span
    )
    {
        if (opCode is PythonOpCode.CompareEqual or PythonOpCode.CompareNotEqual)
        {
            var equal = AreEqual(left, right);
            return PythonTruthValue.FromBoolean(
                opCode == PythonOpCode.CompareEqual ? equal : !equal
            );
        }

        var promotedLeft = PromoteTruthValue(left);
        var promotedRight = PromoteTruthValue(right);
        if (
            IsNumeric(promotedLeft)
            && IsNumeric(promotedRight)
            && (
                promotedLeft is PythonFloatingPointValue
                || promotedRight is PythonFloatingPointValue
            )
        )
        {
            if (promotedLeft is PythonComplexValue || promotedRight is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span);
            }

            var leftFloatingPoint = ToDouble(promotedLeft);
            var rightFloatingPoint = ToDouble(promotedRight);
            return PythonTruthValue.FromBoolean(
                opCode switch
                {
                    PythonOpCode.CompareLessThan => leftFloatingPoint < rightFloatingPoint,
                    PythonOpCode.CompareLessThanOrEqual => leftFloatingPoint <= rightFloatingPoint,
                    PythonOpCode.CompareGreaterThan => leftFloatingPoint > rightFloatingPoint,
                    PythonOpCode.CompareGreaterThanOrEqual => leftFloatingPoint
                        >= rightFloatingPoint,
                    _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
                }
            );
        }

        var comparison = CompareOrdered(left, right, span);
        return PythonTruthValue.FromBoolean(
            opCode switch
            {
                PythonOpCode.CompareLessThan => comparison < 0,
                PythonOpCode.CompareLessThanOrEqual => comparison <= 0,
                PythonOpCode.CompareGreaterThan => comparison > 0,
                PythonOpCode.CompareGreaterThanOrEqual => comparison >= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
            }
        );
    }

    private static bool AreEqual(PythonValue left, PythonValue right)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        var tracksRecursion =
            left is PythonListValue or PythonTupleValue or PythonDictionaryValue
            && right is PythonListValue or PythonTupleValue or PythonDictionaryValue;
        if (tracksRecursion)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            _activeEqualityPairs ??= new HashSet<PythonValuePair>(PythonValuePairComparer.Instance);
            var pair = new PythonValuePair(left, right);
            if (!_activeEqualityPairs.Add(pair))
            {
                return true;
            }

            try
            {
                return AreEqualCore(left, right);
            }
            finally
            {
                _activeEqualityPairs.Remove(pair);
                if (_activeEqualityPairs.Count == 0)
                {
                    _activeEqualityPairs = null;
                }
            }
        }

        return AreEqualCore(left, right);
    }

    private static bool AreEqualCore(PythonValue left, PythonValue right)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                return ToComplex(left) == ToComplex(right);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left) == ToDouble(right);
            }

            return ((PythonWholeNumberValue)left).Value == ((PythonWholeNumberValue)right).Value;
        }

        return (left, right) switch
        {
            (PythonNoneValue, PythonNoneValue) => true,
            (PythonTextValue leftText, PythonTextValue rightText) => string.Equals(
                leftText.Value,
                rightText.Value,
                StringComparison.Ordinal
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceEqual(rightBytes.Value),
            (PythonListValue leftList, PythonListValue rightList) => AreSequencesEqual(
                leftList.Elements,
                rightList.Elements
            ),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => AreSequencesEqual(
                leftTuple.Elements,
                rightTuple.Elements
            ),
            (PythonDictionaryValue leftDictionary, PythonDictionaryValue rightDictionary) =>
                AreDictionariesEqual(leftDictionary, rightDictionary),
            (PythonBuiltinFunctionValue leftFunction, PythonBuiltinFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            (PythonFunctionValue leftFunction, PythonFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            (PythonModuleValue leftModule, PythonModuleValue rightModule) => ReferenceEquals(
                leftModule,
                rightModule
            ),
            _ => false,
        };
    }

    private static bool AreSequencesEqual(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreDictionariesEqual(
        PythonDictionaryValue left,
        PythonDictionaryValue right
    )
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        foreach (var item in left.Items)
        {
            if (
                !TryFindDictionaryItem(right, item.Key, out var rightItem)
                || !AreEqual(item.Value, rightItem.Value)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareOrdered(PythonValue left, PythonValue right, TextSpan span)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left).CompareTo(ToDouble(right));
            }

            return ((PythonWholeNumberValue)left).Value.CompareTo(
                ((PythonWholeNumberValue)right).Value
            );
        }

        return (left, right) switch
        {
            (PythonTextValue leftText, PythonTextValue rightText) => string.CompareOrdinal(
                leftText.Value,
                rightText.Value
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceCompareTo(rightBytes.Value),
            _ => throw Fault("DPY4005", "Values of these types cannot be ordered.", span),
        };
    }

    private static PythonValue ApplyBinary(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span
    )
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (
            opCode == PythonOpCode.BinaryAdd
            && left is PythonTextValue leftText
            && right is PythonTextValue rightText
        )
        {
            return new PythonTextValue(leftText.Value + rightText.Value);
        }

        if (
            opCode == PythonOpCode.BinaryAdd
            && left is PythonByteSequenceValue leftBytes
            && right is PythonByteSequenceValue rightBytes
        )
        {
            return new PythonByteSequenceValue([.. leftBytes.Value, .. rightBytes.Value]);
        }

        if (opCode == PythonOpCode.BinaryMultiply)
        {
            if (left is PythonTextValue text && right is PythonWholeNumberValue count)
            {
                return Repeat(text, count, span);
            }

            if (right is PythonTextValue reverseText && left is PythonWholeNumberValue reverseCount)
            {
                return Repeat(reverseText, reverseCount, span);
            }
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            throw Fault("DPY4005", "Unsupported operands for binary operator.", span);
        }

        if (left is PythonComplexValue || right is PythonComplexValue)
        {
            return ApplyComplex(opCode, ToComplex(left), ToComplex(right), span);
        }

        if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
        {
            return ApplyFloatingPoint(opCode, ToDouble(left), ToDouble(right), span);
        }

        return ApplyWholeNumber(
            opCode,
            ((PythonWholeNumberValue)left).Value,
            ((PythonWholeNumberValue)right).Value,
            span
        );
    }

    private static PythonValue ApplyWholeNumber(
        PythonOpCode opCode,
        BigInteger left,
        BigInteger right,
        TextSpan span
    )
    {
        if (
            opCode
                is PythonOpCode.BinaryTrueDivide
                    or PythonOpCode.BinaryFloorDivide
                    or PythonOpCode.BinaryModulo
            && right.IsZero
        )
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => PythonWholeNumberValue.Create(left + right),
            PythonOpCode.BinarySubtract => PythonWholeNumberValue.Create(left - right),
            PythonOpCode.BinaryMultiply => PythonWholeNumberValue.Create(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonFloatingPointValue(
                (double)left / (double)right
            ),
            PythonOpCode.BinaryFloorDivide => PythonWholeNumberValue.Create(
                FloorDivide(left, right)
            ),
            PythonOpCode.BinaryModulo => PythonWholeNumberValue.Create(
                left - FloorDivide(left, right) * right
            ),
            PythonOpCode.BinaryPower when right >= 0 && right <= int.MaxValue =>
                PythonWholeNumberValue.Create(BigInteger.Pow(left, (int)right)),
            PythonOpCode.BinaryPower when left.IsZero => throw Fault(
                "DPY4004",
                "Zero cannot be raised to a negative power.",
                span
            ),
            PythonOpCode.BinaryPower => new PythonFloatingPointValue(
                Math.Pow((double)left, (double)right)
            ),
            _ => throw Fault("DPY4005", "Unsupported numeric operator.", span),
        };
    }

    private static PythonFloatingPointValue ApplyFloatingPoint(
        PythonOpCode opCode,
        double left,
        double right,
        TextSpan span
    )
    {
        if (
            opCode
                is PythonOpCode.BinaryTrueDivide
                    or PythonOpCode.BinaryFloorDivide
                    or PythonOpCode.BinaryModulo
            && right == 0
        )
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => new PythonFloatingPointValue(left + right),
            PythonOpCode.BinarySubtract => new PythonFloatingPointValue(left - right),
            PythonOpCode.BinaryMultiply => new PythonFloatingPointValue(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonFloatingPointValue(left / right),
            PythonOpCode.BinaryFloorDivide => new PythonFloatingPointValue(
                Math.Floor(left / right)
            ),
            PythonOpCode.BinaryModulo => new PythonFloatingPointValue(
                left - Math.Floor(left / right) * right
            ),
            PythonOpCode.BinaryPower => new PythonFloatingPointValue(Math.Pow(left, right)),
            _ => throw Fault("DPY4005", "Unsupported numeric operator.", span),
        };
    }

    private static PythonComplexValue ApplyComplex(
        PythonOpCode opCode,
        Complex left,
        Complex right,
        TextSpan span
    )
    {
        if (opCode == PythonOpCode.BinaryTrueDivide && right == Complex.Zero)
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => new PythonComplexValue(left + right),
            PythonOpCode.BinarySubtract => new PythonComplexValue(left - right),
            PythonOpCode.BinaryMultiply => new PythonComplexValue(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonComplexValue(left / right),
            PythonOpCode.BinaryPower => new PythonComplexValue(Complex.Pow(left, right)),
            _ => throw Fault("DPY4005", "Unsupported complex-number operator.", span),
        };
    }

    private static PythonTextValue Repeat(
        PythonTextValue text,
        PythonWholeNumberValue count,
        TextSpan span
    )
    {
        if (count.Value <= 0)
        {
            return new PythonTextValue(string.Empty);
        }

        const int maximumRepeatedTextLength = 16 * 1024 * 1024;
        if (
            count.Value > int.MaxValue
            || text.Value.Length != 0 && count.Value * text.Value.Length > maximumRepeatedTextLength
        )
        {
            throw Fault("DPY4006", "Repeated string is too large.", span);
        }

        return new PythonTextValue(string.Concat(Enumerable.Repeat(text.Value, (int)count.Value)));
    }

    private static BigInteger FloorDivide(BigInteger left, BigInteger right)
    {
        var quotient = BigInteger.DivRem(left, right, out var remainder);
        if (!remainder.IsZero && remainder.Sign != right.Sign)
        {
            quotient--;
        }

        return quotient;
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue;

    private static bool IsTruthy(PythonValue value) => ManagedObjectProtocols.IsTrue(value);

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static double ToDouble(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Complex ToComplex(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => new Complex((double)whole.Value, 0),
            PythonFloatingPointValue floatingPoint => new Complex(floatingPoint.Value, 0),
            PythonComplexValue complex => complex.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static int GetJumpTarget(PythonInstruction instruction, int instructionCount)
    {
        if (instruction.Operand >= 0 && instruction.Operand <= instructionCount)
        {
            return instruction.Operand;
        }

        throw Fault("DPY4007", "The DotPython jump target is invalid.", instruction.Span);
    }

    private readonly record struct PythonValuePair(PythonValue Left, PythonValue Right);

    private sealed class PythonValuePairComparer : IEqualityComparer<PythonValuePair>
    {
        internal static PythonValuePairComparer Instance { get; } = new();

        public bool Equals(PythonValuePair left, PythonValuePair right) =>
            ReferenceEquals(left.Left, right.Left) && ReferenceEquals(left.Right, right.Right);

        public int GetHashCode(PythonValuePair pair) =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(pair.Left),
                RuntimeHelpers.GetHashCode(pair.Right)
            );
    }

    private static PythonRuntimeException Fault(
        string code,
        string message,
        TextSpan span,
        string? pythonExceptionTypeName = null
    ) => new(code, message, span, pythonExceptionTypeName);

    private struct PythonFrame
    {
        internal PythonFrame(
            PreparedPythonCode code,
            PythonGlobalNamespace globals,
            int localsBase,
            int localsCount,
            PythonCell[] cells,
            int evaluationStackBase,
            bool hasReturnLocalContinuation,
            PythonModuleValue? initializingModule
        )
        {
            Code = code;
            Cells = cells;
            _encodedEvaluationStackBase = hasReturnLocalContinuation
                ? ~evaluationStackBase
                : evaluationStackBase;
            Globals = globals;
            LocalsBase = localsBase;
            LocalsCount = localsCount;
            InitializingModule = initializingModule;
            ActiveExceptions = new Stack<PythonRaisedException>();
            ExceptionBlocks = [];
            PendingFinalies = new Stack<PythonPendingFinally>();
        }

        // A complemented base marks a return-local continuation without growing each frame.
        private readonly int _encodedEvaluationStackBase;

        internal PreparedPythonCode Code { get; }

        internal Stack<PythonRaisedException> ActiveExceptions { get; }

        internal PythonCell[] Cells { get; }

        internal int EvaluationStackBase =>
            HasReturnLocalContinuation ? ~_encodedEvaluationStackBase : _encodedEvaluationStackBase;

        internal PythonGlobalNamespace Globals { get; }

        internal List<PythonExceptionBlock> ExceptionBlocks { get; }

        internal int InstructionPointer { get; set; }

        internal PythonModuleValue? InitializingModule { get; }

        internal int LocalsBase { get; }

        internal int LocalsCount { get; }

        internal Stack<PythonPendingFinally> PendingFinalies { get; }

        internal bool HasReturnLocalContinuation => _encodedEvaluationStackBase < 0;
    }

    private enum PythonExceptionBlockKind
    {
        Except,
        Finally,
    }

    private readonly record struct PythonExceptionBlock(
        PythonExceptionBlockKind Kind,
        int HandlerTarget,
        int EvaluationStackDepth
    );

    private readonly record struct PythonPendingFinally(
        Exception? Exception,
        PythonValue? ReturnValue,
        int OuterExceptionBlockDepth
    );
}
