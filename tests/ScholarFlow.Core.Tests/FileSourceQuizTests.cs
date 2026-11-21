using ScholarFlow.Core.Database;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Tests;

public sealed class FileSourceQuizTests : IDisposable
{
    // 1x1 transparent PNG
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sf-filesrc-" + Guid.NewGuid().ToString("N")[..8]);

    public FileSourceQuizTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WritePng(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(TinyPngBase64));
        return path;
    }

    private const string CppSource = """
        #include <iostream>

        int factorial(int n)
        {
            if (n <= 1) return 1;
            return n * factorial(n - 1);
        }

        int main()
        {
            std::cout << factorial(5);
        }
        """;

    [Fact]
    public void PromptBundle_NumbersLinesAndSeparatesImages()
    {
        var cpp = Write("fact.cpp", CppSource);
        var png = WritePng("diagram.png");

        var bundle = FileSourceQuizSupport.BuildPromptBundle([cpp, png]);

        Assert.Contains("=== [FILE: fact.cpp]", bundle.Content);
        Assert.Contains("Language: cpp", bundle.Content);
        Assert.Contains("L001: #include <iostream>", bundle.Content);
        Assert.Single(bundle.IncludedTextFiles);
        Assert.Equal([png], bundle.ImageFiles);
    }

    [Fact]
    public void DiscoverCourseStudyFiles_FindsCodeTextImagesAndSkipsJunk()
    {
        Write("a.cpp", CppSource);
        Write("notes.md", "# Heading");
        WritePng("fig.png");
        Write("lecture.pdf", "%PDF");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "skip.cpp"), "x");

        var names = FileSourceQuizSupport
            .DiscoverCourseStudyFiles(_dir)
            .Select(d => d.DisplayName)
            .ToList();

        Assert.Contains("a.cpp", names);
        Assert.Contains("notes.md", names);
        Assert.Contains("fig.png", names);
        Assert.DoesNotContain("lecture.pdf", names);
        Assert.DoesNotContain("skip.cpp", names);
    }

    [Fact]
    public void ImagePacker_PacksPngIntoPdfAndSkipsUnsupported()
    {
        var png = WritePng("one.png");
        var webp = Path.Combine(_dir, "two.webp");
        File.WriteAllBytes(webp, [1, 2, 3]);
        var outPdf = Path.Combine(_dir, "out", "packet.pdf");

        var result = ImagePacketPacker.PackImagesToPdf([png, webp], outPdf);

        Assert.True(File.Exists(outPdf));
        Assert.Single(result.Pages);
        Assert.Equal("one.png", result.Pages[0].FileName);
        Assert.Contains("two.webp", result.Skipped);
    }

    [Fact]
    public void ImagePacker_BuildsDataUriForVisionApi()
    {
        var png = WritePng("v.png");
        var uri = ImagePacketPacker.BuildDataUri(png);

        Assert.NotNull(uri);
        Assert.StartsWith("data:image/png;base64,", uri);
        Assert.Null(ImagePacketPacker.BuildDataUri(Path.Combine(_dir, "missing.png")));
        Assert.Null(ImagePacketPacker.BuildDataUri(Write("x.txt", "hi")));
    }

    [Fact]
    public async Task ImportJson_GroundsCodeQuestionToExactLineRange()
    {
        var cpp = Write("fact.cpp", CppSource);
        var svc = new QuizLlmService();
        var request = new QuizGenerationRequest
        {
            CourseTitle = "Programming",
            SourceFilePaths = [cpp],
        };

        const string json = """
            {
              "quizTitle": "Recursion",
              "questions": [
                {
                  "kind": "Identification",
                  "prompt": "What does this print?",
                  "correctAnswer": "120",
                  "codeSnippet": "factorial(5)",
                  "codeLanguage": "cpp",
                  "evidence": {
                    "file": "fact.cpp",
                    "sectionTitle": "factorial",
                    "verbatimQuote": "if (n <= 1) return 1;\nreturn n * factorial(n - 1);"
                  }
                }
              ]
            }
            """;

        var session = await svc.ImportQuizFromJsonAsync(request, json);
        var q = Assert.Single(session.Questions);

        Assert.False(q.IsPdfSource);
        Assert.Equal(cpp, q.SourcePdfPath);
        Assert.Equal(5, q.SourceStartLine);
        Assert.Equal(6, q.SourceEndLine);
        Assert.Equal("L5–6", q.EvidenceChipLabel);
        Assert.Empty(q.HighlightRects);
    }

    [Fact]
    public async Task ImportJson_ImageEvidenceLinksOriginalImage()
    {
        var png = WritePng("osi.png");
        var cpp = Write("a.cpp", CppSource);
        var svc = new QuizLlmService();
        var request = new QuizGenerationRequest { CourseTitle = "Net", SourceFilePaths = [cpp, png] };

        const string json = """
            {
              "questions": [
                {
                  "kind": "MultipleChoice",
                  "prompt": "Which layer is shown?",
                  "options": ["Physical", "Network", "Transport", "Session"],
                  "correctAnswer": "Network",
                  "evidence": { "file": "osi.png", "verbatimQuote": "layer 3 box" }
                }
              ]
            }
            """;

        var session = await svc.ImportQuizFromJsonAsync(request, json);
        var q = Assert.Single(session.Questions);

        Assert.Equal(png, q.SourcePdfPath);
        Assert.Equal(png, q.ImagePath);
        Assert.Equal("osi.png", q.EvidenceChipLabel);
    }

    [Fact]
    public void ManualPrompt_IncludesFileEvidenceSchemaAndCodeFormats()
    {
        var cpp = Write("fact.cpp", CppSource);
        var svc = new QuizLlmService();
        var request = new QuizGenerationRequest { CourseTitle = "Programming", SourceFilePaths = [cpp] };

        var prompt = svc.BuildFileSourcePromptForManualCopy(request, 100_000, out var bundle);

        Assert.Contains("\"file\"", prompt);
        Assert.Contains("startLine", prompt);
        Assert.Contains("CODE QUESTION FORMATS", prompt);
        Assert.Contains("Output / Trace Prediction", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"pageNumber\": 12", prompt);
        Assert.Single(bundle.IncludedTextFiles);
    }

    [Fact]
    public void PdfPrompt_StillUsesPageEvidenceSchema()
    {
        var req = new QuizGenerationRequest { CourseTitle = "Net", SourcePdfPath = "/x.pdf" };
        var instr = new QuizLlmService().BuildInstructionPromptForUploadedPdf(req, [(1, "Intro")]);

        Assert.Contains("\"pageNumber\": 12", instr);
        Assert.DoesNotContain("startLine", instr);
    }

    [Fact]
    public async Task Database_RoundTripsLineRangeEvidence()
    {
        var cpp = Write("fact.cpp", CppSource);
        var session = new QuizSessionRecord
        {
            CourseFolder = "Prog",
            Title = "Code deck",
            SourcePdfPath = cpp,
            Questions =
            [
                new QuizQuestionRecord
                {
                    Id = "c1",
                    Prompt = "P",
                    CorrectAnswer = "A",
                    SourcePdfPath = cpp,
                    SourceStartLine = 5,
                    SourceEndLine = 6,
                },
            ],
        };

        await ScholarFlowDatabase.SaveQuizSessionAsync(_dir, session);
        var loaded = await ScholarFlowDatabase.LoadQuizSessionsAsync(_dir);

        var q = Assert.Single(Assert.Single(loaded).Questions);
        Assert.Equal(5, q.SourceStartLine);
        Assert.Equal(6, q.SourceEndLine);
        Assert.False(q.IsPdfSource);
    }

    [Fact]
    public async Task OfflineMode_IsRejectedForFileSources()
    {
        var cpp = Write("fact.cpp", CppSource);
        var request = new QuizGenerationRequest
        {
            SourceFilePaths = [cpp],
            UseOfflineHeuristicFallback = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuizLlmService().GenerateQuizAsync(request));
    }

    private string WriteSamplePdf(string name)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    [Fact]
    public void CodeAwareAllocations_EmphasizesNonMcqFormats()
    {
        var pcts = QuizGenerationRequest.GetCodeAwareAllocations(QuestionAllocationMode.Percentages);
        Assert.Equal(100, pcts.Values.Sum());
        Assert.True(pcts[QuizQuestionKind.Identification] >= 25);
        Assert.True(pcts[QuizQuestionKind.FillInTheBlank] >= 20);
        Assert.True(pcts[QuizQuestionKind.Ordering] >= 10);

        var exact = QuizGenerationRequest.GetCodeAwareAllocations(QuestionAllocationMode.ExactCounts, targetCount: 10);
        Assert.Equal(10, exact.Values.Sum());
        Assert.True(exact[QuizQuestionKind.Identification] >= 2);
        Assert.True(exact[QuizQuestionKind.FillInTheBlank] >= 2);
    }

    [Fact]
    public async Task ImportJson_MixedPdfAndCode_RoutesEvidencePerQuestion()
    {
        var pdf = WriteSamplePdf("lecture.pdf");
        var cpp = Write("fact.cpp", CppSource);
        var svc = new QuizLlmService();
        var request = new QuizGenerationRequest
        {
            CourseTitle = "Algorithms",
            SourcePdfPath = pdf,
            SourceFilePaths = [cpp],
            IncludePdfWithFileSources = true,
        };

        Assert.True(request.IsMixedSources);

        const string json = """
            {
              "quizTitle": "Mixed Recursion Deck",
              "questions": [
                {
                  "kind": "MultipleChoice",
                  "prompt": "What defines a base case?",
                  "options": ["Stops recursion", "Loops forever", "Allocates heap", "Compiles twice"],
                  "correctAnswer": "Stops recursion",
                  "evidence": {
                    "pageNumber": 1,
                    "file": null,
                    "sectionTitle": "Lecture 1",
                    "verbatimQuote": "base case terminates recursive calls"
                  }
                },
                {
                  "kind": "Identification",
                  "prompt": "What does factorial(5) return?",
                  "correctAnswer": "120",
                  "codeSnippet": "if (n <= 1) return 1;\nreturn n * factorial(n - 1);",
                  "codeLanguage": "cpp",
                  "evidence": {
                    "file": "fact.cpp",
                    "startLine": 5,
                    "sectionTitle": "factorial",
                    "verbatimQuote": "if (n <= 1) return 1;\nreturn n * factorial(n - 1);"
                  }
                }
              ]
            }
            """;

        var session = await svc.ImportQuizFromJsonAsync(request, json);
        Assert.Equal(2, session.Questions.Count);
        Assert.Equal(pdf, session.SourcePdfPath);

        var pdfQ = session.Questions[0];
        Assert.True(pdfQ.IsPdfSource);
        Assert.Equal(pdf, pdfQ.SourcePdfPath);
        Assert.Equal("p.1", pdfQ.EvidenceChipLabel);

        var codeQ = session.Questions[1];
        Assert.False(codeQ.IsPdfSource);
        Assert.Equal(cpp, codeQ.SourcePdfPath);
        Assert.Equal(5, codeQ.SourceStartLine);
        Assert.Equal(6, codeQ.SourceEndLine);
        Assert.Equal("L5–6", codeQ.EvidenceChipLabel);
    }

    [Fact]
    public void ImagePacker_PacksScopedPdfAndImages_CombinesPages()
    {
        var pdf = WriteSamplePdf("slides.pdf");
        var png = WritePng("diagram.png");
        var outPdf = Path.Combine(_dir, "combined.pdf");

        var result = ImagePacketPacker.PackScopedPdfAndImages(pdf, [1], [png], outPdf);

        Assert.True(File.Exists(outPdf));
        Assert.Single(result.Pages);
        using var check = PdfSharpCore.Pdf.IO.PdfReader.Open(outPdf, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
        Assert.Equal(2, check.PageCount);
    }

    [Fact]
    public async Task ImportJson_ImageEvidenceParsesRegionBox()
    {
        var png = WritePng("cache.png");
        var svc = new QuizLlmService();
        var request = new QuizGenerationRequest { CourseTitle = "Arch", SourceFilePaths = [png] };

        const string json = """
            {
              "questions": [
                {
                  "kind": "Identification",
                  "prompt": "What size is L2 Cache?",
                  "correctAnswer": "256 KB",
                  "evidence": {
                    "file": "cache.png",
                    "sectionTitle": "L2 Cache Block",
                    "verbatimQuote": "Middle-right L2 Cache box labeled 256 KB",
                    "region": { "x": 0.4, "y": 0.3, "w": 0.35, "h": 0.2 }
                  }
                }
              ]
            }
            """;

        var session = await svc.ImportQuizFromJsonAsync(request, json);
        var q = Assert.Single(session.Questions);
        var rect = Assert.Single(q.HighlightRects);
        Assert.Equal(0.4, rect.X, precision: 3);
        Assert.Equal(0.3, rect.Y, precision: 3);
        Assert.Equal(0.35, rect.Width, precision: 3);
        Assert.Equal(0.2, rect.Height, precision: 3);
    }
}
