using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CompilationBuiltinsExecutionTests
{
    [Fact]
    public void CompileEvalAndExecMatchCpython()
    {
        var output = Run(
            """
            code = compile("1 + 2", "<s>", "eval")
            print("code", type(code).__name__, code.co_name, code.co_qualname, code.co_filename,
                  code.co_firstlineno, code.co_argcount, code.co_posonlyargcount,
                  code.co_kwonlyargcount, code.co_nlocals, code.co_varnames, code.co_names,
                  code.co_freevars, code.co_cellvars)
            print("repr", repr(code).startswith("<code object <module> at 0x"), 'file "<s>", line 1' in repr(code))
            print("eq", code == compile("1 + 2", "<s>", "eval"), code == compile("1 + 3", "<s>", "eval"),
                  code == compile("1 + 2", "<other>", "eval"),
                  hash(code) == hash(compile("1 + 2", "<s>", "eval")))
            print("eval-code", eval(code), eval(compile("x + 1", "<s>", "eval"), {"x": 41}))
            d = {}
            exec(compile("z = 5", "<s>", "exec"), d)
            print("exec-dict", d["z"], "__builtins__" in d)
            exec("w = 6")
            print("exec-str", w, eval("w + 1"))
            print("two-dicts", (lambda g, l: (exec("n = 1\nm = n + 1", g, l), (g.get("n"), l.get("n"), l.get("m")))[1])({}, {}))
            print("locals-map", (lambda l: (exec("q = 2", {"__builtins__": __builtins__}, l), l.get("q"))[1])({}))
            def scoped():
                value = 5
                return eval("value + 1")
            print("scope", scoped())
            print("def-inside", (lambda store: (exec("def f(a):\n    return a * 2", store), store["f"](21))[1])({}))
            print("expressions", eval("[i for i in range(3)]"), eval("(lambda a: a)(7)"), eval(" 1 +\n        2 ") if False else eval("{'k': 1}['k']"))
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            report("exec-none", lambda: exec())
            report("exec-4", lambda: exec("1", {}, {}, {}))
            report("exec-int", lambda: exec(1))
            report("exec-globals", lambda: exec("pass", 5))
            report("eval-none", lambda: eval())
            report("eval-4", lambda: eval("1", {}, {}, {}))
            report("eval-int", lambda: eval(None))
            report("eval-globals", lambda: eval("1", []))
            report("compile-1", lambda: compile("1"))
            report("compile-2", lambda: compile("1", "<s>"))
            report("compile-7", lambda: compile("1", "<s>", "eval", 0, False, -1, 7))
            report("compile-mode", lambda: compile("1", "<s>", "nonsense"))
            report("compile-mode-type", lambda: compile("1", "<s>", 1))
            report("compile-src", lambda: compile(1, "<s>", "exec"))
            report("compile-file", lambda: compile("1", 5, "eval"))
            report("compile-flags", lambda: compile("1", "<s>", "eval", 9999))
            report("compile-opt", lambda: compile("assert 1", "<s>", "exec", 0, False, -2))
            report("closure", lambda: exec("1", {}, {}, closure=5))
            report("exec-eval-code", lambda: exec(compile("1", "<s>", "eval")))
            report("empty-exec", lambda: exec(""))
            report("trivial-eval", lambda: eval("None"))
            for label, source, mode in (("syntax", "1 +", "eval"), ("statement", "x = 1", "eval"),
                                        ("empty", "", "eval"), ("indent", "if True:\nprint(1)", "exec"),
                                        ("tab", "if True:\n\tx = 1\n        y = 2", "exec"),
                                        ("multi-single", "1\n2", "single"), ("empty-single", "", "single")):
                try:
                    compile(source, "<s>", mode)
                    print("syntax", label, "ok")
                except SyntaxError as error:
                    print("syntax", label, type(error).__name__)
            """
        );
        Assert.Equal(
            Lines(
                "code code <module> <module> <s> 1 0 0 0 0 () () () ()",
                "repr True True",
                "eq True False True True",
                "eval-code 3 42",
                "exec-dict 5 True",
                "exec-str 6 7",
                "two-dicts (None, 1, 2)",
                "locals-map 2",
                "scope 6",
                "def-inside 42",
                "expressions [0, 1, 2] 7 1",
                "exec-none TypeError exec() takes at least 1 positional argument (0 given)",
                "exec-4 TypeError exec() takes at most 3 positional arguments (4 given)",
                "exec-int TypeError exec() arg 1 must be a string, bytes or code object",
                "exec-globals TypeError exec() globals must be a dict, not int",
                "eval-none TypeError eval() takes at least 1 positional argument (0 given)",
                "eval-4 TypeError eval() takes at most 3 arguments (4 given)",
                "eval-int TypeError eval() arg 1 must be a string, bytes or code object",
                "eval-globals TypeError globals must be a real dict; try eval(expr, {}, mapping)",
                "compile-1 TypeError compile() missing required argument 'filename' (pos 2)",
                "compile-2 TypeError compile() missing required argument 'mode' (pos 3)",
                "compile-7 TypeError compile() takes at most 6 positional arguments (7 given)",
                "compile-mode ValueError compile() mode must be 'exec', 'eval' or 'single'",
                "compile-mode-type TypeError compile() argument 'mode' must be str, not int",
                "compile-src TypeError compile() arg 1 must be a string, bytes or AST object",
                "compile-file TypeError expected str, bytes or os.PathLike object, not int",
                "compile-flags ValueError compile(): unrecognised flags",
                "compile-opt ValueError compile(): invalid optimize value",
                "closure TypeError closure can only be used when source is a code object",
                "exec-eval-code None",
                "empty-exec None",
                "trivial-eval None",
                "syntax syntax SyntaxError",
                "syntax statement SyntaxError",
                "syntax empty SyntaxError",
                "syntax indent IndentationError",
                "syntax tab TabError",
                "syntax multi-single SyntaxError",
                "syntax empty-single SyntaxError"
            ),
            output
        );
    }

    /// <summary>
    /// `single` is the statement an interactive prompt takes: it compiles and runs like
    /// `exec`, without the prompt's display of an expression statement's value, which the
    /// runtime does not have.
    /// </summary>
    [Fact]
    public void SingleModeRunsWithoutThePromptDisplay()
    {
        var output = Run(
            """
            exec(compile("1 + 2", "<s>", "single"))
            exec(compile("value = 5", "<s>", "single"))
            print("ran", value, compile("1 + 2", "<s>", "single").co_name)
            """
        );
        Assert.Equal(Lines("ran 5 <module>"), output);
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
