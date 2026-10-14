using System.Buffers.Binary;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireCftRow(
    int RecordId,
    IReadOnlyDictionary<string, object?> Values);

internal sealed record CrossFireCftTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<int> ColumnTypes,
    IReadOnlyList<CrossFireCftRow> Rows);

/// <summary>
/// Shared row-level reader for CrossFire's LZMA/XOR-wrapped CFT tables.
/// Keeps the fixed 128-column header and typed row boundary intact so
/// specialized tables can be joined without guessing from nearby strings.
/// </summary>
internal static class CrossFireCftTableDecoder
{
    private const int ColumnCapacity = 128;
    private const int HeaderSize = 14 + ColumnCapacity * 64 + ColumnCapacity * sizeof(int);
    private const int MaximumRows = 1_000_000;
    private const int MaximumDecodedBytes = 128 * 1024 * 1024;

    public static bool TryDecode(byte[] data, out CrossFireCftTable? table)
    {
        table = null;
        if (data.Length == 0)
        {
            return false;
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaximumDecodedBytes);
        if (prepared is null || prepared.Length < HeaderSize)
        {
            return false;
        }

        byte[] decoded = prepared;
        if (!HasSignature(decoded))
        {
            decoded = (byte[])prepared.Clone();
            for (int index = 0; index < decoded.Length; index++)
            {
                decoded[index] ^= 0x10;
            }
        }

        if (!HasSignature(decoded))
        {
            return false;
        }

        int columnCount = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(6, sizeof(int)));
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(10, sizeof(int)));
        if (columnCount is <= 0 or > ColumnCapacity || rowCount is < 0 or > MaximumRows)
        {
            return false;
        }

        var columns = new string[columnCount];
        var columnTypes = new int[columnCount];
        for (int column = 0; column < columnCount; column++)
        {
            int nameOffset = 14 + column * 64;
            int nameLength = Array.IndexOf(decoded, (byte)0, nameOffset, 64);
            if (nameLength < 0)
            {
                nameLength = nameOffset + 64;
            }

            columns[column] = Encoding.Latin1.GetString(decoded, nameOffset, nameLength - nameOffset);
            int typeOffset = 14 + ColumnCapacity * 64 + column * sizeof(int);
            columnTypes[column] = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(typeOffset, sizeof(int)));
            if (columnTypes[column] is < 0 or > 5)
            {
                return false;
            }
        }

        bool hasCharacterTypeRowIndex = IsCharacterTypeTable(columns, columnTypes, columnCount);
        if (!TryReadRows(decoded, columns, columnTypes, rowCount, hasCharacterTypeRowIndex, out IReadOnlyList<CrossFireCftRow> rows))
        {
            return false;
        }

        table = new CrossFireCftTable(columns, columnTypes, rows);
        return true;
    }

    private static bool TryReadRows(
        byte[] data,
        IReadOnlyList<string> columns,
        IReadOnlyList<int> columnTypes,
        int rowCount,
        bool hasCharacterTypeRowIndex,
        out IReadOnlyList<CrossFireCftRow> rows)
    {
        rows = [];
        int position = HeaderSize;
        var decodedRows = new List<CrossFireCftRow>(Math.Min(rowCount, 100_000));
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (!TryReadInt32(data, ref position, out int recordId))
            {
                return false;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int column = 0; column < columns.Count; column++)
            {
                object? value;
                switch (columnTypes[column])
                {
                    case 0 when string.IsNullOrEmpty(columns[column]):
                        continue;
                    case 1:
                        if (!TryReadString(data, ref position, out string stringValue))
                        {
                            return false;
                        }

                        value = stringValue;
                        break;
                    default:
                        if (!TryReadInt32(data, ref position, out int integerValue))
                        {
                            return false;
                        }

                        value = integerValue;
                        if (hasCharacterTypeRowIndex && column == 0 && integerValue > 0)
                        {
                            if (!TryReadInt32(data, ref position, out int serializedRowIndex) ||
                                serializedRowIndex != integerValue - 1)
                            {
                                return false;
                            }
                        }

                        break;
                }

                if (!string.IsNullOrEmpty(columns[column]))
                {
                    values[columns[column]] = value;
                }
            }

            decodedRows.Add(new CrossFireCftRow(recordId, values));
        }

        rows = decodedRows;
        return true;
    }

    private static bool IsCharacterTypeTable(
        IReadOnlyList<string> columns,
        IReadOnlyList<int> columnTypes,
        int columnCount)
    {
        return columnCount >= 6 &&
               columnTypes[0] == 3 &&
               columnTypes[1] == 1 &&
               columnTypes[2] == 1 &&
               columnTypes[3] == 1 &&
               columnTypes[4] == 1 &&
               columnTypes[5] == 3 &&
               string.Equals(columns[0], "_RealCharacterType", StringComparison.Ordinal) &&
               string.Equals(columns[1], "_ItemName", StringComparison.Ordinal) &&
               string.Equals(columns[2], "_ResourceName", StringComparison.Ordinal) &&
               string.Equals(columns[3], "_ItemID", StringComparison.Ordinal) &&
               string.Equals(columns[4], "_ItemCode", StringComparison.Ordinal) &&
               string.Equals(columns[5], "_ButeType", StringComparison.Ordinal);
    }

    private static bool HasSignature(byte[] data)
    {
        return data.Length >= 2 && data[0] == (byte)'s' && data[1] == (byte)'u';
    }

    private static bool TryReadInt32(byte[] data, ref int position, out int value)
    {
        value = 0;
        if (position < 0 || position > data.Length - sizeof(int))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, sizeof(int)));
        position += sizeof(int);
        return true;
    }

    private static bool TryReadString(byte[] data, ref int position, out string value)
    {
        value = string.Empty;
        if (position < 0 || position > data.Length - sizeof(ushort))
        {
            return false;
        }

        int byteLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, sizeof(ushort)));
        position += sizeof(ushort);
        int paddedByteLength = byteLength + (byteLength & 1);
        if (position > data.Length - paddedByteLength)
        {
            return false;
        }

        value = Encoding.Unicode.GetString(data, position, byteLength);
        position += paddedByteLength;
        return true;
    }
}
