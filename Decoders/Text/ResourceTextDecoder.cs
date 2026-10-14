using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record ResourceTextDocument(
    string Text,
    string Description,
    int SourceByteCount,
    int DecodedByteCount);

internal static class ResourceTextDecoder
{
    private const int TextPrefixBytes = 4 * 1024 * 1024;
    private const int BinaryPrefixBytes = 1024 * 1024;
    private const int WaveHeaderBytes = 64 * 1024;
    private const int CftPrefixBytes = 1024 * 1024;
    private const int FixedCftStringBytes = 64;
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "apf",
        "cft",
        "fcf",
        "fxf",
        "fxo",
        "lto",
        "nav",
        "nav2",
        "nav3",
        "nav4",
        "nav5",
        "nav6",
        "ref",
        "txt",
        "wav",
        "wave"
    };

    public static bool IsCandidate(string fileName, string extension)
    {
        return Extensions.Contains(NormalizeExtension(extension)) ||
               LithTechRenderStyleDecoder.IsCandidate(fileName, extension) ||
               IsNavExtension(Path.GetExtension(fileName).TrimStart('.'));
    }

    public static bool TryDecode(
        byte[] data,
        string fileName,
        string extension,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        string normalizedExtension = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalizedExtension))
        {
            normalizedExtension = NormalizeExtension(Path.GetExtension(fileName).TrimStart('.'));
        }

        try
        {
            if (string.Equals(normalizedExtension, "lto", StringComparison.OrdinalIgnoreCase))
            {
                if (!CrossFireObjectLtoDecoder.TryDecode(data, fileName, out string objectText, out errorMessage))
                {
                    return false;
                }

                document = new ResourceTextDocument(
                    objectText,
                    "CrossFire compiled object resource",
                    data.Length,
                    data.Length);
                return true;
            }

            if (LithTechRenderStyleDecoder.IsCandidate(fileName, normalizedExtension))
            {
                byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
                if (prepared is null)
                {
                    errorMessage = "RenderStyle outer LZMA stream could not be decoded.";
                    return false;
                }

                string storageDescription = ReferenceEquals(prepared, data)
                    ? "Packed LithTech RenderStyle"
                    : "LZMA-compressed packed LithTech RenderStyle";
                if (LithTechRenderStyleDecoder.TryDecode(
                        prepared,
                        fileName,
                        storageDescription,
                        data.Length,
                        out LithTechRenderStyleDocument? renderStyle,
                        out errorMessage) &&
                    renderStyle is not null)
                {
                    document = new ResourceTextDocument(
                        renderStyle.ToText(fileName),
                        $"{storageDescription} / {renderStyle.Styles.Count} styles",
                        data.Length,
                        prepared.Length);
                    return true;
                }

                if (!LithTechRenderStyleDecoder.TryDecodeShaderAssemblyExtension(
                        prepared,
                        fileName,
                        storageDescription,
                        data.Length,
                        out LithTechRenderStyleShaderExtensionDocument? shaderExtension,
                        out string? extensionError) ||
                    shaderExtension is null)
                {
                    errorMessage = string.IsNullOrWhiteSpace(extensionError) ? errorMessage : extensionError;
                    return false;
                }

                errorMessage = null;
                document = new ResourceTextDocument(
                    shaderExtension.Text,
                    $"{storageDescription} / D3DX8 Shader Assembly extension ({shaderExtension.ShaderAssemblyBlockCount} blocks)",
                    data.Length,
                    prepared.Length);
                return true;
            }

            if (string.Equals(normalizedExtension, "cft", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeCft(data, fileName, out document, out errorMessage);
            }

            if (string.Equals(normalizedExtension, "wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedExtension, "wave", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeWave(data, fileName, out document, out errorMessage);
            }

            if (IsNavExtension(normalizedExtension))
            {
                return TryDecodeNav(data, fileName, normalizedExtension, out document, out errorMessage);
            }

            if (string.Equals(normalizedExtension, "fxo", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeFxo(data, fileName, out document, out errorMessage);
            }

            if (string.Equals(normalizedExtension, "fxf", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeFxf(data, fileName, out document, out errorMessage);
            }

            if (string.Equals(normalizedExtension, "apf", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeBinaryResource(data, fileName, "APF path data", out document, out errorMessage);
            }

            if (string.Equals(normalizedExtension, "ref", StringComparison.OrdinalIgnoreCase))
            {
                return TryDecodeBinaryResource(data, fileName, "REF reference data", out document, out errorMessage);
            }

            return TryDecodeGenericText(data, fileName, normalizedExtension, out document, out errorMessage);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or OverflowException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryDecodeCft(
        byte[] data,
        string fileName,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryPreparePrefix(data, CftPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "CFT compression could not be decoded.";
            return false;
        }

        byte[] decoded = Xor(prepared, 0x10);
        if (decoded.Length < 14 || decoded[0] != (byte)'s' || decoded[1] != (byte)'u')
        {
            return TryDecodeGenericText(data, fileName, "cft", out document, out errorMessage);
        }

        int columnCount = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(6, sizeof(int)));
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(10, sizeof(int)));
        if (columnCount < 0 || columnCount > 4096 || rowCount < 0)
        {
            errorMessage = "CFT header contains invalid table dimensions.";
            return false;
        }

        int columnStart = 14;
        int columnsByteCount = checked(columnCount * FixedCftStringBytes);
        var columns = new List<string>(columnCount);
        if (decoded.Length >= columnStart + columnsByteCount)
        {
            for (int i = 0; i < columnCount; i++)
            {
                int offset = columnStart + i * FixedCftStringBytes;
                string column = ReadFixedText(decoded.AsSpan(offset, FixedCftStringBytes));
                columns.Add(string.IsNullOrWhiteSpace(column) ? $"Column {i + 1}" : column);
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("CrossFire CFT table");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "CFT", data.Length, decodedBytes, prepared.Length)}");
        builder.AppendLine($"Columns: {columnCount:N0}");
        builder.AppendLine($"Rows: {rowCount:N0}");

        if (columns.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Column names:");
            foreach (string column in columns.Take(128))
            {
                builder.AppendLine($"- {column}");
            }

            if (columns.Count > 128)
            {
                builder.AppendLine($"- ... {columns.Count - 128:N0} more");
            }
        }

        List<string> visibleStrings = ExtractAsciiStrings(decoded, minLength: 3, maxCount: 32)
            .Where(value => !columns.Contains(value, StringComparer.OrdinalIgnoreCase) &&
                            !string.Equals(value, "END", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (visibleStrings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Visible strings:");
            foreach (string value in visibleStrings)
            {
                builder.AppendLine($"- {value}");
            }
        }

        AppendTruncationNote(builder, decodedBytes, prepared.Length);
        document = new ResourceTextDocument(builder.ToString(), "CFT / LZMA / XOR 0x10", data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeWave(
        byte[] data,
        string fileName,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length == 0)
        {
            document = new ResourceTextDocument(
                $"Empty WAVE file{Environment.NewLine}File: {fileName}",
                "WAVE",
                0,
                0);
            return true;
        }

        if (!TryPreparePrefix(data, WaveHeaderBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "WAVE compression could not be decoded.";
            return false;
        }

        if (prepared.Length < 12 ||
            !prepared.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !prepared.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            errorMessage = "WAVE data does not contain a RIFF/WAVE header.";
            return false;
        }

        ushort? audioFormat = null;
        ushort? channels = null;
        uint? sampleRate = null;
        uint? byteRate = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        uint? dataByteCount = null;
        var chunks = new List<string>();

        int offset = 12;
        while (offset + 8 <= prepared.Length)
        {
            string chunkId = Latin1.GetString(prepared, offset, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(offset + 4, sizeof(uint)));
            int chunkDataOffset = offset + 8;
            chunks.Add($"{chunkId.TrimEnd('\0', ' ')} ({chunkSize:N0} bytes)");

            if (chunkId == "fmt " && chunkSize >= 16 && chunkDataOffset + 16 <= prepared.Length)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(prepared.AsSpan(chunkDataOffset, sizeof(ushort)));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(prepared.AsSpan(chunkDataOffset + 2, sizeof(ushort)));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(chunkDataOffset + 4, sizeof(uint)));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(chunkDataOffset + 8, sizeof(uint)));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(prepared.AsSpan(chunkDataOffset + 12, sizeof(ushort)));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(prepared.AsSpan(chunkDataOffset + 14, sizeof(ushort)));
            }
            else if (chunkId == "data")
            {
                dataByteCount = chunkSize;
            }

            long next = chunkDataOffset + (long)chunkSize;
            if ((chunkSize & 1) != 0)
            {
                next++;
            }

            if (next <= offset || next > int.MaxValue)
            {
                break;
            }

            offset = (int)next;
        }

        var builder = new StringBuilder();
        builder.AppendLine("RIFF WAVE audio");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "WAVE", data.Length, decodedBytes, prepared.Length)}");
        if (audioFormat is not null)
        {
            builder.AppendLine($"Format: {FormatWaveAudioFormat(audioFormat.Value)} ({audioFormat.Value})");
        }

        if (channels is not null)
        {
            builder.AppendLine($"Channels: {channels.Value:N0}");
        }

        if (sampleRate is not null)
        {
            builder.AppendLine($"Sample rate: {sampleRate.Value:N0} Hz");
        }

        if (bitsPerSample is not null)
        {
            builder.AppendLine($"Bits per sample: {bitsPerSample.Value:N0}");
        }

        if (blockAlign is not null)
        {
            builder.AppendLine($"Block align: {blockAlign.Value:N0} bytes");
        }

        if (dataByteCount is not null)
        {
            builder.AppendLine($"Audio data: {dataByteCount.Value:N0} bytes");
        }

        if (byteRate is > 0 && dataByteCount is not null)
        {
            double seconds = dataByteCount.Value / (double)byteRate.Value;
            builder.AppendLine($"Duration: {FormatDuration(seconds)}");
        }

        if (chunks.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Chunks:");
            foreach (string chunk in chunks.Take(24))
            {
                builder.AppendLine($"- {chunk}");
            }
        }

        AppendTruncationNote(builder, decodedBytes, prepared.Length);
        document = new ResourceTextDocument(builder.ToString(), FormatStorage(compressed, "WAVE", data.Length, decodedBytes, prepared.Length), data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeNav(
        byte[] data,
        string fileName,
        string extension,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryPreparePrefix(data, BinaryPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "NAV compression could not be decoded.";
            return false;
        }

        bool currentHeader = prepared.Length >= 16 && prepared.AsSpan(12, 4).SequenceEqual("VAND"u8);
        bool legacyHeader = prepared.Length >= 12 && prepared.AsSpan(4, 4).SequenceEqual("VAND"u8);
        if (!currentHeader && !legacyHeader)
        {
            errorMessage = "NAV data does not contain a VAND signature.";
            return false;
        }

        int version = legacyHeader ? ReadInt32(prepared, 8) : ReadInt32(prepared, 0);
        int layerMask = legacyHeader ? 0 : ReadInt32(prepared, 4);
        int declaredByteCount = legacyHeader ? ReadInt32(prepared, 0) : ReadInt32(prepared, 8);
        int vandVersion = legacyHeader
            ? version
            : prepared.Length >= 20 ? ReadInt32(prepared, 16) : 0;
        int[] headerInts = ReadInt32Values(prepared, 0, Math.Min(prepared.Length, 96));
        List<string> strings = ExtractAsciiStrings(prepared, minLength: 4, maxCount: 16);

        var builder = new StringBuilder();
        builder.AppendLine("LithTech VAND navigation data");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Extension layer: {extension.ToUpperInvariant()}");
        builder.AppendLine($"Header layout: {(legacyHeader ? "legacy VAND@4" : "VAND@12")}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "NAV", data.Length, decodedBytes, prepared.Length)}");
        builder.AppendLine($"Version: {version}");
        if (!legacyHeader)
        {
            builder.AppendLine($"Layer mask: {layerMask}");
        }

        builder.AppendLine($"Declared {(legacyHeader ? "file" : "payload")} bytes: {declaredByteCount:N0}");
        builder.AppendLine($"VAND version: {vandVersion}");
        builder.AppendLine();
        builder.AppendLine("Header int32 values:");
        builder.AppendLine(string.Join(", ", headerInts.Select(value => value.ToString(CultureInfo.InvariantCulture))));

        if (strings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Embedded strings:");
            foreach (string value in strings)
            {
                builder.AppendLine($"- {value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("First bytes:");
        builder.AppendLine(FormatHexDump(prepared, 0, Math.Min(prepared.Length, 128)));
        AppendTruncationNote(builder, decodedBytes, prepared.Length);

        document = new ResourceTextDocument(builder.ToString(), FormatStorage(compressed, "NAV/VAND", data.Length, decodedBytes, prepared.Length), data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeFxo(
        byte[] data,
        string fileName,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryPreparePrefix(data, BinaryPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "FXO compression could not be decoded.";
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Compiled FXO shader");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "FXO", data.Length, decodedBytes, prepared.Length)}");
        if (prepared.Length >= 8)
        {
            builder.AppendLine($"Header: 0x{ReadUInt32(prepared, 0):X8}");
            builder.AppendLine($"Declared body bytes: {ReadUInt32(prepared, 4):N0}");
        }

        AppendBinaryDetails(builder, prepared);
        AppendTruncationNote(builder, decodedBytes, prepared.Length);
        document = new ResourceTextDocument(builder.ToString(), FormatStorage(compressed, "FXO", data.Length, decodedBytes, prepared.Length), data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeFxf(
        byte[] data,
        string fileName,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryPreparePrefix(data, BinaryPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "FXF compression could not be decoded.";
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine("ClientFX binary catalog");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, "FXF", data.Length, decodedBytes, prepared.Length)}");
        if (prepared.Length >= 8)
        {
            builder.AppendLine($"Entry count: {ReadInt32(prepared, 0):N0}");
            builder.AppendLine($"Version: {ReadInt32(prepared, 4):N0}");
        }

        List<string> strings = ExtractAsciiStrings(prepared, minLength: 4, maxCount: 96);
        if (strings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Names and class strings:");
            foreach (string value in strings)
            {
                builder.AppendLine($"- {value}");
            }
        }

        AppendTruncationNote(builder, decodedBytes, prepared.Length);
        document = new ResourceTextDocument(builder.ToString(), FormatStorage(compressed, "FXF", data.Length, decodedBytes, prepared.Length), data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeBinaryResource(
        byte[] data,
        string fileName,
        string label,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length == 0)
        {
            document = new ResourceTextDocument($"{label}{Environment.NewLine}File: {fileName}{Environment.NewLine}Empty file", label, 0, 0);
            return true;
        }

        if (!TryPreparePrefix(data, BinaryPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = $"{label} compression could not be decoded.";
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendLine(label);
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {FormatStorage(compressed, label, data.Length, decodedBytes, prepared.Length)}");

        if (prepared.Length == 4 && prepared.All(value => value == 0xFF))
        {
            builder.AppendLine("Sentinel: 0xFFFFFFFF");
        }
        else
        {
            AppendBinaryDetails(builder, prepared);
        }

        AppendTruncationNote(builder, decodedBytes, prepared.Length);
        document = new ResourceTextDocument(builder.ToString(), FormatStorage(compressed, label, data.Length, decodedBytes, prepared.Length), data.Length, ToDecodedCount(decodedBytes, prepared.Length));
        return true;
    }

    private static bool TryDecodeGenericText(
        byte[] data,
        string fileName,
        string extension,
        out ResourceTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length == 0)
        {
            document = new ResourceTextDocument(string.Empty, "Empty text", 0, 0);
            return true;
        }

        if (!TryPreparePrefix(data, TextPrefixBytes, out byte[]? prepared, out bool compressed, out long decodedBytes) ||
            prepared is null)
        {
            errorMessage = "Resource compression could not be decoded.";
            return false;
        }

        if (TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string text, out string encoding))
        {
            if (decodedBytes > prepared.Length)
            {
                text += $"{Environment.NewLine}{Environment.NewLine}[Preview truncated after {prepared.Length:N0} of {decodedBytes:N0} decoded bytes.]";
            }

            string label = string.IsNullOrWhiteSpace(extension)
                ? "Text"
                : extension.ToUpperInvariant();
            string description = compressed ? $"LZMA-compressed {label} / {encoding}" : $"{label} / {encoding}";
            document = new ResourceTextDocument(text, description, data.Length, ToDecodedCount(decodedBytes, prepared.Length));
            return true;
        }

        return TryDecodeBinaryResource(data, fileName, $"{extension.ToUpperInvariant()} binary resource", out document, out errorMessage);
    }

    private static void AppendBinaryDetails(StringBuilder builder, byte[] data)
    {
        int intByteCount = Math.Min(data.Length - data.Length % 4, 64);
        if (intByteCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Initial int32 values:");
            builder.AppendLine(string.Join(", ", ReadInt32Values(data, 0, intByteCount).Select(value => value.ToString(CultureInfo.InvariantCulture))));
        }

        List<string> strings = ExtractAsciiStrings(data, minLength: 4, maxCount: 48);
        if (strings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Embedded strings:");
            foreach (string value in strings)
            {
                builder.AppendLine($"- {value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("First bytes:");
        builder.AppendLine(FormatHexDump(data, 0, Math.Min(data.Length, 128)));
    }

    private static bool TryPreparePrefix(
        byte[] data,
        int maxPrefixBytes,
        out byte[]? prepared,
        out bool compressed,
        out long decodedBytes)
    {
        prepared = null;
        compressed = LzmaAloneDecoder.IsCompressed(data);
        decodedBytes = data.Length;

        if (!compressed)
        {
            prepared = data.Length <= maxPrefixBytes
                ? data
                : data.AsSpan(0, maxPrefixBytes).ToArray();
            return true;
        }

        if (!LzmaAloneDecoder.TryGetDecodedByteCount(data, out decodedBytes))
        {
            return false;
        }

        prepared = LzmaAloneDecoder.TryDecompressPrefix(data, maxPrefixBytes);
        return prepared is not null;
    }

    private static byte[] Xor(byte[] data, byte key)
    {
        byte[] decoded = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            decoded[i] = (byte)(data[i] ^ key);
        }

        return decoded;
    }

    private static string ReadFixedText(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        string text = Latin1.GetString(bytes[..length]).Trim();
        return new string(text.Where(ch => !char.IsControl(ch)).ToArray());
    }

    private static List<string> ExtractAsciiStrings(byte[] data, int minLength, int maxCount)
    {
        var strings = new List<string>();
        var current = new StringBuilder();

        foreach (byte value in data)
        {
            if (value is >= 32 and <= 126)
            {
                current.Append((char)value);
                continue;
            }

            FlushCurrent();
            if (strings.Count >= maxCount)
            {
                break;
            }
        }

        FlushCurrent();
        return strings
            .Where(value => value.Length >= minLength)
            .Select(value => value.Trim())
            .Where(value => value.Length >= minLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

        void FlushCurrent()
        {
            if (current.Length >= minLength)
            {
                strings.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private static int[] ReadInt32Values(byte[] data, int offset, int byteCount)
    {
        int count = Math.Max(0, Math.Min(byteCount, data.Length - offset) / sizeof(int));
        int[] values = new int[count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ReadInt32(data, offset + i * sizeof(int));
        }

        return values;
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        return offset + sizeof(int) <= data.Length
            ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)))
            : 0;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return offset + sizeof(uint) <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)))
            : 0;
    }

    private static string FormatHexDump(byte[] data, int offset, int byteCount)
    {
        int count = Math.Max(0, Math.Min(byteCount, data.Length - offset));
        var builder = new StringBuilder();
        for (int row = 0; row < count; row += 16)
        {
            int rowCount = Math.Min(16, count - row);
            ReadOnlySpan<byte> bytes = data.AsSpan(offset + row, rowCount);
            string hex = string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            string ascii = new(bytes.ToArray().Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray());
            builder.Append(CultureInfo.InvariantCulture, $"{offset + row:X8}  {hex.PadRight(47)}  {ascii}");
            if (row + 16 < count)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string FormatWaveAudioFormat(ushort value)
    {
        return value switch
        {
            1 => "PCM",
            2 => "ADPCM",
            3 => "IEEE float",
            6 => "A-law",
            7 => "mu-law",
            17 => "IMA ADPCM",
            85 => "MP3",
            65534 => "Extensible",
            _ => "Unknown"
        };
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "unknown";
        }

        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string FormatStorage(bool compressed, string label, int sourceBytes, long decodedBytes, int preparedBytes)
    {
        if (!compressed)
        {
            return $"{label}, {sourceBytes:N0} bytes";
        }

        string suffix = decodedBytes > preparedBytes
            ? $", previewed {preparedBytes:N0}"
            : string.Empty;
        return $"LZMA-compressed {label}, {sourceBytes:N0} bytes -> {decodedBytes:N0} bytes{suffix}";
    }

    private static void AppendTruncationNote(StringBuilder builder, long decodedBytes, int preparedBytes)
    {
        if (decodedBytes > preparedBytes)
        {
            builder.AppendLine();
            builder.AppendLine($"Preview truncated after {preparedBytes:N0} of {decodedBytes:N0} decoded bytes.");
        }
    }

    private static int ToDecodedCount(long decodedBytes, int preparedBytes)
    {
        long count = decodedBytes > 0 ? decodedBytes : preparedBytes;
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static bool IsNavExtension(string extension)
    {
        return extension.Length >= 3 &&
               extension.StartsWith("nav", StringComparison.OrdinalIgnoreCase) &&
               extension[3..].All(char.IsDigit);
    }

    private static string NormalizeExtension(string extension)
    {
        return extension.Trim().TrimStart('.');
    }
}
