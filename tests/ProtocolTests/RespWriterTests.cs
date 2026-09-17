using System.Buffers;
using System.Text;
using FluentAssertions;
using NovaDB.Protocol;

namespace ProtocolTests;

public sealed class RespWriterTests
{
    [Fact]
    public void Write_SimpleString_UsesPlusPrefix()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.SimpleString("OK"));

        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("+OK\r\n");
    }

    [Fact]
    public void Write_Error_UsesMinusPrefix()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.Error("ERR boom"));

        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("-ERR boom\r\n");
    }

    [Fact]
    public void Write_Integer_UsesColonPrefix()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.FromInteger(123));

        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be(":123\r\n");
    }

    [Fact]
    public void Write_BulkString_IncludesLengthAndPayload()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.BulkString("hello"));

        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("$5\r\nhello\r\n");
    }

    [Fact]
    public void Write_NullBulk_WritesMinusOne()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.NullBulk());

        Encoding.UTF8.GetString(writer.WrittenSpan).Should().Be("$-1\r\n");
    }

    [Fact]
    public void Write_NestedArray_SerializesAllElements()
    {
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, RespValue.FromArray(
        [
            RespValue.BulkString("PING"),
            RespValue.FromArray([RespValue.FromInteger(1), RespValue.SimpleString("nested")])
        ]));

        var text = Encoding.UTF8.GetString(writer.WrittenSpan);
        text.Should().StartWith("*2\r\n");
        text.Should().Contain("$4\r\nPING\r\n");
        text.Should().Contain("*2\r\n:1\r\n+nested\r\n");
    }

    [Fact]
    public void Serialize_RoundTripsThroughParser()
    {
        var original = RespValue.FromArray(
        [
            RespValue.BulkString("MSET"),
            RespValue.BulkString("a"),
            RespValue.BulkString("1"),
            RespValue.BulkString("b"),
            RespValue.BulkString("2")
        ]);

        var bytes = RespWriter.Serialize(original);
        var buffer = new ReadOnlySequence<byte>(bytes);

        RespParser.TryParse(ref buffer, out var parsed, out _).Should().BeTrue();
        parsed.ShouldBeEquivalentTo(original);
    }

    [Fact]
    public void Write_ArrayBufferWriter_MatchesSerialize()
    {
        var value = RespValue.FromArray([RespValue.BulkString("GET"), RespValue.BulkString("key")]);
        var writer = new ArrayBufferWriter<byte>();
        RespWriter.Write(writer, value);

        writer.WrittenSpan.ToArray().Should().Equal(RespWriter.Serialize(value));
    }
}
