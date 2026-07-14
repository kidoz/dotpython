using System.Text;
using DotPython.ParserGenerator.Generation;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments is not [var command, var grammarPath, var outputPath])
    {
        Console.Error.WriteLine(
            "Usage: dotpython-parser-generator <generate|check> <grammar> <generated-output>"
        );
        return 2;
    }

    try
    {
        var grammar = File.ReadAllText(grammarPath);
        var generated = PythonParserSourceGenerator.Generate(grammar);
        return command switch
        {
            "generate" => WriteGeneratedOutput(outputPath, generated),
            "check" => CheckGeneratedOutput(outputPath, generated),
            _ => ReportUnknownCommand(command),
        };
    }
    catch (Exception exception)
        when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int WriteGeneratedOutput(string outputPath, string generated)
{
    if (
        File.Exists(outputPath)
        && string.Equals(File.ReadAllText(outputPath), generated, StringComparison.Ordinal)
    )
    {
        return 0;
    }

    var fullPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllText(
        fullPath,
        generated,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    );
    return 0;
}

static int CheckGeneratedOutput(string outputPath, string generated)
{
    if (
        File.Exists(outputPath)
        && string.Equals(File.ReadAllText(outputPath), generated, StringComparison.Ordinal)
    )
    {
        return 0;
    }

    Console.Error.WriteLine(
        $"Generated parser drift detected in '{outputPath}'. Run 'just parser-generate'."
    );
    return 1;
}

static int ReportUnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown parser-generator command '{command}'.");
    return 2;
}
