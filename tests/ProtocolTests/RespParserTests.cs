using System.Buffers;
using System.Text;
using FluentAssertions;
using NovaDB.Protocol;

namespace ProtocolTests;

public sealed class RespParserTests
{
    [Fact]
    public void TryParse_SimpleString_ReturnsTrue()
    {
        var buffer = ToSequence("+OK\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out var consumed);

        parsed.Should().BeTrue();
        value.Should().NotBeNull();
        value!.Type.Should().Be(RespType.SimpleString);
        value.Simple.Should().Be("OK");
        buffer.GetRemainingLength(consumed).Should().Be(0);
    }

    [Fact]
    public void TryParse_Error_ReturnsTrue()
    {
        var buffer = ToSequence("-ERR unknown\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out _);

        parsed.Should().BeTrue();
        value!.Type.Should().Be(RespType.Error);
        value.Simple.Should().Be("ERR unknown");
    }

    [Fact]
    public void TryParse_Integer_ReturnsTrue()
    {
        var buffer = ToSequence(":42\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out _);

        parsed.Should().BeTrue();
        value!.Type.Should().Be(RespType.Integer);
        value.Integer.Should().Be(42);
    }

    [Fact]
    public void TryParse_BulkString_ReturnsTrue()
    {
        var buffer = ToSequence("$5\r\nhello\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out _);

        parsed.Should().BeTrue();
        value!.Type.Should().Be(RespType.BulkString);
        value.IsNullBulk.Should().BeFalse();
        Encoding.UTF8.GetString(value.Bulk!).Should().Be("hello");
    }

    [Fact]
    public void TryParse_NullBulk_ReturnsTrue()
    {
        var buffer = ToSequence("$-1\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out _);

        parsed.Should().BeTrue();
        value!.Type.Should().Be(RespType.BulkString);
        value.IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public void TryParse_NestedArray_ReturnsTrue()
    {
        var buffer = ToSequence("*2\r\n$3\r\nfoo\r\n*2\r\n:1\r\n+OK\r\n"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out _);

        parsed.Should().BeTrue();
        value!.Type.Should().Be(RespType.Array);
        value.Array.Should().HaveCount(2);
        var array = value.Array!;
        array[0].AsUtf8String().Should().Be("foo");
        array[1].Type.Should().Be(RespType.Array);
        array[1].Array.Should().HaveCount(2);
        var nested = array[1].Array!;
        nested[0].Integer.Should().Be(1);
        nested[1].Simple.Should().Be("OK");
    }

    [Fact]
    public void TryParse_IncompleteBuffer_ReturnsFalse()
    {
        var buffer = ToSequence("$5\r\nhel"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out var consumed);

        parsed.Should().BeFalse();
        value.Should().BeNull();
        consumed.Should().Be(buffer.Start);
    }

    [Fact]
    public void TryParse_IncompleteArray_ReturnsFalse()
    {
        var buffer = ToSequence("*2\r\n$3\r\nfoo\r\n$3\r\nba"u8);

        var parsed = RespParser.TryParse(ref buffer, out var value, out var consumed);

        parsed.Should().BeFalse();
        value.Should().BeNull();
        consumed.Should().Be(buffer.Start);
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsFalse()
    {
        var buffer = ToSequence([]);

        var parsed = RespParser.TryParse(ref buffer, out var value, out var consumed);

        parsed.Should().BeFalse();
        value.Should().BeNull();
        consumed.Should().Be(buffer.Start);
    }

    [Fact]
    public void TryParse_ReadOnlySequenceSpanningMultipleArrays_ParsesCompleteValue()
    {
        var head = "$5\r\n"u8.ToArray();
        var tail = "hello\r\n"u8.ToArray();
        var segment1 = new TestSequenceSegment(head);
        var segment2 = segment1.Append(tail);
        var buffer = new ReadOnlySequence<byte>(segment1, 0, segment2, tail.Length);

        RespParser.TryParse(ref buffer, out var value, out _).Should().BeTrue();
        value!.AsUtf8String().Should().Be("hello");
    }

    [Fact]
    public void RoundTrip_SimpleString_MatchesOriginal()
    {
        RoundTrip(RespValue.SimpleString("PONG"));
    }

    [Fact]
    public void RoundTrip_Error_MatchesOriginal()
    {
        RoundTrip(RespValue.Error("ERR boom"));
    }

    [Fact]
    public void RoundTrip_Integer_MatchesOriginal()
    {
        RoundTrip(RespValue.FromInteger(-100));
    }

    [Fact]
    public void RoundTrip_BulkString_MatchesOriginal()
    {
        RoundTrip(RespValue.BulkString("payload"));
    }

    [Fact]
    public void RoundTrip_NullBulk_MatchesOriginal()
    {
        RoundTrip(RespValue.NullBulk());
    }

    [Fact]
    public void RoundTrip_NestedArray_MatchesOriginal()
    {
        RoundTrip(RespValue.FromArray(
        [
            RespValue.BulkString("SET"),
            RespValue.BulkString("key"),
            RespValue.FromArray([RespValue.FromInteger(1), RespValue.SimpleString("OK")])
        ]));
    }

    private static void RoundTrip(RespValue original)
    {
        var bytes = RespWriter.Serialize(original);
        var buffer = new ReadOnlySequence<byte>(bytes);

        var parsed = RespParser.TryParse(ref buffer, out var value, out var consumed);

        parsed.Should().BeTrue();
        value.ShouldBeEquivalentTo(original);
        buffer.GetRemainingLength(consumed).Should().Be(0);
    }

    private static ReadOnlySequence<byte> ToSequence(ReadOnlySpan<byte> bytes)
        => new(bytes.ToArray());

    [Fact]
    public void TryParse_BulkExceedingLimit_ThrowsBeforeAllocate()
    {
        var limits = new RespParseLimits { MaxBulkBytes = 8, MaxArrayLength = 100, MaxLineLength = 1024 };
        var buffer = ToSequence("$100\r\n"u8);

        var act = () =>
        {
            var local = buffer;
            return RespParser.TryParse(ref local, limits, out _, out _);
        };

        act.Should().Throw<ProtocolException>().WithMessage("*exceeds limit*");
    }

    [Fact]
    public void TryParse_ArrayExceedingLimit_ThrowsBeforeAllocate()
    {
        var limits = new RespParseLimits { MaxBulkBytes = 1024, MaxArrayLength = 2, MaxLineLength = 1024 };
        var buffer = ToSequence("*100\r\n"u8);

        var act = () =>
        {
            var local = buffer;
            return RespParser.TryParse(ref local, limits, out _, out _);
        };

        act.Should().Throw<ProtocolException>().WithMessage("*exceeds limit*");
    }

    [Fact]
    public void TryParse_BulkWithinRaisedLimit_ParsesLargePayload()
    {
        var payload = new byte[70_000];
        payload.AsSpan().Fill((byte)'x');
        var header = Encoding.UTF8.GetBytes($"${payload.Length}\r\n");
        var trailer = "\r\n"u8.ToArray();
        var bytes = new byte[header.Length + payload.Length + trailer.Length];
        header.CopyTo(bytes, 0);
        payload.CopyTo(bytes, header.Length);
        trailer.CopyTo(bytes, header.Length + payload.Length);

        var limits = new RespParseLimits { MaxBulkBytes = 128_000, MaxArrayLength = 10, MaxLineLength = 1024 };
        var buffer = new ReadOnlySequence<byte>(bytes);

        var parsed = RespParser.TryParse(ref buffer, limits, out var value, out _);

        parsed.Should().BeTrue();
        value!.Bulk.Should().HaveCount(70_000);
    }

    private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSequenceSegment(byte[] bytes)
        {
            Memory = bytes;
        }

        public TestSequenceSegment Append(byte[] bytes)
        {
            var next = new TestSequenceSegment(bytes)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}
