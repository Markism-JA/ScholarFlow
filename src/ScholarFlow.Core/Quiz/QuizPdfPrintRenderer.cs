using System.Text;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Pdf;

namespace ScholarFlow.Core.Quiz;

public enum QuizPdfExportMode
{
    /// <summary>Clean university-style Exam Worksheet followed by optional Hints page and full Answer Key &amp; Examiner Appendix.</summary>
    ExamWorksheet,

    /// <summary>Annotated Study Guide with answers, contrastive explanations, and evidence inline under each question.</summary>
    StudyGuideInline,
}

public sealed record QuizPdfExportOptions(
    QuizPdfExportMode Mode = QuizPdfExportMode.ExamWorksheet,
    bool IncludeAnswerKey = true,
    bool IncludeHintsSection = true,
    bool IncludeConfidenceBoxes = false,
    bool IncludeEvidenceCitations = true,
    bool IncludeBlueprintSummary = true
)
{
    public static QuizPdfExportOptions ExamDefault { get; } = new();

    public static QuizPdfExportOptions StudyGuide { get; } =
        new(
            Mode: QuizPdfExportMode.StudyGuideInline,
            IncludeAnswerKey: false,
            IncludeHintsSection: false,
            IncludeConfidenceBoxes: false,
            IncludeEvidenceCitations: true,
            IncludeBlueprintSummary: true
        );

    public static QuizPdfExportOptions QuestionsOnly { get; } =
        new(
            Mode: QuizPdfExportMode.ExamWorksheet,
            IncludeAnswerKey: false,
            IncludeHintsSection: false,
            IncludeConfidenceBoxes: false,
            IncludeEvidenceCitations: false,
            IncludeBlueprintSummary: false
        );
}

/// <summary>
/// Professional-grade printable Exam Paper &amp; Study Guide PDF renderer for Quiz Studio decks.
/// Uses true Regular/Bold/Italic Sans &amp; Monospace fonts via SystemFontResolver, vector graph rendering
/// for flowcharts/diagrams (PdfDiagramGraphRenderer), 2x2 grid layout for short MCQ options,
/// single-line True/False options, embedded images (including image-grounded sources),
/// and a dedicated Examiner Answer Key with a Quick-Grading Grid, Assessment Blueprint,
/// 3-part Contrastive Explanations, and multi-format source citations.
/// </summary>
public static partial class QuizPdfPrintRenderer
{
    private const double MarginX = 52;
    private const double MarginTop = 52;
    private const double MarginBottom = 50;

    private static readonly XBrush InkPrimary = new XSolidBrush(XColor.FromArgb(24, 28, 34));
    private static readonly XBrush InkSecondary = new XSolidBrush(XColor.FromArgb(82, 90, 102));
    private static readonly XBrush InkMuted = new XSolidBrush(XColor.FromArgb(124, 132, 144));
    private static readonly XBrush AccentSlate = new XSolidBrush(XColor.FromArgb(48, 74, 104));
    private static readonly XBrush SuccessInk = new XSolidBrush(XColor.FromArgb(24, 108, 66));
    private static readonly XBrush HintInk = new XSolidBrush(XColor.FromArgb(142, 88, 20));

    private static readonly XBrush SoftPanelBg = new XSolidBrush(XColor.FromArgb(249, 250, 251));
    private static readonly XBrush CodeBg = new XSolidBrush(XColor.FromArgb(248, 249, 251));
    private static readonly XBrush FormulaBg = new XSolidBrush(XColor.FromArgb(246, 249, 253));

    private static readonly XPen HairlinePen = new(XColor.FromArgb(218, 223, 230), 0.65);
    private static readonly XPen StrongRulePen = new(XColor.FromArgb(40, 46, 56), 1.0);
    private static readonly XPen CheckboxPen = new(XColor.FromArgb(140, 148, 160), 0.75);
    private static readonly XPen WriteLinePen = new(XColor.FromArgb(165, 172, 182), 0.65);
    private static readonly XPen SoftAccentPen = new(XColor.FromArgb(135, 155, 180), 1.3); //NOTE: This is not used yet but, might help.
    private static readonly XPen SoftWarnPen = new(XColor.FromArgb(215, 148, 48), 1.3);

    public static void Render(
        QuizSessionRecord quiz,
        string outputPdfPath,
        QuizPdfExportOptions? options = null
    )
    {
        var opt = options ?? QuizPdfExportOptions.ExamDefault;
        MasterPdfService.EnsureFontResolver();

        using var doc = new PdfDocument();
        doc.Info.Title = quiz.Title;
        doc.Info.Author = "ScholarFlow";

        // True Regular, Bold, Italic, and fixed-width Mono fonts
        var titleFont = new XFont("Sans", 14.5, XFontStyle.Bold);
        var sectionFont = new XFont("Sans", 12, XFontStyle.Bold);
        var subFont = new XFont("Sans", 9, XFontStyle.Regular);
        var bodyFont = new XFont("Sans", 10, XFontStyle.Regular);
        var bodyBoldFont = new XFont("Sans", 10, XFontStyle.Bold);
        var smallFont = new XFont("Sans", 8.4, XFontStyle.Regular);
        var smallBoldFont = new XFont("Sans", 8.4, XFontStyle.Bold);
        var italicFont = new XFont("Sans", 8.8, XFontStyle.Italic);
        var monoFont = new XFont("Mono", 8.5, XFontStyle.Regular);
        var monoSmallFont = new XFont("Mono", 7.6, XFontStyle.Regular);

        var pageSectionNames = new List<string>();
        var outlines = new List<(string Title, int PageIndex)>();

        PdfPage page = doc.AddPage();
        XGraphics gfx = XGraphics.FromPdfPage(page);
        double y = MarginTop;
        string currentSectionName =
            opt.Mode == QuizPdfExportMode.StudyGuideInline
                ? "Annotated Study Guide"
                : "Examination Paper";
        pageSectionNames.Add(currentSectionName);

        double ContentWidth() => page.Width.Point - (MarginX * 2);

        void NewPage(string? sectionLabel = null)
        {
            if (sectionLabel is not null)
                currentSectionName = sectionLabel;
            gfx.Dispose();
            page = doc.AddPage();
            gfx = XGraphics.FromPdfPage(page);
            pageSectionNames.Add(currentSectionName);
            y = MarginTop;
        }

        void EnsureSpace(double neededHeight)
        {
            if (y + neededHeight > page.Height.Point - MarginBottom)
                NewPage();
        }

        List<string> WrapLines(string text, XFont font, double maxWidth)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
                return result;

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrEmpty(line))
                {
                    result.Add(string.Empty);
                    continue;
                }

                var words = line.Split(' ');
                var cur = new StringBuilder();
                foreach (var w in words)
                {
                    var cand = cur.Length == 0 ? w : $"{cur} {w}";
                    if (gfx.MeasureString(cand, font).Width > maxWidth && cur.Length > 0)
                    {
                        result.Add(cur.ToString());
                        cur.Clear();
                        cur.Append(w);
                    }
                    else
                    {
                        cur.Clear();
                        cur.Append(cand);
                    }
                }
                if (cur.Length > 0)
                    result.Add(cur.ToString());
            }
            return result;
        }

        void DrawWrapped(
            string text,
            XFont font,
            XBrush brush,
            double indent = 0,
            double lineHeight = 14.2
        )
        {
            double maxW = Math.Max(60, ContentWidth() - indent);
            var lines = WrapLines(text, font, maxW);
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    y += lineHeight * 0.45;
                    continue;
                }
                EnsureSpace(lineHeight);
                gfx.DrawString(
                    line,
                    font,
                    brush,
                    new XPoint(MarginX + indent, y + lineHeight * 0.76)
                );
                y += lineHeight;
            }
        }

        void DrawLabeledParagraph(
            string labelPrefix,
            XFont labelFont,
            XBrush labelBrush,
            string bodyText,
            XFont textFont,
            XBrush textBrush,
            double indent = 0,
            double lineHeight = 14.2
        )
        {
            double maxW = Math.Max(80, ContentWidth() - indent);
            double prefixW = gfx.MeasureString(labelPrefix + " ", labelFont).Width;

            var paragraphs = bodyText.Replace("\r\n", "\n").Split('\n');
            bool isFirstLineOverall = true;

            foreach (var rawPara in paragraphs)
            {
                var trimmed = rawPara.TrimEnd();
                if (string.IsNullOrEmpty(trimmed))
                {
                    y += lineHeight * 0.4;
                    continue;
                }

                var words = trimmed.Split(' ');
                var cur = new StringBuilder();
                foreach (var w in words)
                {
                    double availW = isFirstLineOverall ? Math.Max(40, maxW - prefixW) : maxW;
                    var cand = cur.Length == 0 ? w : $"{cur} {w}";
                    if (gfx.MeasureString(cand, textFont).Width > availW && cur.Length > 0)
                    {
                        EnsureSpace(lineHeight);
                        double drawX = MarginX + indent;
                        if (isFirstLineOverall)
                        {
                            gfx.DrawString(
                                labelPrefix,
                                labelFont,
                                labelBrush,
                                new XPoint(drawX, y + lineHeight * 0.76)
                            );
                            drawX += prefixW;
                            isFirstLineOverall = false;
                        }
                        gfx.DrawString(
                            cur.ToString(),
                            textFont,
                            textBrush,
                            new XPoint(drawX, y + lineHeight * 0.76)
                        );
                        y += lineHeight;
                        cur.Clear();
                        cur.Append(w);
                    }
                    else
                    {
                        cur.Clear();
                        cur.Append(cand);
                    }
                }

                if (cur.Length > 0 || isFirstLineOverall)
                {
                    EnsureSpace(lineHeight);
                    double drawX = MarginX + indent;
                    if (isFirstLineOverall)
                    {
                        gfx.DrawString(
                            labelPrefix,
                            labelFont,
                            labelBrush,
                            new XPoint(drawX, y + lineHeight * 0.76)
                        );
                        drawX += prefixW;
                        isFirstLineOverall = false;
                    }
                    if (cur.Length > 0)
                    {
                        gfx.DrawString(
                            cur.ToString(),
                            textFont,
                            textBrush,
                            new XPoint(drawX, y + lineHeight * 0.76)
                        );
                    }
                    y += lineHeight;
                }
            }
        }

        void DrawChoiceCircleOrBox(double x, double topY, double size = 7.8)
        {
            gfx.DrawRectangle(CheckboxPen, x, topY, size, size);
        }

        void DrawCodeOrFormulaListing(
            string? captionHeader,
            string content,
            XBrush bgBrush,
            XFont textFont,
            double indent = 18,
            bool showLineNumbers = false
        )
        {
            double boxX = MarginX + indent;
            double boxW = Math.Max(120, ContentWidth() - indent);
            double innerPadX = 10;
            double numGutter = showLineNumbers ? 24 : 0;
            double lineH = 12.2;

            var rawLines = content.Replace("\r\n", "\n").Split('\n');
            var visualLines = new List<(int LineNum, string Text)>();
            double maxTextW = boxW - (innerPadX * 2) - numGutter;

            for (int li = 0; li < rawLines.Length; li++)
            {
                var expanded = rawLines[li].Replace("\t", "    ").TrimEnd();
                if (string.IsNullOrEmpty(expanded))
                {
                    visualLines.Add((li + 1, string.Empty));
                    continue;
                }

                int pos = 0;
                bool first = true;
                while (pos < expanded.Length)
                {
                    int take = expanded.Length - pos;
                    while (
                        take > 10
                        && gfx.MeasureString(expanded.Substring(pos, take), textFont).Width
                            > maxTextW
                    )
                    {
                        take = Math.Max(10, (int)(take * 0.84));
                    }
                    visualLines.Add((first ? li + 1 : 0, expanded.Substring(pos, take)));
                    pos += take;
                    first = false;
                }
            }

            bool hasHeader = !string.IsNullOrWhiteSpace(captionHeader);
            double estimatedH = (hasHeader ? 18 : 6) + (visualLines.Count * lineH) + 8;
            if (estimatedH <= page.Height.Point - MarginTop - MarginBottom - 20)
                EnsureSpace(estimatedH);
            else
                EnsureSpace(60);

            int idx = 0;
            bool firstChunk = true;
            while (idx < visualLines.Count)
            {
                double avail = page.Height.Point - MarginBottom - y;
                double headerH = firstChunk && hasHeader ? 17 : 6;
                int fitLines = Math.Max(1, (int)((avail - headerH - 8) / lineH));
                int take = Math.Min(fitLines, visualLines.Count - idx);
                double chunkH = headerH + (take * lineH) + 7;

                gfx.DrawRectangle(bgBrush, boxX, y, boxW, chunkH);
                gfx.DrawRectangle(HairlinePen, boxX, y, boxW, chunkH);

                if (firstChunk && hasHeader)
                {
                    gfx.DrawString(
                        captionHeader!,
                        smallFont,
                        InkMuted,
                        new XPoint(boxX + innerPadX, y + 12)
                    );
                }

                double curY = y + headerH;
                for (int k = 0; k < take; k++)
                {
                    var (ln, txt) = visualLines[idx++];
                    if (showLineNumbers && ln > 0)
                    {
                        gfx.DrawString(
                            ln.ToString(),
                            monoSmallFont,
                            InkMuted,
                            new XRect(boxX + 4, curY, 20, lineH),
                            XStringFormats.TopRight
                        );
                    }
                    if (!string.IsNullOrEmpty(txt))
                    {
                        gfx.DrawString(
                            txt,
                            textFont,
                            InkPrimary,
                            new XPoint(boxX + innerPadX + numGutter, curY + lineH * 0.76)
                        );
                    }
                    curY += lineH;
                }

                y += chunkH + 5;
                firstChunk = false;
                if (idx < visualLines.Count)
                    NewPage();
            }
        }

        void DrawDiagramOrGraphFigure(string diagramText, int questionNumber)
        {
            double figX = MarginX + 18;
            double figW = Math.Max(160, ContentWidth() - 18);

            // Primary path: Mermaider + Svg.Skia high-DPI diagram image (supports all 24 Mermaid types)
            if (MermaidDiagramService.CustomMermaidPngRenderer is not null)
            {
                var pngBytes = MermaidDiagramService.CustomMermaidPngRenderer(diagramText, true);
                if (pngBytes is { Length: > 0 })
                {
                    try
                    {
                        using var xImg = XImage.FromStream(() => new MemoryStream(pngBytes, writable: false));
                        double naturalW = xImg.PixelWidth / 2.5;
                        double naturalH = xImg.PixelHeight / 2.5;
                        double maxDrawW = figW - 16;
                        double maxDrawH = Math.Min(
                            340,
                            page.Height.Point - MarginTop - MarginBottom - 64
                        );
                        double fitScale = Math.Min(
                            1.0,
                            Math.Min(maxDrawW / Math.Max(1, naturalW), maxDrawH / Math.Max(1, naturalH))
                        );
                        double drawW = naturalW * fitScale;
                        double drawH = naturalH * fitScale;
                        double boxH = drawH + 30;

                        EnsureSpace(boxH + 8);
                        gfx.DrawRoundedRectangle(
                            HairlinePen,
                            XBrushes.White,
                            figX,
                            y,
                            figW,
                            boxH,
                            5,
                            5
                        );
                        double imgX = figX + (figW - drawW) * 0.5;
                        gfx.DrawImage(xImg, imgX, y + 8, drawW, drawH);

                        var caption =
                            $"Figure {questionNumber}.1 — {MermaidDiagramService.DetectDiagramTitle(diagramText)}";
                        gfx.DrawString(
                            caption,
                            italicFont,
                            InkMuted,
                            new XRect(figX + 8, y + drawH + 12, figW - 16, 14),
                            XStringFormats.TopCenter
                        );
                        y += boxH + 8;
                        return;
                    }
                    catch
                    {
                        // Fall through to legacy vector fallback below
                    }
                }
            }

            if (PdfDiagramGraphRenderer.TryParseSequenceDiagram(diagramText, out var parsedSeq))
            {
                double neededH = PdfDiagramGraphRenderer.EstimateSequenceDiagramHeight(
                    parsedSeq,
                    hasCaption: true
                );
                EnsureSpace(
                    Math.Min(neededH + 6, page.Height.Point - MarginTop - MarginBottom - 16)
                );
                y = PdfDiagramGraphRenderer.DrawSequenceDiagram(
                    gfx,
                    parsedSeq,
                    figX,
                    y,
                    figW,
                    smallFont,
                    smallFont,
                    italicFont,
                    figureCaption: $"Figure {questionNumber}.1 — Sequence Diagram"
                );
            }
            else if (PdfDiagramGraphRenderer.TryParseGraph(diagramText, out var parsedGraph))
            {
                double neededH = PdfDiagramGraphRenderer.EstimateGraphHeight(
                    gfx,
                    parsedGraph,
                    figW,
                    smallFont,
                    hasCaption: true
                );
                EnsureSpace(
                    Math.Min(neededH + 6, page.Height.Point - MarginTop - MarginBottom - 16)
                );
                y = PdfDiagramGraphRenderer.DrawGraph(
                    gfx,
                    parsedGraph,
                    figX,
                    y,
                    figW,
                    smallFont,
                    smallFont,
                    italicFont,
                    figureCaption: $"Figure {questionNumber}.1 — System / Concept Flow"
                );
            }
            else
            {
                DrawCodeOrFormulaListing(
                    $"Figure {questionNumber}.1 — Diagram",
                    diagramText,
                    SoftPanelBg,
                    monoFont,
                    indent: 18,
                    showLineNumbers: false
                );
            }
        }

        static string? ResolveQuestionImagePath(QuizQuestionRecord q)
        {
            if (!string.IsNullOrWhiteSpace(q.ImagePath))
            {
                if (File.Exists(q.ImagePath))
                    return q.ImagePath;

                if (!Path.IsPathRooted(q.ImagePath) && !string.IsNullOrWhiteSpace(q.SourcePdfPath))
                {
                    var dir = Path.GetDirectoryName(q.SourcePdfPath);
                    for (int depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(dir); depth++)
                    {
                        var candidate = Path.Combine(dir, q.ImagePath);
                        if (File.Exists(candidate))
                            return candidate;
                        dir = Path.GetDirectoryName(dir);
                    }
                }
            }

            if (
                StudySourceClassifier.Classify(q.SourcePdfPath).Kind == StudySourceKind.Image
                && File.Exists(q.SourcePdfPath)
            )
            {
                return q.SourcePdfPath;
            }

            return null;
        }

        void DrawImageFigure(QuizQuestionRecord q, int questionNumber)
        {
            var imgPath = ResolveQuestionImagePath(q);

            if (string.IsNullOrWhiteSpace(imgPath))
                return;

            try
            {
                using var xImg = XImage.FromFile(imgPath);
                double figX = MarginX + 18;
                double maxW = ContentWidth() - 24;
                double scale = Math.Min(
                    1.0,
                    Math.Min(
                        maxW / Math.Max(1, xImg.PixelWidth),
                        195.0 / Math.Max(1, xImg.PixelHeight)
                    )
                );
                double drawW = xImg.PixelWidth * scale;
                double drawH = xImg.PixelHeight * scale;
                EnsureSpace(drawH + 24);

                double centeredX = figX + Math.Max(0, (maxW - drawW) / 2.0);
                gfx.DrawImage(xImg, centeredX, y, drawW, drawH);
                gfx.DrawRectangle(HairlinePen, centeredX, y, drawW, drawH);

                foreach (var rect in q.HighlightRects)
                {
                    double rx = centeredX + (rect.X * drawW);
                    double ry = y + (rect.Y * drawH);
                    double rw = Math.Max(8, rect.Width * drawW);
                    double rh = Math.Max(8, rect.Height * drawH);
                    gfx.DrawRectangle(SoftWarnPen, rx, ry, rw, rh);
                }

                y += drawH + 4;
                gfx.DrawString(
                    $"Figure {questionNumber} — {Path.GetFileName(imgPath)}",
                    italicFont,
                    InkMuted,
                    new XRect(figX, y, maxW, 12),
                    XStringFormats.TopCenter
                );
                y += 16;
            }
            catch
            {
                // Skip unreadable image
            }
        }

        void DrawBlueprintSummaryBox()
        {
            if (!opt.IncludeBlueprintSummary || quiz.Questions.Count == 0)
                return;

            var kindSummary = string.Join(
                " · ",
                quiz.Questions.GroupBy(q => q.Kind)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {FormatKindShort(g.Key)}")
            );
            var cogSummary = string.Join(
                " · ",
                quiz.Questions.GroupBy(q => q.CognitiveLevel)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                        $"{g.Key} {(int)Math.Round(100.0 * g.Count() / quiz.Questions.Count)}%"
                    )
            );
            var topTopics = quiz
                .Questions.Select(q => q.TopicTag?.Trim().TrimStart('@'))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(t => $"@{t}")
                .ToList();

            var bpText = new StringBuilder();
            bpText.AppendLine($"Formats: {kindSummary}");
            bpText.AppendLine($"Cognitive Depth: {cogSummary}");
            if (topTopics.Count > 0)
                bpText.Append($"Topics: {string.Join("  ", topTopics)}");

            DrawCodeOrFormulaListing(
                "ASSESSMENT BLUEPRINT",
                bpText.ToString().TrimEnd(),
                SoftPanelBg,
                smallFont,
                indent: 0,
                showLineNumbers: false
            );
        }

        void DrawAnswerAndExplanationBlock(QuizQuestionRecord q, int displayNumber, bool isInline)
        {
            double indent = isInline ? 18 : 0;
            var answerText = FormatAnswerForKey(q);

            EnsureSpace(46);

            // Classification metadata tag shown in the Answer Key / Study Guide
            var metaParts = new List<string>
            {
                FormatKindShort(q.Kind),
                q.CognitiveLevel.ToString(),
                $"D{Math.Clamp(q.Difficulty, 1, 5)}",
            };
            if (!string.IsNullOrWhiteSpace(q.Archetype))
                metaParts.Add(QuizBlueprintBuilder.FormatArchetypeBadge(q.Archetype));
            if (!string.IsNullOrWhiteSpace(q.TopicTag))
                metaParts.Add($"@{q.TopicTag.TrimStart('@')}");

            var answerLabel = isInline ? "Answer:" : $"{displayNumber}. Answer:";
            DrawLabeledParagraph(
                answerLabel,
                bodyBoldFont,
                SuccessInk,
                $"{answerText}   [{string.Join(" · ", metaParts)}]",
                bodyFont,
                InkPrimary,
                indent: indent,
                lineHeight: 14.2
            );

            if (q.AcceptedAliases.Count > 0)
            {
                DrawLabeledParagraph(
                    "Also accepted:",
                    smallBoldFont,
                    InkSecondary,
                    string.Join(", ", q.AcceptedAliases.Select(LatexMathFormatter.FormatInlineMath)),
                    smallFont,
                    InkSecondary,
                    indent: indent + 12,
                    lineHeight: 12.6
                );
            }

            if (!string.IsNullOrWhiteSpace(q.Explanation))
            {
                y += 1.5;
                var (whyCorrect, whyDistractors, corePrinciple) = ParseContrastiveExplanation(
                    q.Explanation
                );
                if (
                    whyCorrect is not null
                    || whyDistractors is not null
                    || corePrinciple is not null
                )
                {
                    if (!string.IsNullOrWhiteSpace(whyCorrect))
                    {
                        DrawLabeledParagraph(
                            "WHY CORRECT:",
                            smallBoldFont,
                            SuccessInk,
                            LatexMathFormatter.FormatInlineMath(whyCorrect),
                            bodyFont,
                            InkPrimary,
                            indent: indent + 12,
                            lineHeight: 13.6
                        );
                    }
                    if (!string.IsNullOrWhiteSpace(whyDistractors))
                    {
                        DrawLabeledParagraph(
                            "WHY DISTRACTORS FAIL:",
                            smallBoldFont,
                            InkSecondary,
                            LatexMathFormatter.FormatInlineMath(whyDistractors),
                            bodyFont,
                            InkSecondary,
                            indent: indent + 12,
                            lineHeight: 13.6
                        );
                    }
                    if (!string.IsNullOrWhiteSpace(corePrinciple))
                    {
                        DrawLabeledParagraph(
                            "CORE PRINCIPLE:",
                            smallBoldFont,
                            AccentSlate,
                            LatexMathFormatter.FormatInlineMath(corePrinciple),
                            bodyFont,
                            InkPrimary,
                            indent: indent + 12,
                            lineHeight: 13.6
                        );
                    }
                }
                else
                {
                    DrawLabeledParagraph(
                        "Explanation:",
                        smallBoldFont,
                        InkSecondary,
                        LatexMathFormatter.FormatInlineMath(q.Explanation),
                        bodyFont,
                        InkPrimary,
                        indent: indent + 12,
                        lineHeight: 13.6
                    );
                }
            }

            if (opt.IncludeEvidenceCitations)
            {
                var citation = FormatSourceCitation(q);
                if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
                {
                    DrawWrapped(
                        $"Source ({citation}): \"{q.VerbatimQuote}\"",
                        italicFont,
                        InkMuted,
                        indent: indent + 12,
                        lineHeight: 12.2
                    );
                }
                else if (!string.IsNullOrWhiteSpace(citation))
                {
                    DrawWrapped(
                        $"Source: {citation}",
                        italicFont,
                        InkMuted,
                        indent: indent + 12,
                        lineHeight: 12.2
                    );
                }

                foreach (var anchor in q.EvidenceAnchors.Skip(1).Take(2))
                {
                    if (string.IsNullOrWhiteSpace(anchor.VerbatimQuote))
                        continue;
                    var secLoc = !string.IsNullOrWhiteSpace(anchor.SectionTitle)
                        ? $"{anchor.SectionTitle}, p. {anchor.PageNumber}"
                        : $"p. {anchor.PageNumber}";
                    DrawWrapped(
                        $"Additional Evidence ({secLoc}): \"{anchor.VerbatimQuote}\"",
                        italicFont,
                        InkMuted,
                        indent: indent + 12,
                        lineHeight: 12
                    );
                }
            }
        }

        // =====================================================================
        // 1. ACADEMIC EXAM MASTHEAD (Clean, University Paper Style)
        // =====================================================================
        outlines.Add((currentSectionName, 0));

        var courseLine = !string.IsNullOrWhiteSpace(quiz.Term)
            ? $"{quiz.CourseTitle.ToUpperInvariant()}  ·  {quiz.Term.ToUpperInvariant()}"
            : quiz.CourseTitle.ToUpperInvariant();
        gfx.DrawString(
            courseLine,
            smallFont,
            InkSecondary,
            new XRect(MarginX, y, ContentWidth() - 120, 12),
            XStringFormats.TopLeft
        );
        gfx.DrawString(
            $"{quiz.Questions.Count} Items  ·  {DateTime.Now:MMM d, yyyy}",
            smallFont,
            InkSecondary,
            new XRect(MarginX + ContentWidth() - 120, y, 120, 12),
            XStringFormats.TopRight
        );
        y += 14;

        DrawWrapped(quiz.Title, titleFont, InkPrimary, lineHeight: 19);
        if (opt.Mode == QuizPdfExportMode.StudyGuideInline)
        {
            DrawWrapped(
                "Annotated Study Guide & Reference Key",
                subFont,
                InkSecondary,
                lineHeight: 13
            );
        }
        y += 4;
        gfx.DrawLine(StrongRulePen, MarginX, y, page.Width.Point - MarginX, y);
        y += 10;

        if (opt.Mode == QuizPdfExportMode.ExamWorksheet)
        {
            // Formal student identification line
            double lineY = y + 11;
            gfx.DrawString("Name:", smallFont, InkPrimary, new XPoint(MarginX, lineY));
            gfx.DrawLine(WriteLinePen, MarginX + 32, lineY + 1.5, MarginX + 225, lineY + 1.5);

            gfx.DrawString(
                "Section / ID:",
                smallFont,
                InkPrimary,
                new XPoint(MarginX + 240, lineY)
            );
            gfx.DrawLine(WriteLinePen, MarginX + 298, lineY + 1.5, MarginX + 395, lineY + 1.5);

            gfx.DrawString(
                $"Score:  _______ / {quiz.Questions.Count}",
                smallFont,
                InkPrimary,
                new XRect(MarginX + ContentWidth() - 100, lineY - 9, 100, 12),
                XStringFormats.TopRight
            );
            y += 20;
            gfx.DrawLine(HairlinePen, MarginX, y, page.Width.Point - MarginX, y);
            y += 14;
        }
        else
        {
            DrawBlueprintSummaryBox();
            y += 8;
        }

        // =====================================================================
        // 2. QUESTIONS SECTION (Clean, Uncluttered Paper Layout)
        // =====================================================================
        for (int i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            var formattedPrompt = LatexMathFormatter.FormatInlineMath(q.Prompt);

            // Keep-with-next orphan protection: ensure the stem + its immediate equation/media or first options stay together
            double estimatedKeepHeight = 46;
            if (!string.IsNullOrWhiteSpace(q.MathLatex))
                estimatedKeepHeight += 42;
            else if (!string.IsNullOrWhiteSpace(q.CodeSnippet) || !string.IsNullOrWhiteSpace(q.Diagram))
                estimatedKeepHeight += 48;
            if (formattedPrompt.Length > 140)
                estimatedKeepHeight += 28;
            EnsureSpace(Math.Min(150, estimatedKeepHeight));

            // In Study Guide mode only, show the subtle metadata line above the question
            if (opt.Mode == QuizPdfExportMode.StudyGuideInline)
            {
                var metaParts = new List<string>
                {
                    FormatKindShort(q.Kind),
                    q.CognitiveLevel.ToString(),
                    $"D{Math.Clamp(q.Difficulty, 1, 5)}",
                };
                if (!string.IsNullOrWhiteSpace(q.Archetype))
                    metaParts.Add(QuizBlueprintBuilder.FormatArchetypeBadge(q.Archetype));
                if (!string.IsNullOrWhiteSpace(q.TopicTag))
                    metaParts.Add($"@{q.TopicTag.TrimStart('@')}");

                DrawWrapped(
                    string.Join("  ·  ", metaParts),
                    smallFont,
                    InkMuted,
                    indent: 0,
                    lineHeight: 11.5
                );
                y += 1.5;
            }

            // Question Stem: Only the item number "1." is bold; stem is crisp regular weight with formatted inline math
            DrawLabeledParagraph(
                $"{i + 1}.",
                bodyBoldFont,
                InkPrimary,
                formattedPrompt,
                bodyFont,
                InkPrimary,
                indent: 0,
                lineHeight: 14.5
            );
            y += 3;

            // Rich Content: Code Snippet (true fixed-width Mono font)
            if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
            {
                var langHeader = string.IsNullOrWhiteSpace(q.CodeLanguage)
                    ? null
                    : $"Listing ({q.CodeLanguage})";
                DrawCodeOrFormulaListing(
                    langHeader,
                    q.CodeSnippet,
                    CodeBg,
                    monoFont,
                    indent: 18,
                    showLineNumbers: true
                );
            }

            // Rich Content: Mathematical Formula (CSharpMath TeX PNG with smart multi-line wrapping for long derivations)
            if (!string.IsNullOrWhiteSpace(q.MathLatex))
            {
                bool renderedAsTexImage = false;
                if (LatexMathFormatter.CustomLatexPngRenderer is not null)
                {
                    try
                    {
                        double maxW = ContentWidth() - 36;
                        const double pxToPt = 0.40;
                        var singlePng = LatexMathFormatter.CustomLatexPngRenderer(
                            q.MathLatex,
                            13.5f,
                            0xFF0F172A
                        );

                        var linePngs = new List<byte[]>();
                        bool shouldSplit = q.MathLatex.Contains(@"\\", StringComparison.Ordinal);
                        if (!shouldSplit && singlePng is { Length: > 0 })
                        {
                            using var probeImg = XImage.FromStream(() =>
                                new MemoryStream(singlePng, writable: false)
                            );
                            if (probeImg.PixelWidth * pxToPt > maxW * 1.12)
                                shouldSplit = true;
                        }

                        if (shouldSplit)
                        {
                            var splitLines = LatexMathFormatter.SplitDisplayEquationLines(q.MathLatex);
                            if (splitLines.Count > 1)
                            {
                                foreach (var ln in splitLines)
                                {
                                    var lp = LatexMathFormatter.CustomLatexPngRenderer(
                                        ln,
                                        13.0f,
                                        0xFF0F172A
                                    );
                                    if (lp is { Length: > 0 })
                                        linePngs.Add(lp);
                                }
                            }
                        }

                        if (linePngs.Count == 0 && singlePng is { Length: > 0 })
                            linePngs.Add(singlePng);

                        if (linePngs.Count > 0)
                        {
                            var loaded = new List<(XImage Img, double W, double H)>();
                            try
                            {
                                double totalImgH = 0;
                                foreach (var bytes in linePngs)
                                {
                                    var xTex = XImage.FromStream(() =>
                                        new MemoryStream(bytes, writable: false)
                                    );
                                    double dw = xTex.PixelWidth * pxToPt;
                                    double dh = xTex.PixelHeight * pxToPt;
                                    if (dw > maxW)
                                    {
                                        double s = maxW / dw;
                                        dw *= s;
                                        dh *= s;
                                    }
                                    if (dh > 92)
                                    {
                                        double s = 92.0 / dh;
                                        dw *= s;
                                        dh *= s;
                                    }
                                    loaded.Add((xTex, dw, dh));
                                    totalImgH += dh + 4;
                                }

                                double boxH = totalImgH + 8;
                                EnsureSpace(boxH + 6);
                                double boxX = MarginX + 18;
                                double boxW = ContentWidth() - 18;
                                gfx.DrawRectangle(FormulaBg, boxX, y, boxW, boxH);
                                gfx.DrawRectangle(HairlinePen, boxX, y, boxW, boxH);

                                double curImgY = y + 5;
                                foreach (var (img, dw, dh) in loaded)
                                {
                                    double imgX = boxX + Math.Max(8, (boxW - dw) / 2.0);
                                    gfx.DrawImage(img, imgX, curImgY, dw, dh);
                                    curImgY += dh + 4;
                                }
                                y += boxH + 6;
                                renderedAsTexImage = true;
                            }
                            finally
                            {
                                foreach (var (img, _, _) in loaded)
                                    img.Dispose();
                            }
                        }
                    }
                    catch
                    {
                        renderedAsTexImage = false;
                    }
                }

                if (!renderedAsTexImage)
                {
                    DrawCodeOrFormulaListing(
                        null,
                        BeautifyLatexForPrint(q.MathLatex),
                        FormulaBg,
                        bodyFont,
                        indent: 18,
                        showLineNumbers: false
                    );
                }
            }

            // Rich Content: Actual Vector Graph / Flowchart Diagram
            if (!string.IsNullOrWhiteSpace(q.Diagram))
            {
                DrawDiagramOrGraphFigure(q.Diagram, i + 1);
            }

            // Rich Content: Embedded Image Figure (q.ImagePath OR image q.SourcePdfPath)
            DrawImageFigure(q, i + 1);

            // Question Response Area by Kind
            if (q.Kind == QuizQuestionKind.TrueFalse)
            {
                // Compact single-line True / False choices
                EnsureSpace(16);
                double tfY = y + 11;
                DrawChoiceCircleOrBox(MarginX + 18, y + 3.2, size: 7.8);
                gfx.DrawString("A.  True", bodyFont, InkPrimary, new XPoint(MarginX + 30, tfY));

                DrawChoiceCircleOrBox(MarginX + 128, y + 3.2, size: 7.8);
                gfx.DrawString("B.  False", bodyFont, InkPrimary, new XPoint(MarginX + 140, tfY));
                y += 16;
            }
            else if (q.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational)
            {
                var formattedOpts = q
                    .Options.Select(LatexMathFormatter.FormatInlineMath)
                    .ToList();

                // Check if any option contains structural LaTeX (\frac, \sqrt, \int, etc.) for TeX image rendering
                List<(XImage? Img, double W, double H)>? optionTexImages = null;
                bool hasAnyTexOption =
                    LatexMathFormatter.CustomLatexPngRenderer is not null
                    && q.Options.Any(LatexMathFormatter.ShouldRenderOptionAsTeX);

                if (hasAnyTexOption)
                {
                    optionTexImages = new List<(XImage?, double, double)>(q.Options.Count);
                    double maxOptImgW = ContentWidth() - 62;
                    for (int oi = 0; oi < q.Options.Count; oi++)
                    {
                        XImage? optImg = null;
                        double ow = 0;
                        double oh = 0;
                        if (LatexMathFormatter.ShouldRenderOptionAsTeX(q.Options[oi]))
                        {
                            try
                            {
                                var png = LatexMathFormatter.CustomLatexPngRenderer!(
                                    q.Options[oi],
                                    11.5f,
                                    0xFF181C22
                                );
                                if (png is { Length: > 0 })
                                {
                                    optImg = XImage.FromStream(() =>
                                        new MemoryStream(png, writable: false)
                                    );
                                    ow = optImg.PixelWidth * 0.36;
                                    oh = optImg.PixelHeight * 0.36;
                                    if (ow > maxOptImgW)
                                    {
                                        double s = maxOptImgW / ow;
                                        ow *= s;
                                        oh *= s;
                                    }
                                    if (oh > 46)
                                    {
                                        double s = 46.0 / oh;
                                        ow *= s;
                                        oh *= s;
                                    }
                                }
                            }
                            catch
                            {
                                optImg?.Dispose();
                                optImg = null;
                            }
                        }
                        optionTexImages.Add((optImg, ow, oh));
                    }
                }

                try
                {
                    double colW = (ContentWidth() - 36) / 2.0;
                    bool canUseTwoColumnGrid =
                        q.Options.Count == 4
                        && Enumerable
                            .Range(0, 4)
                            .All(oi =>
                            {
                                if (optionTexImages is not null && optionTexImages[oi].Img is not null)
                                    return optionTexImages[oi].W <= colW - 30;
                                return gfx.MeasureString($"A.  {formattedOpts[oi]}", bodyFont).Width
                                    <= colW - 22;
                            });

                    if (canUseTwoColumnGrid)
                    {
                        // Compact 2x2 academic exam option grid (supports both formatted text and TeX option images)
                        for (int row = 0; row < 2; row++)
                        {
                            double rowH = 15.5;
                            if (optionTexImages is not null)
                            {
                                for (int col = 0; col < 2; col++)
                                {
                                    int oi = (row * 2) + col;
                                    if (optionTexImages[oi].Img is not null)
                                        rowH = Math.Max(rowH, optionTexImages[oi].H + 6);
                                }
                            }
                            EnsureSpace(rowH + 2);
                            double rowMidY = y + (rowH * 0.5);
                            for (int col = 0; col < 2; col++)
                            {
                                int oi = (row * 2) + col;
                                char letter = (char)('A' + oi);
                                double cellX = MarginX + 18 + (col * (colW + 12));
                                DrawChoiceCircleOrBox(cellX, rowMidY - 3.9, size: 7.8);
                                if (optionTexImages is not null && optionTexImages[oi].Img is not null)
                                {
                                    gfx.DrawString(
                                        $"{letter}.",
                                        bodyFont,
                                        InkSecondary,
                                        new XPoint(cellX + 12, rowMidY + 3.5)
                                    );
                                    var (oImg, ow, oh) = optionTexImages[oi];
                                    gfx.DrawImage(
                                        oImg!,
                                        cellX + 26,
                                        rowMidY - (oh * 0.5),
                                        ow,
                                        oh
                                    );
                                }
                                else
                                {
                                    gfx.DrawString(
                                        $"{letter}.  {formattedOpts[oi]}",
                                        bodyFont,
                                        InkPrimary,
                                        new XPoint(cellX + 12, rowMidY + 3.5)
                                    );
                                }
                            }
                            y += rowH;
                        }
                    }
                    else
                    {
                        for (int oi = 0; oi < q.Options.Count; oi++)
                        {
                            char letter = (char)('A' + oi);
                            if (optionTexImages is not null && optionTexImages[oi].Img is not null)
                            {
                                var (oImg, ow, oh) = optionTexImages[oi];
                                double rowH = Math.Max(16, oh + 5);
                                EnsureSpace(rowH + 2);
                                double midY = y + (rowH * 0.5);
                                DrawChoiceCircleOrBox(MarginX + 18, midY - 3.9, size: 7.8);
                                gfx.DrawString(
                                    $"{letter}.",
                                    bodyFont,
                                    InkSecondary,
                                    new XPoint(MarginX + 31, midY + 3.5)
                                );
                                gfx.DrawImage(oImg!, MarginX + 46, midY - (oh * 0.5), ow, oh);
                                y += rowH + 1.5;
                            }
                            else
                            {
                                EnsureSpace(15);
                                DrawChoiceCircleOrBox(MarginX + 18, y + 3.2, size: 7.8);
                                DrawLabeledParagraph(
                                    $"{letter}.",
                                    bodyFont,
                                    InkSecondary,
                                    formattedOpts[oi],
                                    bodyFont,
                                    InkPrimary,
                                    indent: 31,
                                    lineHeight: 14.2
                                );
                                y += 1.5;
                            }
                        }
                    }
                }
                finally
                {
                    if (optionTexImages is not null)
                    {
                        foreach (var (img, _, _) in optionTexImages)
                            img?.Dispose();
                    }
                }
            }
            else if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count > 0)
            {
                DrawWrapped(
                    $"Write the sequence order (1 to {q.Options.Count}) in each box:",
                    italicFont,
                    InkSecondary,
                    indent: 18,
                    lineHeight: 13
                );
                y += 2;
                var scrambled = ScrambleOrderingStepsDeterministically(q.Options, q.Id);
                foreach (var step in scrambled)
                {
                    EnsureSpace(16);
                    gfx.DrawRectangle(CheckboxPen, MarginX + 18, y + 2.5, 16, 10);
                    DrawWrapped(
                        LatexMathFormatter.FormatInlineMath(step),
                        bodyFont,
                        InkPrimary,
                        indent: 40,
                        lineHeight: 14.2
                    );
                    y += 1.5;
                }
            }
            else
            {
                // For math calculation items in Exam Worksheet mode, include a compact "Work" box above the answer line
                bool isMathCalculation =
                    opt.Mode == QuizPdfExportMode.ExamWorksheet
                    && (
                        !string.IsNullOrWhiteSpace(q.MathLatex)
                        || LatexMathFormatter.TryParseNumericValue(q.CorrectAnswer, out _)
                    );
                if (isMathCalculation)
                {
                    const double workH = 44;
                    EnsureSpace(workH + 24);
                    double boxX = MarginX + 18;
                    double boxW = ContentWidth() - 18;
                    gfx.DrawRectangle(SoftPanelBg, boxX, y, boxW, workH);
                    gfx.DrawRectangle(HairlinePen, boxX, y, boxW, workH);
                    gfx.DrawString(
                        "Work / Calculation:",
                        smallFont,
                        InkMuted,
                        new XPoint(boxX + 6, y + 11)
                    );
                    y += workH + 5;
                }

                // Identification or Fill-in-the-Blank clean ruled answer line
                EnsureSpace(20);
                double ansY = y + 13;
                gfx.DrawString("Answer:", smallFont, InkSecondary, new XPoint(MarginX + 18, ansY));
                gfx.DrawLine(WriteLinePen, MarginX + 56, ansY + 1.5, MarginX + 310, ansY + 1.5);
                y += 19;
            }

            if (opt.Mode == QuizPdfExportMode.ExamWorksheet && opt.IncludeConfidenceBoxes)
            {
                EnsureSpace(16);
                y += 2;
                double cx = MarginX + 18;
                gfx.DrawString("Confidence:", smallFont, InkMuted, new XPoint(cx, y + 8.5));
                cx += 52;
                foreach (var conf in new[] { "Guess", "Unsure", "Certain" })
                {
                    DrawChoiceCircleOrBox(cx, y + 1.5, size: 7.0);
                    gfx.DrawString(conf, smallFont, InkMuted, new XPoint(cx + 10, y + 8.5));
                    cx += 52;
                }
                y += 13;
            }

            // Inline Study Guide Mode
            if (opt.Mode == QuizPdfExportMode.StudyGuideInline)
            {
                y += 4;
                if (!string.IsNullOrWhiteSpace(q.Hint))
                {
                    DrawLabeledParagraph(
                        "Hint:",
                        smallBoldFont,
                        HintInk,
                        LatexMathFormatter.FormatInlineMath(q.Hint),
                        italicFont,
                        InkSecondary,
                        indent: 18,
                        lineHeight: 13
                    );
                    y += 2;
                }
                DrawAnswerAndExplanationBlock(q, i + 1, isInline: true);
            }

            y += 13;
        }

        // =====================================================================
        // 3. OPTIONAL PROGRESSIVE HINTS SHEET (Separate Page before Answer Key)
        // =====================================================================
        var questionsWithHints = quiz
            .Questions.Select((q, idx) => (Num: idx + 1, Question: q))
            .Where(x => !string.IsNullOrWhiteSpace(x.Question.Hint))
            .ToList();

        if (
            opt.Mode == QuizPdfExportMode.ExamWorksheet
            && opt.IncludeHintsSection
            && questionsWithHints.Count > 0
        )
        {
            NewPage("Progressive Hints");
            outlines.Add(("Progressive Hints", doc.PageCount - 1));

            DrawWrapped(
                $"Progressive Hints — {quiz.Title}",
                sectionFont,
                InkPrimary,
                lineHeight: 17
            );
            DrawWrapped(
                "Conceptual nudges to consult before checking the Answer Key.",
                subFont,
                InkSecondary,
                lineHeight: 13
            );
            y += 6;
            gfx.DrawLine(HairlinePen, MarginX, y, page.Width.Point - MarginX, y);
            y += 12;

            foreach (var (num, q) in questionsWithHints)
            {
                EnsureSpace(26);
                DrawLabeledParagraph(
                    $"Q{num} (@{q.TopicTag.TrimStart('@')}):",
                    smallBoldFont,
                    HintInk,
                    LatexMathFormatter.FormatInlineMath(q.Hint!),
                    bodyFont,
                    InkPrimary,
                    indent: 2,
                    lineHeight: 13.8
                );
                y += 5;
            }
        }

        // =====================================================================
        // 4. ANSWER KEY, QUICK-GRADING STRIP, BLUEPRINT & EXPLANATIONS
        // =====================================================================
        if (opt.Mode == QuizPdfExportMode.ExamWorksheet && opt.IncludeAnswerKey)
        {
            NewPage("Answer Key & Explanations");
            outlines.Add(("Answer Key & Explanations", doc.PageCount - 1));

            DrawWrapped(
                $"Answer Key & Examiner Reference — {quiz.Title}",
                sectionFont,
                InkPrimary,
                lineHeight: 17
            );
            DrawWrapped(
                "Quick-grading key, assessment blueprint, contrastive explanations, and grounded source citations.",
                subFont,
                InkSecondary,
                lineHeight: 13
            );
            y += 6;
            gfx.DrawLine(StrongRulePen, MarginX, y, page.Width.Point - MarginX, y);
            y += 10;

            // Assessment Blueprint Summary inside Examiner Reference
            DrawBlueprintSummaryBox();

            // Quick-Grading Compact Key Grid (for fast marking of MCQ/TF/Short answers)
            if (quiz.Questions.Count > 0)
            {
                var quickCells = quiz
                    .Questions.Select((q, idx) => $"{idx + 1}. {FormatQuickGradeAnswer(q)}")
                    .ToList();
                var quickLines = new List<string>();
                for (int k = 0; k < quickCells.Count; k += 5)
                {
                    quickLines.Add(string.Join("      ", quickCells.Skip(k).Take(5)));
                }
                DrawCodeOrFormulaListing(
                    "QUICK-GRADING KEY",
                    string.Join('\n', quickLines),
                    SoftPanelBg,
                    smallFont,
                    indent: 0,
                    showLineNumbers: false
                );
                y += 4;
            }

            gfx.DrawLine(HairlinePen, MarginX, y, page.Width.Point - MarginX, y);
            y += 12;

            for (int i = 0; i < quiz.Questions.Count; i++)
            {
                DrawAnswerAndExplanationBlock(quiz.Questions[i], i + 1, isInline: false);
                y += 11;
            }
        }

        gfx.Dispose();

        // =====================================================================
        // 5. RUNNING HEADERS, PAGE FOOTERS (Page X of Y), & PDF BOOKMARKS
        // =====================================================================
        int totalPages = doc.PageCount;
        for (int p = 0; p < totalPages; p++)
        {
            var pg = doc.Pages[p];
            using var pgGfx = XGraphics.FromPdfPage(pg);
            double w = pg.Width.Point;
            double h = pg.Height.Point;

            if (p > 0)
            {
                var secName = p < pageSectionNames.Count ? pageSectionNames[p] : string.Empty;
                pgGfx.DrawString(
                    quiz.Title,
                    smallFont,
                    InkMuted,
                    new XRect(MarginX, 20, w - (MarginX * 2) - 140, 14),
                    XStringFormats.TopLeft
                );
                pgGfx.DrawString(
                    secName,
                    smallFont,
                    InkMuted,
                    new XRect(w - MarginX - 140, 20, 140, 14),
                    XStringFormats.TopRight
                );
                pgGfx.DrawLine(HairlinePen, MarginX, 36, w - MarginX, 36);
            }

            pgGfx.DrawLine(HairlinePen, MarginX, h - 36, w - MarginX, h - 36);
            pgGfx.DrawString(
                $"{quiz.CourseTitle} · ScholarFlow",
                smallFont,
                InkMuted,
                new XRect(MarginX, h - 29, 260, 14),
                XStringFormats.TopLeft
            );
            pgGfx.DrawString(
                $"Page {p + 1} of {totalPages}",
                smallFont,
                InkMuted,
                new XRect(w - MarginX - 90, h - 29, 90, 14),
                XStringFormats.TopRight
            );
        }

        foreach (var (bmTitle, pageIndex) in outlines)
        {
            if (pageIndex >= 0 && pageIndex < doc.PageCount)
                doc.Outlines.Add(bmTitle, doc.Pages[pageIndex], true, PdfOutlineStyle.Bold);
        }

        var dir = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        doc.Save(outputPdfPath);
    }

    public static List<string> ScrambleOrderingStepsDeterministically(
        IReadOnlyList<string> steps,
        string seedKey
    )
    {
        var list = steps.ToList();
        if (list.Count <= 1)
            return list;

        int seed = StringComparer.Ordinal.GetHashCode(seedKey ?? string.Empty);
        var rng = new Random(seed);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        if (list.SequenceEqual(steps, StringComparer.Ordinal) && list.Count >= 2)
        {
            (list[0], list[1]) = (list[1], list[0]);
        }

        return list;
    }

    public static (
        string? WhyCorrect,
        string? WhyDistractorsFail,
        string? CorePrinciple
    ) ParseContrastiveExplanation(string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return (null, null, null);

        var m = ContrastiveSectionRegex().Matches(explanation);
        if (m.Count == 0)
            return (null, null, null);

        string? whyCorrect = null;
        string? whyDistractors = null;
        string? corePrinciple = null;

        for (int i = 0; i < m.Count; i++)
        {
            var label = m[i].Groups["label"].Value.Trim().ToLowerInvariant();
            int start = m[i].Index + m[i].Length;
            int end = i + 1 < m.Count ? m[i + 1].Index : explanation.Length;
            var val = explanation[start..end].Trim().TrimEnd(';').Trim();
            if (string.IsNullOrEmpty(val))
                continue;

            if (label.Contains("correct"))
                whyCorrect = val;
            else if (label.Contains("distractor"))
                whyDistractors = val;
            else if (label.Contains("principle"))
                corePrinciple = val;
        }

        return (whyCorrect, whyDistractors, corePrinciple);
    }

    public static string FormatSourceCitation(QuizQuestionRecord q)
    {
        var fileName = !string.IsNullOrWhiteSpace(q.SourcePdfPath)
            ? Path.GetFileName(q.SourcePdfPath)
            : string.Empty;
        var (kind, _) = StudySourceClassifier.Classify(q.SourcePdfPath);

        string loc;
        if (kind is StudySourceKind.Code or StudySourceKind.Markdown or StudySourceKind.PlainText)
        {
            loc = q.SourceStartLine is > 0
                ? (
                    q.SourceEndLine is > 0 && q.SourceEndLine != q.SourceStartLine
                        ? $"L{q.SourceStartLine}–L{q.SourceEndLine}"
                        : $"L{q.SourceStartLine}"
                )
                : string.Empty;
        }
        else if (kind == StudySourceKind.Image)
        {
            loc = "Image";
        }
        else
        {
            loc = q.SourcePage > 0 ? $"p. {q.SourcePage}" : string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(fileName))
            parts.Add(fileName);
        if (!string.IsNullOrWhiteSpace(q.SectionTitle))
            parts.Add(q.SectionTitle);
        if (!string.IsNullOrWhiteSpace(loc))
            parts.Add(loc);

        return string.Join(" · ", parts);
    }

    private static string FormatQuickGradeAnswer(QuizQuestionRecord q)
    {
        if (
            q.Kind
            is QuizQuestionKind.MultipleChoice
                or QuizQuestionKind.Situational
                or QuizQuestionKind.TrueFalse
        )
        {
            var opts =
                q.Options.Count > 0
                    ? q.Options
                    : (q.Kind == QuizQuestionKind.TrueFalse ? ["True", "False"] : []);
            int idx = opts.FindIndex(o =>
                string.Equals(o.Trim(), q.CorrectAnswer.Trim(), StringComparison.OrdinalIgnoreCase)
            );
            if (idx >= 0)
            {
                return q.Kind == QuizQuestionKind.TrueFalse
                    ? $"{(char)('A' + idx)} ({opts[idx]})"
                    : $"{(char)('A' + idx)}";
            }
        }

        if (q.Kind == QuizQuestionKind.Ordering)
            return "[Sequence]";

        var ans = LatexMathFormatter.FormatInlineMath(q.CorrectAnswer);
        return ans.Length > 18 ? ans[..18].Trim() + "…" : ans;
    }

    private static string FormatAnswerForKey(QuizQuestionRecord q)
    {
        if (
            q.Kind
            is QuizQuestionKind.MultipleChoice
                or QuizQuestionKind.Situational
                or QuizQuestionKind.TrueFalse
        )
        {
            var opts =
                q.Options.Count > 0
                    ? q.Options
                    : (q.Kind == QuizQuestionKind.TrueFalse ? ["True", "False"] : []);
            int idx = opts.FindIndex(o =>
                string.Equals(o.Trim(), q.CorrectAnswer.Trim(), StringComparison.OrdinalIgnoreCase)
            );
            if (idx >= 0)
                return $"{(char)('A' + idx)}. {LatexMathFormatter.FormatInlineMath(opts[idx])}";
        }

        if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count > 0)
        {
            return string.Join(
                "  ->  ",
                q.Options.Select((s, idx) => $"{idx + 1}. {LatexMathFormatter.FormatInlineMath(s)}")
            );
        }

        return LatexMathFormatter.FormatInlineMath(q.CorrectAnswer);
    }

    private static string FormatKindShort(QuizQuestionKind kind) =>
        kind switch
        {
            QuizQuestionKind.MultipleChoice => "MCQ",
            QuizQuestionKind.TrueFalse => "True/False",
            QuizQuestionKind.Identification => "Identification",
            QuizQuestionKind.Situational => "Situational",
            QuizQuestionKind.FillInTheBlank => "Fill-in-the-Blank",
            QuizQuestionKind.Ordering => "Ordering",
            _ => kind.ToString(),
        };

    private static string BeautifyLatexForPrint(string latex) =>
        LatexMathFormatter.FormatInlineMath(latex);

    [GeneratedRegex(
        @"(?<label>Why\s+Correct|Why\s+Distractors\s+Fail|Core\s+Principle)\s*:",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ContrastiveSectionRegex();
}
