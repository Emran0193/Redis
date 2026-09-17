using System.Buffers;
using System.Globalization;
using System.Text;

namespace NovaDB.Protocol;

/// <summary>
/// RESP2 value kinds.
/// </summary>
public enum RespType : byte
{
    /// <summary>Simple string (+).</summary>
    SimpleString = 1,

    /// <summary>Error (-).</summary>
    Error = 2,

    /// <summary>Integer (:).</summary>
    Integer = 3,

    /// <summary>Bulk string ($).</summary>
    BulkString = 4,

    /// <summary>Array (*).</summary>
    Array = 5
}

/// <summary>
/// Immutable RESP value. Bulk payloads are owned copies for safe lifetime across pipeline advances.
/// </summary>
public sealed class RespValue
{
    private RespValue(RespType type, string? simple, long integer, byte[]? bulk, RespValue[]? array, bool isNullBulk)
    {
        Type = type;
        Simple = simple;
        Integer = integer;
        Bulk = bulk;
        Array = array;
        IsNullBulk = isNullBulk;
    }

    /// <summary>Gets the RESP type.</summary>
    public RespType Type { get; }

    /// <summary>Gets simple string or error text.</summary>
    public string? Simple { get; }

    /// <summary>Gets integer payload.</summary>
    public long Integer { get; }

    /// <summary>Gets bulk string bytes (null when <see cref="IsNullBulk"/>).</summary>
    public byte[]? Bulk { get; }

    /// <summary>Gets array elements.</summary>
    public RespValue[]? Array { get; }

    /// <summary>Gets a value indicating whether this is a null bulk string ($-1).</summary>
    public bool IsNullBulk { get; }

    /// <summary>Creates a simple string.</summary>
    public static RespValue SimpleString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RespValue(RespType.SimpleString, value, 0, null, null, false);
    }

    /// <summary>Creates an error.</summary>
    public static RespValue Error(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new RespValue(RespType.Error, value, 0, null, null, false);
    }

    /// <summary>Creates an integer.</summary>
    public static RespValue FromInteger(long value)
        => new(RespType.Integer, null, value, null, null, false);

    /// <summary>Creates a bulk string from UTF-8 bytes (takes ownership).</summary>
    public static RespValue BulkString(byte[]? bytes)
        => bytes is null
            ? new RespValue(RespType.BulkString, null, 0, null, null, true)
            : new RespValue(RespType.BulkString, null, 0, bytes, null, false);

    /// <summary>Creates a bulk string from a UTF-8 string.</summary>
    public static RespValue BulkString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return BulkString(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Creates a null bulk string.</summary>
    public static RespValue NullBulk()
        => new(RespType.BulkString, null, 0, null, null, true);

    /// <summary>Creates an array.</summary>
    public static RespValue FromArray(RespValue[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new RespValue(RespType.Array, null, 0, null, items, false);
    }

    /// <summary>Shared OK simple string.</summary>
    public static RespValue Ok { get; } = SimpleString("OK");

    /// <summary>Shared PONG simple string.</summary>
    public static RespValue Pong { get; } = SimpleString("PONG");

    /// <summary>
    /// Decodes bulk or simple string as UTF-8 text.
    /// </summary>
    public string AsUtf8String()
    {
        if (Type == RespType.SimpleString || Type == RespType.Error)
        {
            return Simple ?? string.Empty;
        }

        if (Type == RespType.BulkString)
        {
            if (IsNullBulk || Bulk is null)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(Bulk);
        }

        throw new InvalidOperationException("Value is not a string type.");
    }
}

/// <summary>
/// Zero-copy-friendly RESP2 parser operating on <see cref="ReadOnlySequence{T}"/>.
/// </summary>
public static class RespParser
{
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';

    /// <summary>
    /// Gets or sets process-wide parse limits. Set at startup from <c>NovaDbOptions</c>.
    /// </summary>
    public static RespParseLimits Limits { get; set; } = RespParseLimits.Default;

    /// <summary>
    /// Attempts to parse one RESP value from the buffer.
    /// </summary>
    /// <returns>True when a complete value was parsed; false when more data is required.</returns>
    public static bool TryParse(ref ReadOnlySequence<byte> buffer, out RespValue? value, out SequencePosition consumed)
        => TryParse(ref buffer, Limits, out value, out consumed);

    /// <summary>
    /// Attempts to parse one RESP value from the buffer using explicit limits.
    /// </summary>
    public static bool TryParse(
        ref ReadOnlySequence<byte> buffer,
        RespParseLimits limits,
        out RespValue? value,
        out SequencePosition consumed)
    {
        ArgumentNullException.ThrowIfNull(limits);
        value = null;
        consumed = buffer.Start;

        if (buffer.IsEmpty)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);
        if (!TryParseValue(ref reader, limits, out value))
        {
            value = null;
            return false;
        }

        consumed = reader.Position;
        return true;
    }

    private static bool TryParseValue(ref SequenceReader<byte> reader, RespParseLimits limits, out RespValue? value)
    {
        value = null;
        if (!reader.TryRead(out var typeByte))
        {
            return false;
        }

        return typeByte switch
        {
            (byte)'+' => TryParseSimple(ref reader, limits, RespType.SimpleString, out value),
            (byte)'-' => TryParseSimple(ref reader, limits, RespType.Error, out value),
            (byte)':' => TryParseInteger(ref reader, limits, out value),
            (byte)'$' => TryParseBulk(ref reader, limits, out value),
            (byte)'*' => TryParseArray(ref reader, limits, out value),
            _ => throw new ProtocolException($"Unknown RESP type byte: {(char)typeByte}")
        };
    }

    private static bool TryParseSimple(
        ref SequenceReader<byte> reader,
        RespParseLimits limits,
        RespType type,
        out RespValue? value)
    {
        value = null;
        if (!TryReadLine(ref reader, limits, out var line))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(line);
        value = type == RespType.Error ? RespValue.Error(text) : RespValue.SimpleString(text);
        return true;
    }

    private static bool TryParseInteger(ref SequenceReader<byte> reader, RespParseLimits limits, out RespValue? value)
    {
        value = null;
        if (!TryReadLine(ref reader, limits, out var line))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(line);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new ProtocolException($"Invalid integer: {text}");
        }

        value = RespValue.FromInteger(number);
        return true;
    }

    private static bool TryParseBulk(ref SequenceReader<byte> reader, RespParseLimits limits, out RespValue? value)
    {
        value = null;
        if (!TryReadLine(ref reader, limits, out var lenLine))
        {
            return false;
        }

        var lenText = Encoding.UTF8.GetString(lenLine);
        if (!int.TryParse(lenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            throw new ProtocolException($"Invalid bulk length: {lenText}");
        }

        if (length == -1)
        {
            value = RespValue.NullBulk();
            return true;
        }

        if (length < 0)
        {
            throw new ProtocolException($"Invalid bulk length: {length}");
        }

        // Reject before allocate / before waiting for the full payload (DoS + pipe-pause safety).
        if (length > limits.MaxBulkBytes)
        {
            throw new ProtocolException(
                $"Bulk string length {length} exceeds limit of {limits.MaxBulkBytes} bytes.");
        }

        if (reader.Remaining < length + 2)
        {
            return false;
        }

        var payload = new byte[length];
        if (!reader.TryCopyTo(payload))
        {
            return false;
        }

        reader.Advance(length);
        if (!reader.TryRead(out var cr) || cr != Cr || !reader.TryRead(out var lf) || lf != Lf)
        {
            throw new ProtocolException("Bulk string missing CRLF trailer.");
        }

        value = RespValue.BulkString(payload);
        return true;
    }

    private static bool TryParseArray(ref SequenceReader<byte> reader, RespParseLimits limits, out RespValue? value)
    {
        value = null;
        if (!TryReadLine(ref reader, limits, out var lenLine))
        {
            return false;
        }

        var lenText = Encoding.UTF8.GetString(lenLine);
        if (!int.TryParse(lenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            throw new ProtocolException($"Invalid array length: {lenText}");
        }

        if (count < 0)
        {
            throw new ProtocolException($"Invalid array length: {count}");
        }

        if (count > limits.MaxArrayLength)
        {
            throw new ProtocolException(
                $"Array length {count} exceeds limit of {limits.MaxArrayLength} elements.");
        }

        var items = new RespValue[count];
        for (var i = 0; i < count; i++)
        {
            if (!TryParseValue(ref reader, limits, out var item) || item is null)
            {
                return false;
            }

            items[i] = item;
        }

        value = RespValue.FromArray(items);
        return true;
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, RespParseLimits limits, out ReadOnlySpan<byte> line)
    {
        if (!reader.TryReadTo(out ReadOnlySpan<byte> span, Cr, advancePastDelimiter: true))
        {
            // Incomplete line: if already over MaxLineLength without CRLF, reject.
            if (reader.Remaining > limits.MaxLineLength)
            {
                throw new ProtocolException($"RESP line exceeds limit of {limits.MaxLineLength} bytes.");
            }

            line = default;
            return false;
        }

        if (!reader.TryRead(out var lf) || lf != Lf)
        {
            line = default;
            return false;
        }

        if (span.Length > limits.MaxLineLength)
        {
            throw new ProtocolException($"RESP line exceeds limit of {limits.MaxLineLength} bytes.");
        }

        line = span;
        return true;
    }
}

/// <summary>
/// Protocol framing exception (maps to client disconnect / error).
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolException"/> class.
    /// </summary>
    public ProtocolException(string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }
}
