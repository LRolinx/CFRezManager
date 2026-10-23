using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace CFRezManager;

internal sealed record CftModelTextureMapping(
    string ModelReference,
    IReadOnlyList<CftTextureReference> TextureReferences,
    IReadOnlyList<string> IdentityAliases,
    int RowIndex,
    int? RecordId,
    string ModelColumn,
    int ModelColumnIndex,
    bool HasTypedRowContext,
    IReadOnlyList<string>? RelatedModelReferences = null);

internal sealed record CftTextureReference(
    string Path,
    string Column,
    int ColumnIndex);

/// <summary>
/// Extracts row-scoped model/texture references from CrossFire CFT tables.
/// The fixed column header is checked first, so unrelated or large tables can
/// be rejected without decompressing their full contents.
/// </summary>
internal static class CftModelTextureMappingDecoder
{
    private const int ColumnCapacity = 128;
    private const int MaxTextureSlotIndex = byte.MaxValue;
    private const int HeaderPrefixBytes = 14 + ColumnCapacity * 64 + ColumnCapacity * sizeof(int);
    private const long MaxDecodedBytes = 128 * 1024 * 1024;
    private static readonly Regex ResourcePathRegex = new(
        @"(?i)(?:Models|ModelTextures|ArmModel|UCC/ModelAssets)/[A-Za-z0-9_ ./\\:-]+?\.(?:ltb|lta|ltc|dat|dtx|dds|tga|png|jpg|jpeg|bmp)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ResourceSideRegex = new(
        @"(?:^|[_/])(GR|BL|PV|QV)(?=[^A-Z]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RepeatedPathSeparatorRegex = new(
        @"/{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<CftModelTextureMapping> Extract(byte[] data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        byte[]? header = LzmaAloneDecoder.IsCompressed(data)
            ? LzmaAloneDecoder.TryDecompressPrefix(data, HeaderPrefixBytes)
            : data;
        if (header is null || header.Length < 14)
        {
            return [];
        }

        byte[] decodedHeader = LooksLikeXorCft(header)
            ? Xor(header, 0x10)
            : header;
        if (decodedHeader[0] != (byte)'s' ||
            decodedHeader[1] != (byte)'u' ||
            !HasModelTextureColumns(decodedHeader))
        {
            return [];
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxDecodedBytes);
        if (prepared is null || prepared.Length < 14)
        {
            return [];
        }

        byte[] decoded = LooksLikeXorCft(prepared)
            ? Xor(prepared, 0x10)
            : prepared;
        if (decoded[0] != (byte)'s' || decoded[1] != (byte)'u')
        {
            return [];
        }

        IReadOnlyList<CftModelTextureMapping> rowMappings = ExtractTypedRows(decoded);
        // Keep path-block results as lower-confidence aliases even when typed
        // parsing succeeded. This also preserves coverage for rows after a
        // malformed/variant row stops the typed reader.
        int scanBytes = decoded.Length;
        if (CrossFireCftTableDecoder.TryDecodeWithDiagnostics(decoded, out _, out _, out int consumedBytes, out _) &&
            CrossFireCftTableDecoder.TryGetEndMarkerStart(decoded, consumedBytes, out int endMarkerStart))
        {
            // Some shipped tables retain old model/texture strings after END.
            // Audit those bytes separately; they do not support row bindings.
            scanBytes = endMarkerStart;
        }

        string text = Encoding.Unicode.GetString(decoded, 0, scanBytes);
        MatchCollection matches = ResourcePathRegex.Matches(text);
        if (matches.Count == 0)
        {
            return rowMappings;
        }

        var mappings = new List<CftModelTextureMapping>();
        var modelBlock = new List<string>();
        var textures = new List<string>();

        foreach (Match match in matches)
        {
            string value = NormalizePath(match.Value);
            if (IsModelPath(value))
            {
                if (textures.Count > 0 && modelBlock.Count > 0)
                {
                    AddBlockMappings(mappings, modelBlock, textures);
                    modelBlock.Clear();
                    textures.Clear();
                }

                if (!modelBlock.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    modelBlock.Add(value);
                }

                continue;
            }

            if (IsTexturePath(value) && modelBlock.Count > 0)
            {
                if (!textures.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    textures.Add(value);
                }
            }
        }

        AddBlockMappings(mappings, modelBlock, textures);
        return rowMappings.Count == 0
            ? mappings
            : rowMappings.Concat(mappings).ToArray();
    }

    private static IReadOnlyList<CftModelTextureMapping> ExtractTypedRows(byte[] decoded)
    {
        int columnCount = BitConverter.ToInt32(decoded, 6);
        int rowCount = BitConverter.ToInt32(decoded, 10);
        if (columnCount <= 0 || columnCount > ColumnCapacity || rowCount <= 0 || rowCount > 1_000_000)
        {
            return [];
        }

        var columnNames = new string[ColumnCapacity];
        var columnTypes = new int[ColumnCapacity];
        for (int index = 0; index < ColumnCapacity; index++)
        {
            int nameOffset = 14 + index * 64;
            int nameLength = Array.IndexOf(decoded, (byte)0, nameOffset, 64);
            if (nameLength < 0)
            {
                nameLength = nameOffset + 64;
            }

            columnNames[index] = Encoding.Latin1.GetString(decoded, nameOffset, nameLength - nameOffset);

            int offset = 14 + ColumnCapacity * 64 + index * sizeof(int);
            if (offset < 0 || offset + sizeof(int) > decoded.Length)
            {
                return [];
            }

            columnTypes[index] = BitConverter.ToInt32(decoded, offset);
        }

        bool hasCharacterTypeRowIndex = HasCharacterTypeRowIndex(columnNames, columnTypes, columnCount);
        int position = 14 + ColumnCapacity * 64 + ColumnCapacity * sizeof(int);
        var mappings = new List<CftModelTextureMapping>();
        for (int row = 0; row < rowCount; row++)
        {
            if (!TryReadInt32(decoded, ref position, out int recordId))
            {
                break;
            }

            var modelReferences = new Dictionary<string, (string Column, int ColumnIndex)>(StringComparer.OrdinalIgnoreCase);
            var textureReferences = new List<CftTextureReference>();
            var seenTextureReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identityAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool rowIsComplete = true;
            for (int column = 0; column < columnCount; column++)
            {
                switch (columnTypes[column])
                {
                    case 0 when string.IsNullOrEmpty(columnNames[column]):
                        continue;
                    case 1:
                        if (!TryReadString(decoded, ref position, out string value))
                        {
                            rowIsComplete = false;
                            break;
                        }

                        string normalized = NormalizePath(value);
                        if (IsIdentityNameColumn(columnNames[column]) &&
                            !string.IsNullOrWhiteSpace(value) &&
                            value.Length <= 256 &&
                            !value.Contains('/') &&
                            !value.Contains('\\'))
                        {
                            identityAliases.Add(value.Trim());
                        }

                        if (IsModelReference(columnNames[column], normalized))
                        {
                            modelReferences.TryAdd(normalized, (columnNames[column], column));
                        }
                        else if (IsTextureReference(columnNames[column], normalized))
                        {
                            string textureKey = $"{columnNames[column]}\n{normalized}";
                            if (seenTextureReferences.Add(textureKey))
                            {
                                textureReferences.Add(new CftTextureReference(normalized, columnNames[column], column));
                            }
                        }

                        break;
                    case 0:
                    case 2:
                    case 3:
                    case 4:
                    case 5:
                        if (!TryReadInt32(decoded, ref position, out int integerValue))
                        {
                            rowIsComplete = false;
                            break;
                        }

                        if (integerValue > 0 && IsItemIdentityIdColumn(columnNames[column]))
                        {
                            string itemId = integerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            identityAliases.Add(itemId);
                            identityAliases.Add("item:" + itemId);
                        }
                        else if (integerValue > 0 && IsButeIdentityIdColumn(columnNames[column]))
                        {
                            identityAliases.Add("bute:" + integerValue.ToString(
                                System.Globalization.CultureInfo.InvariantCulture));
                        }

                        // CharacterType.CFT contains a legacy per-row index
                        // immediately after _RealCharacterType. It is not
                        // represented in the header's column list. Without
                        // consuming it, every following field in a row is
                        // shifted and the parser can stop at path bytes that
                        // happen to resemble an END marker.
                        if (hasCharacterTypeRowIndex && column == 0 && integerValue > 0)
                        {
                            if (!TryReadInt32(decoded, ref position, out int rowIndex) ||
                                rowIndex != integerValue - 1)
                            {
                                rowIsComplete = false;
                            }
                        }

                        break;
                    default:
                        rowIsComplete = false;
                        break;
                }

                if (!rowIsComplete)
                {
                    break;
                }
            }

            if (!rowIsComplete)
            {
                break;
            }

            if (modelReferences.Count == 0 || textureReferences.Count == 0)
            {
                continue;
            }

            foreach ((string modelReference, (string modelColumn, int modelColumnIndex)) in modelReferences)
            {
                string? modelSide = GetResourceSide(modelColumn) ?? GetResourceSide(modelReference);
                CftTextureReference[] pairedTextures = SelectTextureReferences(
                    textureReferences,
                    modelReference,
                    modelColumn,
                    modelSide);
                if (pairedTextures.Length > 0)
                {
                    mappings.Add(new CftModelTextureMapping(
                        modelReference,
                        pairedTextures,
                        identityAliases.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        row,
                        recordId,
                        modelColumn,
                        modelColumnIndex,
                        HasTypedRowContext: true,
                        RelatedModelReferences: modelReferences.Keys
                            .Where(reference => !string.Equals(reference, modelReference, StringComparison.OrdinalIgnoreCase))
                            .ToArray()));
                }
            }
        }

        return mappings;
    }

    private static bool IsIdentityNameColumn(string columnName)
    {
        return columnName.Equals("_ItemName", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_WeaponName", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ObjectName", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_CharacterName", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ItemCode", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ResourceName", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsItemIdentityIdColumn(string columnName)
    {
        return columnName.Equals("_ITEM_INDEX", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ItemTableID", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ItemID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsButeIdentityIdColumn(string columnName)
    {
        return columnName.Equals("_ButeIndex", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ModelButeIndex", StringComparison.OrdinalIgnoreCase) ||
               columnName.Equals("_ChangeButeIndex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCharacterTypeRowIndex(
        IReadOnlyList<string> columnNames,
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
               string.Equals(columnNames[0], "_RealCharacterType", StringComparison.Ordinal) &&
               string.Equals(columnNames[1], "_ItemName", StringComparison.Ordinal) &&
               string.Equals(columnNames[2], "_ResourceName", StringComparison.Ordinal) &&
               string.Equals(columnNames[3], "_ItemID", StringComparison.Ordinal) &&
               string.Equals(columnNames[4], "_ItemCode", StringComparison.Ordinal) &&
               string.Equals(columnNames[5], "_ButeType", StringComparison.Ordinal);
    }

    private static bool TryReadInt32(byte[] data, ref int position, out int value)
    {
        value = 0;
        if (position < 0 || position + sizeof(int) > data.Length)
        {
            return false;
        }

        value = BitConverter.ToInt32(data, position);
        position += sizeof(int);
        return true;
    }

    private static bool TryReadString(byte[] data, ref int position, out string value)
    {
        value = string.Empty;
        if (position < 0 || position + sizeof(ushort) > data.Length)
        {
            return false;
        }

        int byteLength = BitConverter.ToUInt16(data, position);
        position += sizeof(ushort);
        // CFT stores string lengths in bytes. Odd-length legacy strings are
        // followed by one alignment byte; rejecting them discards whole tables.
        int paddedByteLength = byteLength + (byteLength & 1);
        if (position + paddedByteLength > data.Length)
        {
            return false;
        }

        value = Encoding.Unicode.GetString(data, position, byteLength);
        position += paddedByteLength;
        return true;
    }

    private static bool HasModelTextureColumns(byte[] decoded)
    {
        int columnCount = BitConverter.ToInt32(decoded, 6);
        if (columnCount <= 0 || columnCount > 128)
        {
            return false;
        }

        bool hasModelColumn = false;
        bool hasTextureColumn = false;
        for (int index = 0; index < columnCount; index++)
        {
            int offset = 14 + index * 64;
            if (offset < 0 || offset + 64 > decoded.Length)
            {
                return false;
            }

            int length = Array.IndexOf(decoded, (byte)0, offset, 64);
            if (length < 0)
            {
                length = offset + 64;
            }

            string column = Encoding.Latin1.GetString(decoded, offset, length - offset);
            string lowerColumn = column.ToLowerInvariant();
            hasModelColumn |= lowerColumn.Contains("model", StringComparison.Ordinal);
            hasTextureColumn |= lowerColumn.Contains("texture", StringComparison.Ordinal) ||
                                lowerColumn.Contains("tex", StringComparison.Ordinal) ||
                                lowerColumn.Contains("skin", StringComparison.Ordinal);
        }

        return hasModelColumn && hasTextureColumn;
    }

    private static void AddBlockMappings(
        List<CftModelTextureMapping> mappings,
        IReadOnlyList<string> modelBlock,
        IReadOnlyList<string> textures)
    {
        if (modelBlock.Count == 0 || textures.Count == 0)
        {
            return;
        }

        foreach (string model in modelBlock)
        {
            mappings.Add(new CftModelTextureMapping(
                model,
                textures.Select(texture => new CftTextureReference(texture, string.Empty, -1)).ToArray(),
                [],
                RowIndex: -1,
                RecordId: null,
                ModelColumn: string.Empty,
                ModelColumnIndex: -1,
                HasTypedRowContext: false));
        }
    }

    private static bool IsModelPath(string value)
    {
        string extension = Path.GetExtension(value);
        bool isModelExtension = extension.Equals(".ltb", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".lta", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".ltc", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".dat", StringComparison.OrdinalIgnoreCase);
        return isModelExtension &&
               (value.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTexturePath(string value)
    {
        return (value.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UCC/ModelAssets/UCC_Textures/", StringComparison.OrdinalIgnoreCase)) &&
               IsSupportedTextureExtension(value);
    }

    private static bool IsModelReference(string columnName, string value)
    {
        if (columnName.Contains("renderstyle", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("animation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsModelPath(value))
        {
            return true;
        }

        if (!columnName.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(value);
        if (string.IsNullOrEmpty(extension) && IsKnownModelPathRoot(value))
        {
            return true;
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            return false;
        }

        if (extension.Equals(".ltb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lta", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ltc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsKnownModelPathRoot(string value)
    {
        return value.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextureReference(string columnName, string value)
    {
        if (IsTexturePath(value))
        {
            return true;
        }

        if (!columnName.Contains("texture", StringComparison.OrdinalIgnoreCase) &&
            !columnName.Contains("tex", StringComparison.OrdinalIgnoreCase) &&
            !columnName.Contains("skin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            return false;
        }

        return IsSupportedTextureExtension(value);
    }

    private static bool IsSupportedTextureExtension(string value)
    {
        string extension = Path.GetExtension(value);
        return extension.Equals(".dtx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static CftTextureReference[] SelectTextureReferences(
        IReadOnlyList<CftTextureReference> textureReferences,
        string modelReference,
        string modelColumn,
        string? modelSide)
    {
        CftTextureReference[] compatibleTextures = textureReferences
            .Where(texture =>
                HaveCompatibleResourceSides(modelSide, texture) &&
                HaveCompatibleResourceRoles(modelColumn, texture, textureReferences))
            .DistinctBy(texture => $"{texture.ColumnIndex}\n{texture.Path}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Motion-only assets share CFT rows with their parent model, but the
        // numbered texture fields belong to the parent. The config index maps
        // them onto a motion child only after validating both models' slots.
        if (Path.GetFileNameWithoutExtension(modelReference).Contains("motion", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // Numbered fields are explicit engine slots. Do not let a same-stem
        // texture short-circuit the rest of a row's slot list (for example,
        // TextureFile1 matching the model name while TextureFile2/3 are skins).
        CftTextureReference[] explicitSlotTextures = compatibleTextures
            .Where(texture => TryGetTextureSlotIndex(texture.Column, out _))
            .ToArray();
        if (explicitSlotTextures.Length > 0)
        {
            return explicitSlotTextures;
        }

        string modelStem = NormalizeResourceStem(modelReference);
        if (modelStem.Length > 0)
        {
            CftTextureReference[] exactStemTextures = compatibleTextures
                .Where(texture =>
                    string.Equals(modelStem, NormalizeResourceStem(texture.Path), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (exactStemTextures.Length > 0)
            {
                return exactStemTextures;
            }
        }

        if (string.IsNullOrWhiteSpace(modelSide))
        {
            return compatibleTextures;
        }

        return compatibleTextures;
    }

    internal static bool TryGetTextureSlotIndex(string columnName, out int slotIndex)
    {
        slotIndex = -1;
        string normalized = columnName.TrimStart('_');
        string[] oneBasedPrefixes =
        [
            "TextureFileName",
            "TextureFile",
            "TexFile",
            "PVTexFile",
            "QVTexFile",
            "BLTexFile",
            "GRTexFile",
            "BLTextureFile",
            "GRTextureFile",
            "ModelTextureFilePath",
            "SocketSkinFileName"
        ];

        foreach (string prefix in oneBasedPrefixes)
        {
            if (TryParseTextureFieldNumber(normalized, prefix, 1, MaxTextureSlotIndex + 1, out int fieldNumber))
            {
                slotIndex = fieldNumber - 1;
                return true;
            }
        }

        // Character skin tables use an explicitly zero-based suffix; unlike
        // TextureFileN, SkinFileName0 refers to the model's native slot 0.
        if (TryParseTextureFieldNumber(normalized, "SkinFileName", 0, MaxTextureSlotIndex, out int zeroBasedSlot))
        {
            slotIndex = zeroBasedSlot;
            return true;
        }

        return false;
    }

    private static bool TryParseTextureFieldNumber(
        string columnName,
        string prefix,
        int minimum,
        int maximum,
        out int fieldNumber)
    {
        fieldNumber = -1;
        if (!columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string suffix = columnName[prefix.Length..];
        return suffix.Length is > 0 and <= 3 &&
               int.TryParse(
                   suffix,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out fieldNumber) &&
               fieldNumber >= minimum &&
               fieldNumber <= maximum;
    }

    private static bool HaveCompatibleResourceRoles(
        string modelColumn,
        CftTextureReference texture,
        IReadOnlyList<CftTextureReference> rowTextures)
    {
        bool modelIsPlayerView = IsPlayerViewColumn(modelColumn);
        bool textureIsPlayerView = IsPlayerViewColumn(texture.Column);
        if (modelIsPlayerView != textureIsPlayerView)
        {
            return false;
        }

        bool modelIsSocket = modelColumn.Contains("socket", StringComparison.OrdinalIgnoreCase);
        bool textureIsSocket = texture.Column.Contains("socket", StringComparison.OrdinalIgnoreCase);
        if (!modelIsSocket && textureIsSocket)
        {
            return false;
        }

        bool rowHasSocketTextures = rowTextures.Any(candidate =>
            candidate.Column.Contains("socket", StringComparison.OrdinalIgnoreCase));
        if (modelIsSocket && rowHasSocketTextures)
        {
            if (!textureIsSocket)
            {
                return false;
            }

            int modelIndex = GetTrailingColumnIndex(modelColumn);
            int textureIndex = GetTrailingColumnIndex(texture.Column);
            if (modelIndex > 0 && textureIndex > 0 && modelIndex != textureIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlayerViewColumn(string columnName)
    {
        return columnName.Contains("pv", StringComparison.OrdinalIgnoreCase) ||
               columnName.Contains("handarm", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetTrailingColumnIndex(string columnName)
    {
        int end = columnName.Length;
        int start = end;
        while (start > 0 && char.IsDigit(columnName[start - 1]))
        {
            start--;
        }

        return start == end || !int.TryParse(columnName.AsSpan(start, end - start), out int index)
            ? 0
            : index;
    }

    private static bool HaveCompatibleResourceSides(string? modelSide, CftTextureReference texture)
    {
        string? textureSide = GetResourceSide(texture.Column) ?? GetResourceSide(texture.Path);
        return string.IsNullOrWhiteSpace(modelSide) ||
               string.IsNullOrWhiteSpace(textureSide) ||
               string.Equals(textureSide, modelSide, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourceStem(string value)
    {
        string stem = Path.GetFileNameWithoutExtension(value).Trim();
        if (stem.Length == 0)
        {
            return string.Empty;
        }

        if (stem.EndsWith("_INDEXED", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^"_INDEXED".Length];
        }

        if (stem.EndsWith("_BR", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^"_BR".Length];
        }

        string? side = GetResourceSide(stem);
        if (side is not null && stem.EndsWith($"_{side}", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^(side.Length + 1)];
        }

        return stem;
    }

    private static string? GetResourceSide(string columnName)
    {
        Match match = ResourceSideRegex.Match(columnName);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string NormalizePath(string value)
    {
        string normalized = value
            .Trim()
            .Replace('\\', '/')
            .TrimStart('/');
        return RepeatedPathSeparatorRegex.Replace(normalized, "/");
    }

    private static bool LooksLikeXorCft(byte[] data)
    {
        return data.Length >= 2 &&
               ((data[0] ^ 0x10) == (byte)'s') &&
               ((data[1] ^ 0x10) == (byte)'u');
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
}
