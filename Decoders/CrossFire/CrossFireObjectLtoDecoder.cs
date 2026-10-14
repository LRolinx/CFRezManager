using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireObjectLtoModelTextureMapping(
    string ModelReference,
    IReadOnlyList<string> TextureReferences,
    int EvidenceOffset);

/// <summary>
/// Extracts high-confidence adjacent model/texture path literals from CrossFire's
/// 32-bit and 64-bit compiled object images. Only consecutive path literals are
/// grouped; unrelated strings terminate a candidate relationship.
/// </summary>
internal static class CrossFireObjectLtoDecoder
{
    private const int DosHeaderSize = 0x40;
    private const int PeFileHeaderSize = 20;
    private const int PeSectionHeaderSize = 40;
    private const int MaximumSections = 96;
    private const int MaximumModelPathBytes = 512;
    private const int MaximumTexturePathBytes = 512;

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ltb",
        ".lta",
        ".ltc"
    };

    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dtx",
        ".dds",
        ".tga",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".bin"
    };

    public static IReadOnlyList<CrossFireObjectLtoModelTextureMapping> Extract(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!TryGetSections(data, out List<PeSection>? sections) || sections is null)
        {
            return [];
        }

        var strings = new List<PeString>();
        foreach (PeSection section in sections)
        {
            int runStart = -1;
            int runLength = 0;
            int sectionEnd = section.RawOffset + section.RawSize;
            for (int offset = section.RawOffset; offset < sectionEnd; offset++)
            {
                byte value = data[offset];
                if (value is >= 0x20 and <= 0x7E)
                {
                    if (runStart < 0)
                    {
                        runStart = offset;
                        runLength = 0;
                    }

                    runLength++;
                    continue;
                }

                AddString();
            }

            AddString();

            void AddString()
            {
                if (runStart >= 0 && runLength >= 4)
                {
                    strings.Add(new PeString(runStart, Encoding.ASCII.GetString(data, runStart, runLength)));
                }

                runStart = -1;
                runLength = 0;
            }
        }

        var mappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var modelOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < strings.Count; index++)
        {
            PeString modelString = strings[index];
            if (!TryNormalizeModelPath(modelString.Value, out string modelReference))
            {
                continue;
            }

            var textures = new List<string>();
            for (int nextIndex = index + 1; nextIndex < strings.Count; nextIndex++)
            {
                PeString next = strings[nextIndex];
                if (next.Offset - modelString.Offset > MaximumModelPathBytes ||
                    TryNormalizeModelPath(next.Value, out _))
                {
                    break;
                }

                if (!TryNormalizeTexturePath(next.Value, out string textureReference))
                {
                    break;
                }

                textures.Add(textureReference);
            }

            if (textures.Count == 0)
            {
                continue;
            }

            if (!mappings.TryGetValue(modelReference, out HashSet<string>? mappedTextures))
            {
                mappedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                mappings.Add(modelReference, mappedTextures);
                modelOffsets.Add(modelReference, modelString.Offset);
            }

            foreach (string texture in textures)
            {
                mappedTextures.Add(texture);
            }
        }

        return mappings
            .OrderBy(pair => modelOffsets[pair.Key])
            .Select(pair => new CrossFireObjectLtoModelTextureMapping(
                pair.Key,
                pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                modelOffsets[pair.Key]))
            .ToArray();
    }

    public static bool TryDecode(byte[] data, string fileName, out string text, out string? errorMessage)
    {
        text = string.Empty;
        errorMessage = null;
        if (!TryGetSections(data, out List<PeSection>? sections) || sections is null)
        {
            errorMessage = "LTO resource is not a supported Windows PE object image.";
            return false;
        }

        int peOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C, sizeof(uint))));
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 4, sizeof(ushort)));
        string architecture = machine == 0x8664 ? "x64" : "x86";
        IReadOnlyList<CrossFireObjectLtoModelTextureMapping> mappings = Extract(data);
        var builder = new StringBuilder();
        builder.AppendLine("CrossFire compiled object resource (LTO)");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"Container: Windows PE, {architecture}, {sections.Count} sections");
        builder.AppendLine($"Embedded adjacent model-texture path groups: {mappings.Count}");
        foreach (CrossFireObjectLtoModelTextureMapping mapping in mappings)
        {
            builder.AppendLine();
            builder.AppendLine($"Evidence offset: 0x{mapping.EvidenceOffset:X}");
            builder.AppendLine($"Model: {mapping.ModelReference}");
            foreach (string texture in mapping.TextureReferences)
            {
                builder.AppendLine($"Texture: {texture}");
            }
        }

        if (mappings.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No adjacent model/texture path literals were found.");
        }

        text = builder.ToString();
        return true;
    }

    private static bool TryGetSections(byte[] data, out List<PeSection>? sections)
    {
        sections = null;
        if (data.Length < DosHeaderSize || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return false;
        }

        uint peOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C, sizeof(uint)));
        if (peOffsetValue > int.MaxValue)
        {
            return false;
        }

        int peOffset = (int)peOffsetValue;
        if ((long)peOffset + 4 + PeFileHeaderSize > data.Length ||
            data[peOffset] != (byte)'P' || data[peOffset + 1] != (byte)'E' ||
            data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
        {
            return false;
        }

        int fileHeaderOffset = peOffset + 4;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset, sizeof(ushort)));
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset + 2, sizeof(ushort)));
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fileHeaderOffset + 16, sizeof(ushort)));
        if (machine is not (0x014C or 0x8664) || sectionCount is 0 or > MaximumSections)
        {
            return false;
        }

        long sectionTableOffset = (long)fileHeaderOffset + PeFileHeaderSize + optionalHeaderSize;
        if (sectionTableOffset + (long)sectionCount * PeSectionHeaderSize > data.Length)
        {
            return false;
        }

        var parsedSections = new List<PeSection>(sectionCount);
        for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            int sectionOffset = checked((int)sectionTableOffset + sectionIndex * PeSectionHeaderSize);
            uint rawSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sectionOffset + 16, sizeof(uint)));
            uint rawOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sectionOffset + 20, sizeof(uint)));
            if (rawSizeValue == 0)
            {
                continue;
            }

            if (rawSizeValue > int.MaxValue || rawOffsetValue > int.MaxValue ||
                (long)rawOffsetValue + rawSizeValue > data.Length)
            {
                return false;
            }

            parsedSections.Add(new PeSection((int)rawOffsetValue, (int)rawSizeValue));
        }

        sections = parsedSections;
        return true;
    }

    private static bool TryNormalizeModelPath(string value, out string normalized)
    {
        normalized = NormalizeResourcePath(value);
        if (normalized.Length > MaximumModelPathBytes ||
            (!normalized.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) &&
             !normalized.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase)) ||
            !ModelExtensions.Contains(Path.GetExtension(normalized)))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryNormalizeTexturePath(string value, out string normalized)
    {
        normalized = NormalizeResourcePath(value);
        if (normalized.Length > MaximumTexturePathBytes ||
            (!normalized.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) &&
             !normalized.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)) ||
            !TextureExtensions.Contains(Path.GetExtension(normalized)))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static string NormalizeResourcePath(string value)
    {
        return value.Trim().Replace('\\', '/');
    }

    private sealed record PeSection(int RawOffset, int RawSize);

    private sealed record PeString(int Offset, string Value);
}
