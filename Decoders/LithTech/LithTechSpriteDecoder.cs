using System.Buffers.Binary;
using System.Text;

namespace CFRezManager;

public sealed record LithTechSpriteDocument(
    string Text,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount,
    int FrameCount,
    int FrameRate,
    IReadOnlyList<string> FramePaths);

internal static class LithTechSpriteDecoder
{
    private const int HeaderLength = 20;
    private const int ReservedOffset = 8;
    private const int ReservedLength = 12;
    private const int MaxFrameCount = 10_000;
    private const int MaxPathLength = 4096;

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "spr", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(
        byte[] data,
        string fileName,
        out LithTechSpriteDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            errorMessage = $"Unable to decompress SPR: {fileName}";
            return false;
        }

        if (prepared.Length < HeaderLength)
        {
            errorMessage = $"Invalid SPR header: {fileName}";
            return false;
        }

        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(prepared.AsSpan(0, sizeof(int)));
        int frameRate = BinaryPrimitives.ReadInt32LittleEndian(prepared.AsSpan(sizeof(int), sizeof(int)));
        if (frameCount <= 0 || frameCount > MaxFrameCount)
        {
            errorMessage = $"Invalid SPR frame count {frameCount}: {fileName}";
            return false;
        }

        if (frameRate <= 0)
        {
            errorMessage = $"Invalid SPR frame rate {frameRate}: {fileName}";
            return false;
        }

        var paths = new List<string>(frameCount);
        int cursor = HeaderLength;
        for (int i = 0; i < frameCount; i++)
        {
            if (!HasBytes(prepared, cursor, sizeof(ushort)))
            {
                errorMessage = $"Invalid SPR path table at frame {i}: {fileName}";
                return false;
            }

            int pathLength = BinaryPrimitives.ReadUInt16LittleEndian(prepared.AsSpan(cursor, sizeof(ushort)));
            cursor += sizeof(ushort);
            if (pathLength <= 0 || pathLength > MaxPathLength || !HasBytes(prepared, cursor, pathLength))
            {
                errorMessage = $"Invalid SPR path length {pathLength} at frame {i}: {fileName}";
                return false;
            }

            string path = Encoding.ASCII.GetString(prepared, cursor, pathLength).TrimEnd('\0');
            cursor += pathLength;
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            {
                errorMessage = $"Invalid SPR path at frame {i}: {fileName}";
                return false;
            }

            paths.Add(path);
        }

        foreach (byte trailingByte in prepared.AsSpan(cursor))
        {
            // Some native sprite tables are followed by a DOS line ending.
            // Keep rejecting arbitrary payload bytes, but accept this benign
            // text-file terminator after the declared frame table.
            if (trailingByte != 0 && trailingByte != (byte)'\r' && trailingByte != (byte)'\n')
            {
                errorMessage = $"Invalid SPR trailing data: {fileName}";
                return false;
            }
        }

        string storageDescription = LzmaAloneDecoder.IsCompressed(data)
            ? "LZMA-compressed SPR"
            : "Uncompressed SPR";
        string text = FormatDocument(fileName, storageDescription, data.Length, prepared.Length, frameCount, frameRate, prepared, paths);

        document = new LithTechSpriteDocument(
            text,
            storageDescription,
            data.Length,
            prepared.Length,
            frameCount,
            frameRate,
            paths);
        return true;
    }

    private static bool HasBytes(byte[] data, int offset, int byteCount)
    {
        return offset >= 0 && byteCount >= 0 && offset <= data.Length - byteCount;
    }

    private static string FormatDocument(
        string fileName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        int frameCount,
        int frameRate,
        byte[] prepared,
        IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LithTech SPR animation");
        builder.AppendLine();
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Storage: {storageDescription}");
        builder.AppendLine($"Source bytes: {sourceByteCount:N0}");
        builder.AppendLine($"Decoded bytes: {decodedByteCount:N0}");
        builder.AppendLine($"Frames: {frameCount:N0}");
        builder.AppendLine($"Frame rate: {frameRate:N0} fps");
        builder.AppendLine($"Duration: {frameCount / (double)frameRate:0.###} s");
        builder.AppendLine($"Reserved: {FormatReservedBytes(prepared)}");
        builder.AppendLine();
        builder.AppendLine("Frame paths:");

        int digits = Math.Max(4, (frameCount - 1).ToString().Length);
        for (int i = 0; i < paths.Count; i++)
        {
            builder.Append(i.ToString().PadLeft(digits, '0'));
            builder.Append("  ");
            builder.AppendLine(paths[i]);
        }

        return builder.ToString();
    }

    private static string FormatReservedBytes(byte[] prepared)
    {
        return Convert.ToHexString(prepared.AsSpan(ReservedOffset, ReservedLength));
    }
}
