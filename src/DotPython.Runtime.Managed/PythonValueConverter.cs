using System.Numerics;
using DotPython.Contracts;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Runtime.Managed;

internal static class PythonValueConverter
{
    internal static PythonValue FromClr(
        object? value,
        PythonTypeContract contract,
        string moduleName,
        string functionName
    )
    {
        if (value is null)
        {
            if (contract.IsNullable)
            {
                return PythonNoneValue.Instance;
            }

            throw ConversionFailure(contract, moduleName, functionName, "Null is not allowed");
        }

        return (contract.ClrTypeName, value) switch
        {
            ("System.Boolean", bool item) => PythonTruthValue.FromBoolean(item),
            ("System.Numerics.BigInteger", BigInteger item) => PythonWholeNumberValue.Create(item),
            ("System.Double", double item) => new PythonFloatingPointValue(item),
            ("System.String", string item) => new PythonTextValue(item),
            ("System.Byte[]", byte[] item) => new PythonByteSequenceValue((byte[])item.Clone()),
            _ => throw ConversionFailure(
                contract,
                moduleName,
                functionName,
                $"CLR value type '{value.GetType().FullName}' is not supported"
            ),
        };
    }

    internal static TResult ToClr<TResult>(
        PythonValue value,
        PythonTypeContract contract,
        string moduleName,
        string functionName
    )
    {
        if (!ResultTypeMatches<TResult>(contract))
        {
            throw ConversionFailure(
                contract,
                moduleName,
                functionName,
                $"Requested CLR result type '{typeof(TResult).FullName}' does not match the contract"
            );
        }

        object? result = (contract.ClrTypeName, value) switch
        {
            (_, PythonNoneValue) when contract.IsNullable => null,
            ("System.Boolean", PythonTruthValue item) => item.Value,
            ("System.Numerics.BigInteger", PythonWholeNumberValue item) => item.Value,
            ("System.Double", PythonFloatingPointValue item) => item.Value,
            ("System.String", PythonTextValue item) => item.Value,
            ("System.Byte[]", PythonByteSequenceValue item) => (byte[])item.Value.Clone(),
            _ => throw ConversionFailure(
                contract,
                moduleName,
                functionName,
                $"Python value '{value.GetType().Name}' is not compatible"
            ),
        };

        if (result is null)
        {
            return default!;
        }

        if (result is TResult typed)
        {
            return typed;
        }

        throw ConversionFailure(
            contract,
            moduleName,
            functionName,
            $"Requested CLR result type '{typeof(TResult).FullName}' does not match the contract"
        );
    }

    private static bool ResultTypeMatches<TResult>(PythonTypeContract contract)
    {
        var requestedType = typeof(TResult);
        var nullableType = Nullable.GetUnderlyingType(requestedType);
        var comparisonType = nullableType ?? requestedType;
        if (!string.Equals(comparisonType.FullName, contract.ClrTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        return !contract.IsNullable || !contract.IsValueType || nullableType is not null;
    }

    private static DotPythonException ConversionFailure(
        PythonTypeContract contract,
        string moduleName,
        string functionName,
        string reason
    ) =>
        new(
            "DPY6007",
            $"{reason} for contract type '{contract.CSharpTypeName}'.",
            DotPythonFailurePhase.Conversion,
            moduleName,
            functionName
        );
}
