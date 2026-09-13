using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnicodeNameDataTests
{
    // Independent CPython 3.14.7 / UCD 16.0 oracle: for each named codepoint,
    // hash uint32 little endian, ASCII name, NUL in ascending codepoint order.
    // The codec mode also includes CPython's internal alias/sequence entries.
    [Theory]
    [InlineData(false, 148853, "39e28ccf137c629e7170a32f6ec26a64f8c29b675df6186549a6b6d97a6df205")]
    [InlineData(true, 149791, "c1052b79ac37dcc19b5e372bfe52b5961f1ce3c8eaebf6cfdfcd23d0448dbc9f")]
    public void CharacterNamesMatchTheCompleteReferenceInventory(
        bool includeAliasesAndSequences,
        int expectedCount,
        string expectedDigest
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> scalar = stackalloc byte[4];
        var count = 0;
        for (var codePoint = 0; codePoint <= 0x10ffff; ++codePoint)
        {
            if ((codePoint & 1023) == 0)
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var name = PythonUnicodeNames.GetName(codePoint, includeAliasesAndSequences);
            if (name is null)
                continue;
            BinaryPrimitives.WriteInt32LittleEndian(scalar, codePoint);
            hash.AppendData(scalar);
            hash.AppendData(Encoding.ASCII.GetBytes(name));
            hash.AppendData("\0"u8);
            ++count;
        }
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedDigest, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.Null(PythonUnicodeNames.GetName(-1, includeAliasesAndSequences));
        Assert.Null(PythonUnicodeNames.GetName(0x110000, includeAliasesAndSequences));
    }
}
