namespace DotPython.Hosting.Packaging;

/// <summary>Represents one expanded wheel compatibility tag.</summary>
public sealed record PythonWheelTag
{
    /// <summary>Gets the language implementation and version tag.</summary>
    public required string Python { get; init; }

    /// <summary>Gets the ABI tag.</summary>
    public required string Abi { get; init; }

    /// <summary>Gets the platform tag.</summary>
    public required string Platform { get; init; }

    /// <summary>Gets whether the tag selects a free-threaded CPython ABI.</summary>
    public required bool IsFreeThreaded { get; init; }

    /// <summary>Gets the normalized combined tag.</summary>
    public string Value => $"{Python}-{Abi}-{Platform}";
}
