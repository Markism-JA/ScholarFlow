namespace ScholarFlow.Core.Services;

/// <summary>
/// Shared multi-token + subsequence fuzzy search matcher used consistently across
/// Quick Search (Ctrl+K), Deck Organizer, and Course Files live filtering.
/// </summary>
public static class QuickSearchMatcher
{
    public static bool Matches(string? query, params string?[] fields) => Score(query, fields) > 0;

    /// <summary>
    /// Returns a positive relevance score if every whitespace-separated token in <paramref name="query"/>
    /// matches at least one field (exact/prefix &gt; substring &gt; ordered subsequence for 3+ char tokens).
    /// Returns 1 for an empty query, and 0 for no match.
    /// </summary>
    public static int Score(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1;

        var tokens = query
            .Trim()
            .Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
        if (tokens.Length == 0)
            return 1;

        int totalScore = 0;

        foreach (var rawToken in tokens)
        {
            var token = rawToken.TrimStart('#', '@');
            if (token.Length == 0)
                token = rawToken;

            int bestTokenScore = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (string.IsNullOrWhiteSpace(field))
                    continue;

                int s = ScoreTokenAgainstField(token, field);
                if (s > bestTokenScore)
                    bestTokenScore = s;
            }

            if (bestTokenScore == 0)
                return 0;

            totalScore += bestTokenScore;
        }

        return totalScore;
    }

    private static int ScoreTokenAgainstField(string token, string field)
    {
        if (field.Equals(token, StringComparison.OrdinalIgnoreCase))
            return 100;

        if (field.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return 80;

        int idx = field.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            // Word-boundary substring match scores higher than mid-word match
            bool atBoundary = idx == 0 || !char.IsLetterOrDigit(field[idx - 1]);
            return atBoundary ? 65 : 50;
        }

        // Word-initial acronym match (e.g., "os" -> "Operating Systems", "vm" -> "Virtual Memory")
        if (token.Length >= 2 && IsWordInitialsMatch(token, field))
            return 40;

        // Ordered character subsequence match (fuzzy) for tokens of length >= 3
        if (token.Length >= 3 && IsSubsequence(token, field))
            return 25;

        return 0;
    }

    private static bool IsWordInitialsMatch(string needle, string haystack)
    {
        int ni = 0;
        bool atWordStart = true;
        for (int hi = 0; hi < haystack.Length && ni < needle.Length; hi++)
        {
            char c = haystack[hi];
            if (!char.IsLetterOrDigit(c))
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart)
            {
                if (char.ToUpperInvariant(c) == char.ToUpperInvariant(needle[ni]))
                    ni++;
                atWordStart = false;
            }
        }
        return ni == needle.Length;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int ni = 0;
        for (int hi = 0; hi < haystack.Length && ni < needle.Length; hi++)
        {
            if (char.ToUpperInvariant(haystack[hi]) == char.ToUpperInvariant(needle[ni]))
                ni++;
        }
        return ni == needle.Length;
    }
}
