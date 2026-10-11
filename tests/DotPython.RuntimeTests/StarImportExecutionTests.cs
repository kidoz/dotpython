using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// `from module import *`, which binds the names a module publishes: its `__all__` when it
/// declares one, and everything that is not private when it does not.
/// </summary>
public sealed class StarImportExecutionTests
{
    [Fact]
    public void StarImportBindsTheNamesAllPublishes()
    {
        var output = Run(
            "from subject import *\n" + "print(one, two, '_three' in dir(), 'four' in dir())\n",
            (
                "subject",
                "__all__ = ['one', 'two', '_three']\none = 1\ntwo = 2\n_three = 3\nfour = 4\n"
            )
        );

        Assert.Equal($"1 2 True False{Environment.NewLine}", output);
    }

    [Fact]
    public void StarImportSkipsPrivateNamesWithoutAll()
    {
        var output = Run(
            "from subject import *\n"
                + "print(public, 'helper' in dir(), 'Kind' in dir(), '_private' in dir())\n",
            (
                "subject",
                "public = 'p'\n_private = 'x'\ndef helper():\n    return 1\nclass Kind:\n    pass\n"
            )
        );

        Assert.Equal($"p True True False{Environment.NewLine}", output);
    }

    [Fact]
    public void StarImportRefusesANameAllDoesNotDefine()
    {
        var output = Run(
            "try:\n"
                + "    from subject import *\n"
                + "except AttributeError as error:\n"
                + "    print(error)\n",
            ("subject", "__all__ = ['nope']\n")
        );

        Assert.Equal($"module 'subject' has no attribute 'nope'{Environment.NewLine}", output);
    }

    [Fact]
    public void StarImportRefusesANonStringEntryInAll()
    {
        var output = Run(
            "try:\n"
                + "    from subject import *\n"
                + "except TypeError as error:\n"
                + "    print(error)\n",
            ("subject", "__all__ = [1]\n")
        );

        Assert.Equal($"Item in subject.__all__ must be str, not int{Environment.NewLine}", output);
    }

    [Fact]
    public void StarImportReportsAMissingModule()
    {
        var output = Run(
            "try:\n"
                + "    from nowhere import *\n"
                + "except ModuleNotFoundError as error:\n"
                + "    print(error)\n"
        );

        Assert.Equal($"No module named 'nowhere'{Environment.NewLine}", output);
    }

    [Fact]
    public void StarImportIsRefusedAwayFromModuleLevel()
    {
        var engine = new ManagedPythonEngine();

        using var output = new StringWriter();
        var function = engine.Execute(
            "def load():\n    from math import *\n",
            "function.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(function.Success);
        Assert.Contains(
            function.Diagnostics,
            diagnostic => diagnostic.Message == "import * only allowed at module level"
        );

        var classBody = engine.Execute(
            "class Loader:\n    from math import *\n",
            "class.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(classBody.Success);
        Assert.Contains(
            classBody.Diagnostics,
            diagnostic => diagnostic.Message == "import * only allowed at module level"
        );
    }

    private static string Run(string source, params (string Name, string Body)[] modules)
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        foreach (var (name, body) in modules)
        {
            sources[name] = new SourceText(body, name + ".py");
        }

        using var output = new StringWriter();
        var result = new ManagedPythonEngine(sources).Execute(
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
}
