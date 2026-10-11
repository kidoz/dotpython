using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SyntaxErrorMembersExecutionTests
{
    [Fact]
    public void SyntaxErrorCarriesItsMembers()
    {
        var output = Run(
            """
            members = ("msg", "filename", "lineno", "offset", "text", "end_lineno", "end_offset",
                       "print_file_and_line")
            def dump(label, error):
                print(label, type(error).__name__, repr(error.args),
                      [(name, repr(getattr(error, name))) for name in members], repr(str(error)))
            dump("empty", SyntaxError())
            dump("message", SyntaxError("m"))
            dump("four", SyntaxError("m", ("f", 2, 3, "text")))
            dump("six", SyntaxError("m", ("f", 2, 3, "text", 4, 5)))
            dump("list", SyntaxError("m", ["f", 2, 3, "t"]))
            dump("indent", IndentationError("m", ("f", 1, 2, "t")))
            dump("tab", TabError("m"))
            def compose(label, **assigned):
                error = SyntaxError()
                for name, value in assigned.items():
                    setattr(error, name, value)
                print("compose", label, repr(str(error)))
            compose("none", )
            compose("file", filename="f")
            compose("line", lineno=2)
            compose("both", filename="f", lineno=2)
            compose("cleared", filename="f", lineno=2, text="t", offset=3)
            compose("msg-int", msg=5, filename="f", lineno=1)
            compose("msg-empty", msg="")
            error = SyntaxError("m", ("f", 2, 3, "text", 4, 5))
            for name, value in (("msg", "changed"), ("lineno", 9)):
                setattr(error, name, value)
            print("mutate", repr(str(error)), repr(error.msg), repr(error.lineno))
            del error.lineno
            print("delete", repr(error.lineno), repr(str(error)))
            error.filename = None
            print("clear", repr(str(error)))
            class Custom(SyntaxError):
                pass
            class Initialized(SyntaxError):
                def __init__(self, message):
                    super().__init__(message, ("f", 1, 2, "t"))
                    self.extra = "e"
            print("subclass", repr(str(Custom("m", ("f", 2, 3, "text", 4, 5)))),
                  repr(Custom("m", ("f", 2, 3, "text", 4, 5)).msg))
            initialized = Initialized("boom")
            print("initialized", repr(initialized.msg), repr(initialized.lineno), repr(str(initialized)),
                  repr(initialized.args), initialized.extra)
            fresh = SyntaxError.__new__(SyntaxError)
            SyntaxError.__init__(fresh, "m", ("f", 2, 3, "t"))
            print("init", repr(fresh.args), repr(str(fresh)))
            plain = SyntaxError("m")
            SyntaxError.__init__(plain, "z", ("g", 9, 1, "txt"))
            print("reinit", repr(plain.args), repr(str(plain)))
            print("class-members", [(name, hasattr(SyntaxError, name)) for name in members])
            descriptor = getattr(SyntaxError, "lineno")
            print("descriptor", type(descriptor).__name__, descriptor.__qualname__, descriptor.__doc__,
                  repr(descriptor))
            print("descriptor-get", descriptor.__get__(SyntaxError("m", ("f", 2, 3, "t")), SyntaxError),
                  descriptor.__get__(None, SyntaxError) is descriptor)
            error = SyntaxError("m", ("f", 2, 3, "t"))
            error.text = "changed"
            print("text", repr(error.text), repr(str(error)))
            # The failures this runtime's parser reports: the type is CPython's, the wording is not.
            for label, source, mode in (("line", "if True:\nprint(1)", "exec"),
                                        ("tab", "if True:\n\tx = 1\n        y = 2", "exec"),
                                        ("close", "f(", "exec"),
                                        ("expr", "1 +", "eval")):
                try:
                    compile(source, "<s>", mode)
                    print("parse", label, "ok")
                except SyntaxError as caught:
                    print("parse", label, type(caught).__name__, repr(caught.filename), repr(caught.lineno))
            # The modes' own refusals, which carry CPython's wording and members both.
            for label, source, mode in (("statement", "x = 1", "eval"), ("empty", "", "eval"),
                                        ("two-single", "1\n2", "single"), ("empty-single", "", "single")):
                try:
                    compile(source, "<s>", mode)
                    print("refusal", label, "ok")
                except SyntaxError as caught:
                    print("refusal", label, type(caught).__name__, repr(caught.msg), repr(caught.filename),
                          repr(caught.lineno), repr(str(caught)))
            """
        );
        Assert.Equal(
            Lines(
                "empty SyntaxError () [('msg', 'None'), ('filename', 'None'), ('lineno', 'None'), ('offset', 'None'), ('text', 'None'), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'None'",
                "message SyntaxError ('m',) [('msg', \"'m'\"), ('filename', 'None'), ('lineno', 'None'), ('offset', 'None'), ('text', 'None'), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'm'",
                "four SyntaxError ('m', ('f', 2, 3, 'text')) [('msg', \"'m'\"), ('filename', \"'f'\"), ('lineno', '2'), ('offset', '3'), ('text', \"'text'\"), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'm (f, line 2)'",
                "six SyntaxError ('m', ('f', 2, 3, 'text', 4, 5)) [('msg', \"'m'\"), ('filename', \"'f'\"), ('lineno', '2'), ('offset', '3'), ('text', \"'text'\"), ('end_lineno', '4'), ('end_offset', '5'), ('print_file_and_line', 'None')] 'm (f, line 2)'",
                "list SyntaxError ('m', ['f', 2, 3, 't']) [('msg', \"'m'\"), ('filename', \"'f'\"), ('lineno', '2'), ('offset', '3'), ('text', \"'t'\"), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'm (f, line 2)'",
                "indent IndentationError ('m', ('f', 1, 2, 't')) [('msg', \"'m'\"), ('filename', \"'f'\"), ('lineno', '1'), ('offset', '2'), ('text', \"'t'\"), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'm (f, line 1)'",
                "tab TabError ('m',) [('msg', \"'m'\"), ('filename', 'None'), ('lineno', 'None'), ('offset', 'None'), ('text', 'None'), ('end_lineno', 'None'), ('end_offset', 'None'), ('print_file_and_line', 'None')] 'm'",
                "compose none 'None'",
                "compose file 'None (f)'",
                "compose line 'None (line 2)'",
                "compose both 'None (f, line 2)'",
                "compose cleared 'None (f, line 2)'",
                "compose msg-int '5 (f, line 1)'",
                "compose msg-empty ''",
                "mutate 'changed (f, line 9)' 'changed' 9",
                "delete None 'changed (f)'",
                "clear 'changed'",
                "subclass 'm (f, line 2)' 'm'",
                "initialized 'boom' 1 'boom (f, line 1)' ('boom', ('f', 1, 2, 't')) e",
                "init ('m', ('f', 2, 3, 't')) 'm (f, line 2)'",
                "reinit ('z', ('g', 9, 1, 'txt')) 'z (g, line 9)'",
                "class-members [('msg', True), ('filename', True), ('lineno', True), ('offset', True), ('text', True), ('end_lineno', True), ('end_offset', True), ('print_file_and_line', True)]",
                "descriptor member_descriptor SyntaxError.lineno exception lineno <member 'lineno' of 'SyntaxError' objects>",
                "descriptor-get 2 True",
                "text 'changed' 'm (f, line 2)'",
                "parse line IndentationError '<s>' 2",
                "parse tab TabError '<s>' 3",
                "parse close SyntaxError '<s>' 1",
                "parse expr SyntaxError '<s>' 1",
                "refusal statement SyntaxError 'invalid syntax' '<s>' 1 'invalid syntax (<s>, line 1)'",
                "refusal empty SyntaxError 'invalid syntax' '<s>' 0 'invalid syntax (<s>, line 0)'",
                "refusal two-single SyntaxError 'multiple statements found while compiling a single statement' '<s>' 1 'multiple statements found while compiling a single statement (<s>, line 1)'",
                "refusal empty-single SyntaxError 'invalid syntax' '<s>' 0 'invalid syntax (<s>, line 0)'"
            ),
            output
        );
    }

    /// <summary>
    /// A module the runtime cannot compile reports it as a `SyntaxError` whose members name
    /// the file and the line, on top of the engine's own message.
    /// </summary>
    [Fact]
    public void AModuleThatFailsToCompileCarriesTheLine()
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["broken"] = new SourceText("if True:\nprint(1)\n", "broken.py"),
        };

        using var output = new StringWriter();
        var result = new ManagedPythonEngine(sources).Execute(
            "import broken\n",
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        var message = Assert.Single(result.Diagnostics).Message;
        Assert.Contains("could not be compiled", message, StringComparison.Ordinal);
        Assert.Contains("line 2", message, StringComparison.Ordinal);
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
