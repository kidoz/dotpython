using System.Diagnostics.CodeAnalysis;

namespace DotPython.Worker.App;

internal static class Program
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The executable boundary reports a bounded startup failure on stderr."
    )]
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Await-using declarations dispose process standard streams at the executable boundary."
    )]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = WorkerHostOptions.Parse(args);
            await using var host = new WorkerHost(options);
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            await host.RunAsync(input, output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
