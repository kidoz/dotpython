using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Compiler;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The builtins that compile source at run time — `compile`, `eval` and `exec` — and the
/// frame the two runners push for the code they are handed.
/// </summary>
internal sealed partial class PythonVirtualMachine
{
    private static readonly string[] ExecuteParameters = ["source", "globals", "locals"];

    /// <summary>Every code object compiled at run time, prepared once per definition.</summary>
    private readonly ConditionalWeakTable<PythonCodeObject, PreparedPythonCode> _compiledCodes =
        new();

    /// <summary>
    /// `compile(source, filename, mode, flags=0, dont_inherit=False, optimize=-1)`: parses
    /// the source the way a module is parsed and compiles it for `exec`, `eval`, or the
    /// single statement `single` allows.
    /// </summary>
    private PythonCodeValue CompileBuiltin(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
        {
            throw MissingCompileArgument("compile", "source", 1, span);
        }
        if (arguments.Count == 1)
        {
            throw MissingCompileArgument("compile", "filename", 2, span);
        }
        if (arguments.Count == 2)
        {
            throw MissingCompileArgument("compile", "mode", 3, span);
        }
        if (arguments.Count > 6)
        {
            throw Fault(
                "DPY4003",
                $"compile() takes at most 6 positional arguments ({arguments.Count} given)",
                span,
                "TypeError"
            );
        }
        if (arguments.Count > 3 && !IsDefaultFlags(arguments[3]))
        {
            throw Fault("DPY4003", "compile(): unrecognised flags", span, "ValueError");
        }
        if (OptimizeLevel(arguments.Count > 5 ? arguments[5] : null, span) < -1)
        {
            throw Fault("DPY4003", "compile(): invalid optimize value", span, "ValueError");
        }

        const string compileSource = "compile() arg 1 must be a string, bytes or AST object";
        var fileName = CompilationFileName(arguments[1], span);
        var mode = CompilationMode(arguments[2], span);
        var source = CompilationSource(arguments[0], compileSource, null, span);
        return CompileSourceText(source, fileName, mode, span);
    }

    /// <summary>
    /// `eval(source, /, globals=None, locals=None)`: evaluates the expression and answers
    /// its value, in the mappings it is given or in the caller's own.
    /// </summary>
    private PythonValue EvalBuiltin(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
        {
            throw Fault(
                "DPY4003",
                "eval() takes at least 1 positional argument (0 given)",
                span,
                "TypeError"
            );
        }
        if (arguments.Count > 3)
        {
            throw Fault(
                "DPY4003",
                $"eval() takes at most 3 arguments ({arguments.Count} given)",
                span,
                "TypeError"
            );
        }

        var code = arguments[0] is PythonCodeValue value
            ? value
            : CompileSourceText(
                CompilationSource(
                    arguments[0],
                    "eval() arg 1 must be a string, bytes or code object",
                    BuiltinSourceName,
                    span
                ),
                BuiltinSourceName,
                "eval",
                span
            );
        return RunCompiled(
            code,
            arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance,
            arguments.Count > 2 ? arguments[2] : PythonNoneValue.Instance,
            builtin: "eval",
            span
        );
    }

    /// <summary>
    /// `exec(source, /, globals=None, locals=None, *, closure=None)`: runs the source — a
    /// string, bytes or a code object — in the mappings it is given or in the caller's own.
    /// </summary>
    private PythonNoneValue ExecBuiltin(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // `closure` is keyword-only, so it comes out before the shared binder runs. No code
        // object this runtime produces expects free variables, so a closure is always too
        // much.
        var names = new List<string>(keywordNames.Count);
        var values = new List<PythonValue>(keywordValues.Count);
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] == "closure")
            {
                if (keywordValues[index] is not PythonNoneValue)
                {
                    // A closure needs a code object to bind to, and none this runtime
                    // compiles expects free variables — so it is always too much.
                    throw Fault(
                        "DPY4003",
                        arguments.Count > 0 && arguments[0] is not PythonCodeValue
                            ? "closure can only be used when source is a code object"
                            : "cannot use a closure with this code object",
                        span,
                        "TypeError"
                    );
                }
                continue;
            }
            names.Add(keywordNames[index]);
            values.Add(keywordValues[index]);
        }

        if (arguments.Count > 3)
        {
            throw Fault(
                "DPY4003",
                $"exec() takes at most 3 positional arguments ({arguments.Count} given)",
                span,
                "TypeError"
            );
        }

        var bound = PythonKeywordArguments.Bind(
            "exec",
            ExecuteParameters,
            positionalOnly: 1,
            arguments,
            names,
            values,
            span
        );
        if (bound[0] is null)
        {
            throw Fault(
                "DPY4003",
                "exec() takes at least 1 positional argument (0 given)",
                span,
                "TypeError"
            );
        }

        var source = bound[0]!;
        var code = source is PythonCodeValue value
            ? value
            : CompileSourceText(
                CompilationSource(
                    source,
                    "exec() arg 1 must be a string, bytes or code object",
                    BuiltinSourceName,
                    span
                ),
                BuiltinSourceName,
                "exec",
                span
            );
        RunCompiled(
            code,
            bound[1] ?? PythonNoneValue.Instance,
            bound[2] ?? PythonNoneValue.Instance,
            builtin: "exec",
            span
        );
        // `exec` runs the statements and answers None, whatever the code returned.
        return PythonNoneValue.Instance;
    }

    /// <summary>The name a source handed straight to `eval` or `exec` is compiled under.</summary>
    private const string BuiltinSourceName = "<string>";

    /// <summary>Whether the flags a compile was given are the ones the runtime implements.</summary>
    private static bool IsDefaultFlags(PythonValue value) =>
        value is PythonNoneValue
        || value is PythonTruthValue { Value: false }
        || value is PythonWholeNumberValue { Value.IsZero: true };

    private static int OptimizeLevel(PythonValue? value, TextSpan span) =>
        value switch
        {
            null or PythonNoneValue => -1,
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            ),
        };

    private static PythonRuntimeException MissingCompileArgument(
        string name,
        string parameter,
        int position,
        TextSpan span
    ) =>
        Fault(
            "DPY4003",
            $"{name}() missing required argument '{parameter}' (pos {position})",
            span,
            "TypeError"
        );

    /// <summary>
    /// The source `compile` was given: text as it stands, bytes through the decoder that
    /// honours the encoding a first or second line declares.
    /// </summary>
    private static SourceText CompilationSource(
        PythonValue value,
        string message,
        string? fileName,
        TextSpan span
    ) =>
        value switch
        {
            PythonTextValue text => new SourceText(text.Value, fileName),
            PythonByteSequenceValue bytes => PythonSourceDecoder.Decode(
                bytes.Value,
                fileName ?? "<string>",
                span
            ),
            _ => throw Fault("DPY4003", message, span, "TypeError"),
        };

    /// <summary>
    /// The name a compile reports, which is what the code object names and what a frame
    /// built from it reports. `os.PathLike` objects answer through `__fspath__`.
    /// </summary>
    private static string CompilationFileName(PythonValue value, TextSpan span)
    {
        switch (value)
        {
            case PythonTextValue text:
                return text.Value;
            case PythonByteSequenceValue bytes:
                return Encoding.UTF8.GetString(bytes.Value);
            case PythonNoneValue:
                throw Fault(
                    "DPY4003",
                    "expected str, bytes or os.PathLike object, not NoneType",
                    span,
                    "TypeError"
                );
        }

        if (ManagedObjectProtocols.TryGetSpecialMethod(value, "__fspath__", out var fileSystemPath))
        {
            var path = UserObjectProtocols.Dispatcher!.Invoke(fileSystemPath, [], span);
            if (path is PythonTextValue pathText)
            {
                return pathText.Value;
            }
            if (path is PythonByteSequenceValue pathBytes)
            {
                return Encoding.UTF8.GetString(pathBytes.Value);
            }
            throw Fault(
                "DPY4003",
                $"expected {ManagedObjectProtocols.GetTypeName(path)}.__fspath__() to return str or bytes, "
                    + $"not {ManagedObjectProtocols.GetTypeName(path)}",
                span,
                "TypeError"
            );
        }

        throw Fault(
            "DPY4003",
            $"expected str, bytes or os.PathLike object, not {ManagedObjectProtocols.GetTypeName(value)}",
            span,
            "TypeError"
        );
    }

    private static string CompilationMode(PythonValue value, TextSpan span)
    {
        if (value is not PythonTextValue mode)
        {
            throw Fault(
                "DPY4003",
                $"compile() argument 'mode' must be str, not {ManagedObjectProtocols.GetTypeName(value)}",
                span,
                "TypeError"
            );
        }
        if (mode.Value is not ("exec" or "eval" or "single"))
        {
            throw Fault(
                "DPY4003",
                "compile() mode must be 'exec', 'eval' or 'single'",
                span,
                "ValueError"
            );
        }
        return mode.Value;
    }

    /// <summary>
    /// Parses and compiles the source for one of the three modes. The statement forms
    /// compile the whole module; `eval` compiles the single expression's value as the
    /// code's return value, and `single` is the one statement an interactive prompt takes.
    /// </summary>
    private static PythonCodeValue CompileSourceText(
        SourceText source,
        string fileName,
        string mode,
        TextSpan span
    )
    {
        // The whitespace an expression opens with is not indentation, so an eval parse
        // reads the first line without it. Every later line is a module's, as CPython's
        // own eval mode reads it.
        if (mode == "eval")
        {
            source = WithoutLeadingWhitespace(source);
        }

        var parse = PythonParser.Parse(source);
        if (!parse.Success)
        {
            throw SyntaxFault(parse.Diagnostics[0], fileName, source, span);
        }

        if (mode == "single")
        {
            if (parse.Module.Statements.Count > 1)
            {
                throw SyntaxErrorFault(
                    "multiple statements found while compiling a single statement",
                    fileName,
                    LineOf(source, parse.Module.Statements[0].Span.Start),
                    source,
                    span
                );
            }
            if (parse.Module.Statements.Count == 0)
            {
                throw InvalidExpressionFault(parse.Module, source, fileName, span);
            }

            // The one statement runs as the statement it is. Displaying an expression
            // statement's value, which a prompt does, is not part of this runtime.
            return CompileModule(parse.Module, fileName, source, span);
        }

        if (mode == "eval")
        {
            if (parse.Module.Statements is not [PythonExpressionStatement expression])
            {
                throw InvalidExpressionFault(parse.Module, source, fileName, span);
            }

            var compilation = PythonCompiler.CompileExpression(
                parse.Module,
                expression.Expression,
                "<module>"
            );
            if (!compilation.Success)
            {
                throw SyntaxFault(compilation.Diagnostics[0], fileName, source, span);
            }
            PythonSourceLocations.RegisterCode(compilation.Code, fileName, source);
            return new PythonCodeValue(compilation.Code, fileName);
        }

        return CompileModule(parse.Module, fileName, source, span);
    }

    /// <summary>
    /// The source without the whitespace its first line opens with, which is the run an
    /// expression may carry and a statement may not.
    /// </summary>
    private static SourceText WithoutLeadingWhitespace(SourceText source)
    {
        var content = source.Content;
        var start = 0;
        while (start < content.Length && content[start] is ' ' or '\t' or '\f')
        {
            start++;
        }
        return start == 0 ? source : new SourceText(content[start..], source.FilePath);
    }

    private static PythonCodeValue CompileModule(
        PythonModule module,
        string fileName,
        SourceText source,
        TextSpan span
    )
    {
        var compilation = PythonCompiler.Compile(module, "<module>");
        if (!compilation.Success)
        {
            throw SyntaxFault(compilation.Diagnostics[0], fileName, source, span);
        }
        PythonSourceLocations.RegisterCode(compilation.Code, fileName, source);
        return new PythonCodeValue(compilation.Code, fileName);
    }

    /// <summary>
    /// The refusal an `eval` earns from a source that is not one expression: CPython
    /// reports it as invalid syntax at the line it gave up on, which is line 0 for an
    /// empty one.
    /// </summary>
    private static PythonRuntimeException InvalidExpressionFault(
        PythonModule module,
        SourceText source,
        string fileName,
        TextSpan span
    ) =>
        SyntaxErrorFault(
            "invalid syntax",
            fileName,
            module.Statements.Count == 0 ? 0 : LineOf(source, module.Statements[0].Span.Start),
            source,
            span
        );

    /// <summary>
    /// The refusals the compile modes themselves make — an eval that is not one expression,
    /// a single-statement compile that is more than one — as the `SyntaxError` CPython
    /// raises for them, in its wording and with its members.
    /// </summary>
    private static PythonRuntimeException SyntaxErrorFault(
        string message,
        string fileName,
        int line,
        SourceText source,
        TextSpan span
    )
    {
        var value = new PythonExceptionValue("SyntaxError", message)
        {
            SyntaxErrorSlots = new PythonSyntaxErrorSlots(
                new PythonTextValue(message),
                new PythonTextValue(fileName),
                PythonWholeNumberValue.Create(line),
                null,
                line >= 1 ? new PythonTextValue(LineText(source, line - 1)) : null,
                null,
                null,
                null
            ),
        };
        value.Message = ManagedObjectProtocols.ComposeSyntaxErrorMessage(value.SyntaxErrorSlots!);
        return new PythonRuntimeException("DPY4021", value.Message, span, "SyntaxError")
        {
            ExceptionValue = value,
        };
    }

    private static int LineOf(SourceText source, int position) =>
        source.GetLinePosition(position).Line + 1;

    /// <summary>
    /// A parse failure as the exception it names: an inconsistent mix of tabs and spaces is
    /// a `TabError` and the indentation failures an `IndentationError`, with everything
    /// else a plain `SyntaxError`. The parser reports both indentation failures under its
    /// own codes and wording, so the wording tells the two apart.
    /// </summary>
    internal static string SyntaxErrorTypeName(Diagnostic diagnostic) =>
        diagnostic.Code switch
        {
            "DPY1003" => "TabError",
            "DPY2006" => "IndentationError",
            _ when diagnostic.Message == "Expected an indented suite." => "IndentationError",
            _ => "SyntaxError",
        };

    private static PythonRuntimeException SyntaxFault(
        Diagnostic diagnostic,
        string fileName,
        SourceText source,
        TextSpan span
    )
    {
        var typeName = SyntaxErrorTypeName(diagnostic);
        var value = SyntaxErrorValue(
            typeName,
            diagnostic.Message,
            fileName,
            diagnostic.Span,
            source
        );
        return new PythonRuntimeException(diagnostic.Code, value.Message, span, typeName)
        {
            ExceptionValue = value,
        };
    }

    /// <summary>
    /// A `SyntaxError` carrying the members CPython fills in: the message, the file, the
    /// line, the column, the line's own text and the end position the failure spans.
    /// </summary>
    internal static PythonExceptionValue SyntaxErrorValue(
        string typeName,
        string message,
        string fileName,
        TextSpan failure,
        SourceText source
    )
    {
        var start = source.GetLinePosition(failure.Start);
        var end = source.GetLinePosition(failure.End);
        var value = new PythonExceptionValue(typeName, message)
        {
            SyntaxErrorSlots = new PythonSyntaxErrorSlots(
                new PythonTextValue(message),
                new PythonTextValue(fileName),
                PythonWholeNumberValue.Create(start.Line + 1),
                PythonWholeNumberValue.Create(start.Character + 1),
                new PythonTextValue(LineText(source, start.Line)),
                PythonWholeNumberValue.Create(end.Line + 1),
                PythonWholeNumberValue.Create(end.Character + 1),
                null
            ),
        };
        value.Message = ManagedObjectProtocols.ComposeSyntaxErrorMessage(value.SyntaxErrorSlots!);
        return value;
    }

    /// <summary>The text of a line, its newline included, as CPython's `text` carries it.</summary>
    internal static string LineText(SourceText source, int line) =>
        line >= 0 && line < source.LineCount
            ? source.GetText(source.GetLineSpan(line))
            : string.Empty;

    /// <summary>
    /// Runs a compiled code object in a frame of its own: the mappings the call named — the
    /// caller's when it named none — with `__builtins__` put where the frame resolves it,
    /// and the two mappings separately for the class-body rule on name lookups.
    /// </summary>
    private PythonValue RunCompiled(
        PythonCodeValue code,
        PythonValue globalsValue,
        PythonValue localsValue,
        string builtin,
        TextSpan span
    )
    {
        var globals = RequireGlobals(globalsValue, builtin, span);
        if (
            code.Definition.Name != "<module>"
            || code.Definition.ArgumentCount != 0
            || code.Definition.IsSuspendable
        )
        {
            // Only the code objects the modes compile — a module body or a single
            // expression — are shapes this runtime can run as a statement body.
            throw Fault(
                "DPY4003",
                $"{builtin}() can only run a code object compiled in 'exec', 'eval' or 'single' mode",
                span,
                "TypeError"
            );
        }

        var locals = LocalNamespace(globalsValue, localsValue, globals, span);
        if (!PythonNamespaceMapping.TryGet(globals, "__builtins__", span, out _))
        {
            PythonNamespaceMapping.Set(
                globals,
                "__builtins__",
                _modules.ResolveAbsolute("builtins", span).Module.Globals.Dictionary,
                span
            );
        }

        var prepared = _compiledCodes.GetValue(code.Definition, PreparedPythonCode.Create);
        var baseFrameCount = _frameCount;
        PushFrame(
            prepared,
            new PythonGlobalNamespace(globals),
            _localsCount,
            0,
            NoCells,
            classNamespace: locals,
            isModule: true
        );
        RunFramesAbove(baseFrameCount);
        return Pop(span);
    }

    /// <summary>
    /// The mapping the statements of a run are bound in: the caller's globals when the call
    /// named none, and the caller's own locals when it named neither.
    /// </summary>
    private PythonDictionaryValue RequireGlobals(
        PythonValue value,
        string builtin,
        TextSpan span
    ) =>
        value switch
        {
            PythonNoneValue => CurrentFrame.Globals.Dictionary,
            PythonDictionaryValue dictionary => dictionary,
            _ when builtin == "exec" => throw Fault(
                "DPY4003",
                $"exec() globals must be a dict, not {ManagedObjectProtocols.GetTypeName(value)}",
                span,
                "TypeError"
            ),
            _ => throw Fault(
                "DPY4003",
                "globals must be a real dict; try eval(expr, {}, mapping)",
                span,
                "TypeError"
            ),
        };

    /// <summary>
    /// The namespace name lookups and stores reach when they are not the globals: the
    /// locals a call named, or the caller's own when it named none. One mapping in two
    /// roles leaves the stores with the globals.
    /// </summary>
    private PythonValue? LocalNamespace(
        PythonValue globalsValue,
        PythonValue localsValue,
        PythonDictionaryValue globals,
        TextSpan span
    )
    {
        // The locals default to the globals the call named, and only a call that named
        // neither takes the caller's own.
        var locals =
            localsValue is not PythonNoneValue ? localsValue
            : globalsValue is not PythonNoneValue ? globals
            : CurrentFrameLocals(span);
        return ReferenceEquals(locals, globals) ? null : locals;
    }
}
