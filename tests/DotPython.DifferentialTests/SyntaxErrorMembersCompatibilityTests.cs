using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SyntaxErrorMembersCompatibilityTests
{
    [Fact]
    public Task SyntaxErrorCarriesItsMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
