using System.Collections.ObjectModel;

namespace DotPython.Contracts;

/// <summary>Describes a statically exported Python function.</summary>
public sealed class PythonFunctionContract
{
    /// <summary>Initializes an exported Python function.</summary>
    public PythonFunctionContract(
        string pythonName,
        string clrName,
        PythonCallShape callShape,
        IEnumerable<PythonParameterContract> parameters,
        PythonTypeContract returnType
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrName);
        if (!ContractNameValidator.IsIdentifier(pythonName))
        {
            throw new ArgumentException(
                "The Python function name must be a portable identifier.",
                nameof(pythonName)
            );
        }

        if (!ContractNameValidator.IsIdentifier(clrName))
        {
            throw new ArgumentException(
                "The CLR member name must be a portable identifier.",
                nameof(clrName)
            );
        }

        if (!Enum.IsDefined(callShape) || callShape == PythonCallShape.None)
        {
            throw new ArgumentOutOfRangeException(nameof(callShape));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(returnType);
        var parameterList = parameters.ToList();
        ValidateParameters(parameterList);

        PythonName = pythonName;
        ClrName = clrName;
        CallShape = callShape;
        Parameters = new ReadOnlyCollection<PythonParameterContract>(parameterList);
        ReturnType = returnType;
    }

    /// <summary>Gets the callable name in Python.</summary>
    public string PythonName { get; }

    /// <summary>Gets the stable generated CLR member name.</summary>
    public string ClrName { get; }

    /// <summary>Gets whether the Python function is synchronous or asynchronous.</summary>
    public PythonCallShape CallShape { get; }

    /// <summary>Gets the parameters in declaration order.</summary>
    public IReadOnlyList<PythonParameterContract> Parameters { get; }

    /// <summary>Gets the function's mapped return type.</summary>
    public PythonTypeContract ReturnType { get; }

    private static void ValidateParameters(IList<PythonParameterContract> parameters)
    {
        var pythonNames = new HashSet<string>(StringComparer.Ordinal);
        var clrNames = new HashSet<string>(StringComparer.Ordinal);
        var foundDefault = false;
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            if (!pythonNames.Add(parameter.PythonName) || !clrNames.Add(parameter.ClrName))
            {
                throw new ArgumentException(
                    "Function parameter names must be unique.",
                    nameof(parameters)
                );
            }

            if (!parameter.HasDefault && foundDefault)
            {
                throw new ArgumentException(
                    "A required parameter cannot follow a parameter with a default.",
                    nameof(parameters)
                );
            }

            foundDefault |= parameter.HasDefault;
        }
    }
}
