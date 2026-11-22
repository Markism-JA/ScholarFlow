using System.Globalization;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Tasks;

public sealed record QuickAddChip(string Kind, string Text);

public sealed record ParsedQuickAddResult(TaskItem Task, IReadOnlyList<QuickAddChip> Chips);

/// <summary>
/// Parses Todoist-style natural-language Quick Add strings such as:
/// "Finish OS lab 2 tomorrow p1 d4 #os @lab midterms /Labs"
/// </summary>
public static partial class QuickAddParser
{
    [GeneratedRegex(@"(?<!\S)p([1-4])(?!\S)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PriorityRegex();

    [GeneratedRegex(@"(?<!\S)d([1-5])(?!\S)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DifficultyRegex();

    [GeneratedRegex(@"(?<!\S)#([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex CourseTagRegex();

    [GeneratedRegex(@"(?<!\S)@([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex LabelTagRegex();

    [GeneratedRegex(@"(?<!\S)/(?!/)([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex SectionTagRegex();

    [GeneratedRegex(
        @"(?<!\S)(prelims?|midterms?|pre-?finals?|finals?)(?!\S)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex TermTokenRegex();

    [GeneratedRegex(@"(?<!\S)(\d{4}-\d{2}-\d{2})(?!\S)", RegexOptions.Compiled)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(
        @"(?<!\S)(today|tod|tomorrow|tom|tmrw|next\s+week|\+\d+d|next\s+(?:mon|monday|tue|tuesday|wed|wednesday|thu|thursday|fri|friday|sat|saturday|sun|sunday))(?!\S)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex RelativeDateRegex();

    [GeneratedRegex(
        @"(?<!\S)(?:at\s+)?((?:[01]?\d|2[0-3]):[0-5]\d(?:\s?[ap]m)?|(?:1[0-2]|[1-9])\s?[ap]m)(?!\S)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex TimeTokenRegex();

    public static ParsedQuickAddResult Parse(
        string input,
        IReadOnlyList<BasinCourseEntry>? courses = null,
        DateOnly? todayOverride = null
    )
    {
        var today = todayOverride ?? DateOnly.FromDateTime(DateTime.Today);
        var working = input ?? string.Empty;
        var chips = new List<QuickAddChip>();

        var priority = TaskPriority.P4;
        working = PriorityRegex()
            .Replace(working, m =>
            {
                priority = (TaskPriority)int.Parse(m.Groups[1].Value);
                chips.Add(new QuickAddChip("Priority", $"P{(int)priority}"));
                return string.Empty;
            });

        int difficulty = 1;
        working = DifficultyRegex()
            .Replace(working, m =>
            {
                difficulty = int.Parse(m.Groups[1].Value);
                chips.Add(new QuickAddChip("Difficulty", $"D{difficulty}"));
                return string.Empty;
            });

        string? courseFolder = null;
        string? courseAlias = null;
        working = CourseTagRegex()
            .Replace(working, m =>
            {
                var tag = m.Groups[1].Value;
                var matched = MatchCourse(tag, courses);
                if (matched is not null)
                {
                    courseFolder = matched.FolderName;
                    courseAlias = matched.Alias;
                    chips.Add(new QuickAddChip("Course", $"#{matched.Alias}"));
                }
                else
                {
                    courseAlias = tag;
                    chips.Add(new QuickAddChip("Course", $"#{tag}"));
                }
                return string.Empty;
            });

        string? term = null;
        var labels = new List<string>();
        working = LabelTagRegex()
            .Replace(working, m =>
            {
                var lbl = m.Groups[1].Value;
                var maybeTerm = TaskItem.NormalizeTerm(lbl);
                if (maybeTerm is not null)
                {
                    term = maybeTerm;
                    chips.Add(new QuickAddChip("Term", maybeTerm));
                }
                if (!labels.Contains(lbl, StringComparer.OrdinalIgnoreCase))
                {
                    labels.Add(lbl);
                    chips.Add(new QuickAddChip("Label", $"@{lbl}"));
                }
                return string.Empty;
            });

        string? section = null;
        working = SectionTagRegex()
            .Replace(working, m =>
            {
                section = m.Groups[1].Value.Replace('_', ' ');
                chips.Add(new QuickAddChip("Section", $"/{section}"));
                return string.Empty;
            });

        working = TermTokenRegex()
            .Replace(working, m =>
            {
                if (term is not null)
                    return m.Value;

                var normalized = TaskItem.NormalizeTerm(m.Groups[1].Value);
                if (normalized is not null)
                {
                    term = normalized;
                    var termTag = normalized.ToLowerInvariant();
                    if (!labels.Contains(termTag, StringComparer.OrdinalIgnoreCase))
                        labels.Add(termTag);
                    chips.Add(new QuickAddChip("Term", normalized));
                    return string.Empty;
                }
                return m.Value;
            });

        DateOnly? dueDate = null;
        working = IsoDateRegex()
            .Replace(working, m =>
            {
                if (
                    dueDate is null
                    && DateOnly.TryParseExact(
                        m.Groups[1].Value,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var d
                    )
                )
                {
                    dueDate = d;
                    chips.Add(new QuickAddChip("Due", d.ToString("MMM d")));
                    return string.Empty;
                }
                return m.Value;
            });

        if (dueDate is null)
        {
            working = RelativeDateRegex()
                .Replace(working, m =>
                {
                    if (dueDate is not null)
                        return m.Value;

                    dueDate = ResolveRelativeDate(m.Groups[1].Value, today);
                    if (dueDate.HasValue)
                        chips.Add(new QuickAddChip("Due", dueDate.Value.ToString("MMM d")));
                    return string.Empty;
                });
        }

        TimeOnly? dueTime = null;
        working = TimeTokenRegex()
            .Replace(working, m =>
            {
                if (dueTime is not null)
                    return m.Value;

                if (TryParseTimeToken(m.Groups[1].Value, out var parsedTime))
                {
                    dueTime = parsedTime;
                    chips.Add(new QuickAddChip("Time", parsedTime.ToString("h:mm tt")));
                    return string.Empty;
                }
                return m.Value;
            });

        if (dueTime.HasValue && !dueDate.HasValue)
        {
            dueDate = today;
            chips.Add(new QuickAddChip("Due", dueDate.Value.ToString("MMM d")));
        }
        else if (dueDate.HasValue && !dueTime.HasValue)
        {
            dueTime = new TimeOnly(0, 0);
        }

        var cleanTitle = string.Join(
                ' ',
                working.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            )
            .Trim();

        var task = new TaskItem
        {
            Title = cleanTitle,
            Priority = priority,
            Difficulty = difficulty,
            DueDate = dueDate,
            DueTime = dueTime,
            CourseFolder = courseFolder,
            CourseAlias = courseAlias,
            Term = term,
            Section = section,
            Labels = labels,
        };

        return new ParsedQuickAddResult(task, chips);
    }

    private static bool TryParseTimeToken(string raw, out TimeOnly time)
    {
        var normalized = raw.Trim().ToUpperInvariant();
        if (
            normalized.EndsWith("AM", StringComparison.Ordinal)
            || normalized.EndsWith("PM", StringComparison.Ordinal)
        )
        {
            // Normalize "2pm" -> "2 PM", "11:59pm" -> "11:59 PM"
            var suffix = normalized[^2..];
            var body = normalized[..^2].Trim();
            normalized = $"{body} {suffix}";
        }

        string[] formats = ["h:mm tt", "hh:mm tt", "h tt", "hh tt", "HH:mm", "H:mm"];
        return TimeOnly.TryParseExact(
            normalized,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time
        );
    }

    private static BasinCourseEntry? MatchCourse(
        string tag,
        IReadOnlyList<BasinCourseEntry>? courses
    )
    {
        if (courses is null || courses.Count == 0)
            return null;

        return courses.FirstOrDefault(c => c.Alias.Equals(tag, StringComparison.OrdinalIgnoreCase))
            ?? courses.FirstOrDefault(c =>
                c.FolderName.Contains(tag, StringComparison.OrdinalIgnoreCase)
            )
            ?? courses.FirstOrDefault(c =>
                c.Title.Contains(tag, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static DateOnly? ResolveRelativeDate(string token, DateOnly today)
    {
        var lower = string.Join(
            ' ',
            token.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );
        if (lower is "today" or "tod")
            return today;
        if (lower is "tomorrow" or "tom" or "tmrw")
            return today.AddDays(1);
        if (lower is "next week")
            return today.AddDays(7);

        if (
            lower.StartsWith('+')
            && lower.EndsWith('d')
            && int.TryParse(lower[1..^1], out int plusDays)
        )
            return today.AddDays(plusDays);

        if (lower.StartsWith("next "))
        {
            var dayPart = lower[5..];
            var targetDow = dayPart switch
            {
                "sun" or "sunday" => DayOfWeek.Sunday,
                "mon" or "monday" => DayOfWeek.Monday,
                "tue" or "tuesday" => DayOfWeek.Tuesday,
                "wed" or "wednesday" => DayOfWeek.Wednesday,
                "thu" or "thursday" => DayOfWeek.Thursday,
                "fri" or "friday" => DayOfWeek.Friday,
                "sat" or "saturday" => DayOfWeek.Saturday,
                _ => (DayOfWeek?)null,
            };

            if (targetDow.HasValue)
            {
                int diff = ((int)targetDow.Value - (int)today.DayOfWeek + 7) % 7;
                if (diff == 0)
                    diff = 7;
                return today.AddDays(diff);
            }
        }

        return null;
    }
}
