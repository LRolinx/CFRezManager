using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class DtxThumbnailDecoder
{
    private const int DtxVersionLt1 = -2;
    private const int DtxVersionLt15 = -3;
    private const int DtxVersionLt2 = -5;
    private const int CommandStringLength = 128;
    private const int BytesPerPixel8Palette = 0;
    private const int BytesPerPixel32 = 3;
    private const int BytesPerPixelDxt1 = 4;
    private const int BytesPerPixelDxt3 = 5;
    private const int BytesPerPixelDxt5 = 6;
    private const int BytesPerPixel32Palette = 7;
    private const uint FormatRgba = 136;
    private const int MaxDecodedPixels = 4096 * 4096;
    private const int ThumbnailMaxSide = 192;

    private enum DtxPixelFormat
    {
        Unknown,
        Bgra32,
        Rgba32,
        Palette8,
        Dxt1,
        Dxt3,
        Dxt5
    }

    private readonly record struct DtxHeader(
        int Version,
        int Width,
        int Height,
        int MipmapCount,
        uint Flags,
        int TextureGroup,
        int BytesPerPixel,
        int DataOffset);

    public static ImageSource? TryDecode(byte[] data)
    {
        return TryDecode(data, ThumbnailMaxSide);
    }

    public static ImageSource? TryDecodeOriginal(byte[] data)
    {
        return TryDecode(data, maxSide: null);
    }

    public static bool IsLzmaCompressed(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data);
    }

    private static ImageSource? TryDecode(byte[] data, int? maxSide)
    {
        byte[]? dtxData = LzmaAloneDecoder.TryPrepareData(data);
        if (dtxData is null ||
            !TryReadHeader(dtxData, out DtxHeader header) ||
            !IsSafeImageSize(header.Width, header.Height))
        {
            return null;
        }

        DtxPixelFormat format = ResolvePixelFormat(header);
        byte[]? pixels = format switch
        {
            DtxPixelFormat.Bgra32 => DecodeBgra32(dtxData, header, preserveAlpha: true),
            DtxPixelFormat.Rgba32 => DecodeRgba32(dtxData, header),
            DtxPixelFormat.Palette8 => DecodePalette8(dtxData, header),
            DtxPixelFormat.Dxt1 => DecodeDxt(dtxData, header, DtxPixelFormat.Dxt1),
            DtxPixelFormat.Dxt3 => DecodeDxt(dtxData, header, DtxPixelFormat.Dxt3),
            DtxPixelFormat.Dxt5 => DecodeDxt(dtxData, header, DtxPixelFormat.Dxt5),
            _ => null
        };

        return pixels is null ? null : CreateImage(header.Width, header.Height, pixels, maxSide);
    }

    private static bool TryReadHeader(byte[] data, out DtxHeader header)
    {
        header = default;
        if (data.Length < 32)
        {
            return false;
        }

        int cursor;
        int first = ReadInt32(data, 0);
        int version;
        if (first == 0 && data.Length >= 36 && IsSupportedVersion(ReadInt32(data, 4)))
        {
            version = ReadInt32(data, 4);
            cursor = 8;
        }
        else if (IsSupportedVersion(first))
        {
            version = first;
            cursor = 4;
        }
        else
        {
            return false;
        }

        if (data.Length < cursor + 28)
        {
            return false;
        }

        int width = ReadUInt16(data, cursor);
        cursor += 2;
        int height = ReadUInt16(data, cursor);
        cursor += 2;
        int mipmapCount = ReadUInt16(data, cursor);
        cursor += 2;
        cursor += 2; // section count
        uint flags = ReadUInt32(data, cursor);
        cursor += 4;
        cursor += 4; // user flags
        int textureGroup = data[cursor++];
        cursor++; // mipmaps to use
        int bytesPerPixel = data[cursor++];
        cursor++; // mipmap offset
        cursor++; // mipmap texture coordinate offset
        cursor++; // texture priority
        cursor += 4; // detail texture scale
        cursor += 2; // detail texture angle

        if (version is DtxVersionLt15 or DtxVersionLt2)
        {
            cursor += CommandStringLength;
        }

        if (width <= 0 ||
            height <= 0 ||
            mipmapCount < 0 ||
            cursor < 0 ||
            cursor >= data.Length)
        {
            return false;
        }

        header = new DtxHeader(version, width, height, mipmapCount, flags, textureGroup, bytesPerPixel, cursor);
        return true;
    }

    private static DtxPixelFormat ResolvePixelFormat(DtxHeader header)
    {
        if (header.Version is DtxVersionLt1 or DtxVersionLt15 ||
            header.BytesPerPixel == BytesPerPixel8Palette)
        {
            return DtxPixelFormat.Palette8;
        }

        return header.BytesPerPixel switch
        {
            BytesPerPixel32 when header.Flags == FormatRgba => DtxPixelFormat.Rgba32,
            // LithTech 2.x files may store extended texture/property flags
            // here instead of the legacy RGBA marker. BPPIdent 3 is the
            // 32-bit ARGB format; on disk its byte order is BGRA.
            BytesPerPixel32 when header.TextureGroup == 0 => DtxPixelFormat.Bgra32,
            BytesPerPixelDxt1 => DtxPixelFormat.Dxt1,
            BytesPerPixelDxt3 => DtxPixelFormat.Dxt3,
            BytesPerPixelDxt5 => DtxPixelFormat.Dxt5,
            BytesPerPixel32Palette => DtxPixelFormat.Unknown,
            _ => DtxPixelFormat.Unknown
        };
    }

    private static byte[]? DecodeBgra32(byte[] data, DtxHeader header, bool preserveAlpha)
    {
        int byteCount = checked(header.Width * header.Height * 4);
        if (!HasBytes(data, header.DataOffset, byteCount))
        {
            return null;
        }

        var pixels = new byte[byteCount];
        data.AsSpan(header.DataOffset, byteCount).CopyTo(pixels);
        if (!preserveAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        return pixels;
    }

    private static byte[]? DecodeRgba32(byte[] data, DtxHeader header)
    {
        int byteCount = checked(header.Width * header.Height * 4);
        if (!HasBytes(data, header.DataOffset, byteCount))
        {
            return null;
        }

        var pixels = new byte[byteCount];
        int source = header.DataOffset;
        for (int target = 0; target < pixels.Length; target += 4)
        {
            byte r = data[source++];
            byte g = data[source++];
            byte b = data[source++];
            source++;
            pixels[target] = b;
            pixels[target + 1] = g;
            pixels[target + 2] = r;
            pixels[target + 3] = 0xFF;
        }

        return pixels;
    }

    private static byte[]? DecodePalette8(byte[] data, DtxHeader header)
    {
        const int paletteHeaderBytes = 8;
        const int paletteBytes = 256 * 4;
        int paletteOffset = header.DataOffset + paletteHeaderBytes;
        int pixelOffset = paletteOffset + paletteBytes;
        int pixelCount = checked(header.Width * header.Height);
        if (!HasBytes(data, paletteOffset, paletteBytes) ||
            !HasBytes(data, pixelOffset, pixelCount))
        {
            return null;
        }

        var palette = new byte[paletteBytes];
        for (int i = 0; i < 256; i++)
        {
            int source = paletteOffset + (i * 4);
            int target = i * 4;
            byte a = data[source];
            byte r = data[source + 1];
            byte g = data[source + 2];
            byte b = data[source + 3];
            palette[target] = b;
            palette[target + 1] = g;
            palette[target + 2] = r;
            palette[target + 3] = a;
        }

        var pixels = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int paletteIndex = data[pixelOffset + i] * 4;
            int target = i * 4;
            pixels[target] = palette[paletteIndex];
            pixels[target + 1] = palette[paletteIndex + 1];
            pixels[target + 2] = palette[paletteIndex + 2];
            pixels[target + 3] = palette[paletteIndex + 3];
        }

        return pixels;
    }

    private static byte[]? DecodeDxt(byte[] data, DtxHeader header, DtxPixelFormat format)
    {
        int blockBytes = format == DtxPixelFormat.Dxt1 ? 8 : 16;
        int compressedBytes = GetDxtLevelByteCount(header.Width, header.Height, blockBytes);
        if (!HasBytes(data, header.DataOffset, compressedBytes))
        {
            return null;
        }

        int blocksX = (header.Width + 3) / 4;
        int blocksY = (header.Height + 3) / 4;
        var pixels = new byte[checked(header.Width * header.Height * 4)];
        int source = header.DataOffset;
        for (int blockY = 0; blockY < blocksY; blockY++)
        {
            for (int blockX = 0; blockX < blocksX; blockX++)
            {
                switch (format)
                {
                    case DtxPixelFormat.Dxt1:
                        DecodeDxt1Block(data, source, pixels, header.Width, header.Height, blockX, blockY);
                        break;
                    case DtxPixelFormat.Dxt3:
                        DecodeDxt3Block(data, source, pixels, header.Width, header.Height, blockX, blockY);
                        break;
                    case DtxPixelFormat.Dxt5:
                        DecodeDxt5Block(data, source, pixels, header.Width, header.Height, blockX, blockY);
                        break;
                }

                source += blockBytes;
            }
        }

        return pixels;
    }

    private static void DecodeDxt1Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        DecodeColorBlock(data, source, pixels, width, height, blockX, blockY, ReadOnlySpan<byte>.Empty, useDxt1Alpha: true);
    }

    private static void DecodeDxt3Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        Span<byte> alpha = stackalloc byte[16];
        ulong alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(source, 8));
        for (int i = 0; i < 16; i++)
        {
            alpha[i] = (byte)(((alphaBits >> (i * 4)) & 0xF) * 17);
        }

        DecodeColorBlock(data, source + 8, pixels, width, height, blockX, blockY, alpha, useDxt1Alpha: false);
    }

    private static void DecodeDxt5Block(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY)
    {
        Span<byte> palette = stackalloc byte[8];
        palette[0] = data[source];
        palette[1] = data[source + 1];
        if (palette[0] > palette[1])
        {
            palette[2] = (byte)((6 * palette[0] + palette[1]) / 7);
            palette[3] = (byte)((5 * palette[0] + 2 * palette[1]) / 7);
            palette[4] = (byte)((4 * palette[0] + 3 * palette[1]) / 7);
            palette[5] = (byte)((3 * palette[0] + 4 * palette[1]) / 7);
            palette[6] = (byte)((2 * palette[0] + 5 * palette[1]) / 7);
            palette[7] = (byte)((palette[0] + 6 * palette[1]) / 7);
        }
        else
        {
            palette[2] = (byte)((4 * palette[0] + palette[1]) / 5);
            palette[3] = (byte)((3 * palette[0] + 2 * palette[1]) / 5);
            palette[4] = (byte)((2 * palette[0] + 3 * palette[1]) / 5);
            palette[5] = (byte)((palette[0] + 4 * palette[1]) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong alphaBits = 0;
        for (int i = 0; i < 6; i++)
        {
            alphaBits |= (ulong)data[source + 2 + i] << (8 * i);
        }

        Span<byte> alpha = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            alpha[i] = palette[(int)((alphaBits >> (i * 3)) & 0x7)];
        }

        DecodeColorBlock(data, source + 8, pixels, width, height, blockX, blockY, alpha, useDxt1Alpha: false);
    }

    private static void DecodeColorBlock(
        byte[] data,
        int source,
        byte[] pixels,
        int width,
        int height,
        int blockX,
        int blockY,
        ReadOnlySpan<byte> alpha,
        bool useDxt1Alpha)
    {
        ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2));
        ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source + 2, 2));
        Span<byte> colors = stackalloc byte[16];
        WriteRgb565(colors, 0, color0, 255);
        WriteRgb565(colors, 4, color1, 255);
        if (color0 > color1 || !useDxt1Alpha)
        {
            InterpolateColor(colors, 8, colors, 0, 2, colors, 4, 1, 3, 255);
            InterpolateColor(colors, 12, colors, 0, 1, colors, 4, 2, 3, 255);
        }
        else
        {
            InterpolateColor(colors, 8, colors, 0, 1, colors, 4, 1, 2, 255);
            colors[12] = 0;
            colors[13] = 0;
            colors[14] = 0;
            colors[15] = 0;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source + 4, 4));
        for (int y = 0; y < 4; y++)
        {
            int targetY = (blockY * 4) + y;
            if (targetY >= height)
            {
                continue;
            }

            for (int x = 0; x < 4; x++)
            {
                int targetX = (blockX * 4) + x;
                if (targetX >= width)
                {
                    continue;
                }

                int pixelIndex = (y * 4) + x;
                int colorIndex = (int)((indices >> (pixelIndex * 2)) & 0x3) * 4;
                int target = ((targetY * width) + targetX) * 4;
                pixels[target] = colors[colorIndex];
                pixels[target + 1] = colors[colorIndex + 1];
                pixels[target + 2] = colors[colorIndex + 2];
                pixels[target + 3] = alpha.IsEmpty ? colors[colorIndex + 3] : alpha[pixelIndex];
            }
        }
    }

    private static void WriteRgb565(Span<byte> target, int offset, ushort color, byte alpha)
    {
        byte r = (byte)((((color >> 11) & 0x1F) * 255) / 31);
        byte g = (byte)((((color >> 5) & 0x3F) * 255) / 63);
        byte b = (byte)(((color & 0x1F) * 255) / 31);
        target[offset] = b;
        target[offset + 1] = g;
        target[offset + 2] = r;
        target[offset + 3] = alpha;
    }

    private static void InterpolateColor(
        Span<byte> target,
        int targetOffset,
        ReadOnlySpan<byte> left,
        int leftOffset,
        int leftWeight,
        ReadOnlySpan<byte> right,
        int rightOffset,
        int rightWeight,
        int divisor,
        byte alpha)
    {
        target[targetOffset] = (byte)((left[leftOffset] * leftWeight + right[rightOffset] * rightWeight) / divisor);
        target[targetOffset + 1] = (byte)((left[leftOffset + 1] * leftWeight + right[rightOffset + 1] * rightWeight) / divisor);
        target[targetOffset + 2] = (byte)((left[leftOffset + 2] * leftWeight + right[rightOffset + 2] * rightWeight) / divisor);
        target[targetOffset + 3] = alpha;
    }

    private static ImageSource CreateImage(int width, int height, byte[] pixels, int? maxSide)
    {
        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        source.Freeze();

        if (maxSide is null)
        {
            return source;
        }

        double scale = Math.Min(1.0, (double)maxSide.Value / Math.Max(width, height));
        if (scale >= 1.0)
        {
            return source;
        }

        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static bool IsSupportedVersion(int version)
    {
        return version is DtxVersionLt1 or DtxVersionLt15 or DtxVersionLt2;
    }

    private static bool IsSafeImageSize(int width, int height)
    {
        return width > 0 &&
               height > 0 &&
               width <= 16384 &&
               height <= 16384 &&
               (long)width * height <= MaxDecodedPixels;
    }

    private static int GetDxtLevelByteCount(int width, int height, int blockBytes)
    {
        return checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
    }

    private static bool HasBytes(byte[] data, int offset, int byteCount)
    {
        return offset >= 0 &&
               byteCount >= 0 &&
               offset <= data.Length &&
               byteCount <= data.Length - offset;
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }
}
