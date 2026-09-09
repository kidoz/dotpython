using System.Collections.ObjectModel;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

public sealed class ManagedExecutionResult
{
    internal ManagedExecutionResult(
        SourceText source,
        IList<Diagnostic> diagnostics,
        int? exitCode = null,
        string? exitMessage = null
    )
    {
        Source = source;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
        ExitCode = exitCode;
        ExitMessage = exitMessage;
    }

    public SourceText Source { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// The process exit code requested by an uncaught `SystemExit` (`sys.exit`), or null
    /// when execution ended normally or with diagnostics.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>The message an uncaught `SystemExit` carried for the error stream, if any.</summary>
    public string? ExitMessage { get; }

    public bool Success => Diagnostics.Count == 0;
}
