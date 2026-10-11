using System.Text;
using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The four names `site` installs in the builtins: `help`, the interactive helper, and the
/// `copyright`, `credits` and `license` printers, whose texts are the runtime's own.
/// `help` renders the runtime's own reflection the way `pydoc`'s plain text does for the
/// shapes it can know — functions, methods, classes and modules — and answers with
/// `pydoc`'s "No Python documentation found" for anything else. A native callable has no
/// argument list and no docstring to give, and the interactive session, the keyword
/// topics and the module reference sections are the slice's gaps.
/// </summary>
internal static class PythonSiteBuiltins
{
    private const string SiteModule = "_sitebuiltins";

    /// <summary>`_Printer.MAXLINES`: past this many lines the repr points at the call.</summary>
    private const int MaxPrinterLines = 23;

    private static readonly string[] CopyrightLines =
    [
        "Copyright (c) 2026 Aleksandr Pavlov.",
        "All Rights Reserved.",
        "",
        "The DotPython managed runtime hosts the CPython interpreter, whose own",
        "copyright follows.",
        "",
        "Copyright (c) 2001 Python Software Foundation.",
        "All Rights Reserved.",
        "",
        "Copyright (c) 2000 BeOpen.com.",
        "All Rights Reserved.",
        "",
        "Copyright (c) 1995-2001 Corporation for National Research Initiatives.",
        "All Rights Reserved.",
        "",
        "Copyright (c) 1991-1995 Stichting Mathematisch Centrum, Amsterdam.",
        "All Rights Reserved.",
    ];

    private static readonly string[] CreditLines =
    [
        "Thanks to CWI, CNRI, BeOpen, Zope Corporation, the Python Software",
        "Foundation, and a cast of thousands for supporting Python development.",
        "See www.python.org for more information.",
        "",
        "The DotPython managed runtime is built by Aleksandr Pavlov and the",
        "DotPython contributors.",
    ];

    private static readonly string[] LicenseLines =
    [
        "MIT License",
        "",
        "Copyright (c) 2026 Aleksandr Pavlov <ckidoz@gmail.com>",
        "",
        "Permission is hereby granted, free of charge, to any person obtaining a copy",
        "of this software and associated documentation files (the \"Software\"), to deal",
        "in the Software without restriction, including without limitation the rights",
        "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell",
        "copies of the Software, and to permit persons to whom the Software is",
        "furnished to do so, subject to the following conditions:",
        "",
        "The above copyright notice and this permission notice shall be included in all",
        "copies or substantial portions of the Software.",
        "",
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR",
        "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,",
        "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE",
        "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER",
        "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,",
        "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE",
        "SOFTWARE.",
    ];

    private static readonly PythonManagedTypeValue HelpType = CreateHelpType();

    private static readonly PythonManagedTypeValue PrinterType = CreatePrinterType();

    /// <summary>The `help` helper, which writes where the interpreter's output goes.</summary>
    internal static PythonManagedObjectValue CreateHelp(TextWriter output) =>
        new(HelpType, new HelpState(output));

    internal static PythonManagedObjectValue CreateCopyright(TextWriter output) =>
        new(PrinterType, new PrinterState("copyright", CopyrightLines, output));

    internal static PythonManagedObjectValue CreateCredits(TextWriter output) =>
        new(PrinterType, new PrinterState("credits", CreditLines, output));

    internal static PythonManagedObjectValue CreateLicense(TextWriter output) =>
        new(PrinterType, new PrinterState("license", LicenseLines, output));

    private sealed class HelpState(TextWriter output)
    {
        internal TextWriter Output { get; } = output;
    }

    private sealed class PrinterState(string name, IReadOnlyList<string> lines, TextWriter output)
    {
        internal string Name { get; } = name;

        internal IReadOnlyList<string> Lines { get; } = lines;

        internal TextWriter Output { get; } = output;
    }

    private static PythonManagedTypeValue CreatePrinterType()
    {
        var type = new PythonManagedTypeValue("_Printer") { Module = SiteModule };
        type.Attributes["__module__"] = new PythonTextValue(SiteModule);
        type.Attributes["__qualname__"] = new PythonTextValue("_Printer");
        type.Attributes["__doc__"] = new PythonTextValue(
            "interactive prompt objects for printing the license text, a list of\n"
                + "contributors and the copyright notice."
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(RepresentPrinter(PrinterOf(self!)))
        );
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, _) => CallPrinter(PrinterOf(self!))
        );
        return type;
    }

    private static PrinterState PrinterOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: PrinterState state }
            ? state
            : throw ManagedObjectProtocols.Fault(
                "DPY4053",
                "a printer prints its own text",
                default,
                "TypeError"
            );

    private static string RepresentPrinter(PrinterState state) =>
        state.Lines.Count <= MaxPrinterLines
            ? string.Join('\n', state.Lines)
            : $"Type {state.Name}() to see the full {state.Name} text";

    private static PythonNoneValue CallPrinter(PrinterState state)
    {
        foreach (var line in state.Lines)
        {
            state.Output.WriteLine(line);
        }
        return PythonNoneValue.Instance;
    }

    private static PythonManagedTypeValue CreateHelpType()
    {
        var type = new PythonManagedTypeValue("_Helper") { Module = SiteModule };
        type.Attributes["__module__"] = new PythonTextValue(SiteModule);
        type.Attributes["__qualname__"] = new PythonTextValue("_Helper");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Define the builtin 'help'.\n"
                + "\n"
                + "This is a wrapper around pydoc.help that provides a helpful message\n"
                + "when 'help' is typed at the Python interactive prompt.\n"
                + "\n"
                + "Calling help() at the Python prompt starts an interactive help session.\n"
                + "Calling help(thing) prints help for the python object 'thing'.\n"
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (_, _) =>
                new PythonTextValue(
                    "Type help() for interactive help, or help(object) for help about object."
                )
        );
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => CallHelp(HelpOf(self!), arguments)
        );
        return type;
    }

    private static HelpState HelpOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: HelpState state }
            ? state
            : throw ManagedObjectProtocols.Fault(
                "DPY4053",
                "help is not callable here",
                default,
                "TypeError"
            );

    private static PythonNoneValue CallHelp(HelpState state, IReadOnlyList<PythonValue> arguments)
    {
        // Without an object the helper answers with its own introduction, the way a
        // session opens. The interactive loop itself is not part of the runtime.
        if (arguments.Count == 0)
        {
            state.Output.Write(Introduction);
            return PythonNoneValue.Instance;
        }
        foreach (var argument in arguments)
        {
            state.Output.Write(Describe(argument));
        }
        return PythonNoneValue.Instance;
    }

    private const string Introduction =
        "Welcome to Python 3.14's help utility!\n"
        + "\n"
        + "Enter the name of any module, keyword, or topic to get help on writing\n"
        + "Python programs and using Python modules.\n"
        + "\n"
        + "To quit this help utility and return to the interpreter, enter \"q\", \"quit\"\n"
        + "or \"exit\".\n";

    // -----------------------------------------------------------------------------
    // `pydoc`'s plain text, for the shapes the runtime knows
    // -----------------------------------------------------------------------------

    private static string Describe(PythonValue value)
    {
        switch (value)
        {
            case PythonTextValue text:
                return DescribeName(text.Value);
            case PythonFunctionValue function:
                return Report(
                    $"Help on function {function.QualName ?? function.Name} "
                        + $"in module {ModuleOfFunction(function)}:",
                    Routine(function, function.Name, null)
                );
            case PythonBoundUserMethodValue bound:
                return Report(
                    $"Help on method {bound.Name} in module {ModuleOfFunction(bound.Function)}:",
                    Routine(
                        bound.Function,
                        bound.Name,
                        $" method of {ClassNameOf(bound.Target)} instance"
                    )
                );
            case PythonBoundMethodValue bound:
                // A native method has no argument list and no docstring to hand the
                // helper, so only its name is reported.
                return Report(
                    $"Help on method {bound.Name} in module builtins:",
                    $"{bound.Name}(...)"
                );
            case PythonBuiltinFunctionValue builtin:
                return Report(
                    $"Help on built-in function {builtin.Name} in module builtins:",
                    $"{builtin.Name}(...)" + DescribeDoc(DocOf(builtin))
                );
            case PythonManagedTypeValue type:
                return Report(
                    $"Help on class {type.Name} in module {type.Module ?? "builtins"}:",
                    DescribeClass(type, type.Module ?? "builtins")
                );
            case PythonModuleValue module:
                return DescribeModule(module);
            case PythonManagedObjectValue instance
                when instance.Type is { } instanceType && instanceType.Module is not null:
                return Report(
                    $"Help on {instanceType.Name} in module {instanceType.Module} object:",
                    DescribeClass(instanceType, instanceType.Module)
                );
            default:
                return NoDocumentation(value.ToRepresentationString());
        }
    }

    private static string DescribeName(string name)
    {
        // A name Python knows is resolved to what it names, as `pydoc` resolves it.
        if (UserObjectProtocols.Dispatcher is { } dispatcher)
        {
            if (dispatcher.Builtins.TryGetValue(name, out var builtin))
            {
                return Describe(builtin);
            }
            try
            {
                return Describe(dispatcher.ImportModule(name, string.Empty, default));
            }
            catch (Exception error)
                when (PythonNamespaceMapping.IsPythonException(error, "ImportError")
                    || PythonNamespaceMapping.IsPythonException(error, "ModuleNotFoundError")
                )
            {
                return NoDocumentation($"'{name}'");
            }
        }
        return NoDocumentation($"'{name}'");
    }

    private static string NoDocumentation(string subject) =>
        $"No Python documentation found for {subject}.\n"
        + "Use help() to get the interactive help utility.\n"
        + "Use help(str) for help on the str class.\n"
        + "\n";

    /// <summary>The `Help on …:` line, a blank, the documentation, and a trailing blank line.</summary>
    private static string Report(string title, string documentation) =>
        $"{title}\n\n{documentation}\n";

    /// <summary>`pydoc`'s routine text: the declaration line and the indented docstring.</summary>
    private static string Routine(PythonFunctionValue function, string name, string? note) =>
        $"{Signature(function, name, SkipFor(note))}{note}\n{DescribeDoc(DocOf(function))}";

    private static int SkipFor(string? note) => note is null ? 0 : 1;

    private static string DescribeDoc(string doc)
    {
        var cleaned = CleanDoc(doc);
        return cleaned.Length == 0 ? string.Empty : Indent(cleaned).TrimEnd(' ') + "\n";
    }

    private static string DescribeClass(PythonManagedTypeValue type, string module)
    {
        var output = new StringBuilder();
        var bases = type.Bases.Count == 0 ? [PythonBuiltinFunctions.ObjectType] : type.Bases;
        output
            .Append("class ")
            .Append(type.Name)
            .Append('(')
            .Append(string.Join(", ", bases.Select(@base => QualifiedName(@base, module))))
            .Append(")\n");
        var body = new List<string>();
        var classDoc = CleanDoc(DocOf(type));
        if (classDoc.Length > 0)
        {
            body.AddRange(classDoc.Split('\n'));
        }
        if (type.Mro.Count > 2)
        {
            body.Add("");
            body.Add("Method resolution order:");
            body.AddRange(type.Mro.Select(step => "    " + QualifiedName(step, module)));
        }
        var methods = type
            .Attributes.Where(pair =>
                pair.Value is PythonFunctionValue or PythonProtocolFunctionValue
            )
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        if (methods.Count > 0)
        {
            body.Add("");
            body.Add("Methods defined here:");
            foreach (var (name, method) in methods)
            {
                body.Add("");
                body.Add(
                    method is PythonFunctionValue function
                        ? Signature(function, name, 0)
                        : $"{name}(...)"
                );
                var doc = CleanDoc(DocOf(method));
                if (doc.Length > 0)
                {
                    body.AddRange(Indent(doc).Split('\n'));
                }
            }
        }
        foreach (var line in body)
        {
            output.Append(line.Length == 0 ? " |" : " |  " + line).Append('\n');
        }
        return output.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// A module's own text: its name with the first line of its docstring, the rest of the
    /// docstring, and the classes and functions it publishes. `pydoc`'s module reference
    /// and file sections need a source file the runtime's native modules do not have.
    /// </summary>
    private static string DescribeModule(PythonModuleValue module)
    {
        var doc = CleanDoc(DocOf(module));
        var (synopsis, description) = SplitDoc(doc);
        var output = new StringBuilder();
        output.Append("Help on module ").Append(module.Name).Append(":\n\n");
        output.Append("NAME\n    ").Append(module.Name);
        if (synopsis.Length > 0)
        {
            output.Append(" - ").Append(synopsis);
        }
        output.Append("\n\n");
        if (description.Length > 0)
        {
            output.Append("DESCRIPTION\n").Append(Indent(description)).Append("\n\n");
        }
        var visible = VisibleNames(module);
        var classes = visible
            .Where(pair => pair.Value is PythonManagedTypeValue)
            .Select(pair => pair.Name)
            .ToList();
        if (classes.Count > 0)
        {
            output.Append("CLASSES\n");
            foreach (var name in classes)
            {
                output.Append("    ").Append(name).Append('\n');
            }
            output.Append('\n');
        }
        var functions = visible
            .Where(pair => pair.Value is PythonFunctionValue or PythonBuiltinFunctionValue)
            .Select(pair =>
                pair.Value is PythonFunctionValue function
                    ? Signature(function, pair.Name, 0)
                    : $"{pair.Name}(...)"
            )
            .ToList();
        if (functions.Count > 0)
        {
            output.Append("FUNCTIONS\n");
            foreach (var declaration in functions)
            {
                output.Append("    ").Append(declaration).Append('\n');
            }
            output.Append('\n');
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>`pydoc.visiblename`: `__all__` when the module has one, otherwise public names.</summary>
    private static List<(string Name, PythonValue Value)> VisibleNames(PythonModuleValue module)
    {
        var entries = module.Globals.Entries.OrderBy(pair => pair.Name, StringComparer.Ordinal);
        HashSet<string>? all = null;
        if (
            module.Globals.TryGetValue("__all__", out var declared)
            && ElementsOf(declared) is { } declaredNames
        )
        {
            all = [.. declaredNames.OfType<PythonTextValue>().Select(name => name.Value)];
        }
        return
        [
            .. entries.Where(pair =>
                pair.Name.Length > 0
                && (all is not null ? all.Contains(pair.Name) : !pair.Name.StartsWith('_'))
            ),
        ];
    }

    private static IReadOnlyList<PythonValue>? ElementsOf(PythonValue value) =>
        value switch
        {
            PythonTupleValue tuple => tuple.Elements,
            PythonListValue list => list.Elements,
            _ => null,
        };

    private static (string Synopsis, string Description) SplitDoc(string doc)
    {
        var lines = doc.Split('\n');
        if (lines.Length == 1)
        {
            return (lines[0], string.Empty);
        }
        if (lines.Length >= 2 && lines[1].Trim().Length == 0)
        {
            return (lines[0], string.Join('\n', lines.Skip(2)));
        }
        return (string.Empty, doc);
    }

    /// <summary>`inspect.cleandoc`: the first line stripped, the rest dedented together.</summary>
    private static string CleanDoc(string doc)
    {
        var lines = doc.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        while (lines.Length > 0 && lines[0].Trim().Length == 0)
        {
            lines = lines[1..];
        }
        while (lines.Length > 0 && lines[^1].Trim().Length == 0)
        {
            lines = lines[..^1];
        }
        if (lines.Length == 0)
        {
            return string.Empty;
        }
        var margin = int.MaxValue;
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            margin = Math.Min(margin, line.Length - line.TrimStart().Length);
        }
        if (margin is int.MaxValue or 0)
        {
            return string.Join('\n', lines);
        }
        return string.Join(
            '\n',
            lines.Select(
                (line, index) => index == 0 || line.Length < margin ? line : line[margin..]
            )
        );
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? line : "    " + line));

    private static string DocOf(PythonValue value)
    {
        if (value is PythonFunctionValue function)
        {
            return function.Code.Definition.DocString ?? string.Empty;
        }
        try
        {
            return
                ManagedObjectProtocols.GetAttribute(value, "__doc__", default)
                    is PythonTextValue { Value: var text }
                ? text
                : string.Empty;
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError"))
        {
            return string.Empty;
        }
    }

    private static string ModuleOfFunction(PythonFunctionValue function) =>
        function.Globals.TryGetValue("__name__", out var name) && name is PythonTextValue text
            ? text.Value
            : "builtins";

    private static string ClassNameOf(PythonValue target)
    {
        if (PythonBuiltinTypes.GetRuntimeType(target) is PythonManagedTypeValue type)
        {
            var name = type.QualName ?? type.Name;
            return type.Module is { } module ? $"{module}.{name}" : name;
        }
        return ManagedObjectProtocols.GetTypeName(target);
    }

    /// <summary>`pydoc.classname`: a name outside the module being documented is qualified.</summary>
    private static string QualifiedName(PythonManagedTypeValue type, string module) =>
        (type.Module ?? "builtins") == module
            ? type.Name
            : $"{type.Module ?? "builtins"}.{type.Name}";

    /// <summary>
    /// The argument list `inspect.signature` writes: positional-only names before a `/`,
    /// then the plain positional ones, `*args`, the keyword-only ones after a bare `*`,
    /// and `**kwargs`, each with the default it was declared with.
    /// </summary>
    private static string Signature(PythonFunctionValue function, string name, int skip)
    {
        var definition = function.Code.Definition;
        var parts = new List<string>();
        var names = definition.VariableNames;
        var positional = definition.PositionalParameterCount;
        var positionalOnly = definition.PositionalOnlyArgumentCount;
        var keywordOnly = definition.KeywordOnlyArgumentCount;
        var firstDefault = positional - function.Defaults.Length;
        for (var slot = skip; slot < positional; slot++)
        {
            var text = names[slot];
            if (slot >= firstDefault)
            {
                text += "=" + function.Defaults[slot - firstDefault].ToRepresentationString();
            }
            parts.Add(text);
            if (slot == positionalOnly - 1)
            {
                parts.Add("/");
            }
        }
        if (definition.HasVariadicPositional)
        {
            parts.Add("*" + names[positional]);
        }
        if (keywordOnly > 0)
        {
            if (!definition.HasVariadicPositional)
            {
                parts.Add("*");
            }
            var first = positional + (definition.HasVariadicPositional ? 1 : 0);
            for (var slot = 0; slot < keywordOnly; slot++)
            {
                var parameter = names[first + slot];
                parts.Add(
                    function.KeywordDefaults is { } defaults
                    && defaults.TryGetValue(parameter, out var value)
                        ? $"{parameter}={value.ToRepresentationString()}"
                        : parameter
                );
            }
        }
        if (definition.HasVariadicKeywords)
        {
            parts.Add("**" + names[definition.ArgumentCount - 1]);
        }
        return $"{name}({string.Join(", ", parts)})";
    }
}
