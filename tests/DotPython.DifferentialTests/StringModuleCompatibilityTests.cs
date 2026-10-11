using Xunit;

namespace DotPython.DifferentialTests;

public sealed class StringModuleCompatibilityTests
{
    [Fact]
    public Task TheStringModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import string
            print("letters", repr(string.ascii_letters), repr(string.ascii_lowercase), repr(string.ascii_uppercase))
            print("digits", repr(string.digits), repr(string.hexdigits), repr(string.octdigits))
            print("sets", repr(string.punctuation), repr(string.whitespace), repr(string.printable))
            print("capwords", repr(string.capwords("hello world!  a b")), repr(string.capwords("a-b", "-")),
                  repr(string.capwords("a  b")), repr(string.capwords("")), repr(string.capwords("a-b c", "-")),
                  repr(string.capwords("a b", None)), repr(string.capwords("a\tb")), repr(string.capwords("a-b")))
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            report("capwords-no-args", lambda: string.capwords())
            report("capwords-int", lambda: string.capwords(5))
            report("capwords-empty-sep", lambda: string.capwords("a-b", ""))
            template = string.Template("$name is ${age} and $$10")
            print("substitute", repr(template.substitute(name="x", age=3)))
            print("safe", repr(template.safe_substitute(name="x")))
            print("attributes", repr(template.template), type(template).__name__)
            print("ids", sorted(template.get_identifiers()), template.is_valid())
            print("invalid", string.Template("$1x").is_valid(), string.Template("$$").is_valid(),
                  string.Template("$a ${b} $$c").get_identifiers())
            print("class", repr(string.Template.delimiter), repr(string.Template.idpattern),
                  repr(string.Template.braceidpattern))
            print("shadow", string.Template("$a").substitute({"a": 1}, a=2),
                  string.Template("$a").substitute({"a": 1}))
            report("no-args", lambda: string.Template("$a").substitute())
            report("missing", lambda: string.Template("$nope").substitute())
            report("safe-missing", lambda: string.Template("$nope").safe_substitute())
            report("safe-invalid", lambda: string.Template("$1x $a").safe_substitute(a=1))
            report("bad-placeholder", lambda: string.Template("$1x").substitute())
            report("multiline", lambda: string.Template("ok\n$1").substitute())
            report("midline", lambda: string.Template("ok $1").substitute())
            report("empty-brace", lambda: string.Template("${}").substitute())
            report("dollar-end", lambda: string.Template("$").substitute())
            report("unicode-id", lambda: string.Template("$é").substitute())
            report("init-zero", lambda: string.Template())
            report("init-two", lambda: string.Template("a", "b"))
            print("init-kw", repr(string.Template(template="x").substitute()),
                  repr(string.Template("$A $a").substitute(A=1, a=2)))
            print("str-value", repr(string.Template("$a").substitute(a=[1, 2])))
            class Custom(string.Template):
                delimiter = "%"
                idpattern = "[a-z]+"
            class Braces(string.Template):
                braceidpattern = "[a-z]+"
            print("custom", repr(Custom("%name and $name").substitute(name="x")),
                  repr(Custom("$$a").substitute()), repr(Braces("${a} $a").substitute(a=1)),
                  Custom.delimiter, Custom().template if False else "skip")
            print("mutate", (lambda t: (setattr(t, "template", "$x"), t.substitute(x=9))[1])(string.Template("")))
            """
        );
}
