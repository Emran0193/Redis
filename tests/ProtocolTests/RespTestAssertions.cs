using System.Buffers;
using FluentAssertions;
using NovaDB.Protocol;

namespace ProtocolTests;

internal static class RespTestAssertions
{
    public static long GetRemainingLength(this ReadOnlySequence<byte> buffer, SequencePosition consumed)
    {
        var slice = buffer.Slice(consumed);
        return slice.Length;
    }

    public static void ShouldBeEquivalentTo(this RespValue? actual, RespValue expected)
    {
        actual.Should().NotBeNull();
        actual!.Type.Should().Be(expected.Type);

        switch (expected.Type)
        {
            case RespType.SimpleString:
            case RespType.Error:
                actual.Simple.Should().Be(expected.Simple);
                break;
            case RespType.Integer:
                actual.Integer.Should().Be(expected.Integer);
                break;
            case RespType.BulkString:
                actual.IsNullBulk.Should().Be(expected.IsNullBulk);
                if (expected.IsNullBulk)
                {
                    break;
                }

                actual.Bulk.Should().BeEquivalentTo(expected.Bulk);
                break;
            case RespType.Array:
                actual.Array.Should().NotBeNull();
                expected.Array.Should().NotBeNull();
                actual.Array!.Length.Should().Be(expected.Array!.Length);
                for (var i = 0; i < expected.Array.Length; i++)
                {
                    actual.Array[i].ShouldBeEquivalentTo(expected.Array[i]);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected), expected.Type, "Unknown RESP type.");
        }
    }
}
