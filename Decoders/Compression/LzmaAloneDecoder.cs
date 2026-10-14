using System.Buffers.Binary;
using System.IO;
using SharpCompress.Compressors.LZMA;

namespace CFRezManager;

internal static class LzmaAloneDecoder
{
    private const int HeaderLength = 13;
    private const int PropertiesLength = 5;
    public const long DefaultMaxDecodedBytes = 128 * 1024 * 1024;

    public static bool IsCompressed(byte[] data)
    {
        return TryReadHeader(data, out _, out _);
    }

    public static bool TryGetDecodedByteCount(byte[] data, out long decodedBytes)
    {
        decodedBytes = 0;
        if (!TryReadHeader(data, out decodedBytes, out bool hasKnownDecodedBytes) ||
            !hasKnownDecodedBytes)
        {
            return false;
        }

        return decodedBytes >= 0;
    }

    public static byte[]? TryPrepareData(byte[] data, long maxDecodedBytes = DefaultMaxDecodedBytes)
    {
        return IsCompressed(data) ? TryDecompress(data, maxDecodedBytes) : data;
    }

    public static byte[]? TryDecompressPrefix(byte[] data, int maxPrefixBytes)
    {
        if (maxPrefixBytes <= 0 ||
            !TryReadHeader(data, out long decodedBytes, out bool hasKnownDecodedBytes))
        {
            return null;
        }

        if (hasKnownDecodedBytes && decodedBytes <= maxPrefixBytes)
        {
            return TryDecompress(data, maxPrefixBytes);
        }

        return TryDecompressPrefix(data, maxPrefixBytes, hasKnownDecodedBytes ? decodedBytes : null);
    }

    private static byte[]? TryDecompressPrefix(byte[] data, int maxPrefixBytes, long? decodedBytes)
    {
        byte[] properties = data.AsSpan(0, PropertiesLength).ToArray();
        using var compressed = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
        return TryDecompressPrefix(properties, compressed, data.Length - HeaderLength, maxPrefixBytes, decodedBytes);
    }

    private static byte[]? TryDecompressPrefix(
        byte[] properties,
        Stream compressed,
        long compressedByteCount,
        int maxPrefixBytes,
        long? decodedBytes)
    {
        long compressedStartPosition = compressed.CanSeek ? compressed.Position : 0;
        byte[]? prefix = TryDecompressPrefixWithDeclaredSize(
            properties,
            compressed,
            compressedByteCount,
            maxPrefixBytes,
            decodedBytes);
        if (prefix is not null)
        {
            return prefix;
        }

        if (compressed.CanSeek)
        {
            compressed.Position = compressedStartPosition;
            return TryDecompressPrefixToStreamEnd(properties, compressed, maxPrefixBytes);
        }

        return null;
    }

    private static byte[]? TryDecompressPrefixWithDeclaredSize(
        byte[] properties,
        Stream compressed,
        long compressedByteCount,
        int maxPrefixBytes,
        long? decodedBytes)
    {
        try
        {
            using LzmaStream lzma = LzmaStream.Create(
                properties,
                compressed,
                compressedByteCount,
                decodedBytes ?? long.MaxValue,
                leaveOpen: true);

            byte[] buffer = new byte[maxPrefixBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = lzma.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total == 0)
            {
                return null;
            }

            if (total == buffer.Length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, total);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecompressPrefixToStreamEnd(byte[] properties, Stream compressed, int maxPrefixBytes)
    {
        try
        {
            using LzmaStream lzma = LzmaStream.Create(properties, compressed, leaveOpen: true);
            byte[] buffer = new byte[maxPrefixBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = lzma.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total == 0)
            {
                return null;
            }

            if (total == buffer.Length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, total);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? TryDecompressPrefix(Stream source, long compressedByteCount, int maxPrefixBytes)
    {
        if (maxPrefixBytes <= 0 || compressedByteCount < HeaderLength)
        {
            return null;
        }

        try
        {
            byte[] header = new byte[HeaderLength];
            source.ReadExactly(header);
            if (!TryReadHeader(header, out long decodedBytes, out bool hasKnownDecodedBytes))
            {
                return null;
            }

            if (hasKnownDecodedBytes && decodedBytes <= 0)
            {
                return null;
            }

            byte[] properties = header.AsSpan(0, PropertiesLength).ToArray();
            return TryDecompressPrefix(
                properties,
                source,
                compressedByteCount - HeaderLength,
                hasKnownDecodedBytes ? (int)Math.Min(maxPrefixBytes, decodedBytes) : maxPrefixBytes,
                hasKnownDecodedBytes ? decodedBytes : null);
        }
        catch
        {
            return null;
        }
    }

    public static Stream? TryCreateDecompressStream(Stream source, long compressedByteCount, out long decodedBytes)
    {
        decodedBytes = 0;
        if (compressedByteCount < HeaderLength)
        {
            return null;
        }

        try
        {
            byte[] header = new byte[HeaderLength];
            source.ReadExactly(header);
            if (!TryReadHeader(header, out decodedBytes, out bool hasKnownDecodedBytes) ||
                !hasKnownDecodedBytes ||
                decodedBytes <= 0)
            {
                return null;
            }

            byte[] properties = header.AsSpan(0, PropertiesLength).ToArray();
            return LzmaStream.Create(
                properties,
                source,
                compressedByteCount - HeaderLength,
                decodedBytes,
                leaveOpen: false);
        }
        catch
        {
            decodedBytes = 0;
            return null;
        }
    }

    private static byte[]? TryDecompress(byte[] data, long maxDecodedBytes)
    {
        if (!TryReadHeader(data, out long decodedBytes, out bool hasKnownDecodedBytes))
        {
            return null;
        }

        if (hasKnownDecodedBytes &&
            (decodedBytes <= 0 || decodedBytes > maxDecodedBytes || decodedBytes > int.MaxValue))
        {
            return null;
        }

        byte[] properties = data.AsSpan(0, PropertiesLength).ToArray();
        return TryDecompressWithDeclaredSize(
            data,
            properties,
            maxDecodedBytes,
            hasKnownDecodedBytes ? decodedBytes : null) ??
            TryDecompressToStreamEnd(data, properties, maxDecodedBytes, hasKnownDecodedBytes ? decodedBytes : null);
    }

    private static byte[]? TryDecompressWithDeclaredSize(
        byte[] data,
        byte[] properties,
        long maxDecodedBytes,
        long? decodedBytes)
    {
        try
        {
            using var compressed = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
            using LzmaStream lzma = LzmaStream.Create(
                properties,
                compressed,
                data.Length - HeaderLength,
                decodedBytes ?? long.MaxValue,
                leaveOpen: false);
            return CopyToMemory(lzma, maxDecodedBytes, decodedBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecompressToStreamEnd(
        byte[] data,
        byte[] properties,
        long maxDecodedBytes,
        long? decodedBytes)
    {
        try
        {
            using var compressed = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
            using LzmaStream lzma = LzmaStream.Create(properties, compressed, leaveOpen: false);
            return CopyToMemory(lzma, maxDecodedBytes, decodedBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? CopyToMemory(Stream source, long maxDecodedBytes, long? expectedByteCount)
    {
        using var decompressed = new MemoryStream(expectedByteCount is > 0 and <= int.MaxValue
            ? (int)expectedByteCount.Value
            : 0);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxDecodedBytes)
            {
                return null;
            }

            decompressed.Write(buffer, 0, read);
        }

        if (expectedByteCount is not null && total != expectedByteCount.Value)
        {
            return null;
        }

        return total == 0 ? null : decompressed.ToArray();
    }

    private static bool TryReadHeader(byte[] data, out long decodedBytes, out bool hasKnownDecodedBytes)
    {
        return TryReadHeader(data.AsSpan(), out decodedBytes, out hasKnownDecodedBytes);
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, out long decodedBytes, out bool hasKnownDecodedBytes)
    {
        decodedBytes = 0;
        hasKnownDecodedBytes = false;
        if (data.Length < HeaderLength || data[0] is not (0x5D or 0x08))
        {
            return false;
        }

        uint dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, sizeof(uint)));
        bool legacyHeaderShape = data[1] == 0 && data[2] == 0 && data[3] == 0;
        if (dictionarySize == 0 && !legacyHeaderShape)
        {
            return false;
        }

        long rawDecodedBytes = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(PropertiesLength, sizeof(long)));
        if (rawDecodedBytes >= 0)
        {
            if (rawDecodedBytes == 0 || rawDecodedBytes > int.MaxValue)
            {
                return false;
            }

            decodedBytes = rawDecodedBytes;
            hasKnownDecodedBytes = true;
            return true;
        }

        return rawDecodedBytes == -1;
    }
}
