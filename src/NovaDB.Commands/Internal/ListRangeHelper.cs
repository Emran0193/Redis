namespace NovaDB.Commands.Internal;

/// <summary>
/// Redis-compatible list index normalization for LRANGE.
/// </summary>
internal static class ListRangeHelper
{
    /// <summary>
    /// Normalizes start/stop indices and returns an inclusive range, or empty when out of bounds.
    /// </summary>
    public static (long Start, long Stop) NormalizeRange(long start, long stop, int count)
    {
        if (count == 0)
        {
            return (-1, -1);
        }

        var normalizedStart = start < 0 ? count + start : start;
        var normalizedStop = stop < 0 ? count + stop : stop;

        if (normalizedStart < 0)
        {
            normalizedStart = 0;
        }

        if (normalizedStop >= count)
        {
            normalizedStop = count - 1;
        }

        if (normalizedStart > normalizedStop)
        {
            return (-1, -1);
        }

        return (normalizedStart, normalizedStop);
    }
}
