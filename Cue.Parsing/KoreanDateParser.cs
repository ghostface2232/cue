using System.Text.RegularExpressions;
using Cue.Domain;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace Cue.Parsing;

/// <summary>
/// The default <see cref="IDateParser"/>: a Korean quick-add parser built as an ordered rule
/// pipeline (the boost seam), backed by the Microsoft.Recognizers library as a fallback stage.
/// </summary>
/// <remarks>
/// The built-in rules (someday / recurrence / deadline / scheduled-date / time-of-day) do the Korean
/// recognition; each consumes the span it recognizes so the leftover text becomes the title. Custom
/// <see cref="IQuickAddRule"/>s passed to the constructor run <i>before</i> the defaults, so they can
/// add expressions the defaults miss or pre-empt a misrecognition.
/// <para>
/// The Recognizers library is wired in (per spec) with <see cref="Culture.Korean"/> as a final
/// fallback for any text the rules left unscheduled. Note: at the pinned library version the Korean
/// DateTime model recognizes nothing, so today the rules carry all Korean parsing and this stage is a
/// no-op that will light up automatically if the library gains Korean support. The parser never
/// throws — any failure (a bad rule, the library, an odd input) degrades to "title only".
/// </para>
/// </remarks>
public sealed class KoreanDateParser : IDateParser
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly IReadOnlyList<IQuickAddRule> _rules;
    private readonly DateTimeModel _library;
    private readonly KoreanDateParserOptions _options;

    /// <param name="boostRules">
    /// Optional custom rules, run before the built-ins so they take precedence.
    /// </param>
    public KoreanDateParser(IEnumerable<IQuickAddRule>? boostRules = null, KoreanDateParserOptions? options = null)
    {
        var rules = new List<IQuickAddRule>();
        if (boostRules is not null)
            rules.AddRange(boostRules);
        rules.AddRange(DefaultRules());
        _rules = rules;
        _options = options ?? new KoreanDateParserOptions();
        _library = new DateTimeRecognizer(Culture.Korean).GetDateTimeModel();
    }

    /// <summary>The built-in rule set, in pipeline order (most specific / claim-first first).</summary>
    public static IReadOnlyList<IQuickAddRule> DefaultRules() => new IQuickAddRule[]
    {
        new SomedayRule(),
        new RecurrenceQuickAddRule(),
        new RelativeHourRule(),
        new DeadlineRule(),
        new WhenDateRule(),
        new TimeOfDayRule(),
    };

    public ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ParsedQuickAdd.TitleOnly(input ?? string.Empty);

        try
        {
            var context = new ParseContext(now, timeZoneId, _options);
            var result = new QuickAddResult();

            var work = input;
            foreach (var rule in _rules)
                work = Apply(rule, work, context, result);

            if (!result.WhenAssigned)
                work = LibraryFallback(work, context, result);

            var title = CollapseWhitespace(work);
            if (title.Length == 0)
                title = input.Trim();

            return new ParsedQuickAdd(
                title,
                result.WhenAssigned ? result.When : ScheduledWhen.Unscheduled,
                result.Recurrence);
        }
        catch
        {
            // Never throw on bad/ambiguous input: fall back to the whole line as the title.
            return ParsedQuickAdd.TitleOnly(input.Trim());
        }
    }

    private static string Apply(IQuickAddRule rule, string work, ParseContext context, QuickAddResult result)
    {
        try
        {
            var match = rule.Pattern.Match(work);
            var guard = 0;
            while (match.Success && guard++ < 64)
            {
                if (rule.Extract(match, context, result))
                {
                    // Strip the recognized span, leaving a space so neighbouring words don't fuse.
                    work = work.Remove(match.Index, match.Length).Insert(match.Index, " ");
                    match = rule.Pattern.Match(work);
                }
                else
                {
                    match = match.NextMatch();
                }
            }
        }
        catch
        {
            // A faulty rule (or pathological input) must never kill the whole parse.
        }

        return work;
    }

    /// <summary>
    /// Last-resort recognition via the library. Defensive throughout: any shape it returns that we
    /// don't understand is ignored, and it only ever fills an unset When.
    /// </summary>
    private string LibraryFallback(string work, ParseContext context, QuickAddResult result)
    {
        try
        {
            var models = _library.Parse(work, context.LocalNow.DateTime);
            foreach (var r in models)
            {
                if (r.TypeName is not ("datetimeV2.date" or "datetimeV2.datetime"))
                    continue;
                if (r.Resolution is null || !r.Resolution.TryGetValue("values", out var raw))
                    continue;
                if (raw is not IList<Dictionary<string, string>> values)
                    continue;

                foreach (var v in values)
                {
                    if (!v.TryGetValue("value", out var text) || !DateTime.TryParse(text, out var dt))
                        continue;

                    var date = DateOnly.FromDateTime(dt);
                    var when = r.TypeName.EndsWith("datetime", StringComparison.Ordinal)
                        ? ScheduledWhen.On(context.Zoned(date, dt.Hour, dt.Minute))
                        : ScheduledWhen.On(context.Zoned(date));
                    if (result.TrySetWhen(when) && !string.IsNullOrEmpty(r.Text))
                    {
                        var at = work.IndexOf(r.Text, StringComparison.Ordinal);
                        if (at >= 0)
                            work = work.Remove(at, r.Text.Length).Insert(at, " ");
                    }
                    return work;
                }
            }
        }
        catch
        {
            // The library must never be able to crash the parser.
        }

        return work;
    }

    private static string CollapseWhitespace(string text)
        => Whitespace.Replace(text, " ").Trim();
}
