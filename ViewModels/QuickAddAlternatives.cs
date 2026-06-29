using System.Text.RegularExpressions;
using Cue.Parsing;

namespace Cue.ViewModels;

/// <summary>One quick-correction offered in the token popover: the menu <paramref name="Label"/> the user
/// reads and the <paramref name="Replacement"/> text that is written over the token's span (which the
/// parser then re-recognizes). Every replacement is verified to re-parse — see QuickAddAlternativesTests
/// and QuickAddPresetTests.</summary>
public sealed record QuickAddAlternative(string Label, string Replacement);

/// <summary>
/// The pure logic that generates a token's quick-correction alternatives for the click-to-correct popover
/// (plan §5.2). It lives here, not in the control, so it can be unit-tested against the real parser — the
/// alternatives use a replace-and-reparse model, so a generated phrase that the parser reads differently
/// (a leaked prefix, a day-crossed time) silently corrupts the token, and only a test that resolves the
/// replacement can catch that. The control keeps just the UI concerns (header text, glyphs, the flyout).
/// </summary>
public static class QuickAddAlternatives
{
    private const string WeekOrder = "월화수목금토일";
    private static readonly Regex WeekdayChar = new(@"[월화수목금토일](?=요일|욜)", RegexOptions.Compiled);

    /// <summary>The quick alternatives for a token, as ordered (label, replacement) pairs. A weekday date
    /// token gets adjacent-weekday + week shifts; any other date token gets the generic presets. A time
    /// token gets ±1 hour (within the same day) and an AM/PM flip computed from the resolved clock.
    /// Recurrence keeps the weekday across a frequency swap. Anything else gets nothing.</summary>
    public static IReadOnlyList<QuickAddAlternative> For(QuickAddToken token, QuickAddPreview preview) => token.Kind switch
    {
        QuickAddTokenKind.Date when WeekdayChar.Match(token.Text) is { Success: true } m => WeekdayDate(token.Text, m.Value[0]),
        QuickAddTokenKind.Date => new[]
        {
            new QuickAddAlternative("오늘", "오늘"),
            new QuickAddAlternative("내일", "내일"),
            new QuickAddAlternative("모레", "모레"),
            new QuickAddAlternative("이번 주말", "이번 주말"),
            new QuickAddAlternative("다음 주", "다음 주"),
        },
        QuickAddTokenKind.Time => Time(preview),
        QuickAddTokenKind.Recurrence => Recurrence(token.Text),
        _ => Array.Empty<QuickAddAlternative>(),
    };

    /// <summary>Weekday-date alternatives, aware of which week the token already denotes (이번/다음/다다음
    /// 주). Offers the adjacent weekdays (전/후) <i>in the same week</i>, then the same weekday in the two
    /// <i>other</i> weeks (so the current week isn't re-suggested). "이번 주" replaces to a bare "{요일}" —
    /// the parser doesn't consume a literal "이번 주" prefix, and bare already means the upcoming one.</summary>
    private static IReadOnlyList<QuickAddAlternative> WeekdayDate(string tokenText, char wd)
    {
        var week = WeekOffset(tokenText);
        var d = WeekOrder.IndexOf(wd);

        var list = new List<QuickAddAlternative>();
        AddDayNeighbor(list, "하루 전", week, d - 1); // the calendar day before
        AddDayNeighbor(list, "하루 뒤", week, d + 1); // the calendar day after
        for (var w = 0; w <= 2; w++)
            if (w != week)
                list.Add(WeekShiftOption(w, wd)); // same weekday, the other two weeks
        return list;
    }

    /// <summary>Adds the calendar-day neighbor (±1 day) as a week-aware weekday option, prefixing the label
    /// with 하루 전/하루 뒤 so the direction reads unambiguously next to "다음 주 목/토요일". Crossing the
    /// Mon/Sun boundary shifts the week; a neighbor that would land in the past (before 이번 주) or beyond
    /// 다다음 주 is skipped rather than mislabeled — a weekday word only ever points forward.</summary>
    private static void AddDayNeighbor(List<QuickAddAlternative> list, string prefix, int week, int dayIndex)
    {
        var w = week;
        if (dayIndex < 0) { dayIndex += 7; w--; }      // before Monday → previous week's Sunday
        else if (dayIndex > 6) { dayIndex -= 7; w++; } // after Sunday → next week's Monday
        if (w is < 0 or > 2)
            return;
        var text = WeekdayPhrase(w, WeekOrder[dayIndex]);
        list.Add(new QuickAddAlternative($"{prefix} · {text}", text));
    }

    /// <summary>Which week the token's weekday phrase denotes: 2 = 다다음 주, 1 = 다음/담(주), 0 = 이번 주 (a
    /// bare weekday). "다다음" must be tested before "다음" since it contains it.</summary>
    private static int WeekOffset(string tokenText)
        => tokenText.Contains("다다음", StringComparison.Ordinal) ? 2
            : tokenText.Contains("다음", StringComparison.Ordinal) || tokenText.Contains("담", StringComparison.Ordinal) ? 1
            : 0;

    private static string WeekdayPhrase(int week, char wd) => week switch
    {
        1 => $"다음 주 {wd}요일",
        2 => $"다다음 주 {wd}요일",
        _ => $"{wd}요일", // this week = bare (the upcoming one)
    };

    /// <summary>A same-weekday option for week <paramref name="week"/>. The label always names the week; the
    /// 이번 주 replacement is the bare weekday (the parser leaks a literal "이번 주" into the title).</summary>
    private static QuickAddAlternative WeekShiftOption(int week, char wd)
    {
        var text = WeekdayPhrase(week, wd);
        var label = week == 0 ? $"이번 주 {wd}요일" : text;
        return new QuickAddAlternative(label, text);
    }

    /// <summary>Time alternatives, computed from the resolved clock: ±1 hour and an AM/PM flip, formatted as
    /// explicit 오전/오후 times so they re-parse unambiguously. The ±1 hour options are emitted only when they
    /// stay on the same calendar day: the replacement swaps the time word alone, never the date token, so an
    /// option crossing midnight (00시 one earlier, 23시 one later) would land a day off while the date phrase
    /// stayed put — those are omitted rather than silently mis-dated. The ±12h flip never crosses midnight.
    /// Empty when the line resolved no time (then the popover shows the header + revert only).</summary>
    private static IReadOnlyList<QuickAddAlternative> Time(QuickAddPreview preview)
    {
        if (preview is not { Hour: int h, Minute: int min })
            return Array.Empty<QuickAddAlternative>();

        var list = new List<QuickAddAlternative>();
        if (h > 0)
            list.Add(Clock("1시간 일찍", h - 1, min));
        if (h < 23)
            list.Add(Clock("1시간 늦게", h + 1, min));
        var flipped = ClockText((h + 12) % 24, min);
        list.Add(new QuickAddAlternative((h >= 12 ? "오전으로" : "오후로") + $" · {flipped}", flipped));
        return list;
    }

    private static QuickAddAlternative Clock(string prefix, int hour24, int minute)
    {
        var text = ClockText(hour24, minute);
        return new QuickAddAlternative($"{prefix} · {text}", text);
    }

    /// <summary>Formats a 24-hour clock as an explicit Korean time ("오후 2시", "오전 3시 30분") — the
    /// unambiguous form, so the replace-and-reparse round-trip can't be re-read as a different hour.</summary>
    private static string ClockText(int hour24, int minute)
    {
        var (meridiem, h12) = hour24 == 0 ? ("오전", 12)
            : hour24 < 12 ? ("오전", hour24)
            : hour24 == 12 ? ("오후", 12)
            : ("오후", hour24 - 12);
        return minute == 0 ? $"{meridiem} {h12}시" : $"{meridiem} {h12}시 {minute}분";
    }

    /// <summary>Recurrence alternatives. 매일/평일 are universal frequency swaps; when the phrase names a
    /// weekday we also offer 매주/격주 of that same weekday (a weekly↔biweekly toggle that keeps the day).</summary>
    private static IReadOnlyList<QuickAddAlternative> Recurrence(string tokenText)
    {
        var list = new List<QuickAddAlternative>
        {
            new("매일", "매일"),
            new("평일", "평일"),
        };
        var m = WeekdayChar.Match(tokenText);
        if (m.Success)
        {
            var wd = $"{m.Value}요일"; // normalize 금욜 → 금요일
            list.Add(new QuickAddAlternative($"매주 {wd}", $"매주 {wd}"));
            list.Add(new QuickAddAlternative($"격주 {wd}", $"격주 {wd}"));
        }
        return list;
    }
}
