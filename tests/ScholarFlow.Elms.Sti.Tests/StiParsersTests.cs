using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;
using ScholarFlow.Elms.Sti.Parsing;

namespace ScholarFlow.Elms.Sti.Tests;

public sealed class StiParsersTests
{
    [Fact]
    public void DashboardParser_FiltersAdminAndCompletedByDefaultAndExtractsCodesAndCleanTitles()
    {
        const string html = """
            <html><body>
              <div id="left_nav_classes_panel_enrolled" class="scroll enrolled">
                <ul class="bypass">
                  <li class="extra-content">
                    <a href="/student_class/show/5808116"><div class="image"></div> Caloocan - Tertiary: Teacher Evaluation SY2026-2027-1T</a>
                    <span><span>Course code </span>STI College Caloocan &middot; <span>Section code </span>Tertiary Survey SY2627-1T</span>
                  </li>
                  <li class="extra-content">
                    <a href="/student_class/show/5725165"><div class="image"></div> Software Quality Assurance - SY2627-1T</a>
                    <span><span>Course code </span>INSY1027 &middot; <span>Section code </span>BSCS4101</span>
                  </li>
                  <li class="extra-content">
                    <a href="/student_class/show/2040360"><div class="image"></div> STI Guidance and Counseling Office</a>
                    <span><span>Course code </span>GCO &middot; <span>Section code </span>STI College Caloocan</span>
                  </li>
                </ul>
              </div>
              <div id="left_nav_classes_panel_completed" class="scroll completed">
                <ul class="bypass">
                  <li>
                    <a href="/student_class/show/3554072"><div class="image"></div> STI Tertiary Student Orientation SY 2022-23</a>
                  </li>
                </ul>
              </div>
            </body></html>
            """;

        var activeAcademic = DashboardParser.ParseEnrolledCourses(html, "https://elms.sti.edu");
        Assert.Single(activeAcademic);
        Assert.Equal("5725165", activeAcademic[0].CourseId);
        Assert.Equal("Software Quality Assurance", activeAcademic[0].Title);
        Assert.Equal("Software_Quality_Assurance", activeAcademic[0].SanitizedFolderName);
        Assert.Equal("INSY1027", activeAcademic[0].CourseCode);
        Assert.Equal("BSCS4101", activeAcademic[0].TermOrSection);
        Assert.Equal("SY2627-1T", activeAcademic[0].TermSuffix);

        var allCourses = DashboardParser.ParseEnrolledCourses(html, "https://elms.sti.edu", includeCompleted: true, includeAdministrative: true);
        Assert.Equal(4, allCourses.Count);
        Assert.True(allCourses.Single(c => c.CourseId == "5808116").IsAdministrativeShell);
        Assert.True(allCourses.Single(c => c.CourseId == "3554072").IsCompleted);
    }

    [Fact]
    public void CoursePageParser_ParsesModernModuleRowsSectionsAndBuildsTermMap()
    {
        var course = new ElmsCourse("5725165", "Software Quality Assurance", "https://elms.sti.edu/student_class/show/5725165", "Software_Quality_Assurance");
        const string html = """
            <html><body><div id="centreColumn">
              <table><tbody>
                <tr class="modern_module_row" data-activity="lesson_activity_26713727">
                  <td>
                    <a href="/student_lesson/show/5725165?lesson_id=26713727" class="modern_module_wrap">
                      <h3 class="evo-lesson-name">03 Software Quality Challenges (Prelim)</h3>
                    </a>
                  </td>
                </tr>
                <tr class="section multicolumn">
                  <td><table>
                    <tr>
                      <td><span class="flex_section_name"><i class="package small icnColor"></i><span><a href="/student_lesson/show/5725165?lesson_id=26713727&section_id=113888016">03 Online Presentation</a></span></span></td>
                    </tr>
                    <tr>
                      <td><span class="flex_section_name"><i class="pageText small icnColor"></i><span><a href="/student_lesson/show/5725165?lesson_id=26713727&section_id=113888017">03 Handout 1</a></span></span></td>
                    </tr>
                    <tr>
                      <td><span class="flex_section_name"><i class="pencil small icnColor"></i><span><a href="/student_lesson/show/5725165?lesson_id=26713727&section_id=113888019">03 Task Performance 1</a></span></span></td>
                    </tr>
                  </table></td>
                </tr>
                <tr class="modern_module_row" data-activity="lesson_activity_26713730">
                  <td>
                    <a href="/student_lesson/show/5725165?lesson_id=26713730" class="modern_module_wrap">
                      <h3 class="evo-lesson-name">06 Product Assurance Activities (Pre-final)</h3>
                    </a>
                  </td>
                </tr>
              </tbody></table>
            </div></body></html>
            """;

        var parsed = CoursePageParser.Parse(html, course, "https://elms.sti.edu");

        Assert.Equal(2, parsed.Modules.Count);
        var mod3 = parsed.Modules[0];
        Assert.Equal("Prelims", mod3.TermHeader);
        Assert.Equal("03", mod3.ModuleNumberPrefix);
        Assert.Equal(3, mod3.ModuleSections.Count);
        Assert.Equal(ElmsSectionKind.Presentation, mod3.ModuleSections[0].Kind);
        Assert.Equal(ElmsSectionKind.Handout, mod3.ModuleSections[1].Kind);
        Assert.Equal(ElmsSectionKind.Assignment, mod3.ModuleSections[2].Kind);

        var snapshot = new ElmsCourseSnapshot(course, [], parsed.Modules, [], [], [], DateTimeOffset.UtcNow);
        var termMap = snapshot.BuildTermMap();
        Assert.Contains("03", termMap["Prelims"]);
        Assert.Contains("06", termMap["Prefinals"]);
    }

    [Fact]
    public void AssignmentsListAndDetailParser_ExtractsTableRowsRightColumnSubmissionAndFiles()
    {
        const string listHtml = """
            <html><body><table>
              <tr>
                <td class="no-print"><i class="pencil medium icnColor"></i></td>
                <td class="assignment">
                  <a title="Essay: 03 Task Performance 1" href="/student_freeform_assignment/show/59104093">03 Task Performance 1</a>
                  <br/><span><a href="/student_lesson/show/5725165?lesson_id=26713727">03 Software Quality Challenges (Prelim)</a></span>
                </td>
                <td>&nbsp;</td>
                <td>Sep 20 <br/><span>11:00 am</span></td>
                <td>25.0</td>
                <td><span title="Yes"><i class="tick icnColor"></i></span></td>
                <td>-</td>
                <td>?/50</td>
                <td>-</td>
              </tr>
            </table></body></html>
            """;

        var summaries = AssignmentsListParser.Parse(listHtml, "5725165", referenceYear: 2026);
        Assert.Single(summaries);
        Assert.Equal("59104093", summaries[0].LmsId);
        Assert.Equal(ElmsAssignmentKind.Freeform, summaries[0].Kind);
        Assert.Equal("Prelims", summaries[0].TermHeader);
        Assert.Equal(AssignmentStatus.Submitted, summaries[0].Status);
        Assert.Equal(25.0, summaries[0].WeightPercent);

        const string detailHtml = """
            <html><body><div id="centreColumn">
              <div id="fixedSectionHeader"><h2>03 Task Performance 1</h2></div>
              <div class="leftColumn">
                <div class="materialStyle">
                  <p>1. Compare software error, fault, and failure. <strong>(10 points)</strong></p>
                  <a href="/files/401/rubric.pdf?lmsauth=111">Grading Rubric</a>
                </div>
              </div>
              <aside class="rightColumn">
                <div class="grading_fixed_col">
                  <div class="block"><h2>Assignment</h2><ul><li>Type: Essay</li><li>Max score: 50</li><li>Due: Sep 20, 11:00 am</li></ul></div>
                  <div class="block"><h2>Score</h2><p>Waiting for grade</p></div>
                  <div class="block">
                    <a class="sectionLink" href="/student_freeform_assignment/submissions/59104093">details</a>
                    <h2>Submission</h2>
                    <p>Submitted: Sep 20, 10:56 am</p>
                    <p>Attempts: <span class="number-of-attempts">1</span></p>
                    <p>Max. attempts: 3</p>
                  </div>
                </div>
              </aside>
            </div></body></html>
            """;

        var detail = AssignmentParser.Parse(detailHtml, summaries[0].Url, "5725165", summary: summaries[0], referenceYear: 2026);
        Assert.NotNull(detail);
        Assert.Equal(50, detail.MaxScore);
        Assert.True(detail.SubmissionInfo.IsSubmitted);
        Assert.Equal(1, detail.SubmissionInfo.Attempts);
        Assert.Equal("https://elms.sti.edu/student_freeform_assignment/submissions/59104093", detail.SubmissionInfo.SubmissionDetailsUrl);
        Assert.Contains("**(10 points)**", detail.InstructionsMarkdown);

        const string submissionsHtml = """
            <html><body><div id="centreColumn"><div class="leftColumn">
              <h2>Download links</h2>
              <p><a href="/files/11603036/SQA_1PRELIMS_03TP1_MOLINA.docx?lmsauth=075e6ba">/files/11603036/SQA_1PRELIMS_03TP1_MOLINA.docx</a></p>
            </div></div></body></html>
            """;

        var subFiles = AssignmentParser.ParseSubmissionFiles(submissionsHtml, "5725165", detail.Title);
        Assert.Single(subFiles);
        Assert.Equal("completed/SQA_1PRELIMS_03TP1_MOLINA.docx", subFiles[0].RelativeTargetPath);
        Assert.Equal("https://elms.sti.edu/files/11603036/SQA_1PRELIMS_03TP1_MOLINA.docx", subFiles[0].CanonicalUrl);
    }

    [Fact]
    public void AssignmentsListParser_ParsesStudentQuizLinks()
    {
        const string listHtml = """
            <table><tbody>
              <tr>
                <td></td>
                <td></td>
                <td class="assignment">
                  <a href="/student_quiz/show/60123456">05 Quiz 1</a>
                  <a href="/student_lesson/show/999001">Finals Module</a>
                </td>
                <td>Nov 25, 11:59 pm</td>
                <td>10%</td>
                <td><span title="Not submitted">-</span></td>
                <td><span title="No">-</span></td>
                <td>-</td>
                <td>-</td>
              </tr>
            </tbody></table>
            """;

        var summaries = AssignmentsListParser.Parse(listHtml, "5725165", referenceYear: 2026);
        var quiz = Assert.Single(summaries);
        Assert.Equal("60123456", quiz.LmsId);
        Assert.Equal(ElmsAssignmentKind.Quiz, quiz.Kind);
        Assert.Equal("05 Quiz 1", quiz.Title);
        Assert.Equal("https://elms.sti.edu/student_quiz/show/60123456", quiz.Url);
        Assert.Equal("Finals", quiz.TermHeader);
        Assert.Equal(AssignmentStatus.Pending, quiz.Status);
    }

    [Fact]
    public void AssignmentParser_ParsesModuleEmbeddedQuizAssignmentPage()
    {
        const string quizHtml = """
            <html><body><div id="centreColumn">
              <div id="fixedSectionHeader" class="student assignment">
                <h1 class="centerHeading"><a href="/student_lessons/list/5725165">07 Assuring Software Quality Conformance for Operation Services (Pre-final)</a></h1>
                <h2>07 Quiz 1</h2>
                <nav class="tabnav">
                  <a role="tab" id="tab_instructions" href="/student_quiz_assignment/show/59104103"><span>Instructions</span></a>
                </nav>
              </div>
              <div class="hasRightColumn">
                <div class="leftColumn">
                  <h2>Instructions</h2>
                  <div class="materialStyle"><p></p></div>
                  <p>The quiz is timed, and you have 20:00 (mm:ss) to complete it.</p>
                </div>
                <aside class="rightColumn">
                  <div class="block">
                    <h2>Assignment</h2>
                    <ul>
                      <li>Type: Quiz</li>
                      <li>Max score: 20</li>
                      <li>Start: Nov 19, 7:00 pm</li>
                      <li>Due: Nov 20, 11:00 am</li>
                      <li>Timer: 20:00 (mm:ss)</li>
                    </ul>
                  </div>
                  <div class="block">
                    <h2>Score</h2>
                    <p>Nothing submitted yet</p>
                  </div>
                  <div class="block">
                    <h2>Submission</h2>
                    <p>Almost due</p>
                    <p>Attempts: <span class="number-of-attempts">0</span></p>
                    <p>Max. attempts: 1</p>
                    <p>Allow late submissions: No</p>
                  </div>
                </aside>
              </div>
            </div></body></html>
            """;

        var parsed = AssignmentParser.Parse(
            quizHtml,
            "https://elms.sti.edu/student_lesson/show/5725165?lesson_id=26713731&section_id=113888033",
            "5725165",
            referenceYear: 2026);

        Assert.NotNull(parsed);
        Assert.Equal("59104103", parsed.LmsId);
        Assert.Equal(ElmsAssignmentKind.Quiz, parsed.Kind);
        Assert.Equal("07 Quiz 1", parsed.Title);
        Assert.Equal("https://elms.sti.edu/student_quiz_assignment/show/59104103", parsed.Url);
        Assert.Equal("Prefinals", parsed.TermHeader);
        Assert.Equal(20, parsed.MaxScore);
        Assert.Equal(AssignmentStatus.Pending, parsed.Status);
        Assert.NotNull(parsed.DueDate);
        Assert.Equal(new DateTime(2026, 11, 20, 11, 0, 0), parsed.DueDate.Value.LocalDateTime);
        Assert.NotNull(parsed.StartDate);
        Assert.Equal(new DateTime(2026, 11, 19, 19, 0, 0), parsed.StartDate.Value.LocalDateTime);
        Assert.Equal("20:00 (mm:ss)", parsed.TimerText);
        Assert.Contains("20:00 (mm:ss)", parsed.InstructionsMarkdown);

        var note = HtmlToMarkdownConverter.FormatAssignmentNote(parsed);
        Assert.Contains("timer: \"20:00 (mm:ss)\"", note);
        Assert.Contains("**Timer:** 20:00 (mm:ss)", note);
        Assert.Contains("- **Attempts:** 0 / 1", note);
        Assert.Contains("- **Allow Late Submissions:** No", note);
    }
}
