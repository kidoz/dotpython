using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DocstringExecutionTests
{
    [Fact]
    public void DocstringSurface()
    {
        var output = Run(
            """
            import contextlib
            import functools


            def documented():
                "the function doc"
                return 1


            def docless():
                pass


            def empty():
                ""
                return 2


            def parens():
                ("parenthesized doc")
                return 3


            def second_is_not_doc():
                x = "value"
                "later"
                return x


            print("fn", documented(), repr(documented.__doc__))
            print("docless", repr(docless.__doc__))
            print("empty", empty(), repr(empty.__doc__))
            print("parens", parens(), repr(parens.__doc__))
            print("later", second_is_not_doc(), repr(second_is_not_doc.__doc__))
            print("lambda", repr((lambda: 1).__doc__))


            class Documented:
                "the class doc"
                member = 1

                def method(self):
                    "the method doc"
                    return 4

                @staticmethod
                def stat():
                    "the static doc"

                @classmethod
                def klass(cls):
                    "the classmethod doc"

                @property
                def prop(self):
                    "the property doc"
                    return 5


            class Docless:
                pass


            print("class", Documented.member, repr(Documented.__doc__), repr(Documented.__doc__))
            print("instance", repr(Documented().__doc__), repr(Docless().__doc__), repr(Docless.__doc__))
            print("method", Documented().method(), repr(Documented.method.__doc__), repr(Documented().method.__doc__))
            print("stat", repr(Documented.stat.__doc__), repr(Documented().stat.__doc__))
            print("klass", repr(Documented.klass.__doc__), repr(Documented().klass.__doc__))
            print("prop", Documented().prop, repr(Documented.prop.__doc__))


            class Body:
                print("body-doc", repr(__doc__))
                "class body doc"


            print("after", repr(Body.__doc__))


            class Override:
                "first"
                __doc__ = "second"


            print("override", repr(Override.__doc__))


            def outer():
                "outer doc"

                def inner():
                    "inner doc"

                class Nested:
                    "nested doc"

                return inner, Nested


            inner, Nested = outer()
            print("nested", repr(outer.__doc__), repr(inner.__doc__), repr(Nested.__doc__))


            @functools.wraps(documented)
            def wrapped():
                return documented()


            print("wraps", wrapped(), repr(wrapped.__doc__), wrapped.__name__)


            @contextlib.contextmanager
            def managed():
                "generator doc"
                yield 6


            with managed() as value:
                print("manager", value, repr(managed.__doc__))

            print("module", repr(__doc__))
            print("globals", "__doc__" in globals(), repr(globals()["__doc__"]))
            """
        );
        Assert.Equal(
            Lines(
                "fn 1 'the function doc'",
                "docless None",
                "empty 2 ''",
                "parens 3 'parenthesized doc'",
                "later value None",
                "lambda None",
                "class 1 'the class doc' 'the class doc'",
                "instance 'the class doc' None None",
                "method 4 'the method doc' 'the method doc'",
                "stat 'the static doc' 'the static doc'",
                "klass 'the classmethod doc' 'the classmethod doc'",
                "prop 5 'the property doc'",
                "body-doc None",
                "after None",
                "override 'second'",
                "nested 'outer doc' 'inner doc' 'nested doc'",
                "wraps 1 'the function doc' documented",
                "manager 6 'generator doc'",
                "module None",
                "globals True None"
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
