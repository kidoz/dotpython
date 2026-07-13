using DotPython.Compiler;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

public sealed class ManagedPythonEngine
{
    private readonly object _executionGate = new();
    private readonly Dictionary<string, PythonValue> _globals = new(StringComparer.Ordinal);

    public ManagedExecutionResult Execute(
        string code,
        string fileName,
        TextWriter output,
        ManagedExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(output);

        return Execute(new SourceText(code, fileName), output, options, cancellationToken);
    }

    public ManagedExecutionResult Execute(
        SourceText source,
        TextWriter output,
        ManagedExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);

        options ??= new ManagedExecutionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InstructionLimit);

        lock (_executionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parseResult = PythonParser.Parse(source);
            if (!parseResult.Success)
            {
                return new ManagedExecutionResult(source, [.. parseResult.Diagnostics]);
            }

            var compilation = PythonCompiler.Compile(
                parseResult.Module,
                source.FilePath ?? "<module>"
            );
            if (!compilation.Success)
            {
                return new ManagedExecutionResult(source, [.. compilation.Diagnostics]);
            }

            try
            {
                var virtualMachine = new PythonVirtualMachine(
                    _globals,
                    output,
                    options.InstructionLimit,
                    cancellationToken
                );
                virtualMachine.Execute(compilation.Code);
                return new ManagedExecutionResult(source, []);
            }
            catch (PythonRuntimeException fault)
            {
                return new ManagedExecutionResult(
                    source,
                    [
                        new Diagnostic(
                            fault.Code,
                            fault.Message,
                            DiagnosticSeverity.Error,
                            fault.Span
                        ),
                    ]
                );
            }
        }
    }
}
