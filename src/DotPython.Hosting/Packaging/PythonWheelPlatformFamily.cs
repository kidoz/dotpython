namespace DotPython.Hosting.Packaging;

/// <summary>Identifies the platform family encoded by a wheel platform tag.</summary>
public enum PythonWheelPlatformFamily
{
    /// <summary>The wheel is platform-independent.</summary>
    Any,

    /// <summary>Microsoft Windows.</summary>
    Windows,

    /// <summary>Apple macOS.</summary>
    MacOS,

    /// <summary>Linux using a manylinux/glibc compatibility baseline.</summary>
    Manylinux,

    /// <summary>Linux using a musllinux/musl compatibility baseline.</summary>
    Musllinux,

    /// <summary>Linux without a portable libc baseline.</summary>
    Linux,

    /// <summary>Android.</summary>
    Android,

    /// <summary>Apple iOS.</summary>
    IOS,

    /// <summary>An unrecognized platform family.</summary>
    Other,
}
