using Cue.Domain;

namespace Cue.Tests;

public class RecurrenceRuleTests
{
    private static ZonedDateTime Anchor()
        => ZonedDateTime.FromLocal(new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");

    [Fact]
    public void Construct_WithRuleAndAnchor_Succeeds()
    {
        var anchor = Anchor();
        var rule = new RecurrenceRule("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", anchor);

        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", rule.Rule);
        Assert.Equal(anchor, rule.Anchor);
    }

    [Fact]
    public void HasNoParameterlessConstructor()
    {
        // Forces every rule to be born with an anchor; a default ZonedDateTime can't sneak in.
        Assert.Null(typeof(RecurrenceRule).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void Construct_WithDefaultAnchor_Throws()
    {
        // default(ZonedDateTime) has a null TimeZoneId that would later throw in ToLocal.
        Assert.Throws<ArgumentException>(() => new RecurrenceRule("FREQ=DAILY", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Construct_WithEmptyRule_Throws(string rule)
    {
        Assert.Throws<ArgumentException>(() => new RecurrenceRule(rule, Anchor()));
    }
}
