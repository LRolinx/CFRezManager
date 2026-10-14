using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireScriptBinDocument(
    string Text,
    string Description,
    int SourceByteCount,
    int DecodedByteCount);

internal static class CrossFireScriptBinDecoder
{
    private const int MaxDecodedBytes = 8 * 1024 * 1024;
    private const int WeaponModelRecordBytes = 72;
    private const int ShopWeaponModelRecordBytes = 68;
    private const int MaxPreviewFields = 64;
    private const int MaxRecordsToPrint = 128;

    public static bool IsCandidate(string fileName, string extension)
    {
        if (!string.Equals(extension.TrimStart('.'), "bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsWeaponPreviewFileName(fileName) || IsWeaponModelFileName(fileName);
    }

    public static bool TryDecode(
        byte[] data,
        string fileName,
        out CrossFireScriptBinDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        try
        {
            byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxDecodedBytes);
            if (prepared is null)
            {
                errorMessage = "BIN LZMA compression could not be decoded.";
                return false;
            }

            bool compressed = !ReferenceEquals(prepared, data);
            if (IsWeaponModelFileName(fileName))
            {
                return TryDecodeWeaponModel(data, prepared, compressed, fileName, out document, out errorMessage);
            }

            if (IsWeaponPreviewFileName(fileName))
            {
                return TryDecodeWeaponPreview(data, prepared, compressed, fileName, out document, out errorMessage);
            }

            errorMessage = "BIN file name does not match a known CrossFire UI script layout.";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or OverflowException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryDecodeWeaponPreview(
        byte[] source,
        byte[] data,
        bool compressed,
        string fileName,
        out CrossFireScriptBinDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length < 1 + sizeof(uint) * 4 || (data.Length - 1) % sizeof(uint) != 0)
        {
            errorMessage = "WeaponPreview BIN length does not match the 1-byte header plus uint32 fields layout.";
            return false;
        }

        int fieldCount = (data.Length - 1) / sizeof(uint);
        if (fieldCount > MaxPreviewFields)
        {
            errorMessage = "WeaponPreview BIN contains more fields than expected.";
            return false;
        }

        TryParseWeaponPreviewName(fileName, out int fileCategory, out int fileEntryId);
        uint itemId = ReadUInt32(data, 1);
        uint previewSlot = ReadUInt32(data, 5);
        uint modelGroupId = ReadUInt32(data, 9);
        uint modelId = ReadUInt32(data, 13);

        var builder = new StringBuilder();
        builder.AppendLine("CrossFire UI script BIN");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Kind: WeaponPreview entry");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "WeaponPreview BIN", source.Length, data.Length)}");
        builder.AppendLine($"Layout: 1 byte header + {fieldCount:N0} little-endian uint32/float32 fields");
        builder.AppendLine();
        builder.AppendLine("Known fields:");
        builder.AppendLine($"- Header byte: {data[0]}");
        if (fileCategory >= 0)
        {
            builder.AppendLine($"- File category: {fileCategory}");
        }

        if (fileEntryId >= 0)
        {
            builder.AppendLine($"- File entry id: {fileEntryId}");
        }
        else
        {
            builder.AppendLine($"- Preview skin entry: {Path.GetFileNameWithoutExtension(fileName)}");
        }

        builder.AppendLine($"- Field 0 / item id: {itemId}");
        builder.AppendLine($"- Field 1 / preview slot: {previewSlot}");
        builder.AppendLine($"- Field 2 / model group id: {modelGroupId} (WeaponModel table key)");
        builder.AppendLine($"- Field 3 / model id: {modelId}");
        if (fileEntryId >= 0 && itemId != fileEntryId)
        {
            builder.AppendLine($"- Note: file entry id differs from embedded item id ({fileEntryId} != {itemId}).");
        }

        if (fieldCount > 0)
        {
            builder.AppendLine($"- Last field marker: 0x{ReadUInt32(data, 1 + (fieldCount - 1) * sizeof(uint)):X8}");
        }

        builder.AppendLine();
        AppendFieldTable(builder, data, 1, fieldCount, "Fields");
        AppendHexPreview(builder, data);

        document = new CrossFireScriptBinDocument(
            builder.ToString(),
            FormatStorage(compressed, "CrossFire WeaponPreview BIN", source.Length, data.Length),
            source.Length,
            data.Length);
        return true;
    }

    private static bool TryDecodeWeaponModel(
        byte[] source,
        byte[] data,
        bool compressed,
        string fileName,
        out CrossFireScriptBinDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length == sizeof(uint))
        {
            return DecodeEmptyWeaponModel(source, data, compressed, fileName, out document);
        }

        bool hasHeader = data.Length >= sizeof(uint) &&
                         (data.Length - sizeof(uint)) % WeaponModelRecordBytes == 0;
        bool shopLayout = !hasHeader && data.Length % ShopWeaponModelRecordBytes == 0;
        if (!hasHeader && !shopLayout)
        {
            errorMessage = "WeaponModel BIN length does not match the known 72-byte or 68-byte record layouts.";
            return false;
        }

        int headerBytes = hasHeader ? sizeof(uint) : 0;
        int recordBytes = hasHeader ? WeaponModelRecordBytes : ShopWeaponModelRecordBytes;
        int recordCount = (data.Length - headerBytes) / recordBytes;
        int fieldsPerRecord = recordBytes / sizeof(uint);
        var modelGroupCounts = new SortedDictionary<uint, int>();
        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            int recordOffset = headerBytes + recordIndex * recordBytes;
            uint modelGroupId = ReadUInt32(data, recordOffset + (fieldsPerRecord - 1) * sizeof(uint));
            modelGroupCounts[modelGroupId] = modelGroupCounts.GetValueOrDefault(modelGroupId) + 1;
        }

        var builder = new StringBuilder();
        builder.AppendLine("CrossFire UI script BIN");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine(hasHeader ? "Kind: WeaponModel table" : "Kind: Shop2 WeaponModel table");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "WeaponModel BIN", source.Length, data.Length)}");
        builder.AppendLine($"Layout: {(hasHeader ? "4-byte header + " : string.Empty)}{recordCount:N0} records x {recordBytes:N0} bytes");
        if (modelGroupCounts.Count > 0)
        {
            builder.AppendLine("Model group ids (record's final uint32 field): " + string.Join(", ", modelGroupCounts.Select(pair =>
                $"{pair.Key.ToString(CultureInfo.InvariantCulture)} ({pair.Value.ToString("N0", CultureInfo.InvariantCulture)} records)")));
        }

        if (hasHeader)
        {
            builder.AppendLine($"Header/version: {ReadUInt32(data, 0)}");
        }

        builder.AppendLine();
        builder.AppendLine("Records:");
        int printedRecords = Math.Min(recordCount, MaxRecordsToPrint);
        for (int recordIndex = 0; recordIndex < printedRecords; recordIndex++)
        {
            int recordOffset = headerBytes + recordIndex * recordBytes;
            uint modelId = ReadUInt32(data, recordOffset);
            uint modelGroupId = ReadUInt32(data, recordOffset + (fieldsPerRecord - 1) * sizeof(uint));
            builder.AppendLine($"Record {recordIndex:N0} @ 0x{recordOffset:X4}, model id {modelId}, model group id {modelGroupId}:");
            AppendCompactRecord(builder, data, recordOffset, fieldsPerRecord);
        }

        if (recordCount > printedRecords)
        {
            builder.AppendLine($"... {recordCount - printedRecords:N0} more records omitted.");
        }

        AppendHexPreview(builder, data);

        document = new CrossFireScriptBinDocument(
            builder.ToString(),
            FormatStorage(compressed, "CrossFire WeaponModel BIN", source.Length, data.Length),
            source.Length,
            data.Length);
        return true;
    }

    private static bool DecodeEmptyWeaponModel(
        byte[] source,
        byte[] data,
        bool compressed,
        string fileName,
        out CrossFireScriptBinDocument? document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CrossFire UI script BIN");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine("Kind: WeaponModel table");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "WeaponModel BIN", source.Length, data.Length)}");
        builder.AppendLine($"Header/version: {ReadUInt32(data, 0)}");
        builder.AppendLine("Records: 0");

        document = new CrossFireScriptBinDocument(
            builder.ToString(),
            FormatStorage(compressed, "CrossFire WeaponModel BIN", source.Length, data.Length),
            source.Length,
            data.Length);
        return true;
    }

    private static void AppendCompactRecord(StringBuilder builder, byte[] data, int offset, int fieldCount)
    {
        for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
        {
            int fieldOffset = offset + fieldIndex * sizeof(uint);
            uint raw = ReadUInt32(data, fieldOffset);
            string floatText = FormatFloat(raw);
            builder.Append("  ");
            builder.Append(CultureInfo.InvariantCulture, $"[{fieldIndex:00}] 0x{fieldOffset:X4} ");
            builder.Append(CultureInfo.InvariantCulture, $"u32={raw,10} i32={unchecked((int)raw),11}");
            if (!string.IsNullOrEmpty(floatText))
            {
                builder.Append(CultureInfo.InvariantCulture, $" f32={floatText}");
            }

            builder.Append(CultureInfo.InvariantCulture, $" hex={FormatFieldHex(data, fieldOffset)}");
            builder.AppendLine();
        }
    }

    private static void AppendFieldTable(StringBuilder builder, byte[] data, int offset, int fieldCount, string title)
    {
        builder.AppendLine(title + ":");
        for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
        {
            int fieldOffset = offset + fieldIndex * sizeof(uint);
            uint raw = ReadUInt32(data, fieldOffset);
            string floatText = FormatFloat(raw);
            builder.Append(CultureInfo.InvariantCulture, $"[{fieldIndex:00}] @0x{fieldOffset:X4} ");
            builder.Append(CultureInfo.InvariantCulture, $"u32={raw,10} i32={unchecked((int)raw),11}");
            if (!string.IsNullOrEmpty(floatText))
            {
                builder.Append(CultureInfo.InvariantCulture, $" f32={floatText}");
            }

            builder.Append(CultureInfo.InvariantCulture, $" hex={FormatFieldHex(data, fieldOffset)}");
            builder.AppendLine();
        }
    }

    private static void AppendHexPreview(StringBuilder builder, byte[] data)
    {
        int count = Math.Min(data.Length, 160);
        builder.AppendLine();
        builder.AppendLine($"First {count:N0} bytes:");
        for (int offset = 0; offset < count; offset += 16)
        {
            int lineCount = Math.Min(16, count - offset);
            ReadOnlySpan<byte> bytes = data.AsSpan(offset, lineCount);
            string hex = string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            string ascii = new(bytes.ToArray().Select(value => value is >= 0x20 and <= 0x7E ? (char)value : '.').ToArray());
            builder.Append(CultureInfo.InvariantCulture, $"{offset:X4}: {hex.PadRight(47)}  {ascii}");
            builder.AppendLine();
        }
    }

    private static bool IsWeaponModelFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        return string.Equals(stem, "WeaponModel", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("WeaponModel", StringComparison.OrdinalIgnoreCase) &&
               stem["WeaponModel".Length..].All(char.IsDigit);
    }

    private static bool IsWeaponPreviewFileName(string fileName)
    {
        string normalizedPath = fileName.Replace('\\', '/');
        string name = Path.GetFileName(normalizedPath);
        if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalizedPath.Contains("/WeaponPreview/Skin/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        int dashIndex = stem.IndexOf('-', StringComparison.Ordinal);
        return dashIndex > 0 &&
               dashIndex < stem.Length - 1 &&
               stem[..dashIndex].All(char.IsDigit) &&
               stem[(dashIndex + 1)..].All(char.IsDigit);
    }

    private static bool TryParseWeaponPreviewName(string fileName, out int category, out int entryId)
    {
        category = -1;
        entryId = -1;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dashIndex = stem.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex <= 0 || dashIndex >= stem.Length - 1)
        {
            return false;
        }

        bool parsedCategory = int.TryParse(stem[..dashIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out category);
        bool parsedEntry = int.TryParse(stem[(dashIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out entryId);
        if (!parsedCategory)
        {
            category = -1;
        }

        if (!parsedEntry)
        {
            entryId = -1;
        }

        return parsedCategory && parsedEntry;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return offset + sizeof(uint) <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)))
            : 0;
    }

    private static string FormatFieldHex(byte[] data, int offset)
    {
        if (offset + sizeof(uint) > data.Length)
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            data.AsSpan(offset, sizeof(uint)).ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatFloat(uint raw)
    {
        if (raw == 0)
        {
            return "0";
        }

        float value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        if (!float.IsFinite(value) || Math.Abs(value) < 0.00001f || Math.Abs(value) > 1_000_000f)
        {
            return string.Empty;
        }

        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static string FormatStorage(bool compressed, string label, int sourceBytes, int decodedBytes)
    {
        return compressed
            ? $"LZMA-compressed {label}, {sourceBytes:N0} bytes -> {decodedBytes:N0} bytes"
            : $"{label}, {sourceBytes:N0} bytes";
    }
}
