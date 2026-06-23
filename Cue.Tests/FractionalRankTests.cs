using Cue.Storage.Ranking;

namespace Cue.Tests;

/// <summary>
/// Exercises the LexoRank-style rank generator: that keys order ordinally, that a key always fits
/// strictly between two neighbors, that repeated same-gap inserts stay correctly ordered, and that
/// the even-distribution helper used by the rebalance path produces an ascending run.
/// </summary>
public sealed class FractionalRankTests
{
    private static int Cmp(string a, string b) => string.CompareOrdinal(a, b);

    [Fact]
    public void Between_Nulls_ProducesAStableFirstKey()
    {
        var key = FractionalRank.Between(null, null);
        Assert.False(string.IsNullOrEmpty(key));
        // It must leave room on both sides.
        Assert.True(Cmp(FractionalRank.Between(null, key), key) < 0);
        Assert.True(Cmp(key, FractionalRank.Between(key, null)) < 0);
    }

    [Fact]
    public void Between_AppendAndPrepend_AreOrdered()
    {
        var first = FractionalRank.Between(null, null);
        var afterFirst = FractionalRank.Between(first, null);
        var beforeFirst = FractionalRank.Between(null, first);

        Assert.True(Cmp(beforeFirst, first) < 0);
        Assert.True(Cmp(first, afterFirst) < 0);
    }

    [Fact]
    public void Between_TwoKeys_FallsStrictlyBetween()
    {
        var a = FractionalRank.Between(null, null);
        var b = FractionalRank.Between(a, null);
        var mid = FractionalRank.Between(a, b);

        Assert.True(Cmp(a, mid) < 0);
        Assert.True(Cmp(mid, b) < 0);
    }

    [Fact]
    public void Between_RepeatedInsertIntoSameGap_StaysOrdered()
    {
        var low = FractionalRank.Between(null, null);
        var high = FractionalRank.Between(low, null);

        // Always insert just above `low` — the gap that grows the fraction the fastest.
        var previous = high;
        for (var i = 0; i < 200; i++)
        {
            var inserted = FractionalRank.Between(low, previous);
            Assert.True(Cmp(low, inserted) < 0);
            Assert.True(Cmp(inserted, previous) < 0);
            previous = inserted;
        }
    }

    [Fact]
    public void Between_RandomizedInserts_PreserveTotalOrder()
    {
        var rng = new Random(20260623);
        var keys = new List<string> { FractionalRank.Between(null, null) };

        for (var i = 0; i < 500; i++)
        {
            var slot = rng.Next(keys.Count + 1);
            var before = slot == 0 ? null : keys[slot - 1];
            var after = slot == keys.Count ? null : keys[slot];
            var key = FractionalRank.Between(before, after);
            keys.Insert(slot, key);
        }

        for (var i = 1; i < keys.Count; i++)
            Assert.True(Cmp(keys[i - 1], keys[i]) < 0, $"order broke at {i}: '{keys[i - 1]}' !< '{keys[i]}'");
    }

    [Fact]
    public void Between_OutOfOrderBounds_Throws()
    {
        var a = FractionalRank.Between(null, null);
        var b = FractionalRank.Between(a, null);
        Assert.Throws<ArgumentException>(() => FractionalRank.Between(b, a));
        Assert.Throws<ArgumentException>(() => FractionalRank.Between(a, a));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(50)]
    public void EvenlyBetween_OpenRange_IsAscendingAndCounted(int count)
    {
        var keys = FractionalRank.EvenlyBetween(null, null, count);
        Assert.Equal(count, keys.Count);
        for (var i = 1; i < keys.Count; i++)
            Assert.True(Cmp(keys[i - 1], keys[i]) < 0);
    }

    [Fact]
    public void EvenlyBetween_BoundedRange_StaysInsideBounds()
    {
        var a = FractionalRank.Between(null, null);
        var b = FractionalRank.Between(a, null);
        var keys = FractionalRank.EvenlyBetween(a, b, 8);

        Assert.Equal(8, keys.Count);
        Assert.True(Cmp(a, keys[0]) < 0);
        Assert.True(Cmp(keys[^1], b) < 0);
        for (var i = 1; i < keys.Count; i++)
            Assert.True(Cmp(keys[i - 1], keys[i]) < 0);
    }
}
