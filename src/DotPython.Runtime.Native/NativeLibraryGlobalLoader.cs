using System.Runtime.InteropServices;

namespace DotPython.Runtime.Native;

internal static class NativeLibraryGlobalLoader
{
    private const int RtldNow = 0x2;
    private static readonly nint LoaderLibrary = LoadLoaderLibrary();

    internal static nint Load(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return NativeLibrary.Load(path);
        }

        var rtldGlobal = OperatingSystem.IsMacOS() ? 0x8 : 0x100;
        var handle = GetDelegate<Dlopen>("dlopen")(path, RtldNow | rtldGlobal);
        if (handle == 0)
        {
            throw new DllNotFoundException(
                Marshal.PtrToStringUTF8(GetDelegate<Dlerror>("dlerror")())
                    ?? $"Unable to load native library '{path}'."
            );
        }

        return handle;
    }

    internal static void Free(nint handle)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeLibrary.Free(handle);
            return;
        }

        if (GetDelegate<Dlclose>("dlclose")(handle) != 0)
        {
            throw new InvalidOperationException(
                Marshal.PtrToStringUTF8(GetDelegate<Dlerror>("dlerror")())
                    ?? "Unable to close native library."
            );
        }
    }

    private static nint LoadLoaderLibrary()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        return NativeLibrary.Load(
            OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libdl.so.2"
        );
    }

    private static T GetDelegate<T>(string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(LoaderLibrary, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Dlclose(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Dlerror();
}
