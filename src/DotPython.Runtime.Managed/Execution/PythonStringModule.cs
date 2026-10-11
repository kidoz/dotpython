using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `string` module's classic surface: the character-set constants, `capwords` and the
/// `Template` placeholder class. `Formatter` is not part of the runtime slice.
/// </summary>
internal static class PythonStringModule
{
    /// <summary>CPython's own `Template.idpattern`, the ASCII-only form it publishes.</summary>
    private const string DefaultIdPattern = "(?a:[_a-z][_a-z0-9]*)";

    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private const string Whitespace = " \t\n\r\x0b\x0c";

    private static readonly string[] PublicNames =
    [
        "ascii_letters",
        "ascii_lowercase",
        "ascii_uppercase",
        "capwords",
        "digits",
        "hexdigits",
        "octdigits",
        "printable",
        "punctuation",
        "whitespace",
        "Template",
    ];

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("ascii_lowercase", new PythonTextValue(Lowercase));
        globals.SetValue("ascii_uppercase", new PythonTextValue(Uppercase));
        globals.SetValue("ascii_letters", new PythonTextValue(Lowercase + Uppercase));
        globals.SetValue("digits", new PythonTextValue(Digits));
        globals.SetValue("hexdigits", new PythonTextValue(Digits + "abcdefABCDEF"));
        globals.SetValue("octdigits", new PythonTextValue("01234567"));
        globals.SetValue("punctuation", new PythonTextValue(Punctuation));
        globals.SetValue("whitespace", new PythonTextValue(Whitespace));
        globals.SetValue(
            "printable",
            new PythonTextValue(Digits + Lowercase + Uppercase + Punctuation + Whitespace)
        );
        globals.SetValue(
            "capwords",
            new PythonBuiltinFunctionValue("capwords", CapWords).WithSignature(
                ["s", "sep"],
                [null, PythonNoneValue.Instance]
            )
        );
        globals.SetValue("Template", CreateTemplateType());
        // `Formatter` is not part of the slice, so the module's own list leaves it out.
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. PublicNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    /// <summary>
    /// `capwords(s, sep=None)`: the string's own `split` cuts it up, every part is
    /// capitalized and the separator joins them again — so a `str` subclass answers with
    /// its own methods, which is what CPython's own implementation lets it do.
    /// </summary>
    private static PythonValue CapWords(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var text = arguments[0];
        var separator =
            arguments.Count > 1 && arguments[1] is not PythonNoneValue ? arguments[1] : null;
        var parts = ManagedObjectProtocols.MaterializeValues(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(text, "split", span),
                separator is null ? [] : [separator],
                span
            ),
            span
        );
        var capitalized = parts
            .Select(part =>
                (PythonValue)
                    new PythonTextValue(
                        PythonUnicodeCase.Capitalize(
                            part is PythonTextValue textPart
                                ? textPart.Value
                                : part.ToDisplayString()
                        )
                    )
            )
            .ToList();
        var joined = separator ?? new PythonTextValue(" ");
        return UserObjectProtocols.Dispatcher!.Invoke(
            ManagedObjectProtocols.GetAttribute(joined, "join", span),
            [new PythonListValue(capitalized)],
            span
        );
    }

    private static PythonManagedTypeValue CreateTemplateType()
    {
        var type = new PythonManagedTypeValue("Template")
        {
            Module = "string",
            QualName = "Template",
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("string");
        type.Attributes["__doc__"] = new PythonTextValue(
            "A string class for supporting $-substitutions.\n"
        );
        type.Attributes["delimiter"] = new PythonTextValue("$");
        type.Attributes["idpattern"] = new PythonTextValue(DefaultIdPattern);
        type.Attributes["braceidpattern"] = PythonNoneValue.Instance;
        type.Attributes["__init__"] = Method("__init__", Initialize);
        type.Attributes["substitute"] = Method(
            "substitute",
            (receiver, arguments, names, values) =>
                Substitute(receiver, arguments, names, values, safe: false)
        );
        type.Attributes["safe_substitute"] = Method(
            "safe_substitute",
            (receiver, arguments, names, values) =>
                Substitute(receiver, arguments, names, values, safe: true)
        );
        type.Attributes["is_valid"] = Method("is_valid", Validate);
        type.Attributes["get_identifiers"] = Method("get_identifiers", Identifiers);
        return type;
    }

    /// <summary>
    /// A method of the class, called the way a Python call reaches it — with the receiver,
    /// and with the keywords a mapping-taking method collects.
    /// </summary>
    private static PythonProtocolFunctionValue Method(
        string name,
        Func<
            PythonValue?,
            IReadOnlyList<PythonValue>,
            IReadOnlyList<string>,
            IReadOnlyList<PythonValue>,
            PythonValue
        > body
    ) =>
        PythonUserTypes.Method(
            "Template",
            name,
            (receiver, arguments) => body(receiver, arguments, [], []),
            (receiver, arguments, names, values) => body(receiver, arguments, names, values)
        );

    /// <summary>
    /// `Template.__init__(self, template)`: the text the substitutions read. CPython stores
    /// whatever it is given and fails on it later, when a substitution reads it.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var template = rest.Count switch
        {
            0 when keywordNames.Count == 1 && keywordNames[0] == "template" => keywordValues[0],
            1 when keywordNames.Count == 0 => rest[0],
            0 => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Template.__init__() missing 1 required positional argument: 'template'",
                default,
                "TypeError"
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Template.__init__() takes 2 positional arguments but "
                    + $"{rest.Count + keywordNames.Count + 1} were given",
                default,
                "TypeError"
            ),
        };
        RequireInstance(self).Attributes["template"] = template;
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `substitute` and `safe_substitute`: every placeholder becomes the `str()` of what
    /// the mapping holds for it, with the keywords the call named ahead of the mapping. A
    /// safe run keeps a placeholder nothing answers for instead of raising.
    /// </summary>
    private static PythonTextValue Substitute(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        bool safe
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count > 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"substitute() takes at most 1 positional argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        }

        var mapping = rest.Count == 1 ? rest[0] : null;
        var text = Walk(
            TemplateText(self),
            Delimiters(self),
            (_, name) =>
            {
                for (var index = 0; index < keywordNames.Count; index++)
                {
                    if (keywordNames[index] == name)
                    {
                        return keywordValues[index].ToDisplayString();
                    }
                }
                if (mapping is null)
                {
                    return null;
                }
                return ManagedObjectProtocols
                    .GetItem(mapping, new PythonTextValue(name), default)
                    .ToDisplayString();
            },
            safe
        );
        return new PythonTextValue(text);
    }

    private static PythonTruthValue Validate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        try
        {
            // Every placeholder resolves, so only a malformed one is an error.
            Walk(TemplateText(self), Delimiters(self), (_, _) => string.Empty, safe: false);
            return PythonTruthValue.True;
        }
        catch (PythonRuntimeException fault) when (fault.PythonExceptionTypeName == "ValueError")
        {
            return PythonTruthValue.False;
        }
    }

    private static PythonListValue Identifiers(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var found = new List<PythonValue>();
        Walk(
            TemplateText(self),
            Delimiters(self),
            (_, name) =>
            {
                if (found.All(value => value is not PythonTextValue text || text.Value != name))
                {
                    found.Add(new PythonTextValue(name));
                }
                return string.Empty;
            },
            // A template that is not valid still names the placeholders it does hold.
            safe: true
        );
        return new PythonListValue(found);
    }

    private readonly record struct TemplateDelimiters(
        string Delimiter,
        string IdPattern,
        string? BracePattern
    );

    private static TemplateDelimiters Delimiters(PythonValue self)
    {
        var delimiter = ReadAttribute(self, "delimiter");
        if (delimiter is not PythonTextValue { Value.Length: > 0 } delimiterText)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Template.delimiter must be a non-empty string",
                default,
                "ValueError"
            );
        }
        var pattern = ReadAttribute(self, "idpattern");
        var brace = ReadAttribute(self, "braceidpattern");
        return new TemplateDelimiters(
            delimiterText.Value,
            pattern is PythonTextValue patternText ? patternText.Value : DefaultIdPattern,
            brace is PythonTextValue braceText ? braceText.Value : null
        );
    }

    /// <summary>The instance a method was reached on, where its own attributes live.</summary>
    private static PythonManagedObjectValue RequireInstance(PythonValue self) =>
        self as PythonManagedObjectValue
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "'Template' object expected",
            default,
            "TypeError"
        );

    private static string TemplateText(PythonValue self)
    {
        var template = ReadAttribute(self, "template");
        if (template is not PythonTextValue text)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "expected string or bytes-like object, got "
                    + $"'{ManagedObjectProtocols.GetTypeName(template)}'",
                default,
                "TypeError"
            );
        }
        return text.Value;
    }

    private static PythonValue ReadAttribute(PythonValue self, string name) =>
        ManagedObjectProtocols.GetAttribute(self, name, default);

    /// <summary>
    /// Walks a template the way CPython's own pattern does: an escaped delimiter becomes
    /// the delimiter, a placeholder is handed to <paramref name="resolve"/>, and anything
    /// else after a delimiter is invalid. A placeholder nothing answers for is a `KeyError`
    /// in a strict walk and stands as it is in a safe one; invalid text raises in a strict
    /// walk and stands as it is in a safe one.
    /// </summary>
    private static string Walk(
        string template,
        TemplateDelimiters delimiters,
        Func<string, string, string?> resolve,
        bool safe
    )
    {
        var delimiter = delimiters.Delimiter;
        var output = new StringBuilder(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var start = template.IndexOf(delimiter, position, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(template, position, template.Length - position);
                break;
            }
            output.Append(template, position, start - position);
            var afterDelimiter = start + delimiter.Length;
            if (afterDelimiter >= template.Length)
            {
                if (!safe)
                {
                    throw InvalidPlaceholder(template, start);
                }
                output.Append(delimiter);
                break;
            }
            if (StartsWith(template, afterDelimiter, delimiter))
            {
                output.Append(delimiter);
                position = afterDelimiter + delimiter.Length;
                continue;
            }

            var braced = template[afterDelimiter] == '{';
            var nameStart = braced ? afterDelimiter + 1 : afterDelimiter;
            var pattern = braced
                ? delimiters.BracePattern ?? delimiters.IdPattern
                : delimiters.IdPattern;
            var name = MatchIdentifier(template, nameStart, pattern);
            var closed =
                braced
                && name.Length > 0
                && nameStart + name.Length < template.Length
                && template[nameStart + name.Length] == '}';
            if (name.Length == 0 || (braced && !closed))
            {
                if (!safe)
                {
                    throw InvalidPlaceholder(template, start);
                }
                output.Append(delimiter);
                position = afterDelimiter;
                continue;
            }

            var length = braced
                ? nameStart + name.Length + 1 - start
                : afterDelimiter + name.Length - start;
            var placeholder = template.Substring(start, length);
            var replacement = resolve(placeholder, name);
            if (replacement is null)
            {
                if (!safe)
                {
                    throw MissingPlaceholder(name);
                }
                output.Append(placeholder);
            }
            else
            {
                output.Append(replacement);
            }
            position = start + length;
        }
        return output.ToString();
    }

    private static bool StartsWith(string text, int position, string value) =>
        position + value.Length <= text.Length
        && string.CompareOrdinal(text, position, value, 0, value.Length) == 0;

    /// <summary>
    /// The identifier a pattern matches at a position. The patterns real templates use are
    /// bracket sets — `[_a-z][_a-z0-9]*`, `[a-z]+` — and those are what this reads; any
    /// other pattern falls back to CPython's default rule, which is also the rule when a
    /// pattern says nothing else.
    /// </summary>
    private static string MatchIdentifier(string text, int position, string pattern)
    {
        var sets = BracketSets(Unwrap(pattern));
        if (sets.Count == 0)
        {
            sets = BracketSets(Unwrap(DefaultIdPattern));
        }
        // CPython's own patterns carry `re.IGNORECASE`, so a set matches either case.
        var leading = Fold(sets[0]);
        var trailing = Fold(sets.Count > 1 ? sets[1] : sets[0]);
        var length = 0;
        if (
            position < text.Length
            && leading.Contains(char.ToUpperInvariant(text[position]), StringComparison.Ordinal)
        )
        {
            length = 1;
        }
        else
        {
            return string.Empty;
        }
        while (
            position + length < text.Length
            && trailing.Contains(
                char.ToUpperInvariant(text[position + length]),
                StringComparison.Ordinal
            )
        )
        {
            length++;
        }
        return text.Substring(position, length);
    }

    /// <summary>A character set with every letter in one case, which is what the flags mean.</summary>
    private static string Fold(string characters) => characters.ToUpperInvariant();

    /// <summary>`(?a:...)`, the ASCII-only wrapper CPython's own patterns carry.</summary>
    private static string Unwrap(string pattern) =>
        pattern.StartsWith("(?a:", StringComparison.Ordinal) && pattern.EndsWith(')')
            ? pattern[4..^1]
            : pattern;

    /// <summary>The bracket sets a pattern is made of, or none when it is something else.</summary>
    private static List<string> BracketSets(string pattern)
    {
        var sets = new List<string>();
        var position = 0;
        while (position < pattern.Length)
        {
            if (pattern[position] != '[')
            {
                return [];
            }
            var end = pattern.IndexOf(']', position + 1);
            if (end < 0)
            {
                return [];
            }
            sets.Add(ExpandRanges(pattern.Substring(position + 1, end - position - 1)));
            position = end + 1;
            while (position < pattern.Length && pattern[position] is '+' or '*' or '?')
            {
                position++;
            }
        }
        return sets;
    }

    /// <summary>`a-z` written out, with everything else standing for itself.</summary>
    private static string ExpandRanges(string characters)
    {
        var builder = new StringBuilder(characters.Length);
        for (var index = 0; index < characters.Length; index++)
        {
            if (index + 2 < characters.Length && characters[index + 1] == '-')
            {
                for (var code = characters[index]; code <= characters[index + 2]; code++)
                {
                    builder.Append(code);
                }
                index += 2;
                continue;
            }
            builder.Append(characters[index]);
        }
        return builder.ToString();
    }

    private static PythonRuntimeException InvalidPlaceholder(string template, int position)
    {
        // CPython counts the line and the column of the placeholder's own start.
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < position; index++)
        {
            if (template[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        return ManagedObjectProtocols.Fault(
            "DPY4012",
            $"Invalid placeholder in string: line {line}, col {position - lineStart + 1}",
            default,
            "ValueError"
        );
    }

    /// <summary>A `KeyError` carrying the name, whose `str` shows the name's repr.</summary>
    private static PythonRuntimeException MissingPlaceholder(string name) =>
        new("DPY4011", new PythonTextValue(name).ToRepresentationString(), default, "KeyError");
}
