namespace DotPython.Hosting.Packaging;

/// <summary>Controls bounded, read-only wheel inspection.</summary>
public sealed record PythonWheelInspectionOptions
{
    /// <summary>Gets the default bounded inspection policy.</summary>
    public static PythonWheelInspectionOptions Default { get; } = new();

    /// <summary>Gets the maximum wheel file size accepted for inspection.</summary>
    public long MaximumWheelBytes { get; init; } = 1L << 30;

    /// <summary>Gets the maximum number of ZIP entries inspected.</summary>
    public int MaximumEntryCount { get; init; } = 100_000;

    /// <summary>Gets the maximum total uncompressed size advertised by the archive.</summary>
    public long MaximumUncompressedBytes { get; init; } = 4L << 30;

    /// <summary>Gets the maximum size of one metadata file read by the inspector.</summary>
    public int MaximumMetadataBytes { get; init; } = 1 << 20;

    /// <summary>Gets the maximum size of one native binary read for symbol inspection.</summary>
    public int MaximumNativeBinaryBytes { get; init; } = 256 << 20;
}
