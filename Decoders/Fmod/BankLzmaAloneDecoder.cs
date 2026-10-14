using System.Buffers.Binary;
using System.IO;
using SharpCompress.Compressors.LZMA;

namespace CFRezManager;

internal static class BankLzmaAloneDecoder
{
    private const int HeaderLength = 13;
    private const int PropertiesLength = 5;

    public static bool IsCompressed(byte[] data)
    {
        return data.Length >= HeaderLength &&
               data[0] == 0x5D &&
               data[1] == 0 &&
               data[2] == 0 &&
               data[3] == 0;
    }

    public static bool TryGetDecodedByteCount(byte[] data, out long decodedBytes)
    {
        decodedBytes = 0;
        if (!IsCompressed(data))
        {
            return false;
        }

        decodedBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(PropertiesLength, sizeof(long)));
        return decodedBytes >= 0;
    }

    public static byte[]? TryPrepareData(byte[] data, long maxDecodedBytes)
    {
        return IsCompressed(data) ? TryDecompress(data, maxDecodedBytes) : data;
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
            if (!TryGetDecodedByteCount(header, out long decodedBytes) || decodedBytes <= 0)
            {
                return null;
            }

            byte[] properties = header.AsSpan(0, PropertiesLength).ToArray();
            using LzmaStream lzma = LzmaStream.Create(
                properties,
                source,
                compressedByteCount - HeaderLength,
                decodedBytes,
                leaveOpen: true);

            byte[] buffer = new byte[(int)Math.Min(maxPrefixBytes, decodedBytes)];
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
            if (!TryGetDecodedByteCount(header, out decodedBytes) || decodedBytes <= 0)
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
        try
        {
            long decodedBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(PropertiesLength, sizeof(long)));
            if (decodedBytes <= 0 || decodedBytes > maxDecodedBytes || decodedBytes > int.MaxValue)
            {
                return null;
            }

            byte[] properties = data.AsSpan(0, PropertiesLength).ToArray();
            using var compressed = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
            using LzmaStream lzma = LzmaStream.Create(
                properties,
                compressed,
                data.Length - HeaderLength,
                decodedBytes,
                leaveOpen: false);
            using var decompressed = new MemoryStream((int)decodedBytes);
            lzma.CopyTo(decompressed);
            return decompressed.Length == decodedBytes ? decompressed.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
