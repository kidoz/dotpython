namespace DotPython.Hosting.Packaging;

/// <summary>Describes one expanded wheel platform tag.</summary>
public sealed record PythonWheelPlatform
{
    /// <summary>Gets the original platform tag.</summary>
    public required string Tag { get; init; }

    /// <summary>Gets the recognized platform family.</summary>
    public required PythonWheelPlatformFamily Family { get; init; }

    /// <summary>Gets the architecture token, or <c>any</c> when platform-independent.</summary>
    public required string Architecture { get; init; }

    /// <summary>Gets the libc family when encoded by the tag.</summary>
    public string? Libc { get; init; }

    /// <summary>Gets the minimum platform or libc version when encoded by the tag.</summary>
    public Version? MinimumVersion { get; init; }
}
