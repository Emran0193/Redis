using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace NovaDB.Protocol;

/// <summary>
/// Writes RESP2 values to a <see cref="PipeWriter"/> or <see cref="IBufferWriter{T}"/>.
/// </summary>
public static class RespWriter
{
    private static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    /// <summary>
    /// Writes a RESP value to the pipe writer.
    /// </summary>
    public static void Write(PipeWriter writer, RespValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        Write((IBufferWriter<byte>)writer, value);
    }

    /// <summary>
    /// Writes a RESP value to a buffer writer.
    /// </summary>
    public static void Write(IBufferWriter<byte> writer, RespValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        switch (value.Type)
        {
            case RespType.SimpleString:
                WritePrefixedLine(writer, (byte)'+', value.Simple!);
                break;
            case RespType.Error:
                WritePrefixedLine(writer, (byte)'-', value.Simple!);
                break;
            case RespType.Integer:
                WriteInteger(writer, value.Integer);
                break;
            case RespType.BulkString:
                WriteBulk(writer, value);
                break;
            case RespType.Array:
                WriteArray(writer, value.Array!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Type, "Unknown RESP type.");
        }
    }

    /// <summary>
    /// Serializes a RESP value to a newly allocated byte array (tests / AOF).
    /// </summary>
    public static byte[] Serialize(RespValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (rented, length) = SerializeRented(value);
        try
        {
            var result = new byte[length];
            rented.AsSpan(0, length).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Serializes into a rented buffer. Caller must return the array to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static (byte[] Buffer, int Length) SerializeRented(RespValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var writer = new ArrayBufferWriter<byte>(256);
        Write(writer, value);
        var rented = ArrayPool<byte>.Shared.Rent(writer.WrittenCount);
        writer.WrittenSpan.CopyTo(rented);
        return (rented, writer.WrittenCount);
    }

    private static void WritePrefixedLine(IBufferWriter<byte> writer, byte prefix, string text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var span = writer.GetSpan(1 + byteCount + 2);
        span[0] = prefix;
        var written = Encoding.UTF8.GetBytes(text, span[1..]);
        span[1 + written] = (byte)'\r';
        span[2 + written] = (byte)'\n';
        writer.Advance(3 + written);
    }

    private static void WriteInteger(IBufferWriter<byte> writer, long number)
    {
        Span<char> chars = stackalloc char[32];
        if (!number.TryFormat(chars, out var charCount, provider: CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Failed to format integer.");
        }

        var span = writer.GetSpan(1 + charCount + 2);
        span[0] = (byte)':';
        for (var i = 0; i < charCount; i++)
        {
            span[1 + i] = (byte)chars[i];
        }

        span[1 + charCount] = (byte)'\r';
        span[2 + charCount] = (byte)'\n';
        writer.Advance(3 + charCount);
    }

    private static void WriteBulk(IBufferWriter<byte> writer, RespValue value)
    {
        if (value.IsNullBulk)
        {
            WriteAscii(writer, "$-1\r\n"u8);
            return;
        }

        var payload = value.Bulk ?? [];
        WriteLengthPrefix(writer, (byte)'$', payload.Length);
        var span = writer.GetSpan(payload.Length + 2);
        payload.CopyTo(span);
        span[payload.Length] = (byte)'\r';
        span[payload.Length + 1] = (byte)'\n';
        writer.Advance(payload.Length + 2);
    }

    private static void WriteArray(IBufferWriter<byte> writer, RespValue[] items)
    {
        WriteLengthPrefix(writer, (byte)'*', items.Length);
        foreach (var item in items)
        {
            Write(writer, item);
        }
    }

    private static void WriteLengthPrefix(IBufferWriter<byte> writer, byte prefix, int length)
    {
        Span<char> chars = stackalloc char[16];
        length.TryFormat(chars, out var charCount, provider: CultureInfo.InvariantCulture);
        var span = writer.GetSpan(1 + charCount + 2);
        span[0] = prefix;
        for (var i = 0; i < charCount; i++)
        {
            span[1 + i] = (byte)chars[i];
        }

        span[1 + charCount] = (byte)'\r';
        span[2 + charCount] = (byte)'\n';
        writer.Advance(3 + charCount);
    }

    private static void WriteAscii(IBufferWriter<byte> writer, ReadOnlySpan<byte> ascii)
    {
        var span = writer.GetSpan(ascii.Length);
        ascii.CopyTo(span);
        writer.Advance(ascii.Length);
    }
}
