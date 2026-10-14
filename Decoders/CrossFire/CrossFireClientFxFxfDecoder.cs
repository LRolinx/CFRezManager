using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireClientFxFxfModelTextureMapping(
    string ModelReference,
    IReadOnlyList<string> TextureReferences,
    IReadOnlyList<string> AuxiliaryTextureReferences,
    IReadOnlyList<string> RenderStyleReferences,
    IReadOnlyList<string> SpriteReferences);

internal sealed record CrossFireClientFxFxfCatalog(
    int DeclaredEntryCount,
    int Version,
    long DecodedByteCount,
    int ModelFieldCount,
    int SkinFieldCount,
    int SpriteSkinFieldCount,
    int RenderStyleFieldCount,
    int OtherTextureFieldCount,
    IReadOnlyList<CrossFireClientFxFxfModelTextureMapping> Mappings);

/// <summary>
/// Reads the tagged model/skin/render-style references in CrossFire's compressed
/// ClientFX catalog. The catalog can expand to hundreds of MiB, so strings are
/// consumed as a stream and only the resulting asset paths are retained.
/// </summary>
internal static class CrossFireClientFxFxfDecoder
{
    private const long MaxDecodedBytes = 512L * 1024 * 1024;
    private const int MaxAsciiRunBytes = 4096;
    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"
    };

    public static bool TryDecode(
        byte[] data,
        out CrossFireClientFxFxfCatalog? catalog,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(data);
        catalog = null;
        errorMessage = null;

        using var source = new MemoryStream(data, writable: false);
        Stream payload = source;
        Stream? decompressed = null;
        long decodedByteCount = data.LongLength;
        if (LzmaAloneDecoder.IsCompressed(data))
        {
            if (!LzmaAloneDecoder.TryGetDecodedByteCount(data, out decodedByteCount) ||
                decodedByteCount is < 8 or > MaxDecodedBytes)
            {
                errorMessage = $"FXF decoded size is unknown or outside the {MaxDecodedBytes:N0}-byte limit.";
                return false;
            }

            decompressed = LzmaAloneDecoder.TryCreateDecompressStream(source, data.LongLength, out long streamDecodedBytes);
            if (decompressed is null || streamDecodedBytes != decodedByteCount)
            {
                decompressed?.Dispose();
                errorMessage = "Unable to initialize the FXF LZMA stream.";
                return false;
            }

            payload = decompressed;
        }

        try
        {
            Span<byte> header = stackalloc byte[sizeof(int) * 2];
            payload.ReadExactly(header);
            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header);
            int version = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(int)..]);
            if (entryCount is < 0 or > 1_000_000 || version is < 0 or > 1000)
            {
                errorMessage = $"Invalid FXF header values: entries={entryCount}, version={version}.";
                return false;
            }

            var state = new ParserState();
            long scannedByteCount = ScanProperties(payload, state, header.Length);
            if (scannedByteCount != decodedByteCount)
            {
                errorMessage = $"FXF payload length mismatch: scanned {scannedByteCount:N0} of {decodedByteCount:N0} declared bytes.";
                return false;
            }

            var mappings = state.Mappings
                .Select(pair => new CrossFireClientFxFxfModelTextureMapping(
                    pair.Key,
                    pair.Value.Textures.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.AuxiliaryTextures.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.RenderStyles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.Sprites.Order(StringComparer.OrdinalIgnoreCase).ToArray()))
                .OrderBy(mapping => mapping.ModelReference, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            catalog = new CrossFireClientFxFxfCatalog(
                entryCount,
                version,
                decodedByteCount,
                state.ModelFieldCount,
                state.SkinFieldCount,
                state.SpriteSkinFieldCount,
                state.RenderStyleFieldCount,
                state.OtherTextureFieldCount,
                mappings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            errorMessage = $"FXF payload could not be decoded: {exception.Message}";
            return false;
        }
        finally
        {
            decompressed?.Dispose();
        }
    }

    private static long ScanProperties(Stream input, ParserState state, int initialOffset)
    {
        byte[] inputBuffer = new byte[64 * 1024];
        byte[] runBuffer = new byte[MaxAsciiRunBytes];
        int runLength = 0;
        bool inRun = false;
        bool runOverflow = false;
        long runStart = initialOffset;
        long offset = initialOffset;
        while (true)
        {
            int bytesRead = input.Read(inputBuffer, 0, inputBuffer.Length);
            if (bytesRead == 0) break;
            for (int i = 0; i < bytesRead; i++, offset++)
            {
                byte value = inputBuffer[i];
                if (value is >= 0x20 and <= 0x7E)
                {
                    if (!inRun)
                    {
                        inRun = true;
                        runStart = offset;
                        runLength = 0;
                        runOverflow = false;
                    }

                    if (runLength < runBuffer.Length)
                    {
                        runBuffer[runLength++] = value;
                    }
                    else
                    {
                        runOverflow = true;
                    }

                    continue;
                }

                if (!inRun) continue;
                if (!runOverflow && runLength >= 4)
                {
                    ProcessRun(runBuffer.AsSpan(0, runLength), runStart, state);
                }
                else
                {
                    state.PendingProperty = PropertyKind.None;
                }

                inRun = false;
                runLength = 0;
            }
        }

        if (inRun && !runOverflow && runLength >= 4)
        {
            ProcessRun(runBuffer.AsSpan(0, runLength), runStart, state);
        }

        return offset;
    }

    private static void ProcessRun(ReadOnlySpan<byte> run, long runOffset, ParserState state)
    {
        if (state.PendingProperty != PropertyKind.None && runOffset - state.PendingOffset <= 32 &&
            TryParseAssetValue(run, out string extension, out string path))
        {
            ApplyPropertyValue(state, state.PendingProperty, extension, path);
        }

        state.PendingProperty = ReadPropertyKind(run);
        state.PendingOffset = runOffset;
    }

    private static PropertyKind ReadPropertyKind(ReadOnlySpan<byte> value)
    {
        if (value.SequenceEqual("Model"u8)) return PropertyKind.Model;
        if (value.SequenceEqual("Sprite"u8)) return PropertyKind.Sprite;
        if (value.SequenceEqual("Texture"u8)) return PropertyKind.Texture;
        if (EqualsAsciiIgnoreCase(value, "Skin"u8)) return PropertyKind.Skin;
        if (EqualsAsciiIgnoreCase(value, "SpriteSkin"u8)) return PropertyKind.SpriteSkin;
        if (EqualsAsciiIgnoreCase(value, "Refraction_NormalMapPath"u8)) return PropertyKind.NormalMap;
        if (IsNumberedProperty(value, "Skin"u8)) return PropertyKind.Skin;
        if (IsNumberedProperty(value, "SpriteSkin"u8)) return PropertyKind.SpriteSkin;
        if (IsNumberedProperty(value, "RenderStyle"u8)) return PropertyKind.RenderStyle;
        return PropertyKind.None;
    }

    private static bool IsNumberedProperty(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (!StartsWithAsciiIgnoreCase(value, prefix) || value.Length <= prefix.Length)
        {
            return false;
        }

        for (int i = prefix.Length; i < value.Length; i++)
        {
            if (value[i] is < (byte)'0' or > (byte)'9') return false;
        }

        return true;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte left = value[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(value[i] + 0x20) : value[i];
            byte right = prefix[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(prefix[i] + 0x20) : prefix[i];
            if (left != right) return false;
        }

        return true;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected) =>
        value.Length == expected.Length && StartsWithAsciiIgnoreCase(value, expected);

    private static bool TryParseAssetValue(ReadOnlySpan<byte> value, out string extension, out string path)
    {
        extension = string.Empty;
        path = string.Empty;
        int separator = value.IndexOf((byte)'|');
        if (separator is <= 0 or > 8)
        {
            return false;
        }

        extension = Encoding.ASCII.GetString(value[..separator]).ToLowerInvariant();
        path = Encoding.ASCII.GetString(value[(separator + 1)..]).Trim().Trim('"', '\'');
        return true;
    }

    private static void ApplyPropertyValue(ParserState state, PropertyKind property, string extension, string path)
    {
        string fileExtension = Path.GetExtension(path);
        if (property == PropertyKind.Model)
        {
            state.CurrentModel = extension.Equals("ltb", StringComparison.OrdinalIgnoreCase) &&
                                 fileExtension.Equals(".ltb", StringComparison.OrdinalIgnoreCase) &&
                                 !path.Equals("...", StringComparison.Ordinal)
                ? NormalizePath(path)
                : null;
            if (state.CurrentModel is not null)
            {
                state.ModelFieldCount++;
                if (!state.Mappings.ContainsKey(state.CurrentModel)) state.Mappings.Add(state.CurrentModel, new MappingBuilder());
            }

            return;
        }

        if (state.CurrentModel is null || !state.Mappings.TryGetValue(state.CurrentModel, out MappingBuilder? mapping))
        {
            return;
        }

        if (property == PropertyKind.Skin &&
            TextureExtensions.Contains(fileExtension) && extension.Equals(fileExtension.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        {
            mapping.Textures.Add(NormalizePath(path));
            state.SkinFieldCount++;
        }
        else if (property == PropertyKind.SpriteSkin &&
                 extension.Equals("spr", StringComparison.OrdinalIgnoreCase) &&
                 fileExtension.Equals(".spr", StringComparison.OrdinalIgnoreCase))
        {
            mapping.Sprites.Add(NormalizePath(path));
            state.SpriteSkinFieldCount++;
        }
        else if (property == PropertyKind.SpriteSkin &&
                 TextureExtensions.Contains(fileExtension) &&
                 extension.Equals(fileExtension.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        {
            mapping.AuxiliaryTextures.Add(NormalizePath(path));
            state.SpriteSkinFieldCount++;
        }
        else if (property is PropertyKind.Texture or PropertyKind.NormalMap &&
                 TextureExtensions.Contains(fileExtension) &&
                 extension.Equals(fileExtension.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        {
            mapping.AuxiliaryTextures.Add(NormalizePath(path));
            state.OtherTextureFieldCount++;
        }
        else if (property == PropertyKind.RenderStyle &&
                 extension.Equals("ltb", StringComparison.OrdinalIgnoreCase) &&
                 fileExtension.Equals(".ltb", StringComparison.OrdinalIgnoreCase))
        {
            mapping.RenderStyles.Add(NormalizePath(path));
            state.RenderStyleFieldCount++;
        }
        else if (property == PropertyKind.Sprite &&
                 extension.Equals("spr", StringComparison.OrdinalIgnoreCase) &&
                 fileExtension.Equals(".spr", StringComparison.OrdinalIgnoreCase))
        {
            mapping.Sprites.Add(NormalizePath(path));
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().TrimStart('/');

    private enum PropertyKind
    {
        None,
        Model,
        Skin,
        SpriteSkin,
        RenderStyle,
        Sprite,
        Texture,
        NormalMap
    }

    private sealed class ParserState
    {
        public Dictionary<string, MappingBuilder> Mappings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PropertyKind PendingProperty { get; set; }
        public long PendingOffset { get; set; }
        public string? CurrentModel { get; set; }
        public int ModelFieldCount { get; set; }
        public int SkinFieldCount { get; set; }
        public int SpriteSkinFieldCount { get; set; }
        public int RenderStyleFieldCount { get; set; }
        public int OtherTextureFieldCount { get; set; }
    }

    private sealed class MappingBuilder
    {
        public HashSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AuxiliaryTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RenderStyles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Sprites { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
