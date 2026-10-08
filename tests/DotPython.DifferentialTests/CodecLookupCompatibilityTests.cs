using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CodecLookupCompatibilityTests
{
    [Fact]
    public Task LookupResolvesAliasesToCanonicalNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            info = codecs.lookup("utf-8")
            print(type(info).__name__, type(info).__module__)
            print(info.name)
            print(codecs.CodecInfo is type(info))
            print(info._is_text_encoding)
            for name in ("utf8", "UTF-8", "Utf_8", "cp65001", "utf", "U8",
                         "latin-1", "latin1", "iso8859-1", "L1", "8859",
                         "ascii", "us-ascii", "646", "cp367",
                         "utf-16", "utf16", "utf-16-le", "utf-16-be", "unicodebigunmarked"):
                print(codecs.lookup(name).name)
            """
        );

    [Fact]
    public Task LookupReportsCPythonsArgumentAndNameErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            try:
                codecs.lookup("nope")
            except LookupError as error:
                print(type(error).__name__, error)
            try:
                codecs.lookup(5)
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.lookup()
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.lookup("a", "b")
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.lookup(encoding="utf-8")
            except TypeError as error:
                print(type(error).__name__, error)
            """
        );

    [Fact]
    public Task EncodersAndDecodersReportConsumedSourceUnits() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            print(codecs.lookup("utf-8").encode("abc"))
            print(codecs.lookup("utf-8").decode(b"abc"))
            print(codecs.getencoder("utf-8")("café"))
            print(codecs.getencoder("utf-8")("\U0001F600"))
            print(codecs.getencoder("utf-8")(""))
            print(codecs.getdecoder("utf-8")(b""))
            print(codecs.getdecoder("utf-16")(b"\xff\xfea\x00"))
            print(codecs.getdecoder("ascii")(b"\xff", "replace"))
            print(codecs.lookup("utf-16-le").encode("abc"))
            print(codecs.lookup("utf-16-be").encode("abc"))
            print(codecs.lookup("utf-16").encode("abc"))
            print(codecs.lookup("latin-1").encode("café"))
            print(codecs.getencoder("utf-8")("abc", "strict"))
            import json
            print(json.dumps(1e16), json.dumps([1e16, -1.5e16]))
            """
        );

    [Fact]
    public Task CodecCallablesRejectWrongOperands() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            try:
                codecs.getencoder("utf-8")(5)
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getencoder("utf-8")(None)
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getdecoder("utf-8")("abc")
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getdecoder("latin-1")(None)
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getencoder("ascii")("café", "nope")
            except LookupError as error:
                print(type(error).__name__, error)
            print(codecs.getencoder("utf-8")("abc", "nope"))
            """
        );

    [Fact]
    public Task GetEncoderAndGetDecoderUsePythonLevelArityErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            print(codecs.getencoder(encoding="utf-8")("abc"))
            try:
                codecs.getencoder()
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getdecoder()
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getencoder("utf-8", 1)
            except TypeError as error:
                print(type(error).__name__, error)
            try:
                codecs.getencoder(5)
            except TypeError as error:
                print(type(error).__name__, error)
            """
        );
}
