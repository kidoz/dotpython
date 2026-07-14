using DotPython.Contracts;
using DotPython.Interop.Contracts;
using DotPython.Language.Text;
using Xunit;

namespace DotPython.InteropTests;

public sealed class PythonStubContractParserTests
{
    [Fact]
    public void Parse_MapsTypedSynchronousAndAsynchronousExports()
    {
        const string stub =
            "from decimal import Decimal\n"
            + "from contracts import OrderDto\n"
            + "from typing import Optional\n"
            + "def calculate(order: OrderDto, discount: Optional[Decimal] = ...) -> Decimal: ...\n"
            + "async def validate(order: OrderDto) -> list[str]:\n"
            + "    ...\n";

        var result = Parse(
            stub,
            [new PythonExternalTypeMapping("contracts.OrderDto", "Pricing.Contracts.OrderDto")]
        );

        Assert.True(result.Success);
        var contract = Assert.IsType<PythonModuleContract>(result.Contract);
        Assert.Equal(DotPythonContractFormat.CurrentVersion, contract.FormatVersion);
        Assert.Equal("pricing", contract.ModuleName);
        Assert.Equal(PythonModuleStatePolicy.PerRuntime, contract.StatePolicy);
        Assert.Equal(2, contract.Functions.Count);

        var calculate = contract.Functions[0];
        Assert.Equal("calculate", calculate.PythonName);
        Assert.Equal("CalculateAsync", calculate.ClrName);
        Assert.Equal(PythonCallShape.Synchronous, calculate.CallShape);
        Assert.Equal("Pricing.Contracts.OrderDto", calculate.Parameters[0].Type.CSharpTypeName);
        Assert.Equal(
            "System.Nullable<System.Decimal>",
            calculate.Parameters[1].Type.CSharpTypeName
        );
        Assert.True(calculate.Parameters[1].HasDefault);
        Assert.Equal("System.Decimal", calculate.ReturnType.CSharpTypeName);

        var validate = contract.Functions[1];
        Assert.Equal(PythonCallShape.Asynchronous, validate.CallShape);
        Assert.Equal(
            "System.Collections.Generic.IReadOnlyList<System.String>",
            validate.ReturnType.CSharpTypeName
        );
    }

    [Fact]
    public void Parse_ResolvesAliasedImportsAndNullableUnion()
    {
        const string stub =
            "import datetime as dt\n"
            + "from uuid import UUID as Identifier\n"
            + "def find(identifier: Identifier) -> dt.datetime | None: ...\n";

        var result = Parse(stub);

        var function = Assert.Single(
            Assert.IsType<PythonModuleContract>(result.Contract).Functions
        );
        Assert.Equal("System.Guid", function.Parameters[0].Type.CSharpTypeName);
        Assert.Equal("System.Nullable<System.DateTimeOffset>", function.ReturnType.CSharpTypeName);
    }

    [Fact]
    public void Parse_UsesPythonRangePreservingAndClsOrientedBuiltins()
    {
        var result = Parse(
            "def convert(value: int, payload: bytes, names: list[str]) -> bool: ...\n"
        );

        var function = Assert.Single(
            Assert.IsType<PythonModuleContract>(result.Contract).Functions
        );
        Assert.Equal("System.Numerics.BigInteger", function.Parameters[0].Type.CSharpTypeName);
        Assert.Equal("System.Byte[]", function.Parameters[1].Type.CSharpTypeName);
        Assert.Equal(
            "System.Collections.Generic.IReadOnlyList<System.String>",
            function.Parameters[2].Type.CSharpTypeName
        );
        Assert.All(function.Parameters, parameter => Assert.True(parameter.Type.IsClsCompliant));
    }

    [Fact]
    public void Parse_IgnoresPrivateDeclarationsAndSortsPublicExports()
    {
        const string stub =
            "def zebra() -> None: ...\n"
            + "def _implementation_detail() -> int: ...\n"
            + "def alpha() -> None: ...\n";

        var result = Parse(stub);

        Assert.Equal(
            ["alpha", "zebra"],
            Assert
                .IsType<PythonModuleContract>(result.Contract)
                .Functions.Select(function => function.PythonName)
        );
    }

    [Theory]
    [InlineData(
        "def missing(value) -> int: ...\n",
        PythonContractDiagnosticCodes.MissingAnnotation
    )]
    [InlineData("def missing(value: int): ...\n", PythonContractDiagnosticCodes.MissingAnnotation)]
    [InlineData("def union() -> int | str: ...\n", PythonContractDiagnosticCodes.UnsupportedType)]
    [InlineData(
        "def unknown(value: UnknownDto) -> None: ...\n",
        PythonContractDiagnosticCodes.UnresolvedExternalType
    )]
    [InlineData(
        "def variadic(*values: int) -> None: ...\n",
        PythonContractDiagnosticCodes.UnsupportedDeclaration
    )]
    [InlineData(
        "def executable() -> int: return 42\n",
        PythonContractDiagnosticCodes.UnsupportedDeclaration
    )]
    public void Parse_ReportsStableDiagnosticsForInvalidContracts(string stub, string code)
    {
        var result = Parse(stub);

        Assert.False(result.Success);
        Assert.Null(result.Contract);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Parse_RejectsClrNameCollisionsAndNonClsExternalTypes()
    {
        const string collision = "def get_value() -> int: ...\n" + "def getValue() -> int: ...\n";
        const string nonCls =
            "from contracts import UnsignedValue\n" + "def read() -> UnsignedValue: ...\n";

        var collisionResult = Parse(collision);
        var nonClsResult = Parse(
            nonCls,
            [
                new PythonExternalTypeMapping(
                    "contracts.UnsignedValue",
                    "System.UInt32",
                    isValueType: true,
                    isClsCompliant: false
                ),
            ]
        );

        Assert.Contains(
            collisionResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonContractDiagnosticCodes.DuplicateExport
        );
        Assert.Contains(
            nonClsResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonContractDiagnosticCodes.InvalidClrSurface
        );
    }

    [Fact]
    public void ContractJson_IsCanonicalAndRoundTrips()
    {
        var contract = Assert.IsType<PythonModuleContract>(
            Parse("def answer() -> int: ...\n").Contract
        );

        var json = PythonModuleContractJson.Serialize(contract);
        var restored = PythonModuleContractJson.Deserialize(json);

        Assert.Equal(
            "{\"formatVersion\":1,\"moduleName\":\"pricing\",\"clrNamespace\":\"PricingRules\","
                + "\"clrTypeName\":\"PricingModule\",\"statePolicy\":\"perRuntime\",\"functions\":[{"
                + "\"pythonName\":\"answer\",\"clrName\":\"AnswerAsync\",\"callShape\":\"synchronous\","
                + "\"parameters\":[],\"returnType\":{\"pythonName\":\"int\","
                + "\"clrTypeName\":\"System.Numerics.BigInteger\",\"nullable\":false,"
                + "\"valueType\":true,\"clsCompliant\":true,\"typeArguments\":[]}}]}",
            json
        );
        Assert.Equal(json, PythonModuleContractJson.Serialize(restored));
    }

    [Fact]
    public void ContractJson_RejectsUnsupportedFormat()
    {
        const string json =
            "{\"formatVersion\":2,\"moduleName\":\"pricing\",\"clrNamespace\":\"PricingRules\","
            + "\"clrTypeName\":\"PricingModule\",\"statePolicy\":\"perRuntime\",\"functions\":[]}";

        Assert.Throws<InvalidDataException>(() => PythonModuleContractJson.Deserialize(json));
    }

    [Fact]
    public void ContractJson_RejectsUnsafeClrTypeNames()
    {
        var contract = Assert.IsType<PythonModuleContract>(
            Parse("def answer() -> int: ...\n").Contract
        );
        var json = PythonModuleContractJson
            .Serialize(contract)
            .Replace(
                "System.Numerics.BigInteger",
                "System.String;System.Console.WriteLine",
                StringComparison.Ordinal
            );

        Assert.Throws<InvalidDataException>(() => PythonModuleContractJson.Deserialize(json));
    }

    [Fact]
    public void NameConverter_EscapesCSharpKeywordsDeterministically()
    {
        Assert.Equal(
            "CalculateTotalAsync",
            PythonContractNameConverter.ToClrMemberName("calculate_total")
        );
        Assert.Equal("@event", PythonContractNameConverter.ToClrParameterName("event"));
    }

    private static PythonContractCompilationResult Parse(
        string stub,
        IReadOnlyList<PythonExternalTypeMapping>? mappings = null
    ) =>
        PythonStubContractParser.Parse(
            new SourceText(stub, "pricing.pyi"),
            new PythonStubContractOptions
            {
                ModuleName = "pricing",
                ClrNamespace = "PricingRules",
                ClrTypeName = "PricingModule",
                ExternalTypeMappings = mappings ?? [],
            }
        );
}
