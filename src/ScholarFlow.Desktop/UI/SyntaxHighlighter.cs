using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ScholarFlow.Core.Models;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Lightweight, zero-dependency, theme-aware syntax highlighter for C/C++/C#/Java, Python,
/// JavaScript/TypeScript, SQL, Rust/Go, Bash, JSON/YAML, and Markdown.
/// Populates Avalonia <see cref="TextBlock.Inlines"/> directly with harmonious Dark &amp; Light palettes.
/// </summary>
public static class SyntaxHighlighter
{
    private readonly record struct Palette(
        IBrush Default,
        IBrush Keyword,
        IBrush TypeName,
        IBrush StringLit,
        IBrush Comment,
        IBrush Number,
        IBrush Function,
        IBrush Directive
    );

    private static readonly Palette DarkPalette = new(
        Default: new SolidColorBrush(Color.Parse("#E5E9F0")),
        Keyword: new SolidColorBrush(Color.Parse("#C792EA")),
        TypeName: new SolidColorBrush(Color.Parse("#82AAFF")),
        StringLit: new SolidColorBrush(Color.Parse("#C3E88D")),
        Comment: new SolidColorBrush(Color.Parse("#6E7787")),
        Number: new SolidColorBrush(Color.Parse("#F78C6C")),
        Function: new SolidColorBrush(Color.Parse("#89DDFF")),
        Directive: new SolidColorBrush(Color.Parse("#FFCB6B"))
    );

    private static readonly Palette LightPalette = new(
        Default: new SolidColorBrush(Color.Parse("#1E2024")),
        Keyword: new SolidColorBrush(Color.Parse("#7C3AED")),
        TypeName: new SolidColorBrush(Color.Parse("#1D4ED8")),
        StringLit: new SolidColorBrush(Color.Parse("#15803D")),
        Comment: new SolidColorBrush(Color.Parse("#808692")),
        Number: new SolidColorBrush(Color.Parse("#C2410C")),
        Function: new SolidColorBrush(Color.Parse("#0369A1")),
        Directive: new SolidColorBrush(Color.Parse("#B45309"))
    );

    private static readonly HashSet<string> CommonKeywords = new(StringComparer.Ordinal)
    {
        "if",
        "else",
        "elif",
        "for",
        "foreach",
        "while",
        "do",
        "switch",
        "case",
        "default",
        "break",
        "continue",
        "return",
        "yield",
        "try",
        "catch",
        "except",
        "finally",
        "throw",
        "raise",
        "new",
        "delete",
        "class",
        "struct",
        "interface",
        "enum",
        "record",
        "namespace",
        "using",
        "import",
        "from",
        "export",
        "package",
        "module",
        "public",
        "private",
        "protected",
        "internal",
        "static",
        "readonly",
        "const",
        "final",
        "virtual",
        "override",
        "abstract",
        "sealed",
        "async",
        "await",
        "var",
        "let",
        "val",
        "def",
        "fn",
        "func",
        "function",
        "lambda",
        "in",
        "is",
        "as",
        "not",
        "and",
        "or",
        "true",
        "false",
        "True",
        "False",
        "null",
        "None",
        "nil",
        "nullptr",
        "this",
        "self",
        "super",
        "base",
        "with",
        "pass",
        "sizeof",
        "typeof",
        "nameof",
        "ref",
        "out",
        "params",
        "where",
        "match",
        "mut",
        "pub",
        "impl",
        "trait",
        "use",
        "mod",
        "defer",
        "go",
        "chan",
        "range",
        "then",
        "fi",
        "esac",
        "done",
        "echo",
        "exit",
        "local",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT",
        "FROM",
        "WHERE",
        "INSERT",
        "INTO",
        "VALUES",
        "UPDATE",
        "SET",
        "DELETE",
        "CREATE",
        "TABLE",
        "VIEW",
        "INDEX",
        "DROP",
        "ALTER",
        "ADD",
        "JOIN",
        "INNER",
        "LEFT",
        "RIGHT",
        "OUTER",
        "FULL",
        "ON",
        "GROUP",
        "BY",
        "ORDER",
        "ASC",
        "DESC",
        "HAVING",
        "LIMIT",
        "OFFSET",
        "DISTINCT",
        "AS",
        "AND",
        "OR",
        "NOT",
        "IN",
        "IS",
        "NULL",
        "LIKE",
        "BETWEEN",
        "EXISTS",
        "CASE",
        "WHEN",
        "THEN",
        "ELSE",
        "END",
        "PRIMARY",
        "KEY",
        "FOREIGN",
        "REFERENCES",
        "UNION",
        "ALL",
        "COUNT",
        "SUM",
        "AVG",
        "MIN",
        "MAX",
        "BEGIN",
        "COMMIT",
        "ROLLBACK",
        "TRANSACTION",
        "CONSTRAINT",
        "UNIQUE",
        "DEFAULT",
    };

    private static readonly HashSet<string> CommonTypes = new(StringComparer.Ordinal)
    {
        "void",
        "int",
        "long",
        "short",
        "byte",
        "sbyte",
        "uint",
        "ulong",
        "float",
        "double",
        "decimal",
        "bool",
        "boolean",
        "char",
        "string",
        "String",
        "object",
        "dynamic",
        "size_t",
        "uint8_t",
        "uint16_t",
        "uint32_t",
        "uint64_t",
        "int32_t",
        "int64_t",
        "i8",
        "i16",
        "i32",
        "i64",
        "u8",
        "u16",
        "u32",
        "u64",
        "f32",
        "f64",
        "usize",
        "isize",
        "str",
        "Vec",
        "Option",
        "Result",
        "List",
        "Dictionary",
        "HashSet",
        "Task",
        "IEnumerable",
        "IReadOnlyList",
        "Array",
        "Map",
        "Set",
        "Promise",
        "number",
        "any",
        "unknown",
        "never",
        "VARCHAR",
        "INTEGER",
        "TEXT",
        "REAL",
        "BLOB",
        "BOOLEAN",
        "TIMESTAMP",
        "DATE",
    };

    public static void PopulateInlines(TextBlock target, string code, string? language = null)
    {
        target.Inlines ??= [];
        target.Inlines.Clear();
        if (string.IsNullOrEmpty(code))
            return;

        var palette = ThemeService.CurrentTheme == ThemeMode.Dark ? DarkPalette : LightPalette;
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();

        if (lang is "markdown" or "md")
        {
            HighlightMarkdown(target.Inlines, code, palette);
            return;
        }

        if (lang is "text" or "txt" or "plain")
        {
            target.Inlines.Add(new Run(code) { Foreground = palette.Default });
            return;
        }

        HighlightCode(target.Inlines, code, lang, palette);
    }

    private static void HighlightMarkdown(InlineCollection inlines, string text, Palette p)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            if (li > 0)
                inlines.Add(new LineBreak());

            var line = lines[li];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                inlines.Add(new Run(line) { Foreground = p.Keyword, FontWeight = FontWeight.Bold });
            }
            else if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inlines.Add(new Run(line) { Foreground = p.Directive });
            }
            else if (trimmed.StartsWith('>'))
            {
                inlines.Add(new Run(line) { Foreground = p.Comment, FontStyle = FontStyle.Italic });
            }
            else if (
                trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
            )
            {
                int bulletIdx = line.IndexOfAny(['-', '*']);
                if (bulletIdx >= 0)
                {
                    inlines.Add(new Run(line[..(bulletIdx + 1)]) { Foreground = p.Number });
                    inlines.Add(new Run(line[(bulletIdx + 1)..]) { Foreground = p.Default });
                }
                else
                {
                    inlines.Add(new Run(line) { Foreground = p.Default });
                }
            }
            else
            {
                inlines.Add(new Run(line) { Foreground = p.Default });
            }
        }
    }

    private static void HighlightCode(InlineCollection inlines, string code, string lang, Palette p)
    {
        bool isHashComment =
            lang
            is "python"
                or "py"
                or "bash"
                or "sh"
                or "yaml"
                or "yml"
                or "ruby"
                or "powershell"
                or "ps1";
        bool isSql = lang is "sql";
        bool isJson = lang is "json";
        bool isCStyle = !isHashComment && !isSql && !isJson;

        int n = code.Length;
        int i = 0;

        while (i < n)
        {
            char ch = code[i];

            // Line comments: //, #, --
            if (
                (isCStyle && ch == '/' && i + 1 < n && code[i + 1] == '/')
                || (isHashComment && ch == '#')
                || (isSql && ch == '-' && i + 1 < n && code[i + 1] == '-')
            )
            {
                int start = i;
                while (i < n && code[i] != '\n')
                    i++;
                inlines.Add(
                    new Run(code[start..i]) { Foreground = p.Comment, FontStyle = FontStyle.Italic }
                );
                continue;
            }

            // Block comments: /* ... */
            if ((isCStyle || isSql) && ch == '/' && i + 1 < n && code[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/'))
                    i++;
                i = Math.Min(n, i + 2);
                inlines.Add(
                    new Run(code[start..i]) { Foreground = p.Comment, FontStyle = FontStyle.Italic }
                );
                continue;
            }

            // Preprocessor directives / decorators (#include, @Override)
            if ((isCStyle && ch == '#') || (ch == '@' && i + 1 < n && char.IsLetter(code[i + 1])))
            {
                int start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    i++;
                inlines.Add(new Run(code[start..i]) { Foreground = p.Directive });
                continue;
            }

            // String literals: "...", '...', `...`
            if (ch is '"' or '\'' or '`')
            {
                char quote = ch;
                int start = i;
                i++;
                while (i < n && code[i] != quote && code[i] != '\n')
                {
                    if (code[i] == '\\' && i + 1 < n)
                        i += 2;
                    else
                        i++;
                }
                if (i < n && code[i] == quote)
                    i++;

                // In JSON, property keys ("key":) get TypeName color; values get StringLit
                bool isJsonKey = false;
                if (isJson)
                {
                    int peek = i;
                    while (peek < n && code[peek] == ' ')
                        peek++;
                    isJsonKey = peek < n && code[peek] == ':';
                }

                inlines.Add(
                    new Run(code[start..i]) { Foreground = isJsonKey ? p.TypeName : p.StringLit }
                );
                continue;
            }

            // Numbers (hex 0x..., decimal, float)
            if (char.IsDigit(ch))
            {
                int start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_'))
                    i++;
                inlines.Add(new Run(code[start..i]) { Foreground = p.Number });
                continue;
            }

            // Identifiers, keywords, types, and function calls
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    i++;
                var ident = code[start..i];

                IBrush brush = p.Default;
                if (isSql && SqlKeywords.Contains(ident))
                    brush = p.Keyword;
                else if (!isJson && CommonKeywords.Contains(ident))
                    brush = p.Keyword;
                else if (CommonTypes.Contains(ident))
                    brush = p.TypeName;
                else
                {
                    int peek = i;
                    while (peek < n && code[peek] == ' ')
                        peek++;
                    if (peek < n && code[peek] == '(')
                        brush = p.Function;
                    else if (ident.Length >= 2 && char.IsUpper(ident[0]) && ident.Any(char.IsLower))
                        brush = p.TypeName;
                }

                inlines.Add(new Run(ident) { Foreground = brush });
                continue;
            }

            // Plain punctuation / whitespace run (batched for fast inline creation)
            int plainStart = i;
            i++;
            while (
                i < n
                && !char.IsLetterOrDigit(code[i])
                && code[i] is not ('_' or '"' or '\'' or '`' or '/' or '#' or '@' or '-')
            )
            {
                i++;
            }
            inlines.Add(new Run(code[plainStart..i]) { Foreground = p.Default });
        }
    }
}
