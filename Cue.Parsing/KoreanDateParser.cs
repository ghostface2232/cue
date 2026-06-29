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
        new MealAfterRule(),
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
            var tokens = new List<QuickAddToken>();

            var work = input;
            foreach (var rule in _rules)
                work = Apply(rule, work, input, context, result, tokens);

            if (!result.WhenAssigned)
                work = LibraryFallback(work, input, context, result, tokens);

            if (!result.WhenAssigned && result.Recurrence is { } recurrence)
                result.TrySetWhen(ScheduledWhen.On(recurrence.Anchor), hasTime: result.RecurrenceAnchorHasTime);

            var title = CollapseWhitespace(work);
            if (title.Length == 0)
                title = input.Trim();

            tokens.Sort((a, b) => a.Start.CompareTo(b.Start));

            return new ParsedQuickAdd(
                title,
                result.WhenAssigned ? result.When : ScheduledWhen.Unscheduled,
                result.Recurrence)
            {
                WhenAssigned = result.WhenAssigned,
                WhenHasTime = result.WhenHasTime,
                Tokens = tokens,
            };
        }
        catch
        {
            // Never throw on bad/ambiguous input: fall back to the whole line as the title.
            return ParsedQuickAdd.TitleOnly(input.Trim());
        }
    }

    private static string Apply(IQuickAddRule rule, string work, string input, ParseContext context, QuickAddResult result, List<QuickAddToken> tokens)
    {
        try
        {
            var match = rule.Pattern.Match(work);
            var guard = 0;
            while (match.Success && guard++ < 64)
            {
                if (match.Length > 0 && rule.Extract(match, context, result))
                {
                    AddTokens(tokens, match, rule.TokenKind, input);
                    // Mask the recognized span with same-length spaces. Length-preserving so every
                    // match index stays aligned to the ORIGINAL input (so token ranges are exact), and
                    // the spaces still keep neighbouring words from fusing in the title.
                    work = Mask(work, match.Index, match.Length);
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

    /// <summary>Replaces <paramref name="length"/> chars at <paramref name="index"/> with spaces, keeping
    /// the string length (and therefore all original offsets) intact.</summary>
    private static string Mask(string work, int index, int length)
        => string.Concat(work.AsSpan(0, index), new string(' ', length), work.AsSpan(index + length));

    /// <summary>
    /// Emits tokens for a claimed match. A match that carries the shared <c>date</c>/<c>time</c>/
    /// <c>recur</c>/<c>custom</c> capture groups is split into one token per present group (so a combined
    /// "금요일 3시" yields a separate date token and time token, each independently editable). A match with
    /// none of those groups (e.g. "언젠가", "3시간 후", "3일 안에") becomes a single token of the rule's
    /// <see cref="IQuickAddRule.TokenKind"/>.
    /// </summary>
    private static void AddTokens(List<QuickAddToken> tokens, Match match, QuickAddTokenKind defaultKind, string input)
    {
        var any = false;
        Emit("recur", QuickAddTokenKind.Recurrence);
        Emit("date", QuickAddTokenKind.Date);
        Emit("custom", QuickAddTokenKind.Date);
        Emit("time", QuickAddTokenKind.Time);
        if (!any)
            tokens.Add(new QuickAddToken(defaultKind, match.Index, match.Length, input.Substring(match.Index, match.Length)));

        void Emit(string group, QuickAddTokenKind kind)
        {
            var g = match.Groups[group];
            if (g.Success && g.Length > 0)
            {
                tokens.Add(new QuickAddToken(kind, g.Index, g.Length, input.Substring(g.Index, g.Length)));
                any = true;
            }
        }
    }

    /// <summary>
    /// Last-resort recognition via the library. Defensive throughout: any shape it returns that we
    /// don't understand is ignored, and it only ever fills an unset When.
    /// </summary>
    private string LibraryFallback(string work, string input, ParseContext context, QuickAddResult result, List<QuickAddToken> tokens)
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
                    var hasTime = r.TypeName.EndsWith("datetime", StringComparison.Ordinal);
                    var when = hasTime
                        ? ScheduledWhen.On(context.Zoned(date, dt.Hour, dt.Minute))
                        : ScheduledWhen.On(context.Zoned(date));
                    if (result.TrySetWhen(when, hasTime) && !string.IsNullOrEmpty(r.Text))
                    {
                        var at = work.IndexOf(r.Text, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            tokens.Add(new QuickAddToken(
                                hasTime ? QuickAddTokenKind.Time : QuickAddTokenKind.Date,
                                at, r.Text.Length, input.Substring(at, r.Text.Length)));
                            work = Mask(work, at, r.Text.Length);
                        }
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
