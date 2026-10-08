using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CodecLookupExecutionTests
{
    [Fact]
    public void LookupResolvesAliasesToCanonicalNames()
    {
        var output = Run(
            """
            import codecs
            info = codecs.lookup("utf-8")
            print(type(info).__name__, type(info).__module__)
            print(info.name)
            print(codecs.CodecInfo is type(info))
            print(info._is_text_encoding)
            for name in ("utf8", "UTF-8", "Utf_8", "cp65001", "latin-1", "L1", "8859",
                         "ascii", "us-ascii", "646", "utf-16", "utf-16-le", "utf-16-be",
                         "unicodebigunmarked"):
                print(codecs.lookup(name).name)
            """
        );

        Assert.Equal(
            Lines(
                "CodecInfo codecs",
                "utf-8",
                "True",
                "True",
                "utf-8",
                "utf-8",
                "utf-8",
                "utf-8",
                "iso8859-1",
                "iso8859-1",
                "iso8859-1",
                "ascii",
                "ascii",
                "ascii",
                "utf-16",
                "utf-16-le",
                "utf-16-be",
                "utf-16-be"
            ),
            output
        );
    }

    [Fact]
    public void LookupReportsCPythonsArgumentAndNameErrors()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "LookupError unknown encoding: nope",
                "TypeError lookup() argument must be str, not int",
                "TypeError _codecs.lookup() takes exactly one argument (0 given)",
                "TypeError _codecs.lookup() takes exactly one argument (2 given)",
                "TypeError _codecs.lookup() takes no keyword arguments"
            ),
            output
        );
    }

    [Fact]
    public void EncodersAndDecodersReportConsumedSourceUnits()
    {
        var output = Run(
            """
            import codecs
            print(codecs.lookup("utf-8").encode("abc"))
            print(codecs.lookup("utf-8").decode(b"abc"))
            print(codecs.getencoder("utf-8")("café"))
            print(codecs.getencoder("utf-8")("\U0001F600"))
            print(codecs.getdecoder("utf-16")(b"\xff\xfea\x00"))
            print(codecs.getdecoder("ascii")(b"\xff", "replace"))
            print(codecs.lookup("utf-16-le").encode("abc"))
            print(codecs.lookup("utf-16-be").encode("abc"))
            print(codecs.lookup("latin-1").encode("café"))
            print(codecs.getencoder("utf-8")("abc", "strict"))
            """
        );

        Assert.Equal(
            Lines(
                "(b'abc', 3)",
                "('abc', 3)",
                "(b'caf\\xc3\\xa9', 4)",
                "(b'\\xf0\\x9f\\x98\\x80', 1)",
                "('a', 4)",
                "('�', 1)",
                "(b'a\\x00b\\x00c\\x00', 3)",
                "(b'\\x00a\\x00b\\x00c', 3)",
                "(b'caf\\xe9', 4)",
                "(b'abc', 3)"
            ),
            output
        );
    }

    [Fact]
    public void CodecCallablesRejectWrongOperands()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "TypeError utf_8_encode() argument 1 must be str, not int",
                "TypeError utf_8_encode() argument 1 must be str, not None",
                "TypeError a bytes-like object is required, not 'str'",
                "TypeError a bytes-like object is required, not 'NoneType'",
                "LookupError unknown error handler name 'nope'",
                "(b'abc', 3)"
            ),
            output
        );
    }

    [Fact]
    public void GetEncoderAndGetDecoderUsePythonLevelArityErrors()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "(b'abc', 3)",
                "TypeError getencoder() missing 1 required positional argument: 'encoding'",
                "TypeError getdecoder() missing 1 required positional argument: 'encoding'",
                "TypeError getencoder() takes 1 positional argument but 2 were given",
                "TypeError lookup() argument must be str, not int"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
