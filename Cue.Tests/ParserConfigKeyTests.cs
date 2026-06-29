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
        var twoNames = ParserConfigKey.Build(false, new[] { ("a", 1), ("b", 2) });
        var oneTrickyName = ParserConfigKey.Build(false, new[] { ("a=1;b", 2) });
        Assert.NotEqual(twoNames, oneTrickyName);
    }

    [Fact]
    public void Same_inputs_produce_the_same_key()
    {
        var a = ParserConfigKey.Build(true, new[] { ("월급날", 25) });
        var b = ParserConfigKey.Build(true, new[] { ("월급날", 25) });
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_afternoon_flag_changes_the_key()
    {
        Assert.NotEqual(
            ParserConfigKey.Build(true, Array.Empty<(string, int)>()),
            ParserConfigKey.Build(false, Array.Empty<(string, int)>()));
    }

    [Fact]
    public void Reordering_or_changing_a_day_changes_the_key()
    {
        var ordered = ParserConfigKey.Build(false, new[] { ("월급날", 25), ("정산일", 10) });
        var reordered = ParserConfigKey.Build(false, new[] { ("정산일", 10), ("월급날", 25) });
        var changedDay = ParserConfigKey.Build(false, new[] { ("월급날", 26), ("정산일", 10) });
        Assert.NotEqual(ordered, reordered); // order is significant (caller passes a stable order)
        Assert.NotEqual(ordered, changedDay);
    }
}
