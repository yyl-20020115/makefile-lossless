using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace MakefileLossless;

/// <summary>The make dialect used to interpret a makefile.</summary>
public enum MakefileVariant
{
    /// <summary>GNU make.</summary>
    GnuMake,
    /// <summary>BSD make, including bmake.</summary>
    BsdMake,
    /// <summary>Microsoft nmake.</summary>
    NMake,
    /// <summary>The POSIX make subset.</summary>
    PosixMake
}

/// <summary>The broad syntactic category of a losslessly preserved logical line.</summary>
public enum MakefileItemKind
{
    BlankLine,
    Comment,
    Rule,
    VariableDefinition,
    Include,
    Conditional,
    Directive,
    Recipe,
    Other
}

/// <summary>A logical line and its exact original spelling, including line endings.</summary>
public sealed record MakefileItem(MakefileItemKind Kind, string Text, int Line, int Column);

/// <summary>
/// A lossless, read-only first-stage C# parser for makefiles.
/// Every parsed item retains its original text, including whitespace, comments,
/// continuations, and line endings.
/// </summary>
public sealed class Makefile
{
    private static readonly Regex Assignment = new(
        @"^[^:=+?!\s][^:=+?]*\s*(?::=|\+=|\?=|!=|::=|=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ReadOnlyCollection<MakefileItem> _items;

    private Makefile(string source, MakefileVariant? variant, List<MakefileItem> items)
    {
        Source = source;
        Variant = variant;
        _items = items.AsReadOnly();
    }

    /// <summary>The exact source text supplied to the parser.</summary>
    public string Source { get; }

    /// <summary>The dialect explicitly selected for this parse, if any.</summary>
    public MakefileVariant? Variant { get; }

    /// <summary>Losslessly preserved logical lines in source order.</summary>
    public IReadOnlyList<MakefileItem> Items => _items;

    /// <summary>Parse using the generic syntax accepted across make variants.</summary>
    public static Makefile Parse(string source) => ParseCore(source, null);

    /// <summary>Parse while classifying syntax in the context of a make dialect.</summary>
    public static Makefile ParseWithVariant(string source, MakefileVariant variant) => ParseCore(source, variant);

    /// <summary>Read and parse a makefile without normalizing its encoding or line endings.</summary>
    public static Makefile FromReader(TextReader reader, MakefileVariant? variant = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = reader.ReadToEnd();
        return ParseCore(source, variant);
    }

    /// <summary>Reconstruct the original input exactly.</summary>
    public override string ToString() => string.Concat(_items.Select(item => item.Text));

    private static Makefile ParseCore(string source, MakefileVariant? variant)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = new List<MakefileItem>();
        var offset = 0;
        var lineNumber = 0;
        while (offset < source.Length)
        {
            var start = offset;
            var firstLine = lineNumber;
            var logical = new StringBuilder();
            string ending;
            do
            {
                var newline = source.IndexOf('\n', offset);
                var end = newline < 0 ? source.Length : newline + 1;
                var physical = source.AsSpan(offset, end - offset);
                ending = newline < 0 ? string.Empty : physical[^1] == '\n' && physical.Length >= 2 && physical[^2] == '\r' ? "\r\n" : "\n";
                var contentLength = physical.Length - ending.Length;
                logical.Append(physical[..contentLength]);
                offset = end;
                lineNumber++;
                if (!Continues(physical, ending, variant) || offset >= source.Length)
                    break;
            } while (true);

            var raw = source[start..offset];
            var lineText = logical.ToString();
            items.Add(new MakefileItem(Classify(lineText, variant), raw, firstLine, 0));
        }

        return new Makefile(source, variant, items);
    }

    private static bool Continues(ReadOnlySpan<char> physical, string ending, MakefileVariant? variant)
    {
        if (ending.Length == 0)
            return false;
        var count = 0;
        for (var i = physical.Length - ending.Length - 1; i >= 0 && physical[i] == '\\'; i--)
            count++;
        if (count % 2 == 0)
            return variant == MakefileVariant.NMake && physical.Length >= ending.Length + 2 &&
                   physical[^ending.Length - 2] == '^' && physical[^ending.Length - 1] == '\\';
        return true;
    }

    private static MakefileItemKind Classify(string text, MakefileVariant? variant)
    {
        var trimmed = text.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
            return MakefileItemKind.BlankLine;
        if (trimmed[0] == '#')
            return MakefileItemKind.Comment;
        if (text.Length > 0 && (text[0] == '\t' || text[0] == ' ' && variant == MakefileVariant.NMake))
            return MakefileItemKind.Recipe;
        if (IsConditional(trimmed, variant))
            return MakefileItemKind.Conditional;
        if (IsInclude(trimmed, variant))
            return MakefileItemKind.Include;
        if (Assignment.IsMatch(text))
            return MakefileItemKind.VariableDefinition;
        if (HasRuleSeparator(text))
            return MakefileItemKind.Rule;
        if (trimmed.StartsWith('.', StringComparison.Ordinal) || trimmed.StartsWith('!'))
            return MakefileItemKind.Directive;
        return MakefileItemKind.Other;
    }

    private static bool IsConditional(string text, MakefileVariant? variant) =>
        text.StartsWith("ifdef ", StringComparison.Ordinal) ||
        text.StartsWith("ifndef ", StringComparison.Ordinal) ||
        text.StartsWith("ifeq", StringComparison.Ordinal) ||
        text.StartsWith("ifneq", StringComparison.Ordinal) ||
        text.StartsWith("else", StringComparison.Ordinal) ||
        text.StartsWith("endif", StringComparison.Ordinal) ||
        text.StartsWith(".if", StringComparison.Ordinal) ||
        text.StartsWith(".elif", StringComparison.Ordinal) ||
        text.StartsWith(".else", StringComparison.Ordinal) ||
        text.StartsWith(".endif", StringComparison.Ordinal) ||
        (variant == MakefileVariant.NMake && text.StartsWith('!'));

    private static bool IsInclude(string text, MakefileVariant? variant) =>
        text.StartsWith("include ", StringComparison.Ordinal) ||
        text.StartsWith("-include ", StringComparison.Ordinal) ||
        text.StartsWith("sinclude ", StringComparison.Ordinal) ||
        (variant == MakefileVariant.BsdMake && text.StartsWith(".include", StringComparison.Ordinal)) ||
        (variant == MakefileVariant.NMake && text.StartsWith("!INCLUDE", StringComparison.OrdinalIgnoreCase));

    private static bool HasRuleSeparator(string text)
    {
        var round = 0;
        var curly = 0;
        var escaped = false;
        foreach (var c in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            switch (c)
            {
                case '$':
                    // References commonly contain colons; the simple scan below
                    // conservatively handles the delimiters on following chars.
                    break;
                case '(':
                    round++;
                    break;
                case ')' when round > 0:
                    round--;
                    break;
                case '{':
                    curly++;
                    break;
                case '}' when curly > 0:
                    curly--;
                    break;
                case ':' when round == 0 && curly == 0:
                    return true;
            }
        }
        return false;
    }
}
