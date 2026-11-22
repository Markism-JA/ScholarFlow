using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Tests;

public sealed class ElmsUrlCanonicalizerTests
{
    [Fact]
    public void ElmsUrlCanonicalizer_StripsLmsAuthTokenAndPreservesOtherParams()
    {
        var raw = "https://elms.sti.edu/files/6767994/01_Handout_1.pdf?lmsauth=4f4903aa33419b13e074e102e0060cd608ebdf02";
        Assert.Equal("https://elms.sti.edu/files/6767994/01_Handout_1.pdf", ElmsUrlCanonicalizer.GetCanonicalUrl(raw));

        var withSection = "https://elms.sti.edu/student_lesson/show/5725165?lesson_id=26713725&lmsauth=abc&section_id=113888011";
        Assert.Equal("https://elms.sti.edu/student_lesson/show/5725165?lesson_id=26713725&section_id=113888011", ElmsUrlCanonicalizer.GetCanonicalUrl(withSection));
    }
}
