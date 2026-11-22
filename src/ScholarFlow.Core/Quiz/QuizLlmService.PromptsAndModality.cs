using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Quiz;

public sealed partial class QuizLlmService
{
    public static (
        bool DetectedCode,
        bool DetectedMath,
        bool DetectedDiagrams,
        string? DetectedLanguage
    ) AnalyzeTextSignals(
        string? combinedText,
        string? courseTitle = null,
        string? sectionTitles = null
    )
    {
        var text = combinedText ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(sectionTitles)
            ? (courseTitle ?? string.Empty)
            : $"{courseTitle} {sectionTitles}";

        // 1. Code Detection
        int codeSignals = 0;
        string? detectedLang = null;

        if (
            Regex.IsMatch(
                text,
                @"\b(public\s+class|private\s+void|namespace\s+[A-Z]|Console\.Write|using\s+System)\b"
            )
        )
        {
            codeSignals += 3;
            detectedLang ??= "csharp";
        }
        if (
            Regex.IsMatch(
                text,
                @"\b(def\s+[a-zA-Z_][a-zA-Z0-9_]*\s*\(|import\s+numpy|print\s*\(|elif\s+)\b"
            )
        )
        {
            codeSignals += 3;
            detectedLang ??= "python";
        }
        if (
            Regex.IsMatch(
                text,
                @"\b(SELECT\s+.+\s+FROM\s+|CREATE\s+TABLE|INSERT\s+INTO|INNER\s+JOIN)\b",
                RegexOptions.IgnoreCase
            )
        )
        {
            codeSignals += 3;
            detectedLang ??= "sql";
        }
        if (
            Regex.IsMatch(
                text,
                @"\b(function\s+[a-zA-Z_]|const\s+[a-zA-Z_]+\s*=|console\.log\(|=>)\b"
            )
        )
        {
            codeSignals += 2;
            detectedLang ??= "javascript";
        }
        if (
            Regex.IsMatch(
                text,
                @"(#include\s*<|#\s*i\s*n\s*c\s*l\s*u\s*d\s*e|\bint\s+main\s*\(|\bprintf\s*\(|\bmalloc\s*\(|\bsizeof\s*\()"
            )
        )
        {
            codeSignals += 3;
            detectedLang ??= "c";
        }
        if (
            Regex.IsMatch(
                text,
                @"(%(rax|eax|rbx|ebx|rcx|ecx|rdx|edx|rsp|esp|rbp|ebp|rdi|rsi)\b|\b(mov[lqwb]?|push[lq]?|pop[lq]?|retq?)\s+)"
            )
        )
        {
            codeSignals += 3;
            detectedLang ??= "asm";
        }
        if (
            Regex.IsMatch(
                text,
                @"(\b(gcc|g\+\+|clang|make|sudo|chmod|grep|awk|curl|dotnet|python3?)\b|linux>\s+)"
            )
        )
        {
            codeSignals += 2;
            detectedLang ??= "bash";
        }
        if (
            Regex.IsMatch(
                text,
                @"(\b(procedure|algorithm)\s+[A-Za-z_]|for\s+each\s+.+\s+do\b|while\s+.+\s+do\b|if\s+.+\s+then\b|:=)"
            )
        )
        {
            codeSignals += 2;
            detectedLang ??= "pseudocode";
        }
        if (Regex.Matches(text, @"[{};]").Count >= 12)
            codeSignals += 2;

        // 2. Math / Symbolic Formula / Quantitative Textbook Detection
        // Handles both raw Unicode/LaTeX AND PdfPig-extracted math textbooks where special glyphs are stripped
        int mathSignals = 0;
        if (
            Regex.IsMatch(
                courseTitle ?? string.Empty,
                @"\b(Math|Mathematics|Calculus|Algebra|Linear|Geometry|Trigonometry|Trig|Differential|Integral|Statistics|Statistical|Probability|Stochastic|Discrete|Combinatorics|Graph\s+Theory|Number\s+Theory|Physics|Kinematics|Thermodynamics|Electromagnetism|Quantum|Chemistry|Stoichiometry|Numerical|Optimization|Operations\s+Research|Quantitative|Econometrics|Actuarial|Signals|Fourier|Laplace|Control\s+Systems|Machine\s+Learning|Algorithm|Complexity|Cryptography)\b",
                RegexOptions.IgnoreCase
            )
        )
            mathSignals += 2;

        if (
            !string.IsNullOrWhiteSpace(sectionTitles)
            && Regex.IsMatch(
                sectionTitles,
                @"\b(Theorem|Lemma|Corollary|Proof|Equation|Derivative|Integral|Limit|Matrix|Matrices|Determinant|Eigenvalue|Vector|Polynomial|Logarithm|Exponential|Trigonometric|Probability|Variance|Distribution|Series|Sequence|Recurrence|Big-O|Asymptotic|Calculus|Algebra)\b",
                RegexOptions.IgnoreCase
            )
        )
            mathSignals += 2;

        int symbolOrLatexHits = Regex
            .Matches(
                text,
                @"[∑∫√≤≥≠±∞∂∆∇λμσπθϕωαβγϵ∈∉⊂⊆∪∩∀∃⇒⇔≈≡∝⊥∥∠]|\\(frac|dfrac|tfrac|sum|prod|int|oint|sqrt|lim|infty|partial|nabla|alpha|beta|gamma|delta|theta|lambda|mu|sigma|pi|omega|sin|cos|tan|log|ln|exp|binom|cdot|times|leq|geq|neq|approx|equiv|subset|cup|cap|forall|exists|rightarrow|Rightarrow|begin\{(equation|align|matrix|pmatrix|bmatrix|cases)\})|\$\$[\s\S]{2,120}?\$\$|\\\[[\s\S]{2,120}?\\\]"
            )
            .Count;
        if (symbolOrLatexHits >= 2)
            mathSignals += 3;
        else if (symbolOrLatexHits == 1)
            mathSignals += 2;

        int mathVocabHits = Regex
            .Matches(
                text,
                @"\b(Theorem|Lemma|Corollary|Proposition|Proof|Q\.E\.D\.|Axiom|Postulate|Conjecture|Equation|Inequality|Polynomial|Derivative|Differentiable|Integrable|Integral|Antiderivative|Limit|Continuity|Converge|Diverge|Series|Matrix|Matrices|Determinant|Eigenvalue|Eigenvector|Vector\s+Space|Subspace|Orthogonal|Normal\s+Distribution|Random\s+Variable|Variance|Standard\s+Deviation|Covariance|Hypothesis\s+Test|p-value|Confidence\s+Interval|Logarithm|Exponential|Trigonometric|Hypotenuse|Parabola|Hyperbola|Asymptote|Factorial|Permutation|Combination|Binomial|Recurrence|Big-O|Asymptotic|Quadratic)\b",
                RegexOptions.IgnoreCase
            )
            .Count;
        if (mathVocabHits >= 5)
            mathSignals += 3;
        else if (mathVocabHits >= 2)
            mathSignals += 2;

        int mathNotationHits = Regex
            .Matches(
                text,
                @"(\bf\s*\(\s*[xyztn]\s*\)\s*=|\bg\s*\(\s*[xyztn]\s*\)|\bdy\s*/\s*dx\b|\bd\s*/\s*dx\b|\blim\s*[_{xtn]|\b(sin|cos|tan|sec|csc|cot|arcsin|arccos|arctan|log|ln)\s*[\(\^a-zA-Z0-9]|\bP\s*\(\s*[A-Z]\s*(\||∩|∪|,)\s*[A-Z]\s*\)|\bE\s*\[\s*[XY]\s*\]|\bVar\s*\(\s*[XY]\s*\)|\bdet\s*\(\s*[A-Z]\s*\)|\bO\s*\(\s*[1nVElg\s\^\*log]+\)|[a-zA-Z]\s*\^\s*\{?[0-9n+-]+\}?|\b\d+\s*[+\-*/=]\s*[a-zA-Z]\b|\b([a-zA-Z]|[0-9]+)\s*([+\-*/^]\s*([a-zA-Z]|[0-9]+)\s*)+=\s*[\-+]?[a-zA-Z0-9])"
            )
            .Count;
        if (mathNotationHits >= 5)
            mathSignals += 3;
        else if (mathNotationHits >= 2)
            mathSignals += 2;

        // 3. Diagram / Architecture / Process Flow Detection
        int diagramSignals = 0;
        if (
            Regex
                .Matches(
                    $"{title}\n{text}",
                    @"\b(Figure\s+\d+|Diagram|Flowchart|Sequence\s+Diagram|State\s+Machine|Architecture|Topology|Lifecycle|Pipeline|OSI\s+Model|State\s+Transition|Layer\s+\d+|UML|Entity\s+Relationship|Block\s+Diagram|Control\s+Flow|Data\s+Flow|Circuit|Protocol\s+Stack)\b",
                    RegexOptions.IgnoreCase
                )
                .Count >= 2
        )
            diagramSignals += 3;
        if (text.Contains("->") || text.Contains("-->") || text.Contains("->>"))
            diagramSignals += 2;

        return (
            DetectedCode: codeSignals >= 3,
            DetectedMath: mathSignals >= 3,
            DetectedDiagrams: diagramSignals >= 3,
            DetectedLanguage: detectedLang
        );
    }

    public static ContentModalityProfile DetectContentModalities(
        IReadOnlyList<GroundedPdfPage> pages,
        string? courseTitle = null,
        QuizContentModalityMode mode = QuizContentModalityMode.AutoDetect,
        QuizContentModalityFlags selectedFlags = QuizContentModalityFlags.None
    )
    {
        var combinedText = string.Join("\n", pages.Select(p => p.Text));
        var sectionTitles = string.Join(
            " | ",
            pages
                .Select(p => p.SectionBreadcrumb)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        );
        var (detCode, detMath, detDiag, detLang) = AnalyzeTextSignals(
            combinedText,
            courseTitle,
            sectionTitles
        );

        return BuildResolvedModalityProfile(
            detCode,
            detMath,
            detDiag,
            detLang,
            hasImageSources: false,
            mode,
            selectedFlags
        );
    }

    public static ContentModalityProfile BuildResolvedModalityProfile(
        bool detectedCode,
        bool detectedMath,
        bool detectedDiagrams,
        string? detectedLanguage,
        bool hasImageSources,
        QuizContentModalityMode mode,
        QuizContentModalityFlags selectedFlags = QuizContentModalityFlags.None
    )
    {
        if (mode == QuizContentModalityMode.StandardProseOnly)
        {
            return new ContentModalityProfile(
                HasCode: false,
                HasMath: false,
                HasDiagrams: false,
                DetectedLanguage: detectedLanguage,
                DetectedCodeInSource: detectedCode,
                DetectedMathInSource: detectedMath,
                DetectedDiagramsInSource: detectedDiagrams,
                IsProseOnlyOverride: true,
                HasImageSources: hasImageSources
            );
        }

        var flags = selectedFlags;
        flags |= mode switch
        {
            QuizContentModalityMode.IncludeCodeSnippets => QuizContentModalityFlags.Code,
            QuizContentModalityMode.IncludeMathLatex => QuizContentModalityFlags.MathLatex,
            QuizContentModalityMode.IncludeDiagrams => QuizContentModalityFlags.Diagrams,
            _ => QuizContentModalityFlags.None,
        };

        bool hasCode = flags.HasFlag(QuizContentModalityFlags.Code) || detectedCode;
        bool hasMath = flags.HasFlag(QuizContentModalityFlags.MathLatex) || detectedMath;
        bool hasDiagrams =
            flags.HasFlag(QuizContentModalityFlags.Diagrams) || detectedDiagrams || hasImageSources;

        return new ContentModalityProfile(
            HasCode: hasCode,
            HasMath: hasMath,
            HasDiagrams: hasDiagrams,
            DetectedLanguage: detectedLanguage,
            DetectedCodeInSource: detectedCode,
            DetectedMathInSource: detectedMath,
            DetectedDiagramsInSource: detectedDiagrams,
            IsProseOnlyOverride: false,
            HasImageSources: hasImageSources
        );
    }

    private static string BuildSystemInstructions(
        QuizGenerationRequest req,
        IReadOnlyList<string> weakTopics,
        ContentModalityProfile modality,
        int? targetQuestionCountOverride = null
    )
    {
        var exactCounts = req.ResolveExactKindCounts(targetQuestionCountOverride);
        var activeKindSpecs = exactCounts
            .Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Value} {kv.Key}")
            .ToList();
        var disabledKinds = exactCounts
            .Where(kv => kv.Value <= 0)
            .Select(kv => kv.Key.ToString())
            .ToList();

        var distributionSpec =
            activeKindSpecs.Count > 0
                ? $"Exact Question-Type Breakdown Required: {string.Join(", ", activeKindSpecs)}."
                    + (
                        disabledKinds.Count > 0
                            ? $" Do NOT generate any of: {string.Join(", ", disabledKinds)}."
                            : ""
                    )
                : "Generate a balanced academic assessment across requested question types.";

        var pedagogicalBlueprint = QuizBlueprintBuilder.BuildPedagogicalBlueprintBlock(
            req,
            weakTopics,
            modality
        );
        var modalityRules = BuildModalitySystemRules(
            modality,
            req.ModalityMode,
            req.SelectedModalityFlags,
            exactCounts
        );
        bool fileMode = req.HasFileSources;
        bool mixedMode = req.IsMixedSources;
        var intro =
            mixedMode
                ? "You are an expert academic examiner creating an evidence-grounded assessment combining a course study PDF and course source files (code, notes, and/or images)."
            : fileMode
                ? "You are an expert academic examiner creating an evidence-grounded assessment from course source files (code, notes, and/or images)."
            : "You are an expert academic examiner creating an evidence-grounded assessment from a course study PDF.";
        var evidenceRule =
            mixedMode ? MixedEvidenceRule
            : fileMode ? FileEvidenceRule
            : PdfEvidenceRule;
        var evidenceSchema =
            mixedMode ? MixedEvidenceSchema
            : fileMode ? FileEvidenceSchema
            : PdfEvidenceSchema;
        var codeFormatRules = modality.HasCode ? CodeQuestionFormatRules : string.Empty;
        var termSchemaValue =
            !string.IsNullOrWhiteSpace(req.Term) ? $"\"{req.Term}\""
            : req.AvailableTerms.Count > 0 ? $"\"{string.Join(" | ", req.AvailableTerms)}\""
            : "null";

        return $$"""
            {{intro}}
            {{distributionSpec}}

            {{pedagogicalBlueprint}}

            {{modalityRules}}{{codeFormatRules}}

            CRITICAL RULES:
            1. Return ONLY a valid JSON object matching the exact schema below. Do not include commentary outside the JSON.
            {{evidenceRule}}
            3. For MultipleChoice and Situational questions:
               - Provide 4 options in "options" (plain text without "A)" prefixes).
               - "correctAnswer" MUST exactly match one of the strings in "options".
               - Distractors must be plausible sibling concepts or misconceptions from the same material, with balanced option lengths.
            4. For Identification:
               - "options" must be an empty array [].
               - "correctAnswer" must be the concise canonical term (1-4 words).
               - "acceptedAliases" should list valid synonyms, acronyms, or alternate spellings.
            5. For FillInTheBlank:
               - "prompt" MUST include "_____" where the missing key term or phrase belongs.
               - "options" must be an empty array [].
               - "correctAnswer" must be the exact missing word/phrase that fills "_____".
            6. For Ordering:
               - "prompt" asks the student to arrange steps, phases, layers, or lifecycle stages in the proper sequence.
               - "options" MUST contain 3 to 5 distinct steps listed IN THEIR CORRECT CHRONOLOGICAL/LOGICAL ORDER (the app shuffles them at runtime).
               - "correctAnswer" MUST be those steps joined by " -> " in the exact correct order.
            7. For TrueFalse:
               - "options" must be ["True", "False"] and "correctAnswer" must be "True" or "False".
            8. Ignore syllabus boilerplate, headers/footers ("STI College", copyright lines, table of contents).
            9. SELF-CONTAINED CONCEPTUAL STEMS & HYBRID SOURCE-FIGURE vs. MERMAID SELECTION:
               - Every question "prompt" and "options" MUST be 100% self-contained for a human in a closed-book exam: always name or describe the actual concept, system, protocol, or mathematical object being tested (e.g., "In the TCP three-way handshake..." or "In a write-back L1 cache...").
               - NEVER write opaque document-location references in "prompt", "options", or "hint" such as "In Figure 3.2...", "According to Table 1...", "In Section 4.1...", "On Page 12...", or "In the handout..."—a human student does not have those figure numbers memorized.
               - CHOOSING BETWEEN REAL SOURCE FIGURE ("sourceFigureId") vs. MERMAID ("diagram"):
                 * When a page header lists "[EXTRACTABLE SOURCE FIGURES ON PAGE N: id=\"pN_figM\" (...)]" and the figure is a mathematical plot, circuit, geometry figure, hardware layout, screenshot, or complex diagram where the real image is clearer than Mermaid: set "sourceFigureId": "pN_figM" (and leave "diagram": null) so the app automatically crops and attaches the actual source figure image to the question card!
                 * NO-ANSWER-LEAKAGE RULE FOR ATTACHED SOURCE FIGURES: Because a cropped source figure has its original labels visibly printed inside the image, NEVER ask the student to simply read or name a label that is already printed in the figure. Instead, ask an interpretation, causal perturbation ("What happens if stage X stalls?"), calculation, or mechanism question about the figure.
                 * When testing a logical/process flow, state machine, sequence interaction, or a cloze-masked node ("[???]") where hiding a label is essential: populate "diagram" (Mermaid "flowchart LR", "sequenceDiagram", or "[A] -> [B]") and keep "sourceFigureId": null.
                 * If neither a cropped figure nor a diagram adds value, keep both "sourceFigureId": null and "diagram": null and state the concept directly in "prompt".

            REQUIRED JSON SCHEMA:
            {
              "quizTitle": "Course — Term Topic Assessment",
              "courseTerm": {{termSchemaValue}},
              "keyConceptsCovered": ["concept 1", "concept 2"],
              "questions": [
                {
                  "kind": "MultipleChoice | Situational | Identification | TrueFalse | FillInTheBlank | Ordering",
                  "cognitiveLevel": "Recall | Comprehension | Application | Analysis",
                  "archetype": "FarTransfer | DiscriminativeContrast | CausalPerturbation | SpotTheFlaw | DualCoding",
                  "difficulty": 3,
                  "topicTag": "short-kebab-tag",
                  "prompt": "Clear question stem, scenario, or sentence containing _____...",
                  "options": ["Option 1", "Option 2", "Option 3", "Option 4"],
                  "correctAnswer": "Option 1",
                  "acceptedAliases": [],
                  "hint": "Self-contained 1-sentence thinking clue (strategy, invariant, or contrast cue) without leaking the answer; NEVER cite 'See Section/Figure/Page X' (or null if disabled).",
                  "explanation": "Why correct: ... Why distractors fail: ... Core principle: ...",
                  "codeSnippet": null,
                  "codeLanguage": null,
                  "mathLatex": null,
                  "diagram": null,
                  "sourceFigureId": null,
                  "evidence": {{evidenceSchema}}
                }
              ]
            }
            """;
    }

    private const string PdfEvidenceRule = """
        2. Every single question MUST include an "evidence" object with:
           - "pageNumber": the exact integer page number from the "=== [PAGE N] | Section: ... ===" header where the fact appears.
           - "sectionTitle": the exact section title from that page header.
           - "verbatimQuote": an exact 8 to 28 word substring copied WORD-FOR-WORD from that page's text so our PDF highlighter can locate its pixel bounding box.
           - "secondaryQuotes" (optional string array): for Ordering questions (include key verbatim phrases for all steps) or Comparison/Distinction questions (include the contrasting statement verbatim), list additional exact quotes so multiple sections/pages are highlighted.
        """;

    private const string FileEvidenceRule = """
        2. Every single question MUST include an "evidence" object with:
           - "file": the exact file name from the "=== [FILE: name] ..." header the fact appears in (for images: the image file name).
           - "sectionTitle": the nearest heading, class, or function name (for images: a concise diagram/component label, e.g. "L2 Cache Block").
           - "verbatimQuote": for text/code files, 1 to 6 consecutive lines (at most ~40 words) copied CHARACTER-FOR-CHARACTER from that file WITHOUT the "L001:" line-number prefixes, so the app can resolve the exact line range. For images: write a specific directional visual pointer describing WHERE to look and WHAT visual feature proves the answer (e.g. "Top-right L2 Cache box (256 KB) connected by a bidirectional arrow to L3 Shared Cache").
           - "startLine": the first line number (from the "L###:" prefix) of the quote. Omit for images.
           - "region" (for images only, optional): normalized bounding box {"x": 0.0-1.0, "y": 0.0-1.0, "w": 0.0-1.0, "h": 0.0-1.0} from the top-left corner highlighting the relevant area of the image so the app can draw a spotlight box over it.
           - The student will NOT see the source file. Any code needed to answer must be placed in "codeSnippet" so the question is self-contained; never say "see line 12".
        """;

    private const string PdfEvidenceSchema = """
        {
                    "pageNumber": 12,
                    "sectionTitle": "Midterms / 04 Handout 1",
                    "verbatimQuote": "Exact 8-28 word sentence copied directly from [PAGE 12]"
                  }
        """;

    private const string FileEvidenceSchema = """
        {
                    "file": "main.cpp",
                    "startLine": 14,
                    "sectionTitle": "int main()",
                    "verbatimQuote": "Exact line(s) copied from the file without the L014: prefix"
                  }
        """;

    private const string MixedEvidenceRule = """
        2. Every single question MUST include an "evidence" object grounded to EITHER a PDF page OR a source file:
           - If grounded in a PDF page ("=== [PAGE N] | Section: ... ==="): set "pageNumber" (exact integer N), "sectionTitle", and "verbatimQuote" (exact 8-28 word substring copied WORD-FOR-WORD from that page), and set "file": null.
           - If grounded in a code/text file ("=== [FILE: name] ... ==="): set "file" (exact file name), "startLine" (first line number from "L###:"), "sectionTitle" (nearest heading/class/function), and "verbatimQuote" (1-6 consecutive lines copied CHARACTER-FOR-CHARACTER WITHOUT "L001:" prefixes).
           - If grounded in an image: set "file" (exact image file name), "sectionTitle" (concise diagram/component label), "verbatimQuote" (specific directional visual pointer describing WHERE to look and WHAT visual detail proves the answer), and optionally "region": {"x": 0.0-1.0, "y": 0.0-1.0, "w": 0.0-1.0, "h": 0.0-1.0} so the app can draw a spotlight highlight box over that area.
           - The student will NOT see the source file. Any code needed to answer must be placed in "codeSnippet" so the question is self-contained; never say "see line 12".
           - Cover BOTH the PDF pages and the selected source files across the quiz, and synthesize concepts across them where natural.
        """;

    private const string MixedEvidenceSchema = """
        {
                    "pageNumber": 12,
                    "file": "main.cpp (set null if grounded in PDF page; set filename if grounded in a source file/image)",
                    "startLine": 14,
                    "sectionTitle": "Section or function name",
                    "verbatimQuote": "Exact quote from the PDF page or source file lines (without L###: prefix)"
                  }
        """;

    private const string CodeQuestionFormatRules = """


        CODE QUESTION FORMATS (STRICTLY APPLY TO CODE-BASED QUESTIONS while keeping the exact question-type counts above):
        - Every "Identification" question on code MUST be Output / Trace Prediction: put a self-contained 3-12 line excerpt in "codeSnippet" and ask "What is printed?", "What value is returned?", or "What is the value of X after execution?" with "correctAnswer" = the exact output/value (and equivalent formats in "acceptedAliases").
        - Every "FillInTheBlank" question on code MUST be Code Completion: place "_____" in "codeSnippet" (and mention _____ in "prompt") for a single critical operator, condition, keyword, or expression; "correctAnswer" is the exact missing token/expression.
        - Every "Ordering" question on code MUST be Execution / Control-Flow Ordering: "options" contains 3-5 runtime trace steps or statements of an algorithm from the source in exact execution order.
        - Every "Situational" question on code MUST be Bug / Edge-Case Spotting or Tradeoff / Complexity Analysis: provide a snippet with a realistic bug/edge case (off-by-one, null/empty, overflow, wrong operator, memory/resource issue) or compare two approaches grounded in the source.
        - For "MultipleChoice" and "TrueFalse" on code: test behavior, side-effects, invariants, or debugging rather than trivial syntax recall.
        - Every "codeSnippet" MUST be a self-contained excerpt derived from the provided source code, never invented unrelated code.
        """;

    private static string BuildModalitySystemRules(
        ContentModalityProfile modality,
        QuizContentModalityMode mode = QuizContentModalityMode.AutoDetect,
        QuizContentModalityFlags selectedFlags = QuizContentModalityFlags.None,
        IReadOnlyDictionary<QuizQuestionKind, int>? exactKindCounts = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("CONTENT MODALITY & SPECIALIZED FORMAT RULES:");

        if (modality.IsProseOnlyOverride || mode == QuizContentModalityMode.StandardProseOnly)
        {
            sb.AppendLine(
                "- PROSE-ONLY MODE ACTIVE: Keep \"codeSnippet\": null, \"codeLanguage\": null, \"mathLatex\": null, and \"diagram\": null on all questions. Express all questions and options in clear academic prose."
            );
            return sb.ToString().TrimEnd();
        }

        // 1. Code Modality Rules + Anti-Overfitting Guardrail
        if (modality.HasCode)
        {
            if (modality.DetectedCodeInSource)
            {
                sb.AppendLine(
                    $"- CODE SNIPPETS DETECTED ({modality.DetectedLanguage ?? "code"}): When testing programming syntax, CLI commands, queries, or code tracing, populate \"codeSnippet\" with a clean 2-12 line code block and \"codeLanguage\" (e.g. \"{modality.DetectedLanguage ?? "csharp"}\"). Leave \"codeSnippet\": null for non-code conceptual questions."
                );
            }
            else
            {
                sb.AppendLine(
                    "- USER REQUESTED CODE SNIPPETS (WITH ANTI-OVERFITTING GUARDRAIL): Populate \"codeSnippet\" and \"codeLanguage\" ONLY where the source material genuinely involves programming, algorithms, CLI commands, or structured queries. NEVER invent unrelated code blocks for purely conceptual/prose topics—leave \"codeSnippet\": null on prose items."
                );
            }
        }
        else
        {
            sb.AppendLine(
                "- CODE SNIPPETS: Populate \"codeSnippet\" and \"codeLanguage\" ONLY if a question directly tests programming code, pseudocode, assembly, or CLI commands present in the source; otherwise set both to null."
            );
        }

        // 2. Math / LaTeX Modality Rules + Question-Type Interaction & Anti-Overfitting Guardrail
        int mcqPlusSituational =
            (exactKindCounts?.GetValueOrDefault(QuizQuestionKind.MultipleChoice, 1) ?? 1)
            + (exactKindCounts?.GetValueOrDefault(QuizQuestionKind.Situational, 0) ?? 0);

        string mathKindGuidance =
            mcqPlusSituational > 0
                ? "Prefer \"MultipleChoice\" or \"Situational\" (with 4 options) for symbolic equations so students never have to type raw LaTeX into a text box; if an \"Identification\" or \"FillInTheBlank\" item includes \"mathLatex\", make \"correctAnswer\" a plain number, variable, or short canonical term (never raw LaTeX commands)."
                : "Keep the exact requested question types above: you may populate \"mathLatex\" to display equations/formulas on the card, but ensure \"correctAnswer\" for \"Identification\" or \"FillInTheBlank\" is a plain numeric value, variable, or canonical term (never raw LaTeX markup like \\\\frac that a student cannot type).";

        if (modality.HasMath)
        {
            if (modality.DetectedMathInSource)
            {
                sb.AppendLine(
                    $"- MATH / FORMULAS DETECTED: When testing equations, formulas, derivations, proofs, or asymptotic/statistical notation, populate \"mathLatex\" with the clean LaTeX expression (e.g. \"\\\\frac{{a + b}}{{n}}\", \"\\\\int_0^1 x^2 \\\\, dx\", \"O(n \\\\log n)\"). {mathKindGuidance} Leave \"mathLatex\": null for non-math questions."
                );
            }
            else
            {
                sb.AppendLine(
                    $"- USER REQUESTED MATH / LATEX (WITH ANTI-OVERFITTING GUARDRAIL): Populate \"mathLatex\" ONLY where the source material genuinely contains mathematical expressions, quantitative formulas, symbolic logic, or calculations. {mathKindGuidance} NEVER invent artificial equations or force fake math onto purely conceptual/historical/prose topics—if a topic is plain prose, keep \"mathLatex\": null."
                );
            }
        }
        else
        {
            sb.AppendLine(
                $"- MATH / FORMULAS: Populate \"mathLatex\" ONLY if a question directly tests a mathematical equation, formula, derivation, or symbolic expression genuinely present in the source ({mathKindGuidance}); otherwise keep \"mathLatex\": null. Never invent formulas for conceptual prose."
            );
        }

        // 3. Diagram Modality Rules + Anti-Overfitting Guardrail
        if (modality.HasDiagrams)
        {
            if (modality.DetectedDiagramsInSource || modality.HasImageSources)
            {
                sb.AppendLine(
                    "- ARCHITECTURE / PROCESS DIAGRAMS DETECTED: When testing multi-stage pipelines, branching logic, network protocols, or state flows, populate \"diagram\" using EITHER a concise 1-line chain (\"[Client] -> [API Gateway] -> [DB]\"), a Mermaid-subset branching flowchart (\"flowchart LR\\nA[CPU] --> B{L1 Hit?}\\nB -->|Yes| C[Fast]\\nB -->|No| D[(RAM)]\"), or a sequenceDiagram (\"sequenceDiagram\\nClient ->> Server: SYN\\nServer -->> Client: SYN-ACK\"). Use \"[???]\" for cloze-style missing nodes. Leave \"diagram\": null for standard questions."
                );
            }
            else
            {
                sb.AppendLine(
                    "- USER REQUESTED FLOW DIAGRAMS (WITH ANTI-OVERFITTING GUARDRAIL): Populate \"diagram\" (1-line \"[A] -> [B]\", Mermaid \"flowchart LR\", or \"sequenceDiagram\") ONLY where a question genuinely tests a multi-step process, causal chain, state transition, or architecture flow present in the source. Do NOT force artificial diagrams onto isolated single-fact definitions."
                );
            }
        }
        else
        {
            sb.AppendLine(
                "- Set \"diagram\": null unless a question directly illustrates a multi-step architecture or process flow present in the source."
            );
        }

        // 4. Universal Anti-Overfitting Principle
        sb.AppendLine(
            "- ANTI-OVERFITTING PRINCIPLE: Match specialized fields (\"codeSnippet\", \"mathLatex\", \"diagram\") organically to the actual subject matter of each page/file. Never fabricate synthetic code, forced math formulas, or gratuitous diagrams when the underlying evidence is plain conceptual prose."
        );

        return sb.ToString().TrimEnd();
    }

    private static string BuildModalityUserSummary(ContentModalityProfile? modality)
    {
        if (modality is null)
            return string.Empty;

        if (modality.IsProseOnlyOverride)
            return "\nActive Modality Mode: Standard Conceptual Prose Only (keep codeSnippet, mathLatex, and diagram null)";

        var activeModalities = new List<string>();
        if (modality.HasCode)
        {
            activeModalities.Add(
                modality.DetectedCodeInSource
                    ? $"Code Snippets ({modality.DetectedLanguage ?? "code"}, detected)"
                    : "Code Snippets (requested — use only where source supports code)"
            );
        }
        if (modality.HasMath)
        {
            activeModalities.Add(
                modality.DetectedMathInSource
                    ? "Math LaTeX (detected)"
                    : "Math LaTeX (requested — use only where genuine formulas/math exist; never overfit prose)"
            );
        }
        if (modality.HasDiagrams)
        {
            activeModalities.Add(
                modality.DetectedDiagramsInSource || modality.HasImageSources
                    ? "Node-Flow / Sequence Diagrams (detected)"
                    : "Node-Flow / Sequence Diagrams (requested — use only for genuine multi-step flows)"
            );
        }

        return activeModalities.Count > 0
            ? $"\nActive Specialized Modalities: {string.Join(", ", activeModalities)} (include where supported by the source; omit on plain conceptual items)"
            : "\nActive Specialized Modalities: Auto-Detect (populate codeSnippet, mathLatex, or diagram only if genuinely present in the source; otherwise keep null)";
    }

    private static string BuildUserPrompt(
        QuizGenerationRequest req,
        string groundedContent,
        int count,
        int batchNumber = 1,
        int totalBatches = 1,
        ContentModalityProfile? modality = null
    )
    {
        var batchNote =
            totalBatches > 1
                ? $" (Batch {batchNumber} of {totalBatches} — ensure unique questions covering these pages)"
                : string.Empty;
        var exactCounts = req.ResolveExactKindCounts(count);
        var breakdown = string.Join(
            ", ",
            exactCounts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}")
        );

        var modalitySummary = BuildModalityUserSummary(modality);

        var excludeBlock =
            req.ExcludePromptsOrTopics.Count > 0
                ? $"\nDo NOT repeat or duplicate any of these existing questions/topics:\n- {string.Join("\n- ", req.ExcludePromptsOrTopics.Take(25))}\n"
                : string.Empty;

        var customBlock = BuildGuardedCustomInstructionsBlock(req.CustomInstructions);

        return $"""
            Course: {req.CourseTitle}
            Target Term: {req.Term ?? "All Covered Terms"}
            Section Scope: {req.SectionFilter ?? "All Sections in Scope"}
            Required Question Breakdown ({Math.Clamp(
                count,
                1,
                150
            )} total): {breakdown}{modalitySummary}{excludeBlock}{customBlock}
            Generate high-quality, evidence-grounded questions{batchNote} strictly matching the required question breakdown from the following grounded PDF pages:

            {groundedContent}
            """;
    }

    /// <summary>
    /// Wraps user-supplied focus/style directives in a subordinate guardrail block so they
    /// cannot override the required JSON schema, verbatim evidence anchors, or kind counts.
    /// </summary>
    public static string BuildGuardedCustomInstructionsBlock(string? rawCustomInstructions)
    {
        if (string.IsNullOrWhiteSpace(rawCustomInstructions))
            return string.Empty;

        var sanitized = rawCustomInstructions
            .Replace("\"\"\"", "'", StringComparison.Ordinal)
            .Trim();
        if (sanitized.Length > 1200)
            sanitized = sanitized[..1200].TrimEnd();

        return $"""

            USER FOCUS & STYLE DIRECTIVES (apply to topic emphasis, tone, and difficulty ONLY — NEVER override the JSON-only output, required schema, verbatimQuote evidence rules, or exact question-type counts):
            <user_directives>
            {sanitized}
            </user_directives>
            """;
    }
}
