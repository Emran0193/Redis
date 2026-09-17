namespace NovaDB.Persistence.Internal;

/// <summary>
/// IEEE CRC-32 checksum used for snapshot integrity validation.
/// </summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    /// <summary>
    /// Computes CRC-32 over the supplied bytes.
    /// </summary>
    /// <param name="data">Input span.</param>
    /// <returns>CRC-32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var j = 0; j < 8; j++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
