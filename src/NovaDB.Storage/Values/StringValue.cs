using System.Globalization;
using System.Text;

namespace NovaDB.Storage.Values;

/// <summary>
/// Redis string value backed by a byte array.
/// </summary>
public sealed class StringValue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StringValue"/> class.
    /// </summary>
    /// <param name="bytes">Raw value bytes.</param>
    public StringValue(byte[] bytes)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    /// <summary>Gets or sets the raw value bytes.</summary>
    public byte[] Bytes { get; set; }

    /// <summary>Gets the estimated memory footprint in bytes.</summary>
    public long EstimatedMemoryBytes => Bytes.Length + MemoryEstimator.ArrayOverhead;

    /// <summary>
    /// Creates a string value containing a decimal integer representation.
    /// </summary>
    /// <param name="value">Integer value.</param>
    /// <returns>A new string value.</returns>
    public static StringValue FromInt64(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return new StringValue(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Attempts to parse the value as a signed 64-bit integer.
    /// </summary>
    /// <param name="value">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public bool TryParseInt64(out long value)
    {
        return long.TryParse(
            Encoding.UTF8.GetString(Bytes),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Sets the value to the decimal representation of <paramref name="value"/>.
    /// </summary>
    /// <param name="value">Integer value.</param>
    public void SetInt64(long value)
    {
        Bytes = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Attempts to parse the value as a floating-point number.
    /// </summary>
    public bool TryParseDouble(out double value)
    {
        return double.TryParse(
            Encoding.UTF8.GetString(Bytes),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Sets the value to an invariant-culture float representation (Redis INCRBYFLOAT style).
    /// </summary>
    public void SetDouble(double value)
    {
        Bytes = Encoding.UTF8.GetBytes(value.ToString("G17", CultureInfo.InvariantCulture));
    }
}
