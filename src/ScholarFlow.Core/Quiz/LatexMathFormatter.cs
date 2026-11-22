using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Shared, token-safe LaTeX math formatter and lightweight numeric/math answer equivalence engine.
/// Uses single-pass command tokenization so shorter commands (e.g. \in, \le) never corrupt longer ones (\int, \leftarrow).
/// Supports a pluggable high-DPI TeX-to-PNG rasterizer (registered by Desktop via CSharpMath.SkiaSharp) for display equations.
/// </summary>
public static partial class LatexMathFormatter
{
    /// <summary>
    /// Optional TeX-to-PNG rasterizer registered by the Desktop host (using CSharpMath.SkiaSharp).
    /// Parameters: (rawLatex, fontSizePt, argbTextColor) -> PNG bytes, or null to fall back to Unicode text.
    /// </summary>
    public static Func<string, float, uint, byte[]?>? CustomLatexPngRenderer { get; set; }

    private static readonly Dictionary<string, string> CommandSymbols = new(
        StringComparer.Ordinal
    )
    {
        // Logic & Implications
        [@"\implies"] = "⇒",
        [@"\impliedby"] = "⇐",
        [@"\iff"] = "⇔",
        [@"\Rightarrow"] = "⇒",
        [@"\Leftarrow"] = "⇐",
        [@"\Leftrightarrow"] = "⇔",
        [@"\Longrightarrow"] = "⇒",
        [@"\Longleftarrow"] = "⇐",
        [@"\Longleftrightarrow"] = "⇔",
        [@"\rightarrow"] = "→",
        [@"\leftarrow"] = "←",
        [@"\leftrightarrow"] = "↔",
        [@"\longrightarrow"] = "→",
        [@"\longleftarrow"] = "←",
        [@"\mapsto"] = "↦",
        [@"\to"] = "→",
        [@"\gets"] = "←",
        [@"\uparrow"] = "↑",
        [@"\downarrow"] = "↓",
        [@"\forall"] = "∀",
        [@"\exists"] = "∃",
        [@"\nexists"] = "∄",
        [@"\land"] = "∧",
        [@"\wedge"] = "∧",
        [@"\lor"] = "∨",
        [@"\vee"] = "∨",
        [@"\lnot"] = "¬",
        [@"\neg"] = "¬",
        [@"\top"] = "⊤",
        [@"\bot"] = "⊥",
        [@"\vdash"] = "⊢",
        [@"\models"] = "⊨",
        [@"\therefore"] = "∴",
        [@"\because"] = "∵",
        [@"\qed"] = "∎",

        // Relations & Number Theory
        [@"\leq"] = "≤",
        [@"\le"] = "≤",
        [@"\geq"] = "≥",
        [@"\ge"] = "≥",
        [@"\neq"] = "≠",
        [@"\ne"] = "≠",
        [@"\approx"] = "≈",
        [@"\equiv"] = "≡",
        [@"\not\equiv"] = "≢",
        [@"\cong"] = "≅",
        [@"\sim"] = "∼",
        [@"\simeq"] = "≃",
        [@"\propto"] = "∝",
        [@"\ll"] = "≪",
        [@"\gg"] = "≫",
        [@"\prec"] = "≺",
        [@"\succ"] = "≻",
        [@"\preceq"] = "≼",
        [@"\succeq"] = "≽",
        [@"\mid"] = "∣",
        [@"\nmid"] = "∤",
        [@"\parallel"] = "∥",
        [@"\perp"] = "⊥",
        [@"\angle"] = "∠",
        [@"\triangle"] = "△",

        // Arithmetic & Binary Operators
        [@"\pm"] = "±",
        [@"\mp"] = "∓",
        [@"\times"] = "×",
        [@"\cdot"] = "·",
        [@"\div"] = "÷",
        [@"\ast"] = "∗",
        [@"\star"] = "⋆",
        [@"\circ"] = "∘",
        [@"\bullet"] = "•",
        [@"\oplus"] = "⊕",
        [@"\ominus"] = "⊖",
        [@"\otimes"] = "⊗",
        [@"\odot"] = "⊙",
        [@"\bmod"] = " mod ",
        [@"\mod"] = " mod ",
        [@"\degree"] = "°",
        [@"\prime"] = "′",

        // Sets
        [@"\in"] = "∈",
        [@"\notin"] = "∉",
        [@"\ni"] = "∋",
        [@"\subset"] = "⊂",
        [@"\subseteq"] = "⊆",
        [@"\supset"] = "⊃",
        [@"\supseteq"] = "⊇",
        [@"\nsubseteq"] = "⊈",
        [@"\cup"] = "∪",
        [@"\cap"] = "∩",
        [@"\bigcup"] = "⋃",
        [@"\bigcap"] = "⋂",
        [@"\setminus"] = "∖",
        [@"\emptyset"] = "∅",
        [@"\varnothing"] = "∅",
        [@"\aleph"] = "ℵ",

        // Calculus & Big Operators
        [@"\sum"] = "∑",
        [@"\prod"] = "∏",
        [@"\coprod"] = "∐",
        [@"\int"] = "∫",
        [@"\iint"] = "∬",
        [@"\iiint"] = "∭",
        [@"\oint"] = "∮",
        [@"\partial"] = "∂",
        [@"\nabla"] = "∇",
        [@"\infty"] = "∞",

        // Delimiters & Ellipsis
        [@"\lfloor"] = "⌊",
        [@"\rfloor"] = "⌋",
        [@"\lceil"] = "⌈",
        [@"\rceil"] = "⌉",
        [@"\langle"] = "⟨",
        [@"\rangle"] = "⟩",
        [@"\lvert"] = "|",
        [@"\rvert"] = "|",
        [@"\lVert"] = "‖",
        [@"\rVert"] = "‖",
        [@"\cdots"] = "⋯",
        [@"\ldots"] = "…",
        [@"\dots"] = "…",
        [@"\vdots"] = "⋮",
        [@"\ddots"] = "⋱",
        [@"\quad"] = "  ",
        [@"\qquad"] = "    ",
        [@"\enspace"] = " ",
        [@"\thinspace"] = " ",
        [@"\left"] = "",
        [@"\right"] = "",
        [@"\big"] = "",
        [@"\Big"] = "",
        [@"\bigg"] = "",
        [@"\Bigg"] = "",
        [@"\displaystyle"] = "",
        [@"\textstyle"] = "",

        // Greek Uppercase
        [@"\Gamma"] = "Γ",
        [@"\Delta"] = "Δ",
        [@"\Theta"] = "Θ",
        [@"\Lambda"] = "Λ",
        [@"\Xi"] = "Ξ",
        [@"\Pi"] = "Π",
        [@"\Sigma"] = "Σ",
        [@"\Upsilon"] = "Υ",
        [@"\Phi"] = "Φ",
        [@"\Psi"] = "Ψ",
        [@"\Omega"] = "Ω",

        // Greek Lowercase
        [@"\alpha"] = "α",
        [@"\beta"] = "β",
        [@"\gamma"] = "γ",
        [@"\delta"] = "δ",
        [@"\epsilon"] = "ε",
        [@"\varepsilon"] = "ε",
        [@"\zeta"] = "ζ",
        [@"\eta"] = "η",
        [@"\theta"] = "θ",
        [@"\vartheta"] = "ϑ",
        [@"\iota"] = "ι",
        [@"\kappa"] = "κ",
        [@"\lambda"] = "λ",
        [@"\mu"] = "μ",
        [@"\nu"] = "ν",
        [@"\xi"] = "ξ",
        [@"\pi"] = "π",
        [@"\varpi"] = "ϖ",
        [@"\rho"] = "ρ",
        [@"\varrho"] = "ϱ",
        [@"\sigma"] = "σ",
        [@"\varsigma"] = "ς",
        [@"\tau"] = "τ",
        [@"\upsilon"] = "υ",
        [@"\phi"] = "φ",
        [@"\varphi"] = "φ",
        [@"\chi"] = "χ",
        [@"\psi"] = "ψ",
        [@"\omega"] = "ω",

        // Named Functions
        [@"\log"] = "log",
        [@"\ln"] = "ln",
        [@"\exp"] = "exp",
        [@"\sin"] = "sin",
        [@"\cos"] = "cos",
        [@"\tan"] = "tan",
        [@"\sec"] = "sec",
        [@"\csc"] = "csc",
        [@"\cot"] = "cot",
        [@"\arcsin"] = "arcsin",
        [@"\arccos"] = "arccos",
        [@"\arctan"] = "arctan",
        [@"\sinh"] = "sinh",
        [@"\cosh"] = "cosh",
        [@"\tanh"] = "tanh",
        [@"\lim"] = "lim",
        [@"\limsup"] = "lim sup",
        [@"\liminf"] = "lim inf",
        [@"\sup"] = "sup",
        [@"\inf"] = "inf",
        [@"\max"] = "max",
        [@"\min"] = "min",
        [@"\arg"] = "arg",
        [@"\det"] = "det",
        [@"\dim"] = "dim",
        [@"\gcd"] = "gcd",
        [@"\lcm"] = "lcm",
        [@"\deg"] = "deg",
        [@"\ker"] = "ker",
        [@"\Pr"] = "Pr",
    };

    private static readonly Dictionary<char, string> BlackboardBoldMap = new()
    {
        ['N'] = "ℕ",
        ['Z'] = "ℤ",
        ['Q'] = "ℚ",
        ['R'] = "ℝ",
        ['C'] = "ℂ",
        ['F'] = "𝔽",
        ['P'] = "ℙ",
        ['E'] = "𝔼",
        ['H'] = "ℍ",
    };

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        ['0'] = '⁰',
        ['1'] = '¹',
        ['2'] = '²',
        ['3'] = '³',
        ['4'] = '⁴',
        ['5'] = '⁵',
        ['6'] = '⁶',
        ['7'] = '⁷',
        ['8'] = '⁸',
        ['9'] = '⁹',
        ['+'] = '⁺',
        ['-'] = '⁻',
        ['='] = '⁼',
        ['('] = '⁽',
        [')'] = '⁾',
        ['a'] = 'ᵃ',
        ['b'] = 'ᵇ',
        ['c'] = 'ᶜ',
        ['d'] = 'ᵈ',
        ['e'] = 'ᵉ',
        ['f'] = 'ᶠ',
        ['g'] = 'ᵍ',
        ['h'] = 'ʰ',
        ['i'] = 'ⁱ',
        ['j'] = 'ʲ',
        ['k'] = 'ᵏ',
        ['l'] = 'ˡ',
        ['m'] = 'ᵐ',
        ['n'] = 'ⁿ',
        ['o'] = 'ᵒ',
        ['p'] = 'ᵖ',
        ['r'] = 'ʳ',
        ['s'] = 'ˢ',
        ['t'] = 'ᵗ',
        ['u'] = 'ᵘ',
        ['v'] = 'ᵛ',
        ['w'] = 'ʷ',
        ['x'] = 'ˣ',
        ['y'] = 'ʸ',
        ['z'] = 'ᶻ',
        ['T'] = 'ᵀ',
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        ['0'] = '₀',
        ['1'] = '₁',
        ['2'] = '₂',
        ['3'] = '₃',
        ['4'] = '₄',
        ['5'] = '₅',
        ['6'] = '₆',
        ['7'] = '₇',
        ['8'] = '₈',
        ['9'] = '₉',
        ['+'] = '₊',
        ['-'] = '₋',
        ['='] = '₌',
        ['('] = '₍',
        [')'] = '₎',
        ['a'] = 'ₐ',
        ['e'] = 'ₑ',
        ['h'] = 'ₕ',
        ['i'] = 'ᵢ',
        ['j'] = 'ⱼ',
        ['k'] = 'ₖ',
        ['l'] = 'ₗ',
        ['m'] = 'ₘ',
        ['n'] = 'ₙ',
        ['o'] = 'ₒ',
        ['p'] = 'ₚ',
        ['r'] = 'ᵣ',
        ['s'] = 'ₛ',
        ['t'] = 'ₜ',
        ['u'] = 'ᵤ',
        ['v'] = 'ᵥ',
        ['x'] = 'ₓ',
    };

    [GeneratedRegex(@"\\(?:d|t)?frac\{([^{}]+)\}\{([^{}]+)\}")]
    private static partial Regex FracInlineRegex();

    [GeneratedRegex(@"\\binom\{([^{}]+)\}\{([^{}]+)\}")]
    private static partial Regex BinomInlineRegex();

    [GeneratedRegex(@"\\sqrt\[([^{}\]]+)\]\{([^{}]+)\}")]
    private static partial Regex NthRootInlineRegex();

    [GeneratedRegex(@"\\sqrt\{([^{}]+)\}")]
    private static partial Regex SqrtInlineRegex();

    [GeneratedRegex(@"\\pmod\{([^{}]+)\}")]
    private static partial Regex PmodRegex();

    [GeneratedRegex(@"\\mathbb\{([A-Za-z]+)\}")]
    private static partial Regex MathbbRegex();

    [GeneratedRegex(@"\\(?:text|textrm|textit|textbf|mathrm|mathbf|mathit|mathsf|mathcal|operatorname)\{([^{}]*)\}")]
    private static partial Regex StyledTextCommandRegex();

    [GeneratedRegex(@"\\(?:overline|bar)\{([^{}]+)\}")]
    private static partial Regex OverlineRegex();

    [GeneratedRegex(@"\\hat\{([^{}]+)\}")]
    private static partial Regex HatRegex();

    [GeneratedRegex(@"\\vec\{([^{}]+)\}")]
    private static partial Regex VecRegex();

    [GeneratedRegex(@"\\([a-zA-Z]+)")]
    private static partial Regex CommandTokenRegex();

    [GeneratedRegex(@"\^\{([^{}]+)\}")]
    private static partial Regex BracedSuperscriptRegex();

    [GeneratedRegex(@"\^([0-9a-zA-Z])")]
    private static partial Regex SingleSuperscriptRegex();

    [GeneratedRegex(@"_\{([^{}]+)\}")]
    private static partial Regex BracedSubscriptRegex();

    [GeneratedRegex(@"_([0-9a-zA-Z])")]
    private static partial Regex SingleSubscriptRegex();

    [GeneratedRegex(@"\\(?:frac|dfrac|tfrac|sum|prod|int|iint|oint|sqrt|implies|impliedby|iff|Rightarrow|Leftrightarrow|begin\{|end\{|mathbb|mathcal|binom|pmod|overline|vec|hat|forall|exists|subseteq|neq|leq|geq)")]
    private static partial Regex ComplexLatexMarkupRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    /// <summary>
    /// Formats inline LaTeX math tokens into clean, readable mathematical Unicode without prefix-collision bugs.
    /// </summary>
    public static string FormatInlineMath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();
        if (!s.Contains('\\') && !s.Contains('$') && !s.Contains('^') && !s.Contains('_'))
            return s;

        s = s.Replace("$$", "").Replace("$", "");
        s = s.Replace(@"\[", "").Replace(@"\]", "").Replace(@"\(", "").Replace(@"\)", "");

        // Preserve escaped literal punctuation before stripping braces
        s = s.Replace(@"\{", "\uE001")
            .Replace(@"\}", "\uE002")
            .Replace(@"\%", "%")
            .Replace(@"\_", "\uE003")
            .Replace(@"\|", "‖")
            .Replace(@"\\ ", " ; ")
            .Replace(@"\\", " ; ")
            .Replace(@"\,", " ")
            .Replace(@"\;", " ")
            .Replace(@"\:", " ")
            .Replace(@"\!", "");

        // Iteratively expand nested structural macros (\frac, \sqrt, \binom, \pmod, \mathbb, \text, accents)
        for (int pass = 0; pass < 4; pass++)
        {
            var prev = s;
            s = FracInlineRegex().Replace(s, "($1)/($2)");
            s = BinomInlineRegex().Replace(s, "C($1, $2)");
            s = NthRootInlineRegex().Replace(s, "$1√($2)");
            s = SqrtInlineRegex().Replace(s, "√($1)");
            s = PmodRegex().Replace(s, " (mod $1)");
            s = MathbbRegex()
                .Replace(
                    s,
                    m =>
                    {
                        var inner = m.Groups[1].Value;
                        return inner.Length == 1 && BlackboardBoldMap.TryGetValue(inner[0], out var bb)
                            ? bb
                            : inner;
                    }
                );
            s = StyledTextCommandRegex().Replace(s, "$1");
            s = OverlineRegex().Replace(s, m => ApplyCombiningAccent(m.Groups[1].Value, '\u0304'));
            s = HatRegex().Replace(s, m => ApplyCombiningAccent(m.Groups[1].Value, '\u0302'));
            s = VecRegex().Replace(s, m => ApplyCombiningAccent(m.Groups[1].Value, '\u20D7'));

            if (s == prev)
                break;
        }

        // Single-pass whole-command token replacement: \in NEVER corrupts \int, \le NEVER corrupts \leftarrow
        s = CommandTokenRegex()
            .Replace(
                s,
                m => CommandSymbols.TryGetValue(m.Value, out var sym) ? sym : m.Value
            );

        // Superscripts ^{...} or ^x
        s = BracedSuperscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));
        s = SingleSuperscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SuperscriptMap, '^'));

        // Subscripts _{...} or _x
        s = BracedSubscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));
        s = SingleSubscriptRegex()
            .Replace(s, m => TryConvertScript(m.Groups[1].Value, SubscriptMap, '_'));

        s = s.Replace("{", "").Replace("}", "");
        s = s.Replace("\uE001", "{").Replace("\uE002", "}").Replace("\uE003", "_");
        s = MultiSpaceRegex().Replace(s, " ");
        return s.Trim();
    }

    /// <summary>
    /// Normalizes common AMS/LLM LaTeX shorthand into core TeX primitives compatible with CSharpMath.
    /// </summary>
    public static string NormalizeForTeXEngine(string rawLatex)
    {
        var s = (rawLatex ?? string.Empty).Trim();
        s = s.Replace("$$", "").Replace("$", "");
        s = s.Replace(@"\[", "").Replace(@"\]", "").Replace(@"\(", "").Replace(@"\)", "");

        // Normalize AMS aliases that CSharpMath 0.5.1 may not include in its built-in symbol table
        s = CommandTokenRegex()
            .Replace(
                s,
                m =>
                    m.Value switch
                    {
                        @"\implies" => @"\Rightarrow",
                        @"\impliedby" => @"\Leftarrow",
                        @"\iff" => @"\Leftrightarrow",
                        @"\dfrac" => @"\frac",
                        @"\tfrac" => @"\frac",
                        @"\varnothing" => @"\emptyset",
                        @"\land" => @"\wedge",
                        @"\lor" => @"\vee",
                        @"\lnot" => @"\neg",
                        @"\dots" => @"\ldots",
                        _ => m.Value,
                    }
            );

        s = PmodRegex().Replace(s, @"\ (\text{mod } $1)");
        return s.Trim();
    }

    /// <summary>
    /// Returns true if the text contains raw LaTeX structural or symbol commands that a student
    /// should not be expected to type verbatim in an Identification / FillInTheBlank box.
    /// </summary>
    public static bool ContainsComplexLatexMarkup(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ComplexLatexMarkupRegex().IsMatch(text);

    /// <summary>
    /// Lightweight mathematical equivalence check for direct-answer math questions:
    /// matches numeric &amp; simple fraction equivalents ("0.5" == "1/2", "2.0" == "2", "-0" == "0")
    /// as well as whitespace-normalized short math expressions ("x + 1" == "x+1", "hk = 1" == "hk=1").
    /// </summary>
    public static bool AreMathAnswersEquivalent(string? userAnswer, string? expectedAnswer)
    {
        if (string.IsNullOrWhiteSpace(userAnswer) || string.IsNullOrWhiteSpace(expectedAnswer))
            return false;

        if (
            TryParseNumericValue(userAnswer, out double uVal)
            && TryParseNumericValue(expectedAnswer, out double eVal)
        )
        {
            double tol = Math.Max(1e-6, Math.Abs(eVal) * 1e-4);
            return Math.Abs(uVal - eVal) <= tol;
        }

        // Normalize Unicode/LaTeX symbols & remove whitespace around mathematical operators
        var normU = NormalizeCompactMathExpression(userAnswer);
        var normE = NormalizeCompactMathExpression(expectedAnswer);
        return normU.Length > 0
            && string.Equals(normU, normE, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseNumericValue(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim()
            .Replace("$", "")
            .Replace(",", "")
            .Replace(" ", "")
            .Replace("−", "-");

        // Handle simple fraction "a/b" or "\frac{a}{b}"
        var fracM = FracInlineRegex().Match(s);
        if (fracM.Success && fracM.Value == s)
            s = $"{fracM.Groups[1].Value}/{fracM.Groups[2].Value}";

        int slashIdx = s.IndexOf('/');
        if (slashIdx > 0 && slashIdx < s.Length - 1 && s.IndexOf('/', slashIdx + 1) < 0)
        {
            if (
                double.TryParse(
                    s[..slashIdx],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double num
                )
                && double.TryParse(
                    s[(slashIdx + 1)..],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double den
                )
                && Math.Abs(den) > 1e-12
            )
            {
                value = num / den;
                return true;
            }
            return false;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeCompactMathExpression(string raw)
    {
        var formatted = FormatInlineMath(raw).Replace("−", "-");
        var sb = new StringBuilder(formatted.Length);
        foreach (char c in formatted)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ApplyCombiningAccent(string text, char combiningChar)
    {
        var t = text.Trim();
        if (t.Length == 1)
            return $"{t}{combiningChar}";
        return t;
    }

    private static string TryConvertScript(
        string content,
        Dictionary<char, char> map,
        char fallbackPrefix
    )
    {
        var chars = new char[content.Length];
        for (int i = 0; i < content.Length; i++)
        {
            if (!map.TryGetValue(content[i], out chars[i]))
                return $"{fallbackPrefix}({content})";
        }
        return new string(chars);
    }
}
