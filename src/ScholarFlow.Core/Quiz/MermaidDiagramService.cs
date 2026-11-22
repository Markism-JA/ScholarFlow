using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mermaider;
using Mermaider.Models;

namespace ScholarFlow.Core.Quiz;

public enum MermaidThemePreset
{
    Dark,
    Light,
    Print,
}

/// <summary>
/// Unified pure-.NET Mermaid diagram service backed by Mermaider (MIT, zero browser/JS).
/// Provides backward-compatible normalization for legacy "[A] -> [B]" chains, SVG generation
/// with Dark/Light/Print theme tokens, and a pluggable PNG rasterizer hook for Printable Exam PDFs.
/// </summary>
public static partial class MermaidDiagramService
{
    /// <summary>
    /// Optional PNG rasterizer hook registered by ScholarFlow.Desktop (backed by Svg.Skia)
    /// so Printable Exam PDFs embed crisp high-DPI Mermaider diagrams without adding SkiaSharp to Core.
    /// Signature: (rawDiagramSpec, forPrint) => pngBytes?
    /// </summary>
    public static Func<string, bool, byte[]?>? CustomMermaidPngRenderer { get; set; }

    private static readonly string[] KnownMermaidHeaders =
    [
        "flowchart",
        "graph",
        "sequencediagram",
        "classdiagram",
        "statediagram",
        "erdiagram",
        "gantt",
        "pie",
        "gitgraph",
        "mindmap",
        "timeline",
        "quadrantchart",
        "sankey",
        "xychart",
        "block-beta",
        "architecture-beta",
        "packet-beta",
        "kanban",
        "journey",
        "requirementdiagram",
        "c4context",
        "c4container",
        "c4component",
        "c4dynamic",
        "c4deployment",
        "radar-beta",
        "treemap-beta",
    ];

    [GeneratedRegex(@"(--[a-zA-Z0-9_-]+)\s*:\s*([^;""}]+)", RegexOptions.Compiled)]
    private static partial Regex CssVarDeclRegex();

    [GeneratedRegex(@"var\(\s*(--[a-zA-Z0-9_-]+)(?:\s*,\s*([^()]+))?\s*\)", RegexOptions.Compiled)]
    private static partial Regex CssVarUseRegex();

    [GeneratedRegex(
        @"color-mix\(\s*in\s+srgb\s*,\s*((?:rgba?\([^)]*\)|[^,()]+?))(?:\s+(\d+(?:\.\d+)?)%)?\s*,\s*((?:rgba?\([^)]*\)|[^,()]+?))(?:\s+(\d+(?:\.\d+)?)%)?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex ColorMixRegex();

    // Svg.Skia 2.0.0.4 only supports SVG filter="url(#...)" and drops entire <g> groups
    // styled with CSS filter: drop-shadow(...). Strip CSS drop-shadow filters defensively.
    [GeneratedRegex(
        @"filter\s*:\s*drop-shadow\((?:[^()]|\([^()]*\))*\)\s*;?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex CssDropShadowFilterRegex();

    /// <summary>
    /// Strips markdown code fences and upgrades legacy 1-line "[A] -> [B] -> [C]" chains
    /// into standard Mermaid "flowchart LR" syntax so older saved decks render seamlessly.
    /// </summary>
    public static string NormalizeMermaidSource(string? rawDiagram)
    {
        var clean = PdfDiagramGraphRenderer.StripMermaidCodeFences(rawDiagram).Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return string.Empty;

        var firstContentLine = clean
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("%%", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(firstContentLine))
            return clean;

        var firstToken = firstContentLine
            .Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (
            KnownMermaidHeaders.Any(h =>
                firstToken.Equals(h, StringComparison.OrdinalIgnoreCase)
                || firstToken.StartsWith(h + "-", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return clean;
        }

        // Legacy 1-line or multi-line arrow chain shorthand: "[Client] -> [API] -> [DB]"
        if (clean.Contains("->", StringComparison.Ordinal) || clean.Contains("→", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            sb.AppendLine("flowchart LR");
            int nodeCounter = 0;
            var idByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string GetOrRegisterNode(string rawNodeText)
            {
                var label = rawNodeText.Trim().Trim('[', ']', '"', '\'');
                if (string.IsNullOrWhiteSpace(label))
                    label = "?";
                if (!idByLabel.TryGetValue(label, out var id))
                {
                    id = $"N{nodeCounter++}";
                    idByLabel[label] = id;
                    var escaped = label.Replace("\"", "'");
                    sb.AppendLine($"  {id}[\"{escaped}\"]");
                }
                return id;
            }

            foreach (var rawLine in clean.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = rawLine.Split(["-->", "->", "→"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                    continue;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var fromId = GetOrRegisterNode(parts[i]);
                    var toId = GetOrRegisterNode(parts[i + 1]);
                    sb.AppendLine($"  {fromId} --> {toId}");
                }
            }

            if (nodeCounter >= 2)
                return sb.ToString().Trim();
        }

        return clean;
    }

    /// <summary>
    /// Returns a concise, human-readable label describing the Mermaid diagram type.
    /// </summary>
    public static string DetectDiagramTitle(string? rawDiagram)
    {
        var normalized = NormalizeMermaidSource(rawDiagram);
        var first = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("%%", StringComparison.Ordinal))
            ?.ToLowerInvariant() ?? string.Empty;

        if (first.StartsWith("sequencediagram"))
            return "SEQUENCE DIAGRAM";
        if (first.StartsWith("erdiagram"))
            return "ENTITY-RELATIONSHIP DIAGRAM";
        if (first.StartsWith("classdiagram"))
            return "CLASS DIAGRAM";
        if (first.StartsWith("statediagram"))
            return "STATE DIAGRAM";
        if (first.StartsWith("mindmap"))
            return "MIND MAP";
        if (first.StartsWith("timeline"))
            return "TIMELINE DIAGRAM";
        if (first.StartsWith("gantt"))
            return "GANTT SCHEDULE";
        if (first.StartsWith("pie"))
            return "PIE CHART";
        if (first.StartsWith("gitgraph"))
            return "GIT BRANCH GRAPH";
        return "ARCHITECTURE / PROCESS FLOW DIAGRAM";
    }

    /// <summary>
    /// Renders Mermaid text to an SVG string using Mermaider with ScholarFlow's theme tokens.
    /// Returns false if the input cannot be parsed as a supported Mermaid diagram.
    /// </summary>
    public static bool TryRenderSvg(
        string? rawDiagram,
        MermaidThemePreset themePreset,
        out string svg
    )
    {
        svg = string.Empty;
        var normalized = NormalizeMermaidSource(rawDiagram);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        try
        {
            var options = BuildRenderOptions(themePreset);
            var rendered = MermaidRenderer.RenderSvg(normalized, options);
            if (string.IsNullOrWhiteSpace(rendered) || !rendered.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                return false;

            svg = ResolveSvgCssForRasterizer(rendered);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flattens CSS custom properties (<c>var(--...)</c>) and <c>color-mix(in srgb, ...)</c> expressions
    /// emitted by Mermaider into concrete hex/rgba colors so Skia/SVG rasterizers (like Svg.Skia)
    /// render every node fill, border, gradient stop, and arrow with exact theme colors.
    /// </summary>
    public static string ResolveSvgCssForRasterizer(string svg)
    {
        if (
            string.IsNullOrWhiteSpace(svg)
            || (
                !svg.Contains("var(--", StringComparison.Ordinal)
                && !svg.Contains("color-mix(", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return svg;
        }

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in CssVarDeclRegex().Matches(svg))
        {
            var name = m.Groups[1].Value.Trim();
            var val = m.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
                vars[name] = val;
        }

        string ExpandOnce(string input)
        {
            var step1 = CssVarUseRegex().Replace(
                input,
                m =>
                {
                    var key = m.Groups[1].Value;
                    if (vars.TryGetValue(key, out var resolved))
                        return resolved;
                    return m.Groups[2].Success ? m.Groups[2].Value.Trim() : "#64748B";
                }
            );

            return ColorMixRegex().Replace(
                step1,
                m =>
                {
                    var c1Raw = m.Groups[1].Value.Trim();
                    var c2Raw = m.Groups[3].Value.Trim();

                    double p1 = 0;
                    bool hasP1 = m.Groups[2].Success
                        && double.TryParse(
                            m.Groups[2].Value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out p1
                        );

                    double p2 = 0;
                    bool hasP2 = m.Groups[4].Success
                        && double.TryParse(
                            m.Groups[4].Value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out p2
                        );

                    double w1, w2;
                    if (hasP1 && hasP2)
                    {
                        w1 = Math.Clamp(p1 / 100.0, 0.0, 1.0);
                        w2 = Math.Clamp(p2 / 100.0, 0.0, 1.0);
                    }
                    else if (hasP1)
                    {
                        w1 = Math.Clamp(p1 / 100.0, 0.0, 1.0);
                        w2 = 1.0 - w1;
                    }
                    else if (hasP2)
                    {
                        w2 = Math.Clamp(p2 / 100.0, 0.0, 1.0);
                        w1 = 1.0 - w2;
                    }
                    else
                    {
                        w1 = 0.5;
                        w2 = 0.5;
                    }

                    if (
                        c2Raw.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                        && TryParseColor(c1Raw, out byte tr, out byte tg, out byte tb, out double ta1)
                    )
                    {
                        return string.Create(
                            CultureInfo.InvariantCulture,
                            $"rgba({tr},{tg},{tb},{Math.Clamp(ta1 * w1, 0.0, 1.0):0.###})"
                        );
                    }

                    if (
                        c1Raw.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                        && TryParseColor(c2Raw, out byte tr2, out byte tg2, out byte tb2, out double ta2)
                    )
                    {
                        return string.Create(
                            CultureInfo.InvariantCulture,
                            $"rgba({tr2},{tg2},{tb2},{Math.Clamp(ta2 * w2, 0.0, 1.0):0.###})"
                        );
                    }

                    if (
                        TryParseColor(c1Raw, out byte r1, out byte g1, out byte b1, out double a1)
                        && TryParseColor(c2Raw, out byte r2, out byte g2, out byte b2, out double a2)
                    )
                    {
                        double sum = w1 + w2;
                        double f1 = sum > 0 ? w1 / sum : 0.5;
                        double f2 = 1.0 - f1;
                        byte r = (byte)Math.Clamp((int)Math.Round(r1 * f1 + r2 * f2), 0, 255);
                        byte g = (byte)Math.Clamp((int)Math.Round(g1 * f1 + g2 * f2), 0, 255);
                        byte b = (byte)Math.Clamp((int)Math.Round(b1 * f1 + b2 * f2), 0, 255);
                        double a = Math.Clamp(a1 * f1 + a2 * f2, 0.0, 1.0);
                        if (a >= 0.999)
                            return $"#{r:X2}{g:X2}{b:X2}";
                        return string.Create(
                            CultureInfo.InvariantCulture,
                            $"rgba({r},{g},{b},{a:0.###})"
                        );
                    }

                    return m.Value;
                }
            );
        }

        // Iteratively resolve nested variable definitions
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            foreach (var key in vars.Keys.ToList())
            {
                var next = ExpandOnce(vars[key]);
                if (!string.Equals(next, vars[key], StringComparison.Ordinal))
                {
                    vars[key] = next;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        // Apply multi-pass substitution to the SVG document
        var output = svg;
        for (int pass = 0; pass < 8; pass++)
        {
            var next = ExpandOnce(output);
            if (string.Equals(next, output, StringComparison.Ordinal))
                break;
            output = next;
        }

        // Strip CSS filter: drop-shadow(...) which Svg.Skia cannot resolve and causes entire <g> nodes to disappear
        output = CssDropShadowFilterRegex().Replace(output, string.Empty);

        return output;
    }

    private static bool TryParseColor(string raw, out byte r, out byte g, out byte b, out double a)
    {
        r = g = b = 0;
        a = 1.0;
        var s = raw.Trim();
        switch (s.ToLowerInvariant())
        {
            case "black":
                r = g = b = 0;
                return true;
            case "white":
                r = g = b = 255;
                return true;
            case "gray":
            case "grey":
                r = g = b = 128;
                return true;
            case "silver":
                r = g = b = 192;
                return true;
            case "currentcolor":
                r = g = b = 100;
                return true;
        }
        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length == 3)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }
            if (hex.Length == 6)
            {
                return byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                    && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                    && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
            }
            if (hex.Length == 8)
            {
                if (
                    byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                    && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                    && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)
                    && byte.TryParse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ab)
                )
                {
                    a = ab / 255.0;
                    return true;
                }
            }
            return false;
        }

        if (
            (s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
            && s.EndsWith(')')
        )
        {
            var inner = s[(s.IndexOf('(') + 1)..^1];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is 3 or 4)
            {
                if (
                    byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r)
                    && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g)
                    && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)
                )
                {
                    if (parts.Length == 4)
                    {
                        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                            return false;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private static RenderOptions BuildRenderOptions(MermaidThemePreset preset) =>
        preset switch
        {
            MermaidThemePreset.Dark => new RenderOptions
            {
                Bg = "#14161B",
                Fg = "#E8ECF2",
                Surface = "#1E222B",
                Border = "#3A4252",
                Line = "#7E8A9F",
                Muted = "#9AA6BA",
                Accent = "#60A5FA",
                Font = "Inter",
                FontSize = "13px",
                Padding = 20,
                Elevation = 0,
                RoundedEdges = true,
                Transparent = false,
            },
            MermaidThemePreset.Light => new RenderOptions
            {
                Bg = "#FFFFFF",
                Fg = "#0F172A",
                Surface = "#F8FAFC",
                Border = "#CBD5E1",
                Line = "#64748B",
                Muted = "#64748B",
                Accent = "#2563EB",
                Font = "Inter",
                FontSize = "13px",
                Padding = 20,
                Elevation = 0,
                RoundedEdges = true,
                Transparent = false,
            },
            _ => new RenderOptions
            {
                Bg = "#FFFFFF",
                Fg = "#0F172A",
                Surface = "#F8FAFC",
                Border = "#475569",
                Line = "#334155",
                Muted = "#475569",
                Accent = "#1E293B",
                Font = "Inter",
                FontSize = "13px",
                Padding = 18,
                Elevation = 0,
                RoundedEdges = true,
                Transparent = false,
            },
        };
}
