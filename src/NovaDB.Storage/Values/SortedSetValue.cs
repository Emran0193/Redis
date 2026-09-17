namespace NovaDB.Storage.Values;

/// <summary>
/// Redis sorted set value backed by a skip list ordered by score then member bytes.
/// </summary>
public sealed class SortedSetValue
{
    private const int MaxLevel = 32;
    private const double Probability = 0.25;
    private readonly SkipListNode _header;
    private readonly Dictionary<MemberKey, SkipListNode> _memberIndex = new();
    private readonly Random _random = Random.Shared;
    private SkipListNode? _tail;
    private int _level;
    private int _count;

    /// <summary>
    /// Initializes a new empty sorted set.
    /// </summary>
    public SortedSetValue()
    {
        _header = new SkipListNode(MaxLevel, [], 0);
        _level = 1;
    }

    /// <summary>Gets the number of members.</summary>
    public int Count => _count;

    /// <summary>Gets the estimated memory footprint in bytes.</summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            long total = MemoryEstimator.ObjectOverhead + MemoryEstimator.DictionaryEntryOverhead;
            var node = _header.Forward[0];
            while (node is not null)
            {
                total += node.Member.Length + MemoryEstimator.ArrayOverhead + MemoryEstimator.ObjectOverhead;
                total += node.Forward.Length * 8;
                total += MemoryEstimator.DictionaryEntryOverhead;
                node = node.Forward[0];
            }

            return total;
        }
    }

    /// <summary>
    /// Adds or updates a member score (ZADD semantics).
    /// </summary>
    /// <param name="score">Member score.</param>
    /// <param name="member">Member bytes.</param>
    /// <returns><see langword="true"/> when a new member was inserted.</returns>
    public bool Add(double score, byte[] member)
    {
        ArgumentNullException.ThrowIfNull(member);

        var key = new MemberKey(member);
        if (_memberIndex.TryGetValue(key, out var existing))
        {
            if (existing.Score == score)
            {
                return false;
            }

            RemoveNode(existing);
            InsertNode(existing.Member, score);
            return false;
        }

        InsertNode(member, score);
        return true;
    }

    /// <summary>
    /// Removes a member when present.
    /// </summary>
    /// <param name="member">Member bytes.</param>
    /// <returns><see langword="true"/> when a member was removed.</returns>
    public bool Remove(byte[] member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!_memberIndex.TryGetValue(new MemberKey(member), out var node))
        {
            return false;
        }

        RemoveNode(node);
        return true;
    }

    /// <summary>
    /// Gets the score for a member (ZSCORE semantics).
    /// </summary>
    /// <param name="member">Member bytes.</param>
    /// <returns>Score when present, otherwise null.</returns>
    public double? GetScore(byte[] member)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (_memberIndex.TryGetValue(new MemberKey(member), out var node))
        {
            return node.Score;
        }

        return null;
    }

    /// <summary>
    /// Returns members by rank range inclusive (ZRANGE semantics).
    /// </summary>
    /// <param name="start">Start rank (0-based, negative counts from end).</param>
    /// <param name="stop">Stop rank inclusive.</param>
    /// <returns>Members in rank order.</returns>
    public byte[][] RangeByRank(long start, long stop)
    {
        if (_count == 0)
        {
            return [];
        }

        var normalizedStart = NormalizeRank(start);
        var normalizedStop = NormalizeRank(stop);
        if (normalizedStart > normalizedStop)
        {
            return [];
        }

        var resultCount = normalizedStop - normalizedStart + 1;
        var result = new byte[resultCount][];
        var node = GetNodeByRank(normalizedStart);
        for (long i = 0; i < resultCount && node is not null; i++)
        {
            result[i] = node.Member;
            node = node.Forward[0];
        }

        return result;
    }

    /// <summary>
    /// Returns members with scores by rank range inclusive.
    /// </summary>
    /// <param name="start">Start rank.</param>
    /// <param name="stop">Stop rank inclusive.</param>
    /// <returns>Score/member pairs in rank order.</returns>
    public (double Score, byte[] Member)[] RangeByRankWithScores(long start, long stop)
    {
        if (_count == 0)
        {
            return [];
        }

        var normalizedStart = NormalizeRank(start);
        var normalizedStop = NormalizeRank(stop);
        if (normalizedStart > normalizedStop)
        {
            return [];
        }

        var resultCount = normalizedStop - normalizedStart + 1;
        var result = new (double Score, byte[] Member)[resultCount];
        var node = GetNodeByRank(normalizedStart);
        for (long i = 0; i < resultCount && node is not null; i++)
        {
            result[i] = (node.Score, node.Member);
            node = node.Forward[0];
        }

        return result;
    }

    private void InsertNode(byte[] member, double score)
    {
        var update = new SkipListNode?[MaxLevel];
        var current = _header;

        for (var i = _level - 1; i >= 0; i--)
        {
            while (current.Forward[i] is SkipListNode next && Compare(next.Score, next.Member, score, member) < 0)
            {
                current = next;
            }

            update[i] = current;
        }

        var newLevel = RandomLevel();
        if (newLevel > _level)
        {
            for (var i = _level; i < newLevel; i++)
            {
                update[i] = _header;
            }

            _level = newLevel;
        }

        var node = new SkipListNode(newLevel, member, score);
        for (var i = 0; i < newLevel; i++)
        {
            node.Forward[i] = update[i]!.Forward[i];
            update[i]!.Forward[i] = node;
        }

        if (node.Forward[0] is null)
        {
            _tail = node;
        }

        _memberIndex[new MemberKey(member)] = node;
        _count++;
    }

    private void RemoveNode(SkipListNode target)
    {
        var update = new SkipListNode?[MaxLevel];
        var current = _header;

        for (var i = _level - 1; i >= 0; i--)
        {
            while (current.Forward[i] is SkipListNode next
                   && Compare(next.Score, next.Member, target.Score, target.Member) < 0)
            {
                current = next;
            }

            update[i] = current;
        }

        current = current.Forward[0]!;
        if (current != target)
        {
            throw new InvalidOperationException("Skip list node mismatch during removal.");
        }

        for (var i = 0; i < _level; i++)
        {
            if (update[i]!.Forward[i] != target)
            {
                break;
            }

            update[i]!.Forward[i] = target.Forward[i];
        }

        while (_level > 1 && _header.Forward[_level - 1] is null)
        {
            _level--;
        }

        if (_tail == target)
        {
            _tail = update[0];
        }

        _memberIndex.Remove(new MemberKey(target.Member));
        _count--;
    }

    private int RandomLevel()
    {
        var level = 1;
        while (level < MaxLevel && _random.NextDouble() < Probability)
        {
            level++;
        }

        return level;
    }

    private long NormalizeRank(long rank)
    {
        long normalized = rank;
        if (normalized < 0)
        {
            normalized = _count + normalized;
        }

        if (normalized < 0)
        {
            normalized = 0;
        }

        if (normalized >= _count)
        {
            normalized = _count - 1;
        }

        return normalized;
    }

    private SkipListNode? GetNodeByRank(long rank)
    {
        var node = _header.Forward[0];
        for (long i = 0; i < rank && node is not null; i++)
        {
            node = node.Forward[0];
        }

        return node;
    }

    private static int Compare(double leftScore, ReadOnlySpan<byte> leftMember, double rightScore, ReadOnlySpan<byte> rightMember)
    {
        if (leftScore < rightScore)
        {
            return -1;
        }

        if (leftScore > rightScore)
        {
            return 1;
        }

        return leftMember.SequenceCompareTo(rightMember);
    }

    private sealed class SkipListNode
    {
        public SkipListNode(int level, byte[] member, double score)
        {
            Member = member;
            Score = score;
            Forward = new SkipListNode?[level];
        }

        public byte[] Member { get; }

        public double Score { get; set; }

        public SkipListNode?[] Forward { get; }
    }
}
