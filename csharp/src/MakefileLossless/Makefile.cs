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
    /// <summary>A whitespace-only line.</summary>
    BlankLine,
    /// <summary>A comment line.</summary>
    Comment,
    /// <summary>A dependency rule.</summary>
    Rule,
    /// <summary>A variable or macro assignment.</summary>
    VariableDefinition,
    /// <summary>An include statement.</summary>
    Include,
    /// <summary>A conditional directive.</summary>
    Conditional,
    /// <summary>A non-conditional make directive.</summary>
    Directive,
    /// <summary>A recipe/command line.</summary>
    Recipe,
    /// <summary>A line not recognized as a standard construct.</summary>
    Other
}

/// <summary>A lossless logical line, including its exact source spelling and line-ending bytes.</summary>
public sealed record MakefileItem(MakefileItemKind Kind, string Text, int Line, int Column);

/// <summary>A parse diagnostic. Parsing is recovery-oriented and always preserves the input.</summary>
public sealed record MakefileParseError(int Line, int Column, string Message);

/// <summary>
/// A lossless makefile parser with dialect-aware logical-line handling and
/// conservative construct classification. Unknown text is retained verbatim.
/// </summary>
public sealed class Makefile
{
    private static readonly Regex Assignment = new(
        @"^(?<name>[^:=+?!\s][^:=+?]*?)\s*(?<operator>:::=|::=|:=|\+=|\?=|!=|=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ReadOnlyCollection<MakefileItem> _items;
    private readonly ReadOnlyCollection<MakefileParseError> _errors;

    private Makefile(string source, MakefileVariant? variant, bool variantDetected,
        List<MakefileItem> items, List<MakefileParseError> errors)
    {
        Source = source;
        Variant = variant;
        VariantDetected = variantDetected;
        _items = items.AsReadOnly();
        _errors = errors.AsReadOnly();
    }

    /// <summary>The exact source text supplied to the parser.</summary>
    public string Source { get; }

    /// <summary>The dialect selected or detected for this parse, if any.</summary>
    public MakefileVariant? Variant { get; }

    /// <summary>True when the parser selected a dialect from the input.</summary>
    public bool VariantDetected { get; }

    /// <summary>Losslessly preserved logical lines in source order.</summary>
    public IReadOnlyList<MakefileItem> Items => _items;

    /// <summary>Diagnostics found while parsing. The source remains available even when errors are present.</summary>
    public IReadOnlyList<MakefileParseError> Errors => _errors;

    /// <summary>Whether parsing produced no diagnostics.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>Parse using syntax accepted by the supported make variants.</summary>
    public static Makefile Parse(string source) => ParseCore(source, null, detectVariant: true);

    /// <summary>Parse while restricting syntax to the selected make dialect.</summary>
    public static Makefile ParseWithVariant(string source, MakefileVariant variant) =>
        ParseCore(source, variant, detectVariant: false);

    /// <summary>Read and parse a makefile without normalizing its line endings.</summary>
    public static Makefile FromReader(TextReader reader, MakefileVariant? variant = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = reader.ReadToEnd();
        return variant is { } selected ? ParseWithVariant(source, selected) : Parse(source);
    }

    /// <summary>Reconstruct the original input exactly.</summary>
    public override string ToString() => string.Concat(_items.Select(item => item.Text));

    private static Makefile ParseCore(string source, MakefileVariant? requestedVariant, bool detectVariant)
    {
        ArgumentNullException.ThrowIfNull(source);
        var variant = requestedVariant;
        var variantDetected = false;
        if (detectVariant && TryDetectNMake(source))
        {
            variant = MakefileVariant.NMake;
            variantDetected = true;
        }

        var items = new List<MakefileItem>();
        var errors = new List<MakefileParseError>();
        var offset = 0;
        var lineNumber = 0;
        var inRule = false;
        var recipePrefix = '\t';
        var conditionalDepth = 0;
        var forDepth = 0;

        while (offset < source.Length)
        {
            var start = offset;
            var firstLine = lineNumber;
            var logical = new StringBuilder();
            var physicalLines = new List<string>();
            var finalEnding = string.Empty;
            do
            {
                var newline = source.IndexOf('\n', offset);
                var end = newline < 0 ? source.Length : newline + 1;
                var physical = source.AsSpan(offset, end - offset);
                finalEnding = GetLineEnding(physical);
                var contentLength = physical.Length - finalEnding.Length;
                var content = physical[..contentLength].ToString();
                physicalLines.Add(content);
                logical.Append(content);
                offset = end;
                lineNumber++;
                if (!Continues(physical, finalEnding, variant, logical.ToString(), inRule) || offset >= source.Length)
                    break;
            } while (true);

            var raw = source[start..offset];
            var lineText = logical.ToString();
            var kind = Classify(lineText, variant, inRule, recipePrefix);
            items.Add(new MakefileItem(kind, raw, firstLine, 0));
            ValidateLine(lineText, kind, variant, firstLine, errors, ref conditionalDepth, ref forDepth);

            if (kind == MakefileItemKind.Rule)
                inRule = true;
            else if (kind is not (MakefileItemKind.BlankLine or MakefileItemKind.Comment or MakefileItemKind.Recipe))
                inRule = false;

            if (variant is null or MakefileVariant.GnuMake)
                recipePrefix = UpdateRecipePrefix(lineText, recipePrefix);
        }

        if (conditionalDepth > 0)
            errors.Add(new MakefileParseError(Math.Max(0, lineNumber - 1), 0, "Conditional directive is not closed."));
        if (forDepth > 0)
            errors.Add(new MakefileParseError(Math.Max(0, lineNumber - 1), 0, "BSD .for loop is not closed."));

        return new Makefile(source, variant, variantDetected, items, errors);
    }

    private static string GetLineEnding(ReadOnlySpan<char> physical)
    {
        if (physical.Length == 0 || physical[^1] != '\n')
            return string.Empty;
        return physical.Length > 1 && physical[^2] == '\r' ? "\r\n" : "\n";
    }

    private static bool Continues(ReadOnlySpan<char> physical, string ending, MakefileVariant? variant,
        string logicalLine, bool inRule)
    {
        if (ending.Length == 0)
            return false;
        var contentEnd = physical.Length - ending.Length;
        var backslashes = 0;
        for (var i = contentEnd - 1; i >= 0 && physical[i] == '\\'; i--)
            backslashes++;
        if ((backslashes & 1) != 0)
            return true;

        if (variant != MakefileVariant.NMake || !IsNMakeMacroDefinition(logicalLine, inRule))
            return false;
        var carets = 0;
        for (var i = contentEnd - 1; i >= 0 && physical[i] == '^'; i--)
            carets++;
        return (carets & 1) != 0;
    }

    private static bool IsNMakeMacroDefinition(string line, bool inRule) =>
        !inRule && Assignment.Match(line.TrimStart()).Success && line.TrimStart().Contains('=');

    private static MakefileItemKind Classify(string text, MakefileVariant? variant, bool inRule, char recipePrefix)
    {
        var trimmed = text.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
            return MakefileItemKind.BlankLine;
        if (IsCommentLine(trimmed, variant))
            return MakefileItemKind.Comment;

        if (StartsRecipe(text, variant, inRule, recipePrefix))
            return MakefileItemKind.Recipe;
        if (IsConditional(trimmed, variant))
            return MakefileItemKind.Conditional;
        if (IsInclude(trimmed, variant))
            return MakefileItemKind.Include;
        if (IsAssignment(text, variant))
            return MakefileItemKind.VariableDefinition;
        if (HasRuleSeparator(text, variant))
            return MakefileItemKind.Rule;
        if (IsDirective(trimmed, variant))
            return MakefileItemKind.Directive;
        return MakefileItemKind.Other;
    }

    private static bool StartsRecipe(string text, MakefileVariant? variant, bool inRule, char prefix)
    {
        if (text.Length == 0)
            return false;
        if (text[0] == prefix && prefix != '\n')
            return true;
        return variant == MakefileVariant.NMake && inRule && text[0] == ' ';
    }

    private static bool IsCommentLine(string text, MakefileVariant? variant)
    {
        if (!text.StartsWith('#'))
            return false;
        if (variant == MakefileVariant.NMake)
            return true;
        var slashes = 0;
        for (var i = text.Length - 2; i >= 0 && text[i] == '\\'; i--)
            slashes++;
        return (slashes & 1) == 0;
    }

    private static bool IsAssignment(string text, MakefileVariant? variant)
    {
        var trimmed = text.TrimStart();
        if (variant is null or MakefileVariant.GnuMake)
        {
            foreach (var modifier in new[] { "override ", "export ", "unexport ", "private ", "define " })
                if (trimmed.StartsWith(modifier, StringComparison.Ordinal))
                    trimmed = trimmed[modifier.Length..].TrimStart();
        }
        if (variant == MakefileVariant.NMake)
            return Regex.IsMatch(trimmed, @"^[^:=\s]+\s*=(?!=)", RegexOptions.CultureInvariant);
        return Assignment.Match(trimmed).Success;
    }

    private static bool IsConditional(string text, MakefileVariant? variant)
    {
        var gnu = text.StartsWith("ifdef", StringComparison.Ordinal) || text.StartsWith("ifndef", StringComparison.Ordinal) ||
                  text.StartsWith("ifeq", StringComparison.Ordinal) || text.StartsWith("ifneq", StringComparison.Ordinal) ||
                  IsKeyword(text, "else") || IsKeyword(text, "endif");
        var bsd = text.StartsWith(".if", StringComparison.Ordinal) || text.StartsWith(".elif", StringComparison.Ordinal) ||
                  IsKeyword(text, ".else") || IsKeyword(text, ".endif");
        var nmake = text.StartsWith("!IF", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("!ELSE", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("!ENDIF", StringComparison.OrdinalIgnoreCase);
        return variant switch
        {
            MakefileVariant.GnuMake => gnu,
            MakefileVariant.BsdMake => bsd,
            MakefileVariant.NMake => nmake,
            MakefileVariant.PosixMake => false,
            _ => gnu || bsd || nmake
        };
    }

    private static bool IsInclude(string text, MakefileVariant? variant)
    {
        var gnu = IsKeyword(text, "include") || IsKeyword(text, "-include") || IsKeyword(text, "sinclude");
        var bsd = text.StartsWith(".include", StringComparison.Ordinal);
        var nmake = text.StartsWith("!INCLUDE", StringComparison.OrdinalIgnoreCase);
        return variant switch
        {
            MakefileVariant.GnuMake => gnu,
            MakefileVariant.BsdMake => bsd || gnu,
            MakefileVariant.NMake => nmake,
            MakefileVariant.PosixMake => IsKeyword(text, "include"),
            _ => gnu || bsd || nmake
        };
    }

    private static bool IsDirective(string text, MakefileVariant? variant)
    {
        if (variant == MakefileVariant.NMake)
            return text.StartsWith('!');
        if (variant == MakefileVariant.BsdMake)
            return text.StartsWith('.') && !text.StartsWith(".include", StringComparison.Ordinal) &&
                   !text.StartsWith(".if", StringComparison.Ordinal) && !text.StartsWith(".else", StringComparison.Ordinal) &&
                   !text.StartsWith(".endif", StringComparison.Ordinal) && !text.StartsWith(".for", StringComparison.Ordinal) &&
                   !text.StartsWith(".endfor", StringComparison.Ordinal);
        return (variant is null or MakefileVariant.GnuMake) &&
               (IsKeyword(text, "define") || IsKeyword(text, "endef") || IsKeyword(text, "export") ||
                IsKeyword(text, "unexport") || IsKeyword(text, "undefine") || IsKeyword(text, "vpath") ||
                IsKeyword(text, "load") || IsKeyword(text, "override"));
    }

    private static bool HasRuleSeparator(string text, MakefileVariant? variant)
    {
        var round = 0;
        var curly = 0;
        var reference = 0;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
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
            if (c == '$' && i + 1 < text.Length && text[i + 1] is '(' or '{')
            {
                reference++;
                if (text[++i] == '(') round++; else curly++;
                continue;
            }
            if (reference > 0)
            {
                if (c == '(') round++;
                else if (c == ')' && round > 0) { round--; if (round == 0 && curly == 0) reference--; }
                else if (c == '{') curly++;
                else if (c == '}' && curly > 0) { curly--; if (round == 0 && curly == 0) reference--; }
                continue;
            }
            if (c == '#')
            {
                var before = i - 1;
                while (before >= 0 && text[before] == '\\') before--;
                if ((i - before - 1) % 2 == 0 || variant == MakefileVariant.BsdMake)
                    break;
            }
            if (c == ':' && (round == 0 && curly == 0))
                return true;
        }
        return false;
    }

    private static bool IsKeyword(string text, string keyword) =>
        text.Equals(keyword, StringComparison.Ordinal) ||
        text.StartsWith(keyword + " ", StringComparison.Ordinal) ||
        text.StartsWith(keyword + "\t", StringComparison.Ordinal);

    private static void ValidateLine(string text, MakefileItemKind kind, MakefileVariant? variant, int line,
        List<MakefileParseError> errors, ref int conditionalDepth, ref int forDepth)
    {
        var trimmed = text.TrimStart(' ', '\t');
        var explicitVariant = variant;
        if (kind == MakefileItemKind.Other && trimmed.Length > 0 && trimmed[0] != '#')
            errors.Add(new MakefileParseError(line, 0, "Unrecognized makefile line."));

        if (explicitVariant == MakefileVariant.PosixMake &&
            (trimmed.StartsWith("ifdef", StringComparison.Ordinal) || trimmed.StartsWith("ifndef", StringComparison.Ordinal) ||
             trimmed.StartsWith("ifeq", StringComparison.Ordinal) || trimmed.StartsWith("ifneq", StringComparison.Ordinal) ||
             trimmed.StartsWith(".if", StringComparison.Ordinal) || trimmed.StartsWith("!IF", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith(".for", StringComparison.Ordinal) || trimmed.StartsWith("override ", StringComparison.Ordinal)))
            errors.Add(new MakefileParseError(line, 0, "This construct is not part of POSIX make."));

        if (explicitVariant == MakefileVariant.GnuMake && trimmed.StartsWith(".if", StringComparison.Ordinal))
            errors.Add(new MakefileParseError(line, 0, "BSD conditional syntax is not valid GNU make syntax."));
        if (explicitVariant == MakefileVariant.BsdMake &&
            (trimmed.StartsWith("ifdef", StringComparison.Ordinal) || trimmed.StartsWith("ifndef", StringComparison.Ordinal) ||
             trimmed.StartsWith("ifeq", StringComparison.Ordinal) || trimmed.StartsWith("ifneq", StringComparison.Ordinal)))
            errors.Add(new MakefileParseError(line, 0, "GNU conditional syntax is not valid BSD make syntax."));
        if (explicitVariant is MakefileVariant.GnuMake or MakefileVariant.BsdMake or MakefileVariant.PosixMake &&
            trimmed.StartsWith('!'))
            errors.Add(new MakefileParseError(line, 0, "nmake directive syntax is not valid for this make variant."));

        var startIf = IsConditionalStart(trimmed, explicitVariant);
        var isElse = IsConditionalElse(trimmed, explicitVariant);
        var isEnd = IsConditionalEnd(trimmed, explicitVariant);
        if (startIf)
            conditionalDepth++;
        else if (isElse && conditionalDepth == 0)
            errors.Add(new MakefileParseError(line, 0, "Conditional else has no matching if."));
        else if (isEnd)
        {
            if (conditionalDepth == 0)
                errors.Add(new MakefileParseError(line, 0, "Conditional end has no matching if."));
            else
                conditionalDepth--;
        }

        if (explicitVariant == MakefileVariant.BsdMake)
        {
            if (trimmed.StartsWith(".for", StringComparison.Ordinal)) forDepth++;
            else if (trimmed.StartsWith(".endfor", StringComparison.Ordinal))
            {
                if (forDepth == 0) errors.Add(new MakefileParseError(line, 0, ".endfor has no matching .for."));
                else forDepth--;
            }
        }
    }

    private static bool IsConditionalStart(string text, MakefileVariant? variant) => variant switch
    {
        MakefileVariant.GnuMake => text.StartsWith("ifdef ", StringComparison.Ordinal) || text.StartsWith("ifndef ", StringComparison.Ordinal) ||
                                   text.StartsWith("ifeq", StringComparison.Ordinal) || text.StartsWith("ifneq", StringComparison.Ordinal),
        MakefileVariant.BsdMake => text.StartsWith(".if", StringComparison.Ordinal) && !text.StartsWith(".ifdef", StringComparison.Ordinal) == false ||
                                   text.StartsWith(".if ", StringComparison.Ordinal) || text.StartsWith(".ifdef", StringComparison.Ordinal) || text.StartsWith(".ifndef", StringComparison.Ordinal),
        MakefileVariant.NMake => text.StartsWith("!IF ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("!IFDEF ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("!IFNDEF ", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsConditionalElse(string text, MakefileVariant? variant) => variant switch
    {
        MakefileVariant.GnuMake => IsKeyword(text, "else"),
        MakefileVariant.BsdMake => IsKeyword(text, ".else") || text.StartsWith(".elif", StringComparison.Ordinal),
        MakefileVariant.NMake => text.StartsWith("!ELSE", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsConditionalEnd(string text, MakefileVariant? variant) => variant switch
    {
        MakefileVariant.GnuMake => IsKeyword(text, "endif"),
        MakefileVariant.BsdMake => IsKeyword(text, ".endif"),
        MakefileVariant.NMake => text.StartsWith("!ENDIF", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static char UpdateRecipePrefix(string text, char current)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith(".RECIPEPREFIX", StringComparison.Ordinal))
            return current;
        var assignment = Assignment.Match(trimmed[".RECIPEPREFIX".Length..].TrimStart());
        if (!assignment.Success)
            return current;
        var rest = trimmed[".RECIPEPREFIX".Length..].TrimStart();
        var op = new[] { ":::", "::", ":", "+", "?", "!", "=" }
            .Select(candidate => candidate + "=")
            .FirstOrDefault(candidate => rest.StartsWith(candidate, StringComparison.Ordinal));
        if (op is null || op == "?=")
            return current;
        var value = rest[op.Length..].TrimStart();
        var comment = value.IndexOf('#');
        if (comment >= 0) value = value[..comment];
        value = value.TrimEnd();
        return value.Length == 0 ? '\t' : value[0];
    }

    private static bool TryDetectNMake(string source)
    {
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith("!IF", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("!INCLUDE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("!MESSAGE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("!UNDEF", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("!CMDSWITCHES", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
