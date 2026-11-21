using ScholarFlow.Core.Knowledge;

namespace ScholarFlow.Core.Tests;

public sealed class KnowledgeSourceTests
{
    [Theory]
    [InlineData("lecture.pdf", StudySourceKind.Pdf, "pdf", false)]
    [InlineData("notes.md", StudySourceKind.Markdown, "markdown", true)]
    [InlineData("readme.txt", StudySourceKind.PlainText, "text", true)]
    [InlineData("diagram.png", StudySourceKind.Image, "png", true)]
    [InlineData("photo.webp", StudySourceKind.Image, "webp", true)]
    [InlineData("Program.cs", StudySourceKind.Code, "csharp", true)]
    [InlineData("solver.py", StudySourceKind.Code, "python", true)]
    [InlineData("schema.sql", StudySourceKind.Code, "sql", true)]
    [InlineData("kernel.c", StudySourceKind.Code, "c", true)]
    [InlineData("archive.zip", StudySourceKind.Unknown, "zip", false)]
    public void StudySourceClassifier_ClassifiesExtensionsAccurately(
        string fileName,
        StudySourceKind expectedKind,
        string expectedLang,
        bool expectedViewable
    )
    {
        var (kind, lang) = StudySourceClassifier.Classify(fileName);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedLang, lang);
        Assert.Equal(expectedViewable, StudySourceClassifier.IsViewableInSourceViewer(fileName));
    }

    [Fact]
    public void SourceEvidenceGrounder_ResolvesCodeLineRangeAndEnclosingFunction()
    {
        string[] lines =
        [
            "using System;",
            "",
            "public class Sorter",
            "{",
            "    public static int BinarySearch(int[] arr, int target)",
            "    {",
            "        int lo = 0, hi = arr.Length - 1;",
            "        while (lo <= hi)",
            "        {",
            "            int mid = lo + (hi - lo) / 2;",
            "            if (arr[mid] == target) return mid;",
            "            if (arr[mid] < target) lo = mid + 1;",
            "            else hi = mid - 1;",
            "        }",
            "        return -1;",
            "    }",
            "}",
        ];

        var quote = "int mid = lo + (hi - lo) / 2;\nif (arr[mid] == target) return mid;";
        var match = SourceEvidenceGrounder.ResolveLineRangeFromLines(
            lines,
            StudySourceKind.Code,
            quote
        );

        Assert.NotNull(match);
        Assert.Equal(10, match!.StartLine);
        Assert.Equal(11, match.EndLine);
        Assert.Contains("BinarySearch", match.SectionTitle);
    }

    [Fact]
    public void SourceEvidenceGrounder_ResolvesMarkdownLineRangeAndEnclosingHeading()
    {
        string[] lines =
        [
            "# Operating Systems",
            "",
            "## Virtual Memory",
            "Paging divides physical memory into fixed-size frames.",
            "A page fault occurs when a referenced page is not in RAM.",
            "",
            "## Scheduling",
            "Round-robin uses a fixed time quantum.",
        ];

        var match = SourceEvidenceGrounder.ResolveLineRangeFromLines(
            lines,
            StudySourceKind.Markdown,
            "A page fault occurs when a referenced page is not in RAM."
        );

        Assert.NotNull(match);
        Assert.Equal(5, match!.StartLine);
        Assert.Equal(5, match.EndLine);
        Assert.Equal("Virtual Memory", match.SectionTitle);
    }

    [Fact]
    public void SourceEvidenceGrounder_ExtractNumberedLines_RespectsRange()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"sf-ground-{Guid.NewGuid():N}.py");
        try
        {
            File.WriteAllLines(
                tempFile,
                ["def add(a, b):", "    return a + b", "", "print(add(2, 3))"]
            );

            var chunk = SourceEvidenceGrounder.ExtractNumberedLines(
                tempFile,
                startLine: 1,
                endLine: 2
            );
            Assert.Contains("L001: def add(a, b):", chunk);
            Assert.Contains("L002:     return a + b", chunk);
            Assert.DoesNotContain("L004:", chunk);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
