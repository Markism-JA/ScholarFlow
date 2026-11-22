using System.Text;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;

namespace ScholarFlow.Core.Quiz;

public enum DiagramNodeShape
{
    Rectangle,
    RoundedPill,
    Diamond,
    CylinderDatabase,
    CircleState,
    InitialFinalState,
}

public sealed record DiagramGraphNode(
    string Id,
    string Label,
    DiagramNodeShape Shape,
    string? Subgraph = null,
    bool IsCloze = false
);

public sealed record DiagramGraphEdge(
    string FromId,
    string ToId,
    string? Label,
    bool IsDashed = false
);

public sealed record ParsedDiagramGraph(
    IReadOnlyList<DiagramGraphNode> Nodes,
    IReadOnlyList<DiagramGraphEdge> Edges,
    bool PreferTopDown = false
);

public sealed record SequenceParticipant(string Id, string Label, bool IsActor = false);

public enum SequenceStepKind
{
    Message,
    Note,
    FragmentStart,
    FragmentElse,
    FragmentEnd,
}

public sealed record SequenceStep(
    SequenceStepKind Kind,
    string FromId,
    string ToId,
    string Text,
    bool IsDashed = false,
    bool IsCrossHead = false,
    string? FragmentTag = null
);

public sealed record ParsedSequenceDiagram(
    IReadOnlyList<SequenceParticipant> Participants,
    IReadOnlyList<SequenceStep> Steps,
    bool AutoNumber = false
);

/// <summary>
/// Full Mermaid diagram parser and PdfSharpCore XGraphics vector renderer supporting:
/// 1. Mermaid sequenceDiagram (participant/actor aliases, solid/dashed/async/cross arrows,
///    self-call loops, Note over/left/right, loop/alt/else/opt/par fragment frames, autonumber,
///    and top + bottom lifeline headers).
/// 2. Mermaid flowchart / graph (LR/TD) &amp; stateDiagram-v2 (rectangles, rounded pills,
///    decision diamonds, database cylinders [(DB)], circle states ((S)), [*] start/end states,
///    [???] cloze nodes, subgraph containers, and solid/dashed routed edges).
/// </summary>
public static partial class PdfDiagramGraphRenderer
{
    private static readonly XBrush FrameBg = new XSolidBrush(XColor.FromArgb(252, 252, 253));
    private static readonly XPen FrameBorder = new(XColor.FromArgb(216, 221, 228), 0.75);

    private static readonly XBrush SubgraphBg = new XSolidBrush(XColor.FromArgb(245, 247, 250));
    private static readonly XPen SubgraphPen = new(XColor.FromArgb(180, 190, 204), 0.8)
    {
        DashStyle = XDashStyle.Dash,
    };

    private static readonly XBrush RectNodeFill = new XSolidBrush(XColor.FromArgb(241, 245, 249));
    private static readonly XPen RectNodePen = new(XColor.FromArgb(71, 85, 105), 1.0);

    private static readonly XBrush PillNodeFill = new XSolidBrush(XColor.FromArgb(236, 253, 245));
    private static readonly XPen PillNodePen = new(XColor.FromArgb(22, 101, 52), 1.0);

    private static readonly XBrush DiamondNodeFill = new XSolidBrush(
        XColor.FromArgb(254, 243, 199)
    );
    private static readonly XPen DiamondNodePen = new(XColor.FromArgb(180, 83, 9), 1.0);

    private static readonly XBrush DbNodeFill = new XSolidBrush(XColor.FromArgb(239, 246, 255));
    private static readonly XPen DbNodePen = new(XColor.FromArgb(37, 99, 235), 1.0);

    private static readonly XBrush ClozeNodeFill = new XSolidBrush(XColor.FromArgb(254, 249, 195));
    private static readonly XPen ClozeNodePen = new(XColor.FromArgb(202, 138, 4), 1.2)
    {
        DashStyle = XDashStyle.Dash,
    };

    private static readonly XBrush NoteFill = new XSolidBrush(XColor.FromArgb(254, 249, 195));
    private static readonly XPen NotePen = new(XColor.FromArgb(217, 119, 6), 0.85);

    private static readonly XBrush FragmentHeaderFill = new XSolidBrush(
        XColor.FromArgb(226, 232, 240)
    );
    private static readonly XPen FragmentPen = new(XColor.FromArgb(100, 116, 139), 0.85);
    private static readonly XPen FragmentDividerPen = new(XColor.FromArgb(148, 163, 184), 0.75)
    {
        DashStyle = XDashStyle.Dash,
    };

    private static readonly XPen LifelinePen = new(XColor.FromArgb(148, 163, 184), 0.85)
    {
        DashStyle = XDashStyle.Dash,
    };

    private static readonly XPen EdgeLinePen = new(XColor.FromArgb(51, 65, 85), 1.1);
    private static readonly XPen DashedEdgeLinePen = new(XColor.FromArgb(71, 85, 105), 1.1)
    {
        DashStyle = XDashStyle.Dash,
    };
    private static readonly XBrush ArrowHeadBrush = new XSolidBrush(XColor.FromArgb(51, 65, 85));

    private static readonly XBrush EdgeLabelBg = new XSolidBrush(XColor.FromArgb(255, 255, 255));
    private static readonly XPen EdgeLabelPen = new(XColor.FromArgb(203, 213, 225), 0.7);
    private static readonly XBrush NodeTextBrush = new XSolidBrush(XColor.FromArgb(15, 23, 42));
    private static readonly XBrush EdgeTextBrush = new XSolidBrush(XColor.FromArgb(51, 65, 85));

    public static string StripMermaidCodeFences(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var lines = raw.Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .ToList();
        return string.Join('\n', lines).Trim();
    }

    // =========================================================================
    // 1. MERMAID SEQUENCE DIAGRAM PARSER & VECTOR RENDERER
    // =========================================================================

    public static bool TryParseSequenceDiagram(
        string? rawDiagram,
        out ParsedSequenceDiagram sequence
    )
    {
        sequence = new ParsedSequenceDiagram([], []);
        var cleaned = StripMermaidCodeFences(rawDiagram);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return false;

        bool hasSequenceHeader = lines[0]
            .Trim()
            .StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase);
        bool hasSequenceArrowsOrParticipants = lines.Any(l =>
            SequenceMessageRegex().IsMatch(l.Trim()) || ParticipantDeclRegex().IsMatch(l.Trim())
        );

        if (!hasSequenceHeader && !hasSequenceArrowsOrParticipants)
            return false;

        // If it starts with flowchart/graph, it's not a sequence diagram
        if (
            !hasSequenceHeader
            && Regex.IsMatch(
                lines[0].Trim(),
                @"^(?:flowchart|graph|stateDiagram)\b",
                RegexOptions.IgnoreCase
            )
        )
        {
            return false;
        }

        var participantsById = new Dictionary<string, SequenceParticipant>(
            StringComparer.OrdinalIgnoreCase
        );
        var participantOrder = new List<string>();
        var steps = new List<SequenceStep>();
        bool autoNumber = false;

        SequenceParticipant EnsureParticipant(
            string rawId,
            string? explicitLabel = null,
            bool isActor = false
        )
        {
            var cleanId = rawId.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(cleanId))
                cleanId = "Actor";

            var label = string.IsNullOrWhiteSpace(explicitLabel)
                ? cleanId
                : explicitLabel.Trim().Trim('"', '\'');

            if (!participantsById.TryGetValue(cleanId, out var existing))
            {
                var created = new SequenceParticipant(cleanId, label, isActor);
                participantsById[cleanId] = created;
                participantOrder.Add(cleanId);
                return created;
            }

            if (
                existing.Label.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)
                && !label.Equals(cleanId, StringComparison.OrdinalIgnoreCase)
            )
            {
                var updated = existing with
                {
                    Label = label,
                    IsActor = existing.IsActor || isActor,
                };
                participantsById[cleanId] = updated;
                return updated;
            }

            return existing;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimEnd(';');
            if (
                string.IsNullOrEmpty(line)
                || line.StartsWith("%%", StringComparison.Ordinal)
                || line.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("activate ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("deactivate ", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (line.Equals("autonumber", StringComparison.OrdinalIgnoreCase))
            {
                autoNumber = true;
                continue;
            }

            // 1. participant / actor declaration: "participant A as Client Browser" or "actor U as User"
            var pMatch = ParticipantDeclRegex().Match(line);
            if (pMatch.Success)
            {
                bool isActor = pMatch
                    .Groups["kw"]
                    .Value.Equals("actor", StringComparison.OrdinalIgnoreCase);
                var id = pMatch.Groups["id"].Value.Trim();
                var alias = pMatch.Groups["alias"].Success
                    ? pMatch.Groups["alias"].Value.Trim()
                    : null;
                EnsureParticipant(id, alias, isActor);
                continue;
            }

            // 2. Note over / Note left of / Note right of
            var noteMatch = SequenceNoteRegex().Match(line);
            if (noteMatch.Success)
            {
                var pos = noteMatch.Groups["pos"].Value.Trim().ToLowerInvariant();
                var actorsRaw = noteMatch.Groups["actors"].Value.Trim();
                var text = noteMatch.Groups["text"].Value.Trim();

                var actorParts = actorsRaw.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                var fromActor = EnsureParticipant(actorParts[0]);
                var toActor = actorParts.Length > 1 ? EnsureParticipant(actorParts[1]) : fromActor;

                steps.Add(
                    new SequenceStep(
                        SequenceStepKind.Note,
                        fromActor.Id,
                        toActor.Id,
                        text,
                        FragmentTag: pos
                    )
                );
                continue;
            }

            // 3. Control-flow fragments: loop, alt, opt, par, critical, break, else, end
            var fragStart = SequenceFragmentStartRegex().Match(line);
            if (fragStart.Success)
            {
                var kw = fragStart.Groups["kw"].Value.ToUpperInvariant();
                var cond = fragStart.Groups["label"].Value.Trim();
                steps.Add(
                    new SequenceStep(
                        SequenceStepKind.FragmentStart,
                        string.Empty,
                        string.Empty,
                        cond,
                        FragmentTag: kw
                    )
                );
                continue;
            }

            if (
                line.StartsWith("else", StringComparison.OrdinalIgnoreCase)
                && (line.Length == 4 || char.IsWhiteSpace(line[4]))
            )
            {
                var cond = line.Length > 4 ? line[4..].Trim() : string.Empty;
                steps.Add(
                    new SequenceStep(
                        SequenceStepKind.FragmentElse,
                        string.Empty,
                        string.Empty,
                        cond,
                        FragmentTag: "ELSE"
                    )
                );
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(
                    new SequenceStep(
                        SequenceStepKind.FragmentEnd,
                        string.Empty,
                        string.Empty,
                        string.Empty
                    )
                );
                continue;
            }

            // 4. Message arrow: A ->> B : Message (supports ->>, -->>, ->, -->, -x, --x, -), --) and +/- activation suffixes)
            var msgMatch = SequenceMessageRegex().Match(line);
            if (msgMatch.Success)
            {
                var fromRaw = msgMatch.Groups["from"].Value.Trim();
                var arrow = msgMatch.Groups["arrow"].Value.Trim();
                var toRaw = msgMatch.Groups["to"].Value.Trim().TrimStart('+', '-').Trim();
                var msgText = msgMatch.Groups["msg"].Value.Trim();

                var pFrom = EnsureParticipant(fromRaw);
                var pTo = EnsureParticipant(toRaw);

                bool isDashed = arrow.StartsWith("--", StringComparison.Ordinal);
                bool isCross = arrow.EndsWith('x') || arrow.EndsWith('X');

                steps.Add(
                    new SequenceStep(
                        SequenceStepKind.Message,
                        pFrom.Id,
                        pTo.Id,
                        msgText,
                        IsDashed: isDashed,
                        IsCrossHead: isCross
                    )
                );
            }
        }

        if (participantOrder.Count < 1 || !steps.Any(s => s.Kind == SequenceStepKind.Message))
            return false;

        var orderedParticipants = participantOrder.Select(id => participantsById[id]).ToList();
        sequence = new ParsedSequenceDiagram(orderedParticipants, steps, autoNumber);
        return true;
    }

    public static double EstimateSequenceDiagramHeight(
        ParsedSequenceDiagram seq,
        bool hasCaption = true
    )
    {
        double h = 38; // Top participant boxes
        foreach (var step in seq.Steps)
        {
            h += step.Kind switch
            {
                SequenceStepKind.Message => string.Equals(
                    step.FromId,
                    step.ToId,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? 34
                    : 26,
                SequenceStepKind.Note => 26,
                SequenceStepKind.FragmentStart => 20,
                SequenceStepKind.FragmentElse => 16,
                SequenceStepKind.FragmentEnd => 10,
                _ => 20,
            };
        }
        h += 38; // Bottom participant boxes + padding
        return h + (hasCaption ? 18 : 4);
    }

    public static double DrawSequenceDiagram(
        XGraphics gfx,
        ParsedSequenceDiagram seq,
        double boxX,
        double startY,
        double availWidth,
        XFont actorFont,
        XFont msgFont,
        XFont captionFont,
        string? figureCaption = null
    )
    {
        int n = Math.Max(1, seq.Participants.Count);
        double innerW = availWidth - 24;
        double colSpan = n == 1 ? innerW : innerW / n;
        double actorBoxW = Math.Clamp(colSpan - 14, 54, 112);
        double actorBoxH = 24;

        double totalInnerH = EstimateSequenceDiagramHeight(seq, hasCaption: false);
        gfx.DrawRectangle(FrameBg, boxX, startY, availWidth, totalInnerH);
        gfx.DrawRectangle(FrameBorder, boxX, startY, availWidth, totalInnerH);

        double leftOrigin = boxX + 12;
        double topActorY = startY + 10;
        double bottomActorY = startY + totalInnerH - actorBoxH - 10;

        double GetLifelineX(int idx) =>
            n == 1 ? leftOrigin + (innerW / 2.0) : leftOrigin + (colSpan * idx) + (colSpan / 2.0);

        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < seq.Participants.Count; i++)
            indexById[seq.Participants[i].Id] = i;

        // 1. Draw vertical dashed lifelines first
        for (int i = 0; i < seq.Participants.Count; i++)
        {
            double cx = GetLifelineX(i);
            gfx.DrawLine(LifelinePen, cx, topActorY + actorBoxH, cx, bottomActorY);
        }

        // 2. Draw Top and Bottom Participant Header Boxes
        void DrawParticipantRow(double rowY)
        {
            for (int i = 0; i < seq.Participants.Count; i++)
            {
                var p = seq.Participants[i];
                double cx = GetLifelineX(i);
                var rect = new XRect(cx - (actorBoxW / 2.0), rowY, actorBoxW, actorBoxH);

                if (p.IsActor)
                    gfx.DrawRoundedRectangle(PillNodePen, PillNodeFill, rect, new XSize(10, 10));
                else
                    gfx.DrawRoundedRectangle(RectNodePen, RectNodeFill, rect, new XSize(4, 4));

                var lines = WrapNodeText(gfx, p.Label, actorFont, actorBoxW - 8);
                double lh = 10.0;
                double blockH = lines.Count * lh;
                double ty = rect.Y + (rect.Height - blockH) / 2.0;
                for (int li = 0; li < lines.Count; li++)
                {
                    gfx.DrawString(
                        lines[li],
                        actorFont,
                        NodeTextBrush,
                        new XRect(rect.X + 3, ty + (li * lh), rect.Width - 6, lh),
                        XStringFormats.Center
                    );
                }
            }
        }

        DrawParticipantRow(topActorY);
        DrawParticipantRow(bottomActorY);

        // 3. Walk steps chronologically down the lifelines
        double curY = topActorY + actorBoxH + 14;
        int msgCounter = 1;
        var openFragments = new Stack<(double StartY, string Tag, string Label)>();

        foreach (var step in seq.Steps)
        {
            switch (step.Kind)
            {
                case SequenceStepKind.FragmentStart:
                {
                    openFragments.Push((curY - 4, step.FragmentTag ?? "ALT", step.Text));
                    double fragLeft = leftOrigin + 6;
                    double tagW = Math.Max(
                        34,
                        gfx.MeasureString(step.FragmentTag ?? "ALT", msgFont).Width + 10
                    );
                    gfx.DrawRectangle(FragmentHeaderFill, fragLeft, curY - 4, tagW, 13);
                    gfx.DrawRectangle(FragmentPen, fragLeft, curY - 4, tagW, 13);
                    gfx.DrawString(
                        step.FragmentTag ?? "ALT",
                        msgFont,
                        NodeTextBrush,
                        new XRect(fragLeft, curY - 4, tagW, 13),
                        XStringFormats.Center
                    );
                    if (!string.IsNullOrWhiteSpace(step.Text))
                    {
                        gfx.DrawString(
                            $"[{step.Text}]",
                            msgFont,
                            EdgeTextBrush,
                            new XPoint(fragLeft + tagW + 6, curY + 5.5)
                        );
                    }
                    curY += 18;
                    break;
                }

                case SequenceStepKind.FragmentElse:
                {
                    double fragLeft = leftOrigin + 6;
                    double fragRight = leftOrigin + innerW - 6;
                    gfx.DrawLine(FragmentDividerPen, fragLeft, curY - 2, fragRight, curY - 2);
                    var elseText = string.IsNullOrWhiteSpace(step.Text)
                        ? "[else]"
                        : $"[else {step.Text}]";
                    gfx.DrawString(
                        elseText,
                        msgFont,
                        EdgeTextBrush,
                        new XPoint(fragLeft + 8, curY + 9)
                    );
                    curY += 15;
                    break;
                }

                case SequenceStepKind.FragmentEnd:
                {
                    if (openFragments.Count > 0)
                    {
                        var (fragStartY, _, _) = openFragments.Pop();
                        double fragLeft = leftOrigin + 6;
                        double fragW = innerW - 12;
                        double fragH = Math.Max(18, curY - fragStartY);
                        gfx.DrawRectangle(FragmentPen, fragLeft, fragStartY, fragW, fragH);
                    }
                    curY += 8;
                    break;
                }

                case SequenceStepKind.Note:
                {
                    int i1 = indexById.GetValueOrDefault(step.FromId, 0);
                    int i2 = indexById.GetValueOrDefault(step.ToId, i1);
                    double x1 = GetLifelineX(Math.Min(i1, i2));
                    double x2 = GetLifelineX(Math.Max(i1, i2));
                    double noteW =
                        i1 == i2
                            ? Math.Clamp(colSpan * 0.85, 76, 136)
                            : Math.Max(100, (x2 - x1) + 56);
                    double noteX = (x1 + x2) / 2.0 - (noteW / 2.0);
                    if (step.FragmentTag?.Contains("left") == true)
                        noteX = Math.Max(leftOrigin + 4, x1 - noteW - 6);
                    else if (step.FragmentTag?.Contains("right") == true)
                        noteX = Math.Min(leftOrigin + innerW - noteW - 4, x1 + 6);

                    var noteRect = new XRect(noteX, curY - 2, noteW, 18);
                    gfx.DrawRoundedRectangle(NotePen, NoteFill, noteRect, new XSize(3, 3));
                    var displayNote =
                        step.Text.Length > 42 ? step.Text[..42].Trim() + "…" : step.Text;
                    gfx.DrawString(
                        displayNote,
                        msgFont,
                        NodeTextBrush,
                        noteRect,
                        XStringFormats.Center
                    );
                    curY += 24;
                    break;
                }

                case SequenceStepKind.Message:
                {
                    int iFrom = indexById.GetValueOrDefault(step.FromId, 0);
                    int iTo = indexById.GetValueOrDefault(step.ToId, 0);
                    double x1 = GetLifelineX(iFrom);
                    double x2 = GetLifelineX(iTo);
                    var pen = step.IsDashed ? DashedEdgeLinePen : EdgeLinePen;
                    var msgLabel = seq.AutoNumber ? $"{msgCounter}. {step.Text}" : step.Text;
                    msgCounter++;

                    if (iFrom == iTo)
                    {
                        // Self-message loop on the right of lifeline x1
                        double loopW = Math.Clamp(colSpan * 0.38, 24, 42);
                        double loopH = 14;
                        double yTop = curY + 4;
                        double yBot = yTop + loopH;

                        var p1 = new XPoint(x1, yTop);
                        var p2 = new XPoint(x1 + loopW, yTop);
                        var p3 = new XPoint(x1 + loopW, yBot);
                        var p4 = new XPoint(x1, yBot);

                        gfx.DrawLine(pen, p1, p2);
                        gfx.DrawLine(pen, p2, p3);
                        gfx.DrawLine(pen, p3, p4);
                        DrawArrowHead(gfx, p3, p4);

                        if (!string.IsNullOrWhiteSpace(msgLabel))
                        {
                            DrawEdgeLabelPill(
                                gfx,
                                msgLabel,
                                new XPoint(x1 + loopW + 4, yTop - 5),
                                msgFont,
                                alignLeft: true
                            );
                        }
                        curY += 32;
                    }
                    else
                    {
                        double arrowY = curY + 11;
                        var startPt = new XPoint(x1, arrowY);
                        var endPt = new XPoint(x2, arrowY);

                        gfx.DrawLine(pen, startPt, endPt);
                        if (step.IsCrossHead)
                        {
                            double dir = x2 >= x1 ? 1 : -1;
                            double cx = x2 - (dir * 4);
                            gfx.DrawLine(
                                EdgeLinePen,
                                cx - 3.5,
                                arrowY - 3.5,
                                cx + 3.5,
                                arrowY + 3.5
                            );
                            gfx.DrawLine(
                                EdgeLinePen,
                                cx - 3.5,
                                arrowY + 3.5,
                                cx + 3.5,
                                arrowY - 3.5
                            );
                        }
                        else
                        {
                            DrawArrowHead(gfx, startPt, endPt);
                        }

                        if (!string.IsNullOrWhiteSpace(msgLabel))
                        {
                            DrawEdgeLabelPill(
                                gfx,
                                msgLabel,
                                new XPoint((x1 + x2) / 2.0, arrowY - 7),
                                msgFont
                            );
                        }
                        curY += 25;
                    }
                    break;
                }
            }
        }

        // Close any unclosed fragment blocks gracefully
        while (openFragments.Count > 0)
        {
            var (fragStartY, _, _) = openFragments.Pop();
            gfx.DrawRectangle(
                FragmentPen,
                leftOrigin + 6,
                fragStartY,
                innerW - 12,
                Math.Max(18, curY - fragStartY)
            );
        }

        double endY = startY + totalInnerH;
        if (!string.IsNullOrWhiteSpace(figureCaption))
        {
            gfx.DrawString(
                figureCaption,
                captionFont,
                EdgeTextBrush,
                new XRect(boxX, endY + 3, availWidth, 12),
                XStringFormats.TopCenter
            );
            endY += 16;
        }

        return endY + 4;
    }

    // =========================================================================
    // 2. MERMAID FLOWCHART / DIRECTED GRAPH / STATE DIAGRAM PARSER & RENDERER
    // =========================================================================

    public static bool TryParseGraph(string? rawDiagram, out ParsedDiagramGraph graph)
    {
        graph = new ParsedDiagramGraph([], []);
        var cleaned = StripMermaidCodeFences(rawDiagram);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        // If it's a sequence diagram, let TryParseSequenceDiagram handle it instead!
        if (TryParseSequenceDiagram(cleaned, out _))
            return false;

        var nodesById = new Dictionary<string, DiagramGraphNode>(StringComparer.OrdinalIgnoreCase);
        var nodeOrder = new List<string>();
        var edges = new List<DiagramGraphEdge>();
        string? currentSubgraph = null;
        bool preferTopDown = false;
        int starCounter = 0;

        DiagramGraphNode RegisterNode(string rawToken)
        {
            var tok = rawToken.Trim();
            if (tok == "[*]")
            {
                // In stateDiagram, [*] at start vs end
                starCounter++;
                var starId = $"__state_star_{starCounter}";
                var starNode = new DiagramGraphNode(
                    starId,
                    "●",
                    DiagramNodeShape.InitialFinalState,
                    currentSubgraph
                );
                nodesById[starId] = starNode;
                nodeOrder.Add(starId);
                return starNode;
            }

            var (id, label, shape, isCloze) = ParseNodeToken(tok);
            if (!nodesById.TryGetValue(id, out var existing))
            {
                var created = new DiagramGraphNode(id, label, shape, currentSubgraph, isCloze);
                nodesById[id] = created;
                nodeOrder.Add(id);
                return created;
            }

            if (
                existing.Label.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)
                && !label.Equals(id, StringComparison.OrdinalIgnoreCase)
            )
            {
                var upgraded = new DiagramGraphNode(
                    existing.Id,
                    label,
                    shape,
                    existing.Subgraph ?? currentSubgraph,
                    existing.IsCloze || isCloze
                );
                nodesById[id] = upgraded;
                return upgraded;
            }

            return existing;
        }

        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var treeStack = new List<(int Indent, string NodeId)>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("%%", StringComparison.Ordinal))
                continue;

            if (HeaderDirectiveRegex().IsMatch(trimmed))
            {
                if (
                    trimmed.Contains("TD", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("TB", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase)
                )
                {
                    preferTopDown = true;
                }
                continue;
            }

            if (trimmed.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase))
            {
                var sgRaw = trimmed["subgraph ".Length..].Trim();
                var mSg = Regex.Match(sgRaw, @"^[A-Za-z0-9_]+\s*\[(.+)\]$");
                currentSubgraph = mSg.Success
                    ? mSg.Groups[1].Value.Trim().Trim('"')
                    : sgRaw.Trim('"');
                continue;
            }

            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                currentSubgraph = null;
                continue;
            }

            var statements = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var stmt in statements)
            {
                var s = stmt.Trim();
                if (string.IsNullOrEmpty(s))
                    continue;

                string? trailingEdgeLabel = null;
                int colonIdx = FindTrailingEdgeLabelColon(s);
                if (colonIdx > 0)
                {
                    trailingEdgeLabel = s[(colonIdx + 1)..].Trim();
                    s = s[..colonIdx].Trim();
                }

                var arrowMatches = ArrowTokenRegex().Matches(s);
                if (arrowMatches.Count > 0)
                {
                    int cursor = 0;
                    DiagramGraphNode? prevNode = null;

                    for (int mIdx = 0; mIdx < arrowMatches.Count; mIdx++)
                    {
                        var m = arrowMatches[mIdx];
                        var leftToken = s[cursor..m.Index].Trim();
                        if (!string.IsNullOrEmpty(leftToken))
                            prevNode = RegisterNode(leftToken);

                        cursor = m.Index + m.Length;
                        int nextStart =
                            mIdx + 1 < arrowMatches.Count ? arrowMatches[mIdx + 1].Index : s.Length;
                        var rightToken = s[cursor..nextStart].Trim();

                        if (prevNode is not null && !string.IsNullOrEmpty(rightToken))
                        {
                            var nextNode = RegisterNode(rightToken);
                            var edgeLabel = ExtractEdgeLabelFromArrow(m.Value);
                            if (
                                string.IsNullOrEmpty(edgeLabel)
                                && mIdx == arrowMatches.Count - 1
                                && !string.IsNullOrEmpty(trailingEdgeLabel)
                            )
                            {
                                edgeLabel = trailingEdgeLabel;
                            }

                            bool isDashed = m.Value.Contains('.');
                            edges.Add(
                                new DiagramGraphEdge(prevNode.Id, nextNode.Id, edgeLabel, isDashed)
                            );
                            prevNode = nextNode;
                        }
                    }
                    continue;
                }

                var treeMatch = TreeBranchRegex().Match(rawLine);
                if (treeMatch.Success)
                {
                    int indent = treeMatch.Groups["prefix"].Value.Length;
                    var content = treeMatch.Groups["label"].Value.Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        var node = RegisterNode(content);
                        while (treeStack.Count > 0 && treeStack[^1].Indent >= indent)
                            treeStack.RemoveAt(treeStack.Count - 1);

                        if (treeStack.Count > 0)
                            edges.Add(new DiagramGraphEdge(treeStack[^1].NodeId, node.Id, null));

                        treeStack.Add((indent, node.Id));
                    }
                }
                else if (edges.Count == 0 && !s.Contains("  "))
                {
                    var rootNode = RegisterNode(s);
                    treeStack.Clear();
                    treeStack.Add((-1, rootNode.Id));
                }
            }
        }

        if (nodesById.Count < 2 || edges.Count == 0)
            return false;

        var orderedNodes = nodeOrder.Select(id => nodesById[id]).ToList();
        graph = new ParsedDiagramGraph(orderedNodes, edges, preferTopDown);
        return true;
    }

    public static double DrawGraph(
        XGraphics gfx,
        ParsedDiagramGraph graph,
        double boxX,
        double startY,
        double availWidth,
        XFont nodeFont,
        XFont edgeFont,
        XFont captionFont,
        string? figureCaption = null
    )
    {
        var layout = ComputeLayout(gfx, graph, availWidth - 24, nodeFont);
        double frameH = layout.TotalHeight + 24;

        gfx.DrawRectangle(FrameBg, boxX, startY, availWidth, frameH);
        gfx.DrawRectangle(FrameBorder, boxX, startY, availWidth, frameH);

        double originX = boxX + 12 + Math.Max(0, (availWidth - 24 - layout.TotalWidth) / 2.0);
        double originY = startY + 12;

        // 0. Draw Subgraph Containers
        foreach (
            var sg in graph
                .Nodes.Where(n =>
                    !string.IsNullOrWhiteSpace(n.Subgraph) && layout.NodeRects.ContainsKey(n.Id)
                )
                .GroupBy(n => n.Subgraph!)
        )
        {
            double minX = sg.Min(n => layout.NodeRects[n.Id].X) - 8;
            double minY = sg.Min(n => layout.NodeRects[n.Id].Y) - 16;
            double maxX = sg.Max(n => layout.NodeRects[n.Id].Right) + 8;
            double maxY = sg.Max(n => layout.NodeRects[n.Id].Bottom) + 8;
            var sgRect = new XRect(originX + minX, originY + minY, maxX - minX, maxY - minY);
            gfx.DrawRoundedRectangle(SubgraphPen, SubgraphBg, sgRect, new XSize(5, 5));
            gfx.DrawString(
                sg.Key,
                edgeFont,
                EdgeTextBrush,
                new XPoint(sgRect.X + 6, sgRect.Y + 10)
            );
        }

        // 1. Draw Edges
        foreach (var edge in graph.Edges)
        {
            if (
                !layout.NodeRects.TryGetValue(edge.FromId, out var fromRect)
                || !layout.NodeRects.TryGetValue(edge.ToId, out var toRect)
            )
            {
                continue;
            }

            var r1 = new XRect(
                originX + fromRect.X,
                originY + fromRect.Y,
                fromRect.Width,
                fromRect.Height
            );
            var r2 = new XRect(originX + toRect.X, originY + toRect.Y, toRect.Width, toRect.Height);

            DrawRoutedEdge(
                gfx,
                r1,
                r2,
                edge.Label,
                edge.IsDashed,
                edgeFont,
                originY,
                layout.TotalHeight
            );
        }

        // 2. Draw Nodes
        foreach (var node in graph.Nodes)
        {
            if (!layout.NodeRects.TryGetValue(node.Id, out var relRect))
                continue;

            var rect = new XRect(
                originX + relRect.X,
                originY + relRect.Y,
                relRect.Width,
                relRect.Height
            );
            DrawNodeShape(gfx, node, rect, layout.WrappedLabels[node.Id], nodeFont);
        }

        double endY = startY + frameH;
        if (!string.IsNullOrWhiteSpace(figureCaption))
        {
            gfx.DrawString(
                figureCaption,
                captionFont,
                EdgeTextBrush,
                new XRect(boxX, endY + 3, availWidth, 12),
                XStringFormats.TopCenter
            );
            endY += 16;
        }

        return endY + 4;
    }

    public static double EstimateGraphHeight(
        XGraphics gfx,
        ParsedDiagramGraph graph,
        double availWidth,
        XFont nodeFont,
        bool hasCaption = true
    )
    {
        var layout = ComputeLayout(gfx, graph, availWidth - 24, nodeFont);
        return layout.TotalHeight + 24 + (hasCaption ? 20 : 4);
    }

    private sealed record GraphLayoutResult(
        Dictionary<string, XRect> NodeRects,
        Dictionary<string, List<string>> WrappedLabels,
        double TotalWidth,
        double TotalHeight
    );

    private static GraphLayoutResult ComputeLayout(
        XGraphics gfx,
        ParsedDiagramGraph graph,
        double maxWidth,
        XFont nodeFont
    )
    {
        var nodes = graph.Nodes;
        var wrappedLabels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var nodeSizes = new Dictionary<string, XSize>(StringComparer.OrdinalIgnoreCase);

        double maxNodeW = Math.Clamp((maxWidth - 90) / 3.8, 78, 130);
        foreach (var n in nodes)
        {
            if (n.Shape == DiagramNodeShape.InitialFinalState)
            {
                wrappedLabels[n.Id] = [];
                nodeSizes[n.Id] = new XSize(18, 18);
                continue;
            }

            var lines = WrapNodeText(gfx, n.Label, nodeFont, maxNodeW - 16);
            wrappedLabels[n.Id] = lines;

            double textW =
                lines.Count > 0 ? lines.Max(l => gfx.MeasureString(l, nodeFont).Width) : 36;
            double padW = n.Shape == DiagramNodeShape.Diamond ? 28 : 18;
            double padH = n.Shape is DiagramNodeShape.Diamond or DiagramNodeShape.CylinderDatabase
                ? 17
                : 12;
            double w = Math.Clamp(textW + padW, 56, maxNodeW + 14);
            double h = Math.Max(25, (lines.Count * 11.2) + padH);
            if (n.Shape == DiagramNodeShape.CircleState)
            {
                double d = Math.Clamp(Math.Max(w, h), 34, 76);
                w = d;
                h = d;
            }
            nodeSizes[n.Id] = new XSize(w, h);
        }

        var outMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            outMap[n.Id] = [];
            inDegree[n.Id] = 0;
        }
        foreach (var e in graph.Edges)
        {
            if (
                outMap.TryGetValue(e.FromId, out var list)
                && !list.Contains(e.ToId, StringComparer.OrdinalIgnoreCase)
            )
            {
                list.Add(e.ToId);
                if (inDegree.ContainsKey(e.ToId))
                    inDegree[e.ToId]++;
            }
        }

        var layerByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var n in nodes.Where(n => inDegree[n.Id] == 0))
        {
            layerByNode[n.Id] = 0;
            queue.Enqueue(n.Id);
        }
        if (queue.Count == 0 && nodes.Count > 0)
        {
            layerByNode[nodes[0].Id] = 0;
            queue.Enqueue(nodes[0].Id);
        }

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            int uLayer = layerByNode[u];
            foreach (var v in outMap[u])
            {
                if (!layerByNode.TryGetValue(v, out int existingLayer))
                {
                    layerByNode[v] = uLayer + 1;
                    queue.Enqueue(v);
                }
                else if (existingLayer < uLayer + 1 && uLayer + 1 < nodes.Count)
                {
                    if (!CanReach(outMap, v, u))
                    {
                        layerByNode[v] = uLayer + 1;
                        queue.Enqueue(v);
                    }
                }
            }
        }

        foreach (var n in nodes)
        {
            if (!layerByNode.ContainsKey(n.Id))
                layerByNode[n.Id] = 0;
        }

        var layers = nodes
            .GroupBy(n => layerByNode[n.Id])
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        bool hasSubgraphs = nodes.Any(n => !string.IsNullOrWhiteSpace(n.Subgraph));
        bool isSingleNodePerLayer = layers.All(l => l.Count == 1);
        var rects = new Dictionary<string, XRect>(StringComparer.OrdinalIgnoreCase);

        if (!graph.PreferTopDown && !hasSubgraphs && isSingleNodePerLayer && layers.Count > 4)
        {
            const int colsPerRow = 4;
            const double hGap = 32;
            const double vGap = 30;
            double curY = 8;
            double maxUsedW = 0;

            for (int rowStart = 0; rowStart < layers.Count; rowStart += colsPerRow)
            {
                var slice = layers.Skip(rowStart).Take(colsPerRow).Select(l => l[0]).ToList();
                int rowIndex = rowStart / colsPerRow;
                bool reverseRow = (rowIndex % 2) == 1;

                double rowH = slice.Max(n => nodeSizes[n.Id].Height);
                double rowW = slice.Sum(n => nodeSizes[n.Id].Width) + ((slice.Count - 1) * hGap);
                maxUsedW = Math.Max(maxUsedW, rowW);

                double curX = 0;
                var displayOrder = reverseRow ? slice.AsEnumerable().Reverse().ToList() : slice;
                foreach (var n in displayOrder)
                {
                    var sz = nodeSizes[n.Id];
                    rects[n.Id] = new XRect(
                        curX,
                        curY + (rowH - sz.Height) / 2.0,
                        sz.Width,
                        sz.Height
                    );
                    curX += sz.Width + hGap;
                }

                curY += rowH + vGap;
            }

            return new GraphLayoutResult(
                rects,
                wrappedLabels,
                maxUsedW,
                Math.Max(36, curY - vGap + 12)
            );
        }

        bool horizontalFlow = !graph.PreferTopDown && layers.Count <= 4;
        if (horizontalFlow)
        {
            const double hGap = 40;
            const double vGap = 20;

            var layerWidths = layers.Select(l => l.Max(n => nodeSizes[n.Id].Width)).ToList();
            var layerHeights = layers
                .Select(l => l.Sum(n => nodeSizes[n.Id].Height) + ((l.Count - 1) * vGap))
                .ToList();
            double totalH = Math.Max(42, layerHeights.Max() + (hasSubgraphs ? 28 : 14));
            double curX = hasSubgraphs ? 10 : 0;

            for (int li = 0; li < layers.Count; li++)
            {
                var layer = layers[li];
                double lw = layerWidths[li];
                double lh = layerHeights[li];
                double curY = (totalH - lh) / 2.0;

                foreach (var n in layer)
                {
                    var sz = nodeSizes[n.Id];
                    rects[n.Id] = new XRect(
                        curX + (lw - sz.Width) / 2.0,
                        curY,
                        sz.Width,
                        sz.Height
                    );
                    curY += sz.Height + vGap;
                }

                curX += lw + hGap;
            }

            double totalW = Math.Max(80, curX - hGap + (hasSubgraphs ? 10 : 0));
            return new GraphLayoutResult(rects, wrappedLabels, totalW, totalH);
        }
        else
        {
            const double hGap = 26;
            const double vGap = 28;

            var layerWidths = layers
                .Select(l => l.Sum(n => nodeSizes[n.Id].Width) + ((l.Count - 1) * hGap))
                .ToList();
            var layerHeights = layers.Select(l => l.Max(n => nodeSizes[n.Id].Height)).ToList();
            double totalW = Math.Max(120, layerWidths.Max() + (hasSubgraphs ? 24 : 0));
            double curY = hasSubgraphs ? 18 : 6;

            for (int li = 0; li < layers.Count; li++)
            {
                var layer = layers[li];
                double lw = layerWidths[li];
                double lh = layerHeights[li];
                double curX = (totalW - lw) / 2.0;

                foreach (var n in layer)
                {
                    var sz = nodeSizes[n.Id];
                    rects[n.Id] = new XRect(
                        curX,
                        curY + (lh - sz.Height) / 2.0,
                        sz.Width,
                        sz.Height
                    );
                    curX += sz.Width + hGap;
                }

                curY += lh + vGap;
            }

            double totalH = Math.Max(36, curY - vGap + 12);
            return new GraphLayoutResult(rects, wrappedLabels, totalW, totalH);
        }
    }

    private static void DrawNodeShape(
        XGraphics gfx,
        DiagramGraphNode node,
        XRect rect,
        List<string> lines,
        XFont font
    )
    {
        if (node.Shape == DiagramNodeShape.InitialFinalState)
        {
            gfx.DrawEllipse(RectNodePen, ArrowHeadBrush, rect);
            return;
        }

        var fill = node.IsCloze ? ClozeNodeFill : RectNodeFill;
        var pen = node.IsCloze ? ClozeNodePen : RectNodePen;

        if (node.Shape == DiagramNodeShape.Diamond)
        {
            double cx = rect.X + rect.Width / 2.0;
            double cy = rect.Y + rect.Height / 2.0;
            var pts = new[]
            {
                new XPoint(cx, rect.Y),
                new XPoint(rect.Right, cy),
                new XPoint(cx, rect.Bottom),
                new XPoint(rect.X, cy),
            };
            gfx.DrawPolygon(
                node.IsCloze ? ClozeNodePen : DiamondNodePen,
                node.IsCloze ? ClozeNodeFill : DiamondNodeFill,
                pts,
                XFillMode.Winding
            );
        }
        else if (node.Shape == DiagramNodeShape.CylinderDatabase)
        {
            // Draw vector database cylinder (body + top/bottom elliptical caps)
            var dbFill = node.IsCloze ? ClozeNodeFill : DbNodeFill;
            var dbPen = node.IsCloze ? ClozeNodePen : DbNodePen;
            double capH = 7.0;
            var bodyRect = new XRect(
                rect.X,
                rect.Y + capH / 2.0,
                rect.Width,
                Math.Max(10, rect.Height - capH)
            );
            var topCap = new XRect(rect.X, rect.Y, rect.Width, capH);
            var botCap = new XRect(rect.X, rect.Bottom - capH, rect.Width, capH);

            gfx.DrawEllipse(dbPen, dbFill, botCap);
            gfx.DrawRectangle(dbFill, bodyRect);
            gfx.DrawLine(dbPen, rect.X, topCap.Y + capH / 2.0, rect.X, botCap.Y + capH / 2.0);
            gfx.DrawLine(
                dbPen,
                rect.Right,
                topCap.Y + capH / 2.0,
                rect.Right,
                botCap.Y + capH / 2.0
            );
            gfx.DrawEllipse(dbPen, dbFill, topCap);
        }
        else if (node.Shape == DiagramNodeShape.CircleState)
        {
            gfx.DrawEllipse(
                node.IsCloze ? ClozeNodePen : PillNodePen,
                node.IsCloze ? ClozeNodeFill : PillNodeFill,
                rect
            );
        }
        else if (node.Shape == DiagramNodeShape.RoundedPill)
        {
            gfx.DrawRoundedRectangle(
                node.IsCloze ? ClozeNodePen : PillNodePen,
                node.IsCloze ? ClozeNodeFill : PillNodeFill,
                rect,
                new XSize(11, 11)
            );
        }
        else
        {
            gfx.DrawRoundedRectangle(pen, fill, rect, new XSize(4, 4));
        }

        double lineH = 11.0;
        double blockH = lines.Count * lineH;
        double yOffset = node.Shape == DiagramNodeShape.CylinderDatabase ? 2.5 : 0;
        double startY = rect.Y + yOffset + (rect.Height - blockH) / 2.0;

        for (int i = 0; i < lines.Count; i++)
        {
            gfx.DrawString(
                lines[i],
                font,
                NodeTextBrush,
                new XRect(rect.X + 4, startY + (i * lineH), rect.Width - 8, lineH),
                XStringFormats.Center
            );
        }
    }

    private static void DrawRoutedEdge(
        XGraphics gfx,
        XRect from,
        XRect to,
        string? label,
        bool isDashed,
        XFont edgeFont,
        double originY,
        double totalH
    )
    {
        double c1x = from.X + from.Width / 2.0;
        double c1y = from.Y + from.Height / 2.0;
        double c2x = to.X + to.Width / 2.0;
        double c2y = to.Y + to.Height / 2.0;

        double dx = c2x - c1x;
        double dy = c2y - c1y;
        var pen = isDashed ? DashedEdgeLinePen : EdgeLinePen;

        bool sameRowBackEdge = Math.Abs(dy) < 16 && dx < -(from.Width + 20);
        if (sameRowBackEdge)
        {
            double arcY = Math.Max(originY + 2, Math.Min(from.Y, to.Y) - 12);
            var pStart = new XPoint(c1x, from.Y);
            var pMid1 = new XPoint(c1x, arcY);
            var pMid2 = new XPoint(c2x, arcY);
            var pEnd = new XPoint(c2x, to.Y);

            gfx.DrawLine(pen, pStart, pMid1);
            gfx.DrawLine(pen, pMid1, pMid2);
            gfx.DrawLine(pen, pMid2, pEnd);
            DrawArrowHead(gfx, pMid2, pEnd);

            if (!string.IsNullOrWhiteSpace(label))
                DrawEdgeLabelPill(gfx, label, new XPoint((c1x + c2x) / 2.0, arcY), edgeFont);

            return;
        }

        XPoint startPt;
        XPoint endPt;

        if (Math.Abs(dx) >= Math.Abs(dy) * 1.15)
        {
            if (dx >= 0)
            {
                startPt = new XPoint(from.Right, c1y);
                endPt = new XPoint(to.Left, c2y);
            }
            else
            {
                startPt = new XPoint(from.Left, c1y);
                endPt = new XPoint(to.Right, c2y);
            }
        }
        else
        {
            if (dy >= 0)
            {
                startPt = new XPoint(c1x, from.Bottom);
                endPt = new XPoint(c2x, to.Top);
            }
            else
            {
                startPt = new XPoint(c1x, from.Top);
                endPt = new XPoint(c2x, to.Bottom);
            }
        }

        gfx.DrawLine(pen, startPt, endPt);
        DrawArrowHead(gfx, startPt, endPt);

        if (!string.IsNullOrWhiteSpace(label))
        {
            var mid = new XPoint((startPt.X + endPt.X) / 2.0, (startPt.Y + endPt.Y) / 2.0);
            DrawEdgeLabelPill(gfx, label, mid, edgeFont);
        }

        _ = totalH;
    }

    private static void DrawArrowHead(XGraphics gfx, XPoint from, XPoint to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-3)
            return;

        double ux = dx / len;
        double uy = dy / len;
        double px = -uy;
        double py = ux;

        const double arrowLen = 6.2;
        const double arrowHalfW = 3.0;

        var basePt = new XPoint(to.X - (ux * arrowLen), to.Y - (uy * arrowLen));
        var leftPt = new XPoint(basePt.X + (px * arrowHalfW), basePt.Y + (py * arrowHalfW));
        var rightPt = new XPoint(basePt.X - (px * arrowHalfW), basePt.Y - (py * arrowHalfW));

        gfx.DrawPolygon(ArrowHeadBrush, [to, leftPt, rightPt], XFillMode.Winding);
    }

    private static void DrawEdgeLabelPill(
        XGraphics gfx,
        string label,
        XPoint center,
        XFont font,
        bool alignLeft = false
    )
    {
        var text = label.Length > 28 ? label[..28].Trim() + "…" : label;
        var sz = gfx.MeasureString(text, font);
        double w = sz.Width + 8;
        double h = 11.5;
        double x = alignLeft ? center.X : center.X - (w / 2.0);
        var rect = new XRect(x, center.Y - (h / 2.0), w, h);

        gfx.DrawRoundedRectangle(EdgeLabelPen, EdgeLabelBg, rect, new XSize(3, 3));
        gfx.DrawString(text, font, EdgeTextBrush, rect, XStringFormats.Center);
    }

    private static List<string> WrapNodeText(
        XGraphics gfx,
        string text,
        XFont font,
        double maxWidth
    )
    {
        var result = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        return result.Count > 0 ? result : [text];
    }

    private static bool CanReach(Dictionary<string, List<string>> adj, string start, string target)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<string>();
        q.Enqueue(start);
        visited.Add(start);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur.Equals(target, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!adj.TryGetValue(cur, out var neighbors))
                continue;
            foreach (var nxt in neighbors)
            {
                if (visited.Add(nxt))
                    q.Enqueue(nxt);
            }
        }
        return false;
    }

    private static (string Id, string Label, DiagramNodeShape Shape, bool IsCloze) ParseNodeToken(
        string raw
    )
    {
        var t = raw.Trim();
        bool isCloze = t.Contains("???", StringComparison.Ordinal);

        // Database Cylinder: A[(Database)] or [(Database)]
        var mDb = Regex.Match(t, @"^(?<id>[A-Za-z0-9_]*)\s*\[\((?<label>.+)\)\]$");
        if (mDb.Success)
        {
            var lbl = mDb.Groups["label"].Value.Trim().Trim('"', '\'');
            var id = string.IsNullOrEmpty(mDb.Groups["id"].Value)
                ? lbl
                : mDb.Groups["id"].Value.Trim();
            return (id, lbl, DiagramNodeShape.CylinderDatabase, isCloze);
        }

        // Circle State: A((State)) or ((State))
        var mCircle = Regex.Match(t, @"^(?<id>[A-Za-z0-9_]*)\s*\(\((?<label>.+)\)\)$");
        if (mCircle.Success)
        {
            var lbl = mCircle.Groups["label"].Value.Trim().Trim('"', '\'');
            var id = string.IsNullOrEmpty(mCircle.Groups["id"].Value)
                ? lbl
                : mCircle.Groups["id"].Value.Trim();
            return (id, lbl, DiagramNodeShape.CircleState, isCloze);
        }

        // Mermaid ID + shape syntax: A[Label], B(Label), C{Label}, D([Label])
        var m = MermaidNodeRegex().Match(t);
        if (m.Success)
        {
            var id = m.Groups["id"].Value.Trim();
            var open = m.Groups["open"].Value;
            var inner = m.Groups["label"].Value.Trim().Trim('"', '\'');
            var shape = open.Contains('{')
                ? DiagramNodeShape.Diamond
                : (open.Contains('(') ? DiagramNodeShape.RoundedPill : DiagramNodeShape.Rectangle);
            return (id, string.IsNullOrEmpty(inner) ? id : inner, shape, isCloze);
        }

        if (t.Length >= 3)
        {
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                var inner = t[1..^1].Trim().Trim('"', '\'');
                return (inner, inner, DiagramNodeShape.Rectangle, isCloze);
            }
            if (t.StartsWith('(') && t.EndsWith(')'))
            {
                var inner = t.Trim('(', ')', '[', ']').Trim().Trim('"', '\'');
                return (inner, inner, DiagramNodeShape.RoundedPill, isCloze);
            }
            if (t.StartsWith('{') && t.EndsWith('}'))
            {
                var inner = t[1..^1].Trim().Trim('"', '\'');
                return (inner, inner, DiagramNodeShape.Diamond, isCloze);
            }
        }

        var clean = t.Trim('"', '\'');
        var autoShape = clean.EndsWith('?') ? DiagramNodeShape.Diamond : DiagramNodeShape.Rectangle;
        return (clean, clean, autoShape, isCloze);
    }

    private static string? ExtractEdgeLabelFromArrow(string arrowToken)
    {
        var pipeMatch = PipeLabelRegex().Match(arrowToken);
        if (pipeMatch.Success)
            return pipeMatch.Groups["lbl"].Value.Trim();

        var midMatch = MidArrowLabelRegex().Match(arrowToken);
        if (midMatch.Success)
            return midMatch.Groups["lbl"].Value.Trim();

        var dotMidMatch = DottedMidArrowLabelRegex().Match(arrowToken);
        if (dotMidMatch.Success)
            return dotMidMatch.Groups["lbl"].Value.Trim();

        return null;
    }

    private static int FindTrailingEdgeLabelColon(string s)
    {
        var matches = ArrowTokenRegex().Matches(s);
        if (matches.Count == 0)
            return -1;

        var lastArrow = matches[^1];
        int afterArrow = lastArrow.Index + lastArrow.Length;
        int colon = s.IndexOf(':', afterArrow);
        return colon > afterArrow ? colon : -1;
    }

    [GeneratedRegex(
        @"^(?<kw>participant|actor)\s+(?<id>""[^""]+""|[A-Za-z0-9_\-]+)(?:\s+as\s+(?<alias>.+))?$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ParticipantDeclRegex();

    [GeneratedRegex(
        @"^Note\s+(?<pos>left\s+of|right\s+of|over)\s+(?<actors>[^:]+)\s*:\s*(?<text>.+)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SequenceNoteRegex();

    [GeneratedRegex(
        @"^(?<kw>loop|alt|opt|par|critical|break)\b\s*(?<label>.*)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SequenceFragmentStartRegex();

    [GeneratedRegex(
        @"^(?<from>[A-Za-z0-9_\-""\s]+?)\s*(?<arrow>-->>>|-->>|->>>|->>|--x|-x|--\)|-\)|-->|->)\s*(?<to>[\+\-]?[A-Za-z0-9_\-""\s]+?)\s*:\s*(?<msg>.+)$"
    )]
    private static partial Regex SequenceMessageRegex();

    [GeneratedRegex(@"^(?:graph|flowchart|stateDiagram(?:-v2)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderDirectiveRegex();

    [GeneratedRegex(
        @"(?:-\.\s*(?<lbl>[A-Za-z0-9_\-\?\./\s]{1,24}?)\s*\.->|--+\s*(?<lbl>[A-Za-z0-9_\-\?\./\s]{1,24}?)\s*--+>|-\.->\s*\|(?<lbl>[^\|]+)\||==+>\s*\|(?<lbl>[^\|]+)\||--+>\s*\|(?<lbl>[^\|]+)\|-->?|--+>\s*\|(?<lbl>[^\|]+)\||->\s*\|(?<lbl>[^\|]+)\||-\.->|\.\.>|<\-+>|<--+>|==+>|--+>|->|=>|──>|→|⟶)"
    )]
    private static partial Regex ArrowTokenRegex();

    [GeneratedRegex(@"\|(?<lbl>[^\|]+)\|")]
    private static partial Regex PipeLabelRegex();

    [GeneratedRegex(@"^--+\s*(?<lbl>[^->]+?)\s*--+>$")]
    private static partial Regex MidArrowLabelRegex();

    [GeneratedRegex(@"^-\.\s*(?<lbl>[^.>]+?)\s*\.->$")]
    private static partial Regex DottedMidArrowLabelRegex();

    [GeneratedRegex(
        @"^(?<id>[A-Za-z0-9_]+)\s*(?<open>\(\[|\[\[|\[|\(\(|\(|\{)(?<label>.+?)(?:\]\)|\]\]|\]|\)\)|\)|\})$"
    )]
    private static partial Regex MermaidNodeRegex();

    [GeneratedRegex(@"^(?<prefix>[\s│\|]*[├└\+\|][─\-]+)\s*(?<label>.+)$")]
    private static partial Regex TreeBranchRegex();
}
