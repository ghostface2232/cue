using Cue.Parsing;

namespace Cue.Tests;

/// <summary>
/// The preference-backed parser caches one parser per config key (Services/PreferenceDateParser). If two
/// distinct configurations could produce the same key the wrong cached parser would be served, so these
/// tests pin that distinct inputs yield distinct keys and identical inputs yield identical keys.
/// </summary>
public sealed class ParserConfigKeyTests
{
    [Fact]
    public void Distinct_meaning_sets_with_delimiter_chars_in_names_do_not_collide()
    {
        // The naive "name=day;" join made these collide (both → "a=1;b=2;"); length-prefixing the name
        // keeps them distinct, so changing between the two configs rebuilds the parser instead of reusing it.
        var twoNames = Build(customDateMeanings: new[] { ("a", 1), ("b", 2) });
        var oneTrickyName = Build(customDateMeanings: new[] { ("a=1;b", 2) });
        Assert.NotEqual(twoNames, oneTrickyName);
    }

    [Fact]
    public void Same_inputs_produce_the_same_key()
    {
        var a = Build(autoAfternoon: true, customDateMeanings: new[] { ("월급날", 25) });
        var b = Build(autoAfternoon: true, customDateMeanings: new[] { ("월급날", 25) });
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_afternoon_flag_changes_the_key()
    {
        Assert.NotEqual(
            Build(autoAfternoon: true),
            Build(autoAfternoon: false));
    }

    [Fact]
    public void The_week_number_flag_changes_the_key()
    {
        Assert.NotEqual(
            Build(showWeekNumber: true),
            Build(showWeekNumber: false));
    }

    [Fact]
    public void The_week_roll_forward_flag_changes_the_key()
    {
        Assert.NotEqual(
            Build(weekNumberRollsForward: true),
            Build(weekNumberRollsForward: false));
    }

    [Fact]
    public void Reordering_or_changing_a_day_changes_the_key()
    {
        var ordered = Build(customDateMeanings: new[] { ("월급날", 25), ("정산일", 10) });
        var reordered = Build(customDateMeanings: new[] { ("정산일", 10), ("월급날", 25) });
        var changedDay = Build(customDateMeanings: new[] { ("월급날", 26), ("정산일", 10) });
        Assert.NotEqual(ordered, reordered); // order is significant (caller passes a stable order)
        Assert.NotEqual(ordered, changedDay);
    }

    private static string Build(
        bool autoAfternoon = false,
        bool showWeekNumber = false,
        bool weekNumberRollsForward = false,
        IEnumerable<(string Name, int DayOfMonth)>? customDateMeanings = null)
        => ParserConfigKey.Build(
            autoAfternoon,
            showWeekNumber,
            weekNumberRollsForward,
            customDateMeanings ?? Array.Empty<(string, int)>());
}
