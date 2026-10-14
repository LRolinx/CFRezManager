using System.Text;

namespace CFRezManager;

public enum TextPreviewStorageKind
{
    Plain,
    EncBase64,
    EncLzmaBase64,
    LtcConverted,
    CrossFireDat,
    LithTechSprite,
    ResourceDecoded,
    FmodBank
}

public sealed record TextPreviewDocument(
    string Text,
    string EncodingName,
    TextPreviewStorageKind StorageKind,
    int SourceByteCount,
    int DecodedByteCount);

internal static class EncTextDecoder
{
    private const byte XorKey = 0x0B;
    private const long MaxLzmaDecodedBytes = 128L * 1024 * 1024;

    public static bool IsCandidate(string fileName, string extension)
    {
        return string.Equals(extension, "enc", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".enc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out byte[] decoded)
    {
        return TryDecode(source, out decoded, out _);
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out byte[] decoded, out bool lzmaWrapped)
    {
        decoded = [];
        lzmaWrapped = false;
        if (source.IsEmpty)
        {
            return false;
        }

        byte[] sourceBytes = source.ToArray();
        if (LzmaAloneDecoder.IsCompressed(sourceBytes))
        {
            byte[]? prepared = LzmaAloneDecoder.TryPrepareData(sourceBytes, MaxLzmaDecodedBytes);
            if (prepared is not null && !ReferenceEquals(prepared, sourceBytes) &&
                TryDecodeBase64(prepared, out decoded))
            {
                lzmaWrapped = true;
                return true;
            }
        }

        return TryDecodeBase64(source, out decoded);
    }

    private static bool TryDecodeBase64(ReadOnlySpan<byte> source, out byte[] decoded)
    {
        decoded = [];
        if (source.IsEmpty || source.Length % 4 != 0)
        {
            return false;
        }

        byte[] base64Bytes = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            base64Bytes[i] = (byte)((~source[i] & 0xFF) ^ XorKey);
            if (!IsBase64Byte(base64Bytes[i]))
            {
                return false;
            }
        }

        try
        {
            decoded = Convert.FromBase64String(Encoding.ASCII.GetString(base64Bytes));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsBase64Byte(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ||
               value is >= (byte)'a' and <= (byte)'z' ||
               value is >= (byte)'0' and <= (byte)'9' ||
               value is (byte)'+' or (byte)'/' or (byte)'=';
    }
}

internal static class TextPreviewDecoder
{
    private static readonly Encoding Utf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly Encoding Utf16Le =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false);

    private static readonly Encoding Utf16Be =
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false);

    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cfg",
        "csv",
        "ini",
        "json",
        "log",
        "lua",
        "nut",
        "txt",
        "xml"
    };

    static TextPreviewDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsPlainTextExtension(string extension)
    {
        return PlainTextExtensions.Contains(extension);
    }

    public static bool TryDecode(byte[] data, bool preferKorean, out string text, out string encodingName)
    {
        ReadOnlySpan<byte> bytes = TrimTrailingNulls(data);
        if (TryDecodeBom(bytes, out text, out encodingName))
        {
            return true;
        }

        foreach (EncodingCandidate candidate in GetCandidates(preferKorean))
        {
            if (TryDecodeWithEncoding(bytes, candidate.Encoding, out text) && LooksLikeText(text))
            {
                encodingName = candidate.DisplayName;
                return true;
            }
        }

        text = string.Empty;
        encodingName = string.Empty;
        return false;
    }

    private static ReadOnlySpan<byte> TrimTrailingNulls(byte[] data)
    {
        int length = data.Length;
        while (length > 0 && data[length - 1] == 0)
        {
            length--;
        }

        return data.AsSpan(0, length);
    }

    private static bool TryDecodeBom(ReadOnlySpan<byte> bytes, out string text, out string encodingName)
    {
        if (StartsWithBytes(bytes, 0xEF, 0xBB, 0xBF))
        {
            return TryDecodeNamed(bytes[3..], Utf8, "UTF-8", out text, out encodingName);
        }

        if (StartsWithBytes(bytes, 0xFF, 0xFE))
        {
            return TryDecodeNamed(bytes[2..], Utf16Le, "UTF-16 LE", out text, out encodingName);
        }

        if (StartsWithBytes(bytes, 0xFE, 0xFF))
        {
            return TryDecodeNamed(bytes[2..], Utf16Be, "UTF-16 BE", out text, out encodingName);
        }

        text = string.Empty;
        encodingName = string.Empty;
        return false;
    }

    private static bool StartsWithBytes(ReadOnlySpan<byte> bytes, params byte[] prefix)
    {
        return bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool TryDecodeNamed(
        ReadOnlySpan<byte> bytes,
        Encoding encoding,
        string displayName,
        out string text,
        out string encodingName)
    {
        if (TryDecodeWithEncoding(bytes, encoding, out text) && LooksLikeText(text))
        {
            encodingName = displayName;
            return true;
        }

        encodingName = string.Empty;
        return false;
    }

    private static IEnumerable<EncodingCandidate> GetCandidates(bool preferKorean)
    {
        EncodingCandidate utf8 = new("UTF-8", Utf8);
        EncodingCandidate cp949 = new("CP949", GetTextEncoding(949));
        EncodingCandidate gb18030 = new("GB18030", GetTextEncoding("GB18030"));

        if (preferKorean)
        {
            yield return cp949;
            yield return utf8;
        }
        else
        {
            yield return utf8;
            yield return cp949;
        }

        yield return gb18030;
    }

    private static Encoding GetTextEncoding(int codePage)
    {
        return Encoding.GetEncoding(codePage);
    }

    private static Encoding GetTextEncoding(string name)
    {
        return Encoding.GetEncoding(name);
    }

    private static bool TryDecodeWithEncoding(ReadOnlySpan<byte> bytes, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return text.IndexOf('\uFFFD') < 0;
        }
        catch (ArgumentException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool LooksLikeText(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        int suspicious = 0;
        foreach (char ch in text)
        {
            if (ch == '\uFFFD' || char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            {
                suspicious++;
            }
        }

        return suspicious <= Math.Max(2, text.Length / 50);
    }

    private sealed record EncodingCandidate(string DisplayName, Encoding Encoding);
}
