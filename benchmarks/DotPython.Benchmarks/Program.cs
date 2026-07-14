using BenchmarkDotNet.Running;

namespace DotPython.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args is ["--profile-opcode-pairs"])
        {
            OpcodePairProfileReporter.Write(Console.Out);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
