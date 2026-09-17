using System.Text;
using FluentAssertions;
using NovaDB.Storage.Values;

namespace UnitTests;

public sealed class SortedSetSkipListTests
{
    [Fact]
    public void Add_OrdersMembersByScoreThenLexicographic()
    {
        var set = new SortedSetValue();
        set.Add(2, Encoding.UTF8.GetBytes("b"));
        set.Add(1, Encoding.UTF8.GetBytes("a"));
        set.Add(1, Encoding.UTF8.GetBytes("c"));

        var members = set.RangeByRank(0, -1).Select(Encoding.UTF8.GetString).ToArray();

        members.Should().Equal("a", "c", "b");
    }

    [Fact]
    public void Add_UpdatesExistingMemberScoreAndMaintainsOrder()
    {
        var set = new SortedSetValue();
        set.Add(1, Encoding.UTF8.GetBytes("member"));
        set.Add(5, Encoding.UTF8.GetBytes("member"));

        set.GetScore(Encoding.UTF8.GetBytes("member")).Should().Be(5);
        var members = set.RangeByRank(0, -1);
        members.Should().ContainSingle();
        members[0].Should().Equal("member"u8.ToArray());
    }

    [Fact]
    public void Remove_DropsMemberAndAdjustsCount()
    {
        var set = new SortedSetValue();
        set.Add(1, Encoding.UTF8.GetBytes("x"));
        set.Add(2, Encoding.UTF8.GetBytes("y"));

        set.Remove(Encoding.UTF8.GetBytes("x")).Should().BeTrue();
        set.Count.Should().Be(1);
        set.GetScore(Encoding.UTF8.GetBytes("x")).Should().BeNull();
    }

    [Fact]
    public void RangeByRankWithScores_ReturnsOrderedPairs()
    {
        var set = new SortedSetValue();
        set.Add(10, Encoding.UTF8.GetBytes("ten"));
        set.Add(1, Encoding.UTF8.GetBytes("one"));

        var pairs = set.RangeByRankWithScores(0, -1);

        pairs.Should().HaveCount(2);
        Encoding.UTF8.GetString(pairs[0].Member).Should().Be("one");
        pairs[0].Score.Should().Be(1);
        Encoding.UTF8.GetString(pairs[1].Member).Should().Be("ten");
        pairs[1].Score.Should().Be(10);
    }
}
