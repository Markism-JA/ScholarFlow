using ScholarFlow.Core.Database;
using ScholarFlow.Core.Knowledge;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Core.Tests;

public sealed class KnowledgeSearchTests : IDisposable
{
    private readonly string _tempBasin;

    public KnowledgeSearchTests()
    {
        _tempBasin = Path.Combine(
            Path.GetTempPath(),
            "ScholarFlow_KnowledgeTests_" + Guid.NewGuid().ToString("N")[..10]
        );
        Directory.CreateDirectory(_tempBasin);
        Directory.CreateDirectory(Path.Combine(_tempBasin, ".scholarflow"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempBasin))
                Directory.Delete(_tempBasin, recursive: true);
        }
        catch
        {
            // Ignore cleanup locks
        }
    }

    [Fact]
    public void ParseQuery_ExtractsCoursesSourcesTopicsWeakAndExactPhrases()
    {
        var parsed = KnowledgeSearchEngine.ParseQuery(
            "#os #sqa @code @pdf @paging @weak \"translation lookaside buffer\" virtual memory"
        );

        Assert.Equal(["os", "sqa"], parsed.CourseFilters);
        Assert.Equal(
            [KnowledgeSourceFilterKind.Code, KnowledgeSourceFilterKind.Pdf],
            parsed.SourceFilters
        );
        Assert.Equal(["paging"], parsed.TopicFilters);
        Assert.True(parsed.OnlyWeakSpots);
        Assert.Equal(["translation lookaside buffer"], parsed.ExactPhrases);
        Assert.Equal(["virtual", "memory"], parsed.FreeTextTokens);
        Assert.Equal("virtual memory", parsed.FreeTextCombined);
    }

    [Fact]
    public void BuildFts5MatchExpression_SanitizesSpecialCharactersSafely()
    {
        var parsed = KnowledgeSearchEngine.ParseQuery(
            "\"page fault\" C++ node->next O(1) page_table"
        );
        var expr = KnowledgeSearchEngine.BuildFts5MatchExpression(parsed);

        Assert.NotNull(expr);
        Assert.Contains("\"page fault\"", expr);
        Assert.Contains("page_table*", expr);
        Assert.DoesNotContain("->", expr);
        Assert.DoesNotContain("++", expr);
    }

    [Fact]
    public void ExtractMarkdownAndCodeChunks_PreservesExactLineRangesAndSections()
    {
        var courseDir = Path.Combine(_tempBasin, "Operating_Systems");
        Directory.CreateDirectory(courseDir);

        var mdPath = Path.Combine(courseDir, "memory_notes.md");
        File.WriteAllLines(
            mdPath,
            [
                "# Memory Management",
                "Intro to address binding and logical vs physical addresses.",
                "",
                "## Page Replacement Algorithms",
                "Belady's anomaly occurs in FIFO page replacement when adding frames increases page faults.",
                "LRU avoids Belady's anomaly because it is a stack algorithm.",
            ]
        );

        var mdChunks = KnowledgeIndexService.ExtractMarkdownOrTextChunks(
            mdPath,
            "Operating_Systems",
            "os",
            StudySourceKind.Markdown
        );

        Assert.Equal(2, mdChunks.Count);
        Assert.Equal("Memory Management", mdChunks[0].SectionTitle);
        Assert.Equal(1, mdChunks[0].StartLine);
        Assert.Equal(3, mdChunks[0].EndLine);

        Assert.Equal("Page Replacement Algorithms", mdChunks[1].SectionTitle);
        Assert.Equal(4, mdChunks[1].StartLine);
        Assert.Equal(6, mdChunks[1].EndLine);
        Assert.Contains("Belady's anomaly", mdChunks[1].Content);

        var codePath = Path.Combine(courseDir, "scheduler.py");
        File.WriteAllLines(
            codePath,
            [
                "class ProcessControlBlock:",
                "    def __init__(self, pid, burst):",
                "        self.pid = pid",
                "        self.burst = burst",
                "        self.waiting = 0",
                "        self.turnaround = 0",
                "        self.priority = 1",
                "        self.remaining = burst",
                "        self.state = 'READY'",
                "        self.arrival = 0",
                "        self.quantum = 4",
                "        self.context_switches = 0",
                "        self.io_wait = 0",
                "        self.deadline = 100",
                "",
                "def schedule_round_robin(queue, quantum):",
                "    time_elapsed = 0",
                "    while queue:",
                "        proc = queue.pop(0)",
                "        step = min(quantum, proc.remaining)",
                "        proc.remaining -= step",
                "        time_elapsed += step",
                "    return time_elapsed",
            ]
        );

        var codeChunks = KnowledgeIndexService.ExtractCodeChunks(
            codePath,
            "Operating_Systems",
            "os",
            "python"
        );

        Assert.True(codeChunks.Count >= 2);
        Assert.Contains(
            codeChunks,
            c =>
                c.SectionTitle.Contains("schedule_round_robin", StringComparison.OrdinalIgnoreCase)
                && c.StartLine == 16
        );
    }

    [Fact]
    public async Task IncrementalIndexing_SkipsUnchanged_ReindexesModified_AndPrunesDeleted()
    {
        var courses = new List<BasinCourseEntry>
        {
            new()
            {
                FolderName = "Operating_Systems",
                Title = "Operating Systems",
                Alias = "os",
            },
        };

        var courseDir = Path.Combine(_tempBasin, "Operating_Systems");
        Directory.CreateDirectory(courseDir);

        var mdFile = Path.Combine(courseDir, "deadlocks.md");
        var codeFile = Path.Combine(courseDir, "banker.c");

        await File.WriteAllTextAsync(
            mdFile,
            "# Deadlock Conditions\nCoffman conditions: mutual exclusion, hold and wait, no preemption, circular wait."
        );
        await File.WriteAllTextAsync(
            codeFile,
            "int is_safe_state(int available[], int allocation[][4], int need[][4]) {\n    return 1;\n}"
        );

        var indexer = new KnowledgeIndexService();

        // 1. Initial index pass
        var pass1 = await indexer.IndexBasinIncrementalAsync(_tempBasin, courses, quizzes: []);
        Assert.Equal(2, pass1.FilesIndexed);
        Assert.Equal(0, pass1.FilesSkipped);
        Assert.Equal(0, pass1.FilesPruned);
        Assert.True(pass1.TotalIndexedChunks >= 2);

        // 2. Unchanged pass -> O(1) skip
        var pass2 = await indexer.IndexBasinIncrementalAsync(_tempBasin, courses, quizzes: []);
        Assert.Equal(0, pass2.FilesIndexed);
        Assert.Equal(2, pass2.FilesSkipped);
        Assert.Equal(0, pass2.FilesPruned);

        // 3. Modify only mdFile -> only 1 file reindexed
        await File.WriteAllTextAsync(
            mdFile,
            "# Deadlock Conditions\nCoffman conditions plus resource allocation graph cycle detection and Ostrich algorithm."
        );
        File.SetLastWriteTimeUtc(mdFile, DateTime.UtcNow.AddSeconds(5));

        var pass3 = await indexer.IndexBasinIncrementalAsync(_tempBasin, courses, quizzes: []);
        Assert.Equal(1, pass3.FilesIndexed);
        Assert.Equal(1, pass3.FilesSkipped);
        Assert.Equal(0, pass3.FilesPruned);

        var searchModified = await KnowledgeSearchEngine.SearchAsync(
            _tempBasin,
            "Ostrich algorithm"
        );
        Assert.Single(searchModified.Results);
        Assert.Contains("«Ostrich»", searchModified.Results[0].HighlightedSnippet);

        // 4. Delete codeFile -> pruned from FTS5 index
        File.Delete(codeFile);
        var pass4 = await indexer.IndexBasinIncrementalAsync(_tempBasin, courses, quizzes: []);
        Assert.Equal(0, pass4.FilesIndexed);
        Assert.Equal(1, pass4.FilesSkipped);
        Assert.Equal(1, pass4.FilesPruned);

        var searchDeleted = await KnowledgeSearchEngine.SearchAsync(
            _tempBasin,
            "is_safe_state"
        );
        Assert.Empty(searchDeleted.Results);
    }

    [Fact]
    public async Task SearchAsync_FiltersByCourseAndSource_AndAppliesWeakSpotBoost()
    {
        var courses = new List<BasinCourseEntry>
        {
            new()
            {
                FolderName = "Operating_Systems",
                Title = "Operating Systems",
                Alias = "os",
            },
            new()
            {
                FolderName = "Software_Quality_Assurance",
                Title = "Software Quality Assurance",
                Alias = "sqa",
            },
        };

        var osDir = Path.Combine(_tempBasin, "Operating_Systems");
        var sqaDir = Path.Combine(_tempBasin, "Software_Quality_Assurance");
        Directory.CreateDirectory(osDir);
        Directory.CreateDirectory(sqaDir);

        await File.WriteAllTextAsync(
            Path.Combine(osDir, "paging.md"),
            "# Virtual Memory Paging\nPage tables translate virtual pages to physical frames using a translation lookaside buffer."
        );
        await File.WriteAllTextAsync(
            Path.Combine(osDir, "mmu.c"),
            "int translate_tlb_entry(int virtual_page) {\n    // Translation lookaside buffer lookup\n    return virtual_page & 0xFF;\n}"
        );
        await File.WriteAllTextAsync(
            Path.Combine(sqaDir, "testing.md"),
            "# Regression Testing\nAutomated test suites verify translation lookaside buffer simulators after refactoring."
        );

        var quiz = new QuizSessionRecord
        {
            Id = "qz_os_1",
            CourseFolder = "Operating_Systems",
            CourseTitle = "Operating Systems",
            Title = "OS Midterm Review",
            SourcePdfPath = Path.Combine(osDir, "paging.md"),
            ModelUsed = "Test",
            Questions =
            [
                new QuizQuestionRecord
                {
                    Id = "q_tlb_weak",
                    QuizId = "qz_os_1",
                    TopicTag = "paging",
                    SectionTitle = "Virtual Memory Paging",
                    Prompt = "Why does a translation lookaside buffer (TLB) reduce effective memory access time?",
                    CorrectAnswer = "It caches recent virtual-to-physical page frame translations in fast associative hardware.",
                    Explanation = "Why Correct: Associative lookup avoids extra page-table memory reads.",
                    SrsLapses = 3,
                    LastAnsweredCorrect = false,
                },
            ],
        };

        await ScholarFlowDatabase.SaveQuizSessionAsync(_tempBasin, quiz);

        var indexer = new KnowledgeIndexService();
        await indexer.IndexBasinIncrementalAsync(_tempBasin, courses, [quiz]);

        var weakProfile = KnowledgeWeakSpotProfile.FromQuizzes([quiz]);
        Assert.Equal(1, weakProfile.WeakTopicCount);

        // 1. Filter by #os and @code
        var codeOnly = await KnowledgeSearchEngine.SearchAsync(
            _tempBasin,
            "#os @code translation lookaside",
            weakProfile
        );
        Assert.Single(codeOnly.Results);
        Assert.Equal("code", codeOnly.Results[0].SourceKind);
        Assert.Equal("mmu.c", codeOnly.Results[0].Title);

        // 2. Weak-spot boost surfaces weak @paging items and sets IsWeakSpot = true
        var allTlb = await KnowledgeSearchEngine.SearchAsync(
            _tempBasin,
            "\"translation lookaside buffer\"",
            weakProfile
        );
        Assert.True(allTlb.Results.Count >= 3);
        Assert.True(allTlb.Results[0].IsWeakSpot);
        Assert.Equal("os", allTlb.Results[0].CourseAlias);

        // 3. @weak filter returns only weak-spot items
        var weakOnly = await KnowledgeSearchEngine.SearchAsync(
            _tempBasin,
            "@weak",
            weakProfile
        );
        Assert.NotEmpty(weakOnly.Results);
        Assert.All(weakOnly.Results, r => Assert.True(r.IsWeakSpot));
    }

    [Fact]
    public async Task CoursePacketPlan_OriginAwareDefaults_AndMultiFormatMasterPdfCompilation()
    {
        var courseDir = Path.Combine(_tempBasin, "Local_Algorithms");
        Directory.CreateDirectory(courseDir);

        await File.WriteAllTextAsync(
            Path.Combine(courseDir, "01_graph_notes.md"),
            "# Graph Traversal\nBFS explores level by level using a queue.\n\n## Complexity\nO(V + E) time."
        );
        await File.WriteAllTextAsync(
            Path.Combine(courseDir, "bfs.py"),
            "from collections import deque\n\ndef bfs(graph, start):\n    visited = {start}\n    q = deque([start])\n    return visited\n"
        );

        // eLMS default (includeNonPdfByDefault = false) marks non-PDFs excluded by default
        var elmsPlan = Core.Pdf.CoursePacketPlan.LoadOrBuildPlan(
            courseDir,
            configuredTerms: [],
            useStiSyllabusFallback: true,
            includeNonPdfByDefault: false
        );
        var elmsItems = elmsPlan.Sections.SelectMany(s => s.Items).ToList();
        Assert.Equal(2, elmsItems.Count);
        Assert.All(elmsItems, i => Assert.True(i.IsExcluded));

        // Local Hub default (includeNonPdfByDefault = true) includes Markdown & Code by default
        var localPlan = Core.Pdf.CoursePacketPlan.LoadOrBuildPlan(
            courseDir,
            configuredTerms: [],
            useStiSyllabusFallback: false,
            includeNonPdfByDefault: true
        );
        var localItems = localPlan.Sections.SelectMany(s => s.Items).ToList();
        Assert.Equal(2, localItems.Count);
        Assert.All(localItems, i => Assert.False(i.IsExcluded));

        Core.Pdf.CoursePacketPlan.SavePlan(courseDir, localPlan);
        var pdfService = new Core.Pdf.MasterPdfService();
        var compiled = await pdfService.CompileIfStaleAsync(
            _tempBasin,
            "Local_Algorithms",
            "Local Algorithms",
            force: true,
            configuredTerms: [],
            useStiSyllabusFallback: false
        );

        Assert.True(compiled.WasRegenerated);
        Assert.True(File.Exists(compiled.OutputPdfPath));
        // Cover + TOC + at least 2 content pages (md + py)
        Assert.True(compiled.PageCount >= 4);
    }
}
