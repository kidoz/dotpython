using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BuiltinsModuleCompatibilityTests
{
    [Fact]
    public Task BuiltinsModuleSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import builtins
            import math

            print("builtins-name", builtins.__name__, type(builtins).__name__)
            print("identity", builtins.len is len, builtins.print is print, builtins.open is open, builtins.list is list, builtins.Exception is Exception)
            print("singletons", builtins.Ellipsis is Ellipsis, builtins.NotImplemented is NotImplemented, getattr(builtins, "None", "missing") is None, getattr(builtins, "True", "missing") is True, getattr(builtins, "False", "missing") is False)
            print("entry-kind", type(__builtins__).__name__, __builtins__ is builtins)
            print("getattr", getattr(builtins, "len").__name__)


            def inner():
                print("inner", __builtins__ is builtins)


            inner()
            from math import *

            print("math-star", round(pi, 4), sqrt(16), "sqrt" in dir())
            import collections.abc

            from collections.abc import *

            print("abc-star", "Mapping" in dir(), "Sequence" in dir())
            import sys

            from sys import *

            print("sys-star", "argv" in dir(), "path" in dir())
            print("still-here", len("abc"), max(1, 2))
            from math import pi

            print("explicit-then-star", round(pi, 2))
            """
        );
}
