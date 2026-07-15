namespace DotPython.Hosting.Packaging;

/// <summary>Identifies a native binary container found inside a wheel.</summary>
public enum PythonNativeBinaryFormat
{
    /// <summary>The binary container is not recognized.</summary>
    Unknown,

    /// <summary>Executable and Linkable Format, normally used on Linux.</summary>
    Elf,

    /// <summary>Mach-O, normally used on macOS or iOS.</summary>
    MachO,

    /// <summary>Portable Executable, normally used on Windows.</summary>
    PortableExecutable,
}
