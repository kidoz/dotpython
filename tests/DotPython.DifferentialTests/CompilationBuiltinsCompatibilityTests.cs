using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CompilationBuiltinsCompatibilityTests
{
    [Fact]
    public Task CompileEvalAndExecMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
