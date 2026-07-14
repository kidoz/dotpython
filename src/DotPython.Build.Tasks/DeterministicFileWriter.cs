namespace DotPython.Build.Tasks;

internal static class DeterministicFileWriter
{
    internal static void Write(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(content))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
