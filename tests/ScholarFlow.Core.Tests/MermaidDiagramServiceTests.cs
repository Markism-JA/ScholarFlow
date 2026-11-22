using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Tests;

public sealed class MermaidDiagramServiceTests
{
    [Fact]
    public void TryRenderSvg_RendersFlowchartSequenceClassAndErDiagrams()
    {
        var samples = new[]
        {
            "flowchart LR\n  A[Client] --> B{Cache Hit?}\n  B -->|Yes| C[Fast Return]\n  B -->|No| D[(Database)]",
            "sequenceDiagram\n  Client->>Server: SYN\n  Server-->>Client: SYN-ACK\n  Client->>Server: ACK",
            "classDiagram\n  class Animal {\n    +String name\n    +speak()\n  }\n  Animal <|-- Dog",
            "erDiagram\n  STUDENT ||--o{ ENROLLMENT : registers\n  COURSE ||--o{ ENROLLMENT : includes",
            "stateDiagram-v2\n  [*] --> Idle\n  Idle --> Running : Start\n  Running --> [*]",
        };

        foreach (var spec in samples)
        {
            Assert.True(
                MermaidDiagramService.TryRenderSvg(spec, MermaidThemePreset.Dark, out var darkSvg),
                $"Failed to render dark SVG for: {spec}"
            );
            Assert.Contains("<svg", darkSvg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("var(--", darkSvg, StringComparison.Ordinal);
            Assert.DoesNotContain("color-mix(", darkSvg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("drop-shadow(", darkSvg, StringComparison.OrdinalIgnoreCase);

            Assert.True(
                MermaidDiagramService.TryRenderSvg(spec, MermaidThemePreset.Print, out var printSvg),
                $"Failed to render print SVG for: {spec}"
            );
            Assert.Contains("<svg", printSvg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("var(--", printSvg, StringComparison.Ordinal);
            Assert.DoesNotContain("color-mix(", printSvg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("drop-shadow(", printSvg, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NormalizeMermaidSource_UpgradesLegacyArrowChainsToFlowchartLr()
    {
        const string legacy = "[Client] -> [API Gateway] -> [Database]";
        var normalized = MermaidDiagramService.NormalizeMermaidSource(legacy);
        Assert.StartsWith("flowchart LR", normalized, StringComparison.Ordinal);

        Assert.True(
            MermaidDiagramService.TryRenderSvg(legacy, MermaidThemePreset.Light, out var svg)
        );
        Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API Gateway", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectDiagramTitle_IdentifiesDiagramTypes()
    {
        Assert.Equal("SEQUENCE DIAGRAM", MermaidDiagramService.DetectDiagramTitle("sequenceDiagram\nA->>B: hi"));
        Assert.Equal("ENTITY-RELATIONSHIP DIAGRAM", MermaidDiagramService.DetectDiagramTitle("erDiagram\nA ||--o{ B : has"));
        Assert.Equal("CLASS DIAGRAM", MermaidDiagramService.DetectDiagramTitle("classDiagram\nclass Foo"));
        Assert.Equal("STATE DIAGRAM", MermaidDiagramService.DetectDiagramTitle("stateDiagram-v2\n[*] --> Ready"));
    }
}
