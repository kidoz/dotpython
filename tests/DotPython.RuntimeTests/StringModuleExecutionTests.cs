using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StringModuleExecutionTests
{
    [Fact]
    public void TheStringModuleMatchesCpython()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "letters 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ' 'abcdefghijklmnopqrstuvwxyz' 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'",
                "digits '0123456789' '0123456789abcdefABCDEF' '01234567'",
                "sets '!\"#$%&\\'()*+,-./:;<=>?@[\\\\]^_`{|}~' ' \\t\\n\\r\\x0b\\x0c' '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ!\"#$%&\\'()*+,-./:;<=>?@[\\\\]^_`{|}~ \\t\\n\\r\\x0b\\x0c'",
                "capwords 'Hello World! A B' 'A-B' 'A B' '' 'A-B c' 'A B' 'A B' 'A-b'",
                "capwords-no-args TypeError capwords() missing 1 required positional argument: 's'",
                "capwords-int AttributeError 'int' object has no attribute 'split'",
                "capwords-empty-sep ValueError empty separator",
                "substitute 'x is 3 and $10'",
                "safe 'x is ${age} and $10'",
                "attributes '$name is ${age} and $$10' Template",
                "ids ['age', 'name'] True",
                "invalid False True ['a', 'b']",
                "class '$' '(?a:[_a-z][_a-z0-9]*)' None",
                "shadow 2 1",
                "no-args KeyError 'a'",
                "missing KeyError 'nope'",
                "safe-missing '$nope'",
                "safe-invalid '$1x 1'",
                "bad-placeholder ValueError Invalid placeholder in string: line 1, col 1",
                "multiline ValueError Invalid placeholder in string: line 2, col 1",
                "midline ValueError Invalid placeholder in string: line 1, col 4",
                "empty-brace ValueError Invalid placeholder in string: line 1, col 1",
                "dollar-end ValueError Invalid placeholder in string: line 1, col 1",
                "unicode-id ValueError Invalid placeholder in string: line 1, col 1",
                "init-zero TypeError Template.__init__() missing 1 required positional argument: 'template'",
                "init-two TypeError Template.__init__() takes 2 positional arguments but 3 were given",
                "init-kw 'x' '1 2'",
                "str-value '[1, 2]'",
                "custom 'x and $name' '$$a' '1 1' % skip",
                "mutate 9"
            ),
            output
        );
    }

    /// <summary>
    /// The module's own list of names, which this runtime publishes without `Formatter`.
    /// </summary>
    [Fact]
    public void ThePublicNamesLeaveOutFormatter()
    {
        var output = Run(
            """
            import string
            print(string.__all__)
            """
        );
        Assert.Equal(
            Lines(
                "['ascii_letters', 'ascii_lowercase', 'ascii_uppercase', 'capwords', 'digits', "
                    + "'hexdigits', 'octdigits', 'printable', 'punctuation', 'whitespace', "
                    + "'Template']"
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
