using System.Text;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PythonSourceDecoderTests
{
    [Theory]
    [InlineData("latin-1")]
    [InlineData("latin1")]
    [InlineData("iso-8859-1")]
    [InlineData("ISO_8859_1")]
    [InlineData("iso-latin-1")]
    [InlineData("iso_ir_100")]
    [InlineData("csisolatin1")]
    public void Decode_UsesLatin1CookieOnTheFirstOrSecondPhysicalLine(string encoding)
    {
        foreach (
            var prefix in new[] { "", "#!/usr/bin/python\n", "\n", "# comment\r\n", "# comment\r" }
        )
        {
            var code = prefix + $"# coding: {encoding}\nvalue = 'café'\n";
            var source = PythonSourceDecoder.Decode(Encoding.Latin1.GetBytes(code), "encoded.py");
            Assert.Equal(code.ReplaceLineEndings("\n"), source.Content);
            Assert.Equal("encoded.py", source.FilePath);
        }
    }

    [Theory]
    [InlineData("# coding=utf_8\nvalue='é'\n")]
    [InlineData("# vim:fileencoding=utf-8\nvalue='é'\n")]
    [InlineData("# coding: utf-8-sig\nvalue='é'\n")]
    [InlineData("value='é'\n# coding: ascii\n")]
    [InlineData("\n\n# coding: ascii\nvalue='é'\n")]
    [InlineData("value='é' # coding: ascii\n")]
    public void Decode_UsesUtf8AndHonorsCookiePlacement(string code)
    {
        var result = PythonSourceDecoder.Decode(Encoding.UTF8.GetBytes(code), "encoded.py");
        Assert.Equal(code, result.Content);
    }

    [Theory]
    [InlineData("us_ascii")]
    [InlineData("cp367")]
    [InlineData("ANSI_X3.4_1986")]
    [InlineData("cp65001")]
    [InlineData("utf8_ucs4")]
    public void Decode_AcceptsAliasesOfTheSupportedEncodings(string encoding)
    {
        var code = $"# coding: {encoding}\nprint(42)";
        Assert.Equal(
            code,
            PythonSourceDecoder.Decode(Encoding.ASCII.GetBytes(code), "encoded.py").Content
        );
    }

    [Fact]
    public void Decode_StripsUtf8BomAndNormalizesPhysicalNewlinesInsideStrings()
    {
        var code = "# coding: utf-8\r\nvalue='''a\rb\r\nc'''\r";
        var result = PythonSourceDecoder.Decode(
            [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(code)],
            "encoded.py"
        );
        Assert.Equal("# coding: utf-8\nvalue='''a\nb\nc'''\n", result.Content);
    }

    [Theory]
    [InlineData("# coding: ascii\nvalue='é'")]
    [InlineData("# coding: unknown-codec\npass")]
    [InlineData("# coding: utf-16\npass")]
    [InlineData("value='é'\n# coding: latin1")]
    [InlineData("pass\n\n# coding: latin1\nvalue='é'")]
    [InlineData("pass\0")]
    public void Decode_RejectsMalformedOrUnsupportedByteSourcesWithSyntaxError(string code)
    {
        var span = new TextSpan(7, 3);
        var fault = Assert.Throws<PythonRuntimeException>(() =>
            PythonSourceDecoder.Decode(Encoding.Latin1.GetBytes(code), "encoded.py", span)
        );
        Assert.Equal("SyntaxError", fault.PythonExceptionTypeName);
        Assert.Equal(span, fault.Span);
    }

    [Fact]
    public void Decode_RejectsBomConflicts()
    {
        var fault = Assert.Throws<PythonRuntimeException>(() =>
            PythonSourceDecoder.Decode(
                [.. Encoding.UTF8.Preamble, .. Encoding.ASCII.GetBytes("# coding: latin1\npass")],
                "encoded.py"
            )
        );
        Assert.Equal("SyntaxError", fault.PythonExceptionTypeName);
    }

    [Fact]
    public void Discovery_DefersDecodeFailuresAndRetainsTheByteSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dotpython-decode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "bad.py"), [0xff]);
            File.WriteAllBytes(
                Path.Combine(directory, "encoded.py"),
                Encoding.Latin1.GetBytes("# coding: latin1\nvalue='café'")
            );
            var engine = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
            );
            Directory.Delete(directory, recursive: true);
            using var output = new StringWriter();
            var result = engine.Execute(
                "print('started')\nfor attempt in range(2):\n try:\n  import bad\n except SyntaxError:\n  print('bad source')\nimport encoded\nprint(encoded.value)",
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(
                "started\nbad source\nbad source\ncafé\n",
                output.ToString().ReplaceLineEndings("\n")
            );
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Discovery_StillRejectsSourceFilesAboveTheByteLimit()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-decode-limit-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            using (var stream = File.Create(Path.Combine(directory, "oversized.py")))
            {
                stream.SetLength(PythonSourceDecoder.MaximumByteLength + 1);
            }
            Assert.Throws<InvalidDataException>(() =>
                new ManagedPythonEngine(
                    new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
                )
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
