using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Compiler;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// A minimal `asyncio`: a single-threaded run-to-completion loop over the runtime's
/// coroutines. `run`, `sleep`, `create_task`/`ensure_future`, `gather`, `wait_for`,
/// cancellation and a `Task` object are implemented; selectors, transports, subprocesses
/// and the wider loop API are out of scope.
/// </summary>
/// <remarks>
/// The async builtins return synthetic coroutines whose resume step yields an internal
/// scheduling token instead of running Python code. The loop drives the top coroutine
/// through its `send`/`throw` methods; an awaited token travels out through the runtime's
/// existing await delegation exactly as a generator's `yield from` would.
/// </remarks>
internal static class PythonAsyncioModule
{
    private static readonly PythonManagedTypeValue CancelledErrorType = CreateExceptionType(
        "CancelledError",
        "BaseException"
    );

    private static readonly PythonManagedTypeValue InvalidStateErrorType = CreateExceptionType(
        "InvalidStateError",
        "Exception"
    );

    private static readonly PythonManagedTypeValue TaskType = CreateTaskType();

    private static readonly Lazy<PreparedPythonCode> DummyCode = new(CreateDummyCode);

    [ThreadStatic]
    private static EventLoop? _loop;

    private static readonly string[] ExportedNames =
    [
        "CancelledError",
        "InvalidStateError",
        "Task",
        "TimeoutError",
        "create_task",
        "ensure_future",
        "gather",
        "iscoroutine",
        "run",
        "sleep",
        "wait_for",
    ];

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "run",
            new PythonBuiltinFunctionValue(
                "run",
                (arguments, span) => Run(arguments, [], [], span),
                (arguments, names, values, span) => Run(arguments, names, values, span)
            )
        );
        globals.SetValue(
            "sleep",
            new PythonBuiltinFunctionValue(
                "sleep",
                (arguments, span) => Sleep(arguments, [], [], span),
                (arguments, names, values, span) => Sleep(arguments, names, values, span)
            )
        );
        globals.SetValue(
            "create_task",
            new PythonBuiltinFunctionValue(
                "create_task",
                (arguments, span) => CreateTaskBuiltin(arguments, [], [], span),
                (arguments, names, values, span) =>
                    CreateTaskBuiltin(arguments, names, values, span)
            )
        );
        globals.SetValue(
            "ensure_future",
            new PythonBuiltinFunctionValue(
                "ensure_future",
                (arguments, span) => EnsureFuture(arguments, [], [], span),
                (arguments, names, values, span) => EnsureFuture(arguments, names, values, span)
            )
        );
        globals.SetValue(
            "gather",
            new PythonBuiltinFunctionValue(
                "gather",
                (arguments, span) => Gather(arguments, [], [], span),
                (arguments, names, values, span) => Gather(arguments, names, values, span)
            )
        );
        globals.SetValue(
            "wait_for",
            new PythonBuiltinFunctionValue(
                "wait_for",
                (arguments, span) => WaitFor(arguments, [], [], span),
                (arguments, names, values, span) => WaitFor(arguments, names, values, span)
            )
        );
        globals.SetValue("CancelledError", CancelledErrorType);
        globals.SetValue("InvalidStateError", InvalidStateErrorType);
        globals.SetValue("Task", TaskType);
        globals.SetValue("TimeoutError", PythonBuiltinTypes.GetExceptionType("TimeoutError"));
        globals.SetValue(
            "iscoroutine",
            new PythonBuiltinFunctionValue(
                "iscoroutine",
                (arguments, _) =>
                    PythonTruthValue.FromBoolean(
                        arguments.Count == 1
                            && arguments[0] is PythonGeneratorValue { IsCoroutine: true }
                    )
            )
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. ExportedNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    // -----------------------------------------------------------------------------
    // Builtins
    // -----------------------------------------------------------------------------

    private static PythonValue Run(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind("run", ["coro", "debug"], arguments, names, values, span);
        if (slots[0] is not PythonGeneratorValue { IsCoroutine: true } coroutine)
        {
            throw TypeError("An asyncio.Future, a coroutine or an awaitable is required", span);
        }
        if (_loop is not null)
        {
            throw RuntimeError("asyncio.run() cannot be called from a running event loop", span);
        }
        var loop = new EventLoop();
        var main = new TaskState(coroutine, "main", loop.NextTaskNumber());
        _loop = loop;
        try
        {
            return loop.Run(main, span);
        }
        finally
        {
            _loop = null;
        }
    }

    private static PythonGeneratorValue Sleep(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind("sleep", ["delay", "result"], arguments, names, values, span);
        if (slots[0] is null)
        {
            throw TypeError("sleep() missing required argument 'delay' (pos 1)", span);
        }
        var delay = Math.Max(0, Seconds(slots[0]!, "sleep() delay", span));
        var result = slots[1] ?? (PythonValue)PythonNoneValue.Instance;
        var step = 0;
        return NewAwaitable(
            "sleep",
            _ =>
            {
                if (step++ == 0)
                {
                    return (
                        true,
                        Token(
                            new AsyncioYield
                            {
                                Kind = AsyncioYieldKind.Sleep,
                                Delay = delay,
                                Value = result,
                            }
                        )
                    );
                }
                return (false, result);
            }
        );
    }

    private static PythonValue CreateTaskBuiltin(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind("create_task", ["coro", "name"], arguments, names, values, span);
        var loop = _loop ?? throw RuntimeError("no running event loop", span);
        if (slots[0] is not PythonGeneratorValue { IsCoroutine: true } coroutine)
        {
            throw TypeError($"a coroutine was expected, got {Describe(slots[0])}", span);
        }
        // CPython draws an automatic `Task-N` name only when the caller gave none.
        var number = slots[1] is null ? loop.NextTaskNumber() : 0;
        var name = slots[1] is PythonTextValue text ? text.Value : $"Task-{number}";
        var task = new TaskState(coroutine, name, number);
        loop.Schedule(task);
        return TaskValueOf(task);
    }

    private static PythonValue EnsureFuture(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (
            arguments.Count == 1
            && names.Count == 0
            && arguments[0] is PythonManagedObjectValue { Payload: TaskState }
        )
        {
            return arguments[0];
        }
        return CreateTaskBuiltin(arguments, names, values, span);
    }

    private static PythonGeneratorValue Gather(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var returnExceptions = false;
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] != "return_exceptions")
            {
                throw TypeError(
                    $"gather() got an unexpected keyword argument '{names[index]}'",
                    span
                );
            }
            returnExceptions = ManagedObjectProtocols.IsTrue(values[index]);
        }
        foreach (var argument in arguments)
        {
            if (
                argument is not PythonGeneratorValue { IsCoroutine: true }
                && argument is not PythonManagedObjectValue { Payload: TaskState }
            )
            {
                throw TypeError("An asyncio.Future, a coroutine or an awaitable is required", span);
            }
        }
        var loop = _loop ?? throw RuntimeError("no running event loop", span);
        var tasks = new List<TaskState>();
        foreach (var argument in arguments)
        {
            tasks.Add(EnsureTask(argument, loop, span));
        }
        var step = 0;
        return NewAwaitable(
            "gather",
            sent =>
            {
                if (step++ == 0)
                {
                    return (
                        true,
                        Token(
                            new AsyncioYield
                            {
                                Kind = AsyncioYieldKind.Gather,
                                Tasks = tasks,
                                ReturnExceptions = returnExceptions,
                            }
                        )
                    );
                }
                return (false, sent ?? (PythonValue)PythonNoneValue.Instance);
            }
        );
    }

    private static PythonGeneratorValue WaitFor(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind("wait_for", ["fut", "timeout"], arguments, names, values, span);
        if (slots[0] is null)
        {
            throw TypeError("wait_for() missing required argument 'fut' (pos 1)", span);
        }
        if (
            slots[0] is not PythonGeneratorValue { IsCoroutine: true }
            && slots[0] is not PythonManagedObjectValue { Payload: TaskState }
        )
        {
            throw TypeError("An asyncio.Future, a coroutine or an awaitable is required", span);
        }
        var loop = _loop ?? throw RuntimeError("no running event loop", span);
        var task = EnsureTask(slots[0]!, loop, span);
        double? timeout = slots[1] is null or PythonNoneValue
            ? null
            : Math.Max(0, Seconds(slots[1]!, "wait_for() timeout", span));
        var step = 0;
        return NewAwaitable(
            "wait_for",
            sent =>
            {
                if (step++ == 0)
                {
                    return (
                        true,
                        Token(
                            new AsyncioYield
                            {
                                Kind = AsyncioYieldKind.WaitFor,
                                Task = task,
                                Delay = timeout ?? double.PositiveInfinity,
                            }
                        )
                    );
                }
                return (false, sent ?? (PythonValue)PythonNoneValue.Instance);
            }
        );
    }

    private static TaskState EnsureTask(PythonValue value, EventLoop loop, TextSpan span)
    {
        if (value is PythonManagedObjectValue { Payload: TaskState state })
        {
            return state;
        }
        if (value is PythonGeneratorValue { IsCoroutine: true } coroutine)
        {
            var task = new TaskState(
                coroutine,
                $"Task-{loop.NextTaskNumber()}",
                loop.NextTaskNumber()
            );
            loop.Schedule(task);
            return task;
        }
        throw TypeError("An asyncio.Future, a coroutine or an awaitable is required", span);
    }

    // -----------------------------------------------------------------------------
    // Synthetic coroutines and scheduling tokens
    // -----------------------------------------------------------------------------

    private static PythonGeneratorValue NewAwaitable(
        string name,
        Func<PythonValue?, (bool HasValue, PythonValue Value)> step,
        bool coroutine = true
    )
    {
        var generator = new PythonGeneratorValue(
            name,
            DummyCode.Value,
            new PythonGlobalNamespace(),
            [],
            []
        )
        {
            IsCoroutine = coroutine,
        };
        generator.ResumeCore = (sent, injected) =>
        {
            if (injected is not null)
            {
                generator.State = PythonGeneratorState.Completed;
                throw new PythonRaisedException(injected);
            }
            try
            {
                var (hasValue, value) = step(sent);
                generator.State = hasValue
                    ? PythonGeneratorState.Suspended
                    : PythonGeneratorState.Completed;
                if (!hasValue)
                {
                    generator.ReturnValue = value;
                }
                else
                {
                    generator.YieldedValue = value;
                }
                return (hasValue, value);
            }
            catch (PythonRaisedException)
            {
                generator.State = PythonGeneratorState.Completed;
                throw;
            }
        };
        return generator;
    }

    private static PythonExternalObjectValue Token(AsyncioYield yield) => new(yield);

    private enum AsyncioYieldKind
    {
        Sleep,
        WaitTask,
        Gather,
        WaitFor,
    }

    /// <summary>What an async builtin yields to the loop: a timer, a task wait, or a gather.</summary>
    private sealed class AsyncioYield : PythonExternalObjectProtocol
    {
        internal AsyncioYieldKind Kind { get; init; }

        internal double Delay { get; init; }

        internal PythonValue? Value { get; init; }

        internal TaskState? Task { get; init; }

        internal List<TaskState>? Tasks { get; init; }

        internal bool ReturnExceptions { get; init; }

        PythonValue PythonExternalObjectProtocol.Call(
            IReadOnlyList<PythonValue> arguments,
            TextSpan span
        ) => throw NotCallable(span);

        PythonValue PythonExternalObjectProtocol.CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw NotCallable(span);

        PythonValue PythonExternalObjectProtocol.GetAttribute(string name, TextSpan span) =>
            throw ManagedObjectProtocols.Fault(
                "DPY4022",
                $"'_asyncio.Future' object has no attribute '{name}'",
                span,
                "AttributeError"
            );

        PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
            throw NotCallable(span);

        long PythonExternalObjectProtocol.GetHash(TextSpan span) =>
            RuntimeHelpers.GetHashCode(this);

        int PythonExternalObjectProtocol.GetLength(TextSpan span) =>
            throw ManagedObjectProtocols.Fault(
                "DPY4039",
                "object of type '_asyncio.Future' has no len()",
                span,
                "TypeError"
            );

        PythonTruthValue PythonExternalObjectProtocol.RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        ) => PythonTruthValue.FromBoolean(comparison == PythonRichComparison.NotEqual);

        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) => false;

        string PythonExternalObjectProtocol.ToDisplayString() => "<Future pending>";

        string PythonExternalObjectProtocol.ToRepresentationString() => "<Future pending>";

        private static PythonRuntimeException NotCallable(TextSpan span) =>
            ManagedObjectProtocols.Fault(
                "DPY4039",
                "'_asyncio.Future' object is not callable",
                span,
                "TypeError"
            );
    }

    // -----------------------------------------------------------------------------
    // Task objects
    // -----------------------------------------------------------------------------

    private sealed class TaskState(PythonGeneratorValue coroutine, string name, int number)
    {
        internal PythonGeneratorValue Coroutine { get; } = coroutine;

        internal string Name { get; set; } = name;

        internal int Number { get; } = number;

        internal bool Done { get; set; }

        internal bool Cancelled { get; set; }

        internal bool CancelRequested { get; set; }

        internal bool Queued { get; set; }

        internal PythonValue? Result { get; set; }

        internal PythonExceptionValue? Exception { get; set; }

        internal PythonValue? PendingSend { get; set; }

        internal PythonExceptionValue? PendingThrow { get; set; }

        internal WaitKind WaitKind { get; set; }

        internal TaskState? WaitingOn { get; set; }

        internal List<TaskState>? WaitingGather { get; set; }

        internal bool ReturnExceptions { get; set; }

        internal bool TimedOut { get; set; }

        internal double WakeAt { get; set; } = double.PositiveInfinity;

        internal PythonValue? WakeValue { get; set; }

        internal List<PythonValue> Callbacks { get; } = [];

        internal PythonManagedObjectValue? PublicValue { get; set; }
    }

    private enum WaitKind
    {
        None,
        Task,
        Gather,
        WaitFor,
    }

    private static PythonValue TaskValueOf(TaskState state) =>
        state.PublicValue ??= new PythonManagedObjectValue(TaskType, state);

    private static PythonManagedTypeValue CreateTaskType()
    {
        var type = new PythonManagedTypeValue("Task") { Module = "asyncio" };
        type.Attributes["__module__"] = Text("asyncio");
        type.Attributes["__qualname__"] = Text("Task");
        type.Attributes["done"] = Method(
            "Task",
            "done",
            (self, _) => PythonTruthValue.FromBoolean(TaskOf(self).Done)
        );
        type.Attributes["cancelled"] = Method(
            "Task",
            "cancelled",
            (self, _) => PythonTruthValue.FromBoolean(TaskOf(self).Cancelled)
        );
        type.Attributes["result"] = Method(
            "Task",
            "result",
            (self, _) =>
            {
                var task = TaskOf(self);
                if (!task.Done)
                {
                    throw Fault("InvalidStateError", "Result is not set.", InvalidStateErrorType);
                }
                return task.Exception is { } exception
                    ? throw new PythonRaisedException(exception)
                    : task.Result ?? PythonNoneValue.Instance;
            }
        );
        type.Attributes["exception"] = Method(
            "Task",
            "exception",
            (self, _) =>
            {
                var task = TaskOf(self);
                if (!task.Done)
                {
                    throw Fault(
                        "InvalidStateError",
                        "Exception is not set.",
                        InvalidStateErrorType
                    );
                }
                return (PythonValue?)task.Exception ?? PythonNoneValue.Instance;
            }
        );
        type.Attributes["cancel"] = Method(
            "Task",
            "cancel",
            (self, _) => PythonTruthValue.FromBoolean(Cancel(TaskOf(self)))
        );
        type.Attributes["get_name"] = Method(
            "Task",
            "get_name",
            (self, _) => Text(TaskOf(self).Name)
        );
        type.Attributes["set_name"] = Method(
            "Task",
            "set_name",
            (self, arguments) =>
            {
                if (arguments.Count != 1 || arguments[0] is not PythonTextValue name)
                {
                    throw Fault("TypeError", "set_name() argument must be str");
                }
                TaskOf(self).Name = name.Value;
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["add_done_callback"] = Method(
            "Task",
            "add_done_callback",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault("TypeError", "add_done_callback() takes exactly one argument");
                }
                var task = TaskOf(self);
                if (task.Done)
                {
                    if (_loop is not { } loop)
                    {
                        throw RuntimeError("no running event loop", default);
                    }
                    loop.QueueCallback(arguments[0], task);
                }
                else
                {
                    task.Callbacks.Add(arguments[0]);
                }
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["__await__"] = Method(
            "Task",
            "__await__",
            (self, _) =>
            {
                var task = TaskOf(self);
                var step = 0;
                return NewAwaitable(
                    "Task",
                    sent =>
                    {
                        if (step++ == 0)
                        {
                            return (
                                true,
                                Token(
                                    new AsyncioYield
                                    {
                                        Kind = AsyncioYieldKind.WaitTask,
                                        Task = task,
                                    }
                                )
                            );
                        }
                        return (false, sent ?? (PythonValue)PythonNoneValue.Instance);
                    },
                    coroutine: false
                );
            }
        );
        return type;
    }

    private static TaskState TaskOf(PythonValue? self) =>
        self is PythonManagedObjectValue { Payload: TaskState state }
            ? state
            : throw Fault("TypeError", "the value is not a Task");

    private static bool Cancel(TaskState task)
    {
        if (task.Done)
        {
            return false;
        }
        task.CancelRequested = true;
        if (_loop is not { } loop)
        {
            return true;
        }
        if (task.WaitKind != WaitKind.None)
        {
            task.WaitKind = WaitKind.None;
            task.WakeAt = double.PositiveInfinity;
            loop.EnqueueThrow(task, CancelledError());
        }
        else if (task.Queued)
        {
            task.PendingThrow ??= CancelledError();
        }
        else
        {
            loop.EnqueueThrow(task, CancelledError());
        }
        return true;
    }

    private static void InvokeCallback(PythonValue callback, TaskState task)
    {
        if (UserObjectProtocols.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.Invoke(callback, [TaskValueOf(task)], default);
        }
    }

    private static PythonExceptionValue CancelledError() =>
        new("CancelledError", string.Empty) { ManagedType = CancelledErrorType };

    // -----------------------------------------------------------------------------
    // The event loop
    // -----------------------------------------------------------------------------

    private sealed class EventLoop
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<TaskState> _ready = new();
        private readonly Queue<(PythonValue Callback, TaskState Task)> _callbacks = new();
        private readonly List<TaskState> _tasks = [];
        private int _taskNumber;

        internal double Now => _clock.Elapsed.TotalSeconds;

        internal int NextTaskNumber() => ++_taskNumber;

        internal void Schedule(TaskState task)
        {
            _tasks.Add(task);
            EnqueueSend(task, PythonNoneValue.Instance);
        }

        internal void QueueCallback(PythonValue callback, TaskState task) =>
            _callbacks.Enqueue((callback, task));

        internal void EnqueueSend(TaskState task, PythonValue value)
        {
            task.PendingSend = value;
            task.PendingThrow = null;
            task.Queued = true;
            _ready.Enqueue(task);
        }

        internal void EnqueueThrow(TaskState task, PythonExceptionValue exception)
        {
            task.PendingSend = null;
            task.PendingThrow = exception;
            task.Queued = true;
            _ready.Enqueue(task);
        }

        internal PythonValue Run(TaskState main, TextSpan span)
        {
            _tasks.Add(main);
            EnqueueSend(main, PythonNoneValue.Instance);
            while (!main.Done)
            {
                while (_callbacks.Count != 0)
                {
                    var (callback, completed) = _callbacks.Dequeue();
                    InvokeCallback(callback, completed);
                }
                WakeParked();
                if (_ready.Count != 0)
                {
                    var task = _ready.Dequeue();
                    task.Queued = false;
                    Step(task, span);
                    continue;
                }
                if (ProcessTimers())
                {
                    continue;
                }
                if (NextDeadline() is { } deadline)
                {
                    var remaining = deadline - Now;
                    if (remaining > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(remaining, 1)));
                    }
                    continue;
                }
                throw RuntimeError("Event loop is deadlocked", span);
            }
            if (main.Exception is { } exception)
            {
                throw new PythonRaisedException(exception);
            }
            return main.Result ?? PythonNoneValue.Instance;
        }

        private void Step(TaskState task, TextSpan span)
        {
            task.WakeAt = double.PositiveInfinity;
            var throwValue = task.PendingThrow;
            var sendValue = task.PendingSend ?? PythonNoneValue.Instance;
            task.PendingThrow = null;
            task.PendingSend = null;
            PythonValue yielded;
            try
            {
                // ResumeCore is the coroutine's own resume entry point: the VM installs
                // one for Python coroutines and the async builtins install theirs, and
                // both accept an exception injected at the suspended await.
                var advanced = task.Coroutine.ResumeCore!(
                    throwValue is null ? sendValue : null,
                    throwValue
                );
                if (!advanced.HasValue)
                {
                    Complete(task, result: advanced.Value);
                    return;
                }
                yielded = advanced.Value;
            }
            catch (PythonRaisedException raised)
            {
                Complete(task, exception: raised.Value);
                return;
            }
            catch (PythonRuntimeException fault)
            {
                Complete(
                    task,
                    exception: fault.ExceptionValue
                        ?? new PythonExceptionValue(
                            PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code)
                                ?? "RuntimeError",
                            fault.Message
                        )
                );
                return;
            }
            HandleYield(task, yielded);
        }

        private void HandleYield(TaskState task, PythonValue yielded)
        {
            if (yielded is not PythonExternalObjectValue { Protocol: AsyncioYield control })
            {
                Complete(
                    task,
                    exception: new PythonExceptionValue(
                        "RuntimeError",
                        $"Task got bad yield: {yielded.ToRepresentationString()}"
                    )
                );
                return;
            }
            switch (control.Kind)
            {
                case AsyncioYieldKind.Sleep:
                    if (control.Delay <= 0)
                    {
                        EnqueueSend(task, control.Value ?? PythonNoneValue.Instance);
                    }
                    else
                    {
                        task.WaitKind = WaitKind.Task;
                        task.WaitingOn = null;
                        task.WakeAt = Now + control.Delay;
                        task.WakeValue = control.Value ?? PythonNoneValue.Instance;
                    }
                    return;
                case AsyncioYieldKind.WaitTask:
                {
                    var target = control.Task!;
                    if (target.Done)
                    {
                        ResumeFromTarget(task, target);
                    }
                    else
                    {
                        task.WaitKind = WaitKind.Task;
                        task.WaitingOn = target;
                    }
                    return;
                }
                case AsyncioYieldKind.Gather:
                {
                    var targets = control.Tasks!;
                    task.WaitingGather = targets;
                    task.ReturnExceptions = control.ReturnExceptions;
                    task.WaitKind = WaitKind.Gather;
                    return;
                }
                case AsyncioYieldKind.WaitFor:
                {
                    var target = control.Task!;
                    if (target.Done)
                    {
                        ResumeFromTarget(task, target);
                    }
                    else if (control.Delay <= 0)
                    {
                        // A zero timeout wins over a target that has not finished yet.
                        task.WaitKind = WaitKind.WaitFor;
                        task.WaitingOn = target;
                        task.TimedOut = true;
                        Cancel(target);
                    }
                    else
                    {
                        task.WaitKind = WaitKind.WaitFor;
                        task.WaitingOn = target;
                        if (!double.IsPositiveInfinity(control.Delay))
                        {
                            task.WakeAt = Now + control.Delay;
                        }
                    }
                    return;
                }
            }
        }

        private void WakeParked()
        {
            foreach (var task in _tasks)
            {
                if (task.Done || task.Queued)
                {
                    continue;
                }
                switch (task.WaitKind)
                {
                    case WaitKind.Task:
                        if (task.WaitingOn is null)
                        {
                            // A plain sleep is woken by ProcessTimers, not by a task.
                            continue;
                        }
                        if (task.WaitingOn.Done)
                        {
                            task.WaitKind = WaitKind.None;
                            ResumeFromTarget(task, task.WaitingOn);
                        }
                        break;
                    case WaitKind.WaitFor:
                        if (task.WaitingOn!.Done)
                        {
                            task.WaitKind = WaitKind.None;
                            if (task.TimedOut)
                            {
                                task.TimedOut = false;
                                EnqueueThrow(
                                    task,
                                    new PythonExceptionValue("TimeoutError", string.Empty)
                                );
                            }
                            else
                            {
                                ResumeFromTarget(task, task.WaitingOn);
                            }
                        }
                        break;
                    case WaitKind.Gather:
                        if (task.WaitingGather!.All(target => target.Done))
                        {
                            task.WaitKind = WaitKind.None;
                            ResumeFromGather(task);
                        }
                        break;
                }
            }
        }

        private void ResumeFromTarget(TaskState waiter, TaskState target)
        {
            if (target.Exception is { } exception)
            {
                EnqueueThrow(waiter, exception);
            }
            else
            {
                EnqueueSend(waiter, target.Result ?? PythonNoneValue.Instance);
            }
        }

        private void ResumeFromGather(TaskState waiter)
        {
            var targets = waiter.WaitingGather!;
            waiter.WaitingGather = null;
            if (
                !waiter.ReturnExceptions
                && targets.FirstOrDefault(t => t.Exception is not null) is { } failed
            )
            {
                EnqueueThrow(waiter, failed.Exception!);
                return;
            }
            EnqueueSend(
                waiter,
                new PythonListValue([
                    .. targets.Select(target =>
                        target.Exception is { } failure
                            ? (PythonValue)failure
                            : target.Result ?? PythonNoneValue.Instance
                    ),
                ])
            );
        }

        private bool ProcessTimers()
        {
            var woke = false;
            foreach (var task in _tasks)
            {
                if (task.Done || task.Queued || task.WakeAt > Now)
                {
                    continue;
                }
                if (task.WaitKind == WaitKind.WaitFor && task.WaitingOn is { Done: false } inner)
                {
                    // CPython waits for the cancellation to finish before the timeout
                    // surfaces, so the coroutine's cleanup runs first.
                    task.TimedOut = true;
                    task.WakeAt = double.PositiveInfinity;
                    Cancel(inner);
                    woke = true;
                    continue;
                }
                if (task.WaitKind == WaitKind.Task && task.WaitingOn is null)
                {
                    var value = task.WakeValue ?? (PythonValue)PythonNoneValue.Instance;
                    task.WaitKind = WaitKind.None;
                    task.WakeAt = double.PositiveInfinity;
                    task.WakeValue = null;
                    EnqueueSend(task, value);
                    woke = true;
                }
            }
            return woke;
        }

        private double? NextDeadline()
        {
            var next = double.PositiveInfinity;
            foreach (var task in _tasks)
            {
                if (!task.Done && !task.Queued && task.WakeAt < next)
                {
                    next = task.WakeAt;
                }
            }
            return double.IsPositiveInfinity(next) ? null : next;
        }

        private static void Complete(
            TaskState task,
            PythonValue? result = null,
            PythonExceptionValue? exception = null
        )
        {
            task.Done = true;
            task.Queued = false;
            task.WaitKind = WaitKind.None;
            task.WakeAt = double.PositiveInfinity;
            task.Result = exception is null ? result ?? PythonNoneValue.Instance : null;
            task.Exception = exception;
            task.Cancelled =
                exception is not null
                && exception.TypeName == "CancelledError"
                && task.CancelRequested;
            if (_loop is { } loop)
            {
                foreach (var callback in task.Callbacks)
                {
                    loop.QueueCallback(callback, task);
                }
            }
            task.Callbacks.Clear();
        }
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static PreparedPythonCode CreateDummyCode()
    {
        var parsed = PythonParser.Parse(new SourceText(string.Empty, "<asyncio>"));
        var compilation = PythonCompiler.Compile(parsed.Module, "<asyncio>");
        return PreparedPythonCode.Create(compilation.Code);
    }

    private static double Seconds(PythonValue value, string name, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floating => floating.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw TypeError(
                $"{name} must be int or float, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            ),
        };

    private static string Describe(PythonValue? value) =>
        value is null ? "missing" : value.ToRepresentationString();

    private static PythonTextValue Text(string value) => new(value);

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body
    ) => PythonUserTypes.Method(owner, name, body);

    private static PythonRuntimeException Fault(
        string type,
        string message,
        PythonManagedTypeValue? managedType = null,
        TextSpan span = default
    ) =>
        managedType is null
            ? ManagedObjectProtocols.Fault("DPY4064", message, span, type)
            : new PythonRuntimeException("DPY4064", message, span, type)
            {
                ExceptionValue = new PythonExceptionValue(type, message)
                {
                    ManagedType = managedType,
                },
            };

    private static PythonRuntimeException TypeError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4064", message, span, "TypeError");

    private static PythonRuntimeException RuntimeError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4064", message, span, "RuntimeError");

    private static PythonManagedTypeValue CreateExceptionType(string name, string baseName)
    {
        var baseType = PythonBuiltinTypes.GetExceptionType(baseName);
        var type = new PythonManagedTypeValue(name, exceptionBaseName: baseName)
        {
            Module = "asyncio",
            LayoutBase = baseType,
        };
        type.Attributes["__module__"] = Text("asyncio");
        type.Attributes["__qualname__"] = Text(name);
        type.SetDeclaredBases(new PythonTupleValue([baseType]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, baseType, PythonBuiltinFunctions.Object])
        );
        return type;
    }

    /// <summary>Binds a small builtin signature: required slots must be supplied by the
    /// caller; keywords match the parameter names.</summary>
    private static PythonValue?[] Bind(
        string name,
        string[] parameters,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw TypeError(
                $"{name}() takes at most {parameters.Length} arguments "
                    + $"({positional.Count} given)",
                span
            );
        }
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
            {
                throw TypeError(
                    $"{name}() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            if (slots[slot] is not null)
            {
                throw TypeError(
                    $"{name}() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            }
            slots[slot] = keywordValues[index];
        }
        return slots;
    }
}
