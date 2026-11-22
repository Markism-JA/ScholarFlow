using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Tests;

public sealed class SourceFigureExtractionTests
{
    [Fact]
    public void ExtractPages_DiscoversUncaptionedPureVectorChartCluster()
    {
        MasterPdfService.EnsureFontResolver();
        var tempPdf = Path.Combine(
            Path.GetTempPath(),
            $"scholarflow_vec_test_{Guid.NewGuid():N}.pdf"
        );
        try
        {
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(595);
                page.Height = XUnit.FromPoint(842);
                using var gfx = XGraphics.FromPdfPage(page);

                var font = new XFont("Arial", 12, XFontStyle.Bold);
                var bodyFont = new XFont("Arial", 10, XFontStyle.Regular);
                gfx.DrawString("Throughput vs Concurrency", font, XBrushes.Black, new XPoint(120, 145));

                // Draw an uncaptioned pure-vector bar/line chart (10+ vector paths, no "Figure N" caption)
                var pen = new XPen(XColors.DarkSlateBlue, 1.5);
                gfx.DrawLine(pen, 100, 360, 480, 360); // X-axis
                gfx.DrawLine(pen, 100, 160, 100, 360); // Y-axis
                for (int i = 0; i < 6; i++)
                {
                    double bx = 130 + i * 52;
                    double bh = 40 + i * 24;
                    gfx.DrawRectangle(pen, XBrushes.CornflowerBlue, bx, 360 - bh, 32, bh);
                }
                gfx.DrawLine(pen, 146, 310, 250, 260);
                gfx.DrawLine(pen, 250, 260, 354, 210);
                gfx.DrawLine(pen, 354, 210, 406, 185);

                gfx.DrawString(
                    "As worker thread concurrency scales linearly, request throughput increases until I/O saturation.",
                    bodyFont,
                    XBrushes.Black,
                    new XPoint(72, 420)
                );

                doc.Save(tempPdf);
            }

            var pages = PdfQuizGrounder.ExtractGroundedPages(tempPdf, startPage: 1, endPage: 1);
            var firstPage = Assert.Single(pages);
            Assert.NotNull(firstPage.Figures);
            Assert.NotEmpty(firstPage.Figures);

            var fig = firstPage.Figures[0];
            Assert.Equal("p1_fig1", fig.FigureId);
            Assert.Equal(-1, fig.EmbeddedImageIndex);
            Assert.True(fig.Region.Width >= 0.22);
            Assert.True(fig.Region.Height >= 0.14);
            Assert.Contains("Throughput vs Concurrency", fig.Caption, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPdf))
                File.Delete(tempPdf);
        }
    }

    [Fact]
    public void QuizPdfPrintRenderer_ResolvesRelativeQuizMediaUsingBasinRoot()
    {
        MasterPdfService.EnsureFontResolver();
        var tempBasin = Path.Combine(
            Path.GetTempPath(),
            $"scholarflow_basin_test_{Guid.NewGuid():N}"
        );
        var outputPdf = Path.Combine(tempBasin, "exam.pdf");
        try
        {
            var relImgPath = Path.Combine(".scholarflow", "quiz_media", "qtest", "q1_p1_fig1.png");
            var fullImgPath = Path.Combine(tempBasin, relImgPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullImgPath)!);

            // Minimal valid 1x1 PNG
            byte[] tinyPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII="
            );
            File.WriteAllBytes(fullImgPath, tinyPng);

            var session = new QuizSessionRecord
            {
                Id = "qtest",
                Title = "Basin Media Resolution Test",
                CourseTitle = "CS 101",
                Questions =
                [
                    new QuizQuestionRecord
                    {
                        Id = "q1",
                        Kind = QuizQuestionKind.MultipleChoice,
                        Prompt = "Analyze the attached reference chart.",
                        Options = ["Option A", "Option B", "Option C", "Option D"],
                        CorrectAnswer = "Option A",
                        ImagePath = relImgPath,
                        SourcePage = 3,
                        SourcePdfPath = string.Empty,
                        HighlightRects = [new NormalizedHighlightRect(0.1, 0.2, 0.5, 0.1)],
                    },
                ],
            };

            QuizPdfPrintRenderer.Render(
                session,
                outputPdf,
                QuizPdfExportOptions.ExamDefault with { BasinRoot = tempBasin }
            );

            Assert.True(File.Exists(outputPdf));
            Assert.True(new FileInfo(outputPdf).Length > 1000);
        }
        finally
        {
            if (Directory.Exists(tempBasin))
                Directory.Delete(tempBasin, recursive: true);
        }
    }
}
