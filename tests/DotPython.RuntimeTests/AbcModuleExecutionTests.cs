using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class AbcModuleExecutionTests
{
    [Fact]
    public void AbcModuleSurface()
    {
        var output = Run(
            """
            import abc
            import collections.abc

            print("names", sorted(n for n in dir(abc) if not n.startswith("_")))
            print("meta", abc.ABCMeta is collections.abc.ABCMeta, repr(abc.ABCMeta), abc.ABCMeta.__module__, abc.ABCMeta.__name__)
            print("abc-class", abc.ABC, type(abc.ABC).__name__, abc.ABC.__bases__, abc.ABC.__module__, abc.ABC.__abstractmethods__)
            print("abc-doc", abc.ABC.__doc__.strip(), abc.ABC.__slots__)
            print("token", type(abc.get_cache_token()).__name__, abc.get_cache_token() == abc.get_cache_token())
            print("decorator", type(abc.abstractmethod).__name__, abc.abstractmethod.__module__, abc.abstractmethod.__doc__.strip().split("\n")[0])


            def f(self):
                return 1


            print("marker-apply", abc.abstractmethod(f) is f, f.__isabstractmethod__, f()(None) if False else "ok")


            class Base(abc.ABC):
                @abc.abstractmethod
                def method(self):
                    pass

                @property
                @abc.abstractmethod
                def prop(self):
                    pass

                @classmethod
                @abc.abstractmethod
                def cm(cls):
                    pass

                @staticmethod
                @abc.abstractmethod
                def sm():
                    pass

                @abc.abstractmethod
                def other(self):
                    pass

                def concrete(self):
                    return "ok"


            print("base", sorted(Base.__abstractmethods__), Base.__bases__, Base.__mro__)
            print("markers", Base.method.__isabstractmethod__, Base.prop.__isabstractmethod__, Base.cm.__isabstractmethod__, Base.sm.__isabstractmethod__)
            print("concrete", hasattr(Base.concrete, "__isabstractmethod__"), hasattr(Base, "__dict__"))


            def probe(label, thunk):
                try:
                    print(label, repr(thunk()))
                except TypeError as error:
                    print(label, "TypeError", error)
                except AttributeError as error:
                    print(label, "AttributeError", error)


            probe("instantiate", lambda: Base())


            class Partial(Base):
                def method(self):
                    return 1

                def cm(cls):
                    return 2


            print("partial", sorted(Partial.__abstractmethods__))
            probe("partial-instantiate", lambda: Partial())


            class Full(Partial):
                prop = 5
                sm = staticmethod(lambda: 6)

                def other(self):
                    return 2


            print("full", sorted(Full.__abstractmethods__), Full().method(), Full().concrete(), Full().prop, Full().other())
            print("update", abc.update_abstractmethods(Full) is Full, sorted(Full.__abstractmethods__))


            class NoMeta:
                @abc.abstractmethod
                def m(self):
                    pass


            print("nomeata", hasattr(NoMeta, "__abstractmethods__"), NoMeta().m())
            print("marker-plain", hasattr(NoMeta.m, "__isabstractmethod__"), NoMeta.m.__isabstractmethod__)


            class Plain:
                @property
                def prop(self):
                    return 1

                @classmethod
                def cm(cls):
                    return 2


            print("plain-markers", Plain.prop.__isabstractmethod__, hasattr(Plain.cm, "__isabstractmethod__"), hasattr(Plain.sm if hasattr(Plain, "sm") else Plain.cm, "__isabstractmethod__"))


            class Virtual:
                pass


            print("register", abc.ABCMeta.register(Base, Virtual) is Virtual, isinstance(Virtual(), Base), issubclass(Virtual, Base))
            print("abc-check", isinstance(Base, abc.ABCMeta), issubclass(abc.ABC, object))
            print("deprecated", hasattr(abc, "abstractproperty"), hasattr(abc, "abstractclassmethod"), hasattr(abc, "abstractstaticmethod"))
            """
        );
        Assert.Equal(
            Lines(
                "names ['ABC', 'ABCMeta', 'abstractclassmethod', 'abstractmethod', 'abstractproperty', 'abstractstaticmethod', 'get_cache_token', 'update_abstractmethods']",
                "meta True <class 'abc.ABCMeta'> abc ABCMeta",
                "abc-class <class 'abc.ABC'> ABCMeta (<class 'object'>,) abc frozenset()",
                "abc-doc Helper class that provides a standard way to create an ABC using",
                "inheritance. ()",
                "token int True",
                "decorator function abc A decorator indicating abstract methods.",
                "marker-apply True True ok",
                "base ['cm', 'method', 'other', 'prop', 'sm'] (<class 'abc.ABC'>,) (<class '__main__.Base'>, <class 'abc.ABC'>, <class 'object'>)",
                "markers True True True True",
                "concrete False True",
                "instantiate TypeError Can't instantiate abstract class Base without an implementation for abstract methods 'cm', 'method', 'other', 'prop', 'sm'",
                "partial ['other', 'prop', 'sm']",
                "partial-instantiate TypeError Can't instantiate abstract class Partial without an implementation for abstract methods 'other', 'prop', 'sm'",
                "full [] 1 ok 5 2",
                "update True []",
                "nomeata False None",
                "marker-plain True True",
                "plain-markers False False False",
                "register True True True",
                "abc-check True True",
                "deprecated True True True"
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
