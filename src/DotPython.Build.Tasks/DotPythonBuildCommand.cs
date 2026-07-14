using DotPython.Contracts;

namespace DotPython.Build.Tasks;

internal static class DotPythonBuildCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        try
        {
            var options = Parse(arguments);
            return DotPythonModuleBuilder.Build(options) ? 0 : 1;
        }
        catch (BuildUsageException exception)
        {
            Console.Error.WriteLine($"error DPY7001: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error DPY7005: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"error DPY7005: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"error DPY7005: {exception.Message}");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"error DPY7005: {exception.Message}");
            return 1;
        }
    }

    private static DotPythonModuleBuildOptions Parse(IReadOnlyList<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (
                index + 1 >= arguments.Count
                || !arguments[index].StartsWith("--", StringComparison.Ordinal)
            )
            {
                throw new BuildUsageException("Build-tool arguments must be '--name value' pairs.");
            }

            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                throw new BuildUsageException($"Duplicate build-tool option '{arguments[index]}'.");
            }
        }

        var statePolicy = GetOptional(values, "--state-policy") ?? "PerRuntime";
        if (
            !Enum.TryParse<PythonModuleStatePolicy>(
                statePolicy,
                ignoreCase: false,
                out var parsedPolicy
            )
        )
        {
            throw new BuildUsageException($"Unknown module state policy '{statePolicy}'.");
        }

        return new DotPythonModuleBuildOptions(
            GetRequired(values, "--source"),
            GetRequired(values, "--contract"),
            GetRequired(values, "--module-name"),
            GetRequired(values, "--clr-namespace"),
            GetRequired(values, "--clr-type-name"),
            parsedPolicy,
            GetRequired(values, "--artifact-output"),
            GetRequired(values, "--contract-output"),
            GetRequired(values, "--facade-output"),
            GetRequired(values, "--artifact-resource-name")
        );
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string name) =>
        GetOptional(values, name) is { Length: > 0 } value
            ? value
            : throw new BuildUsageException(
                $"Required build-tool option '{name}' was not supplied."
            );

    private static string? GetOptional(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private sealed class BuildUsageException : Exception
    {
        public BuildUsageException() { }

        public BuildUsageException(string message)
            : base(message) { }

        public BuildUsageException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
