using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal sealed record CrossFireUccObjectAsset(
    int RecordId,
    string Name,
    string ModelReference,
    IReadOnlySet<int> AllowedTextureIds);

internal sealed record CrossFireUccTextureAsset(
    int RecordId,
    string TextureReference);

internal sealed record CrossFireUccMapTextureMapping(
    string InstanceName,
    string SerializedAssetName,
    string ObjectName,
    int ObjectRecordId,
    int TextureSelectorValue,
    int TextureRecordId,
    string ModelReference,
    string? TextureReference,
    int MapVersion,
    bool IsTextureAllowedByObject,
    bool HasExplicitObjectTableReference,
    int ReferenceOffset);

internal sealed record CrossFireUccAssetCatalog(
    IReadOnlyDictionary<string, CrossFireUccObjectAsset> ObjectsByName,
    IReadOnlyDictionary<int, CrossFireUccTextureAsset> TexturesById);

/// <summary>
/// Resolves the UCC placement-record references that join UCCAssetObject.CFT
/// rows to UCCAssetObjectTextures.CFT rows. Serialized texture selectors are
/// zero-based CFT row indexes; the CFT row IDs are one-based. The original
/// selector is retained alongside the resolved row so callers can audit the
/// translation and any object-palette inconsistencies.
/// </summary>
internal static class CrossFireUccResourceDecoder
{
    private const int MaximumMapBytes = 4 * 1024 * 1024;
    private const int ObjectClassId = 3;
    private const int ObjectNameFieldBytes = 64;
    private const int SerializedAssetFieldOffset = -84;
    private const int PointerSearchStart = 55;
    private const int PointerSearchEnd = 105;

    private static readonly Regex ObjectInstanceRegex = new(
        @"Object(?<id>[0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryDecodeAssetCatalog(
        byte[] objectTableData,
        byte[] textureTableData,
        out CrossFireUccAssetCatalog? catalog)
    {
        catalog = null;
        if (!CrossFireCftTableDecoder.TryDecode(objectTableData, out CrossFireCftTable? objectTable) ||
            objectTable is null ||
            !CrossFireCftTableDecoder.TryDecode(textureTableData, out CrossFireCftTable? textureTable) ||
            textureTable is null)
        {
            return false;
        }

        if (!objectTable.Columns.Contains("_ObjectName", StringComparer.OrdinalIgnoreCase) ||
            !objectTable.Columns.Contains("_ModelFileName1", StringComparer.OrdinalIgnoreCase) ||
            !objectTable.Columns.Contains("_UseTextureIndex", StringComparer.OrdinalIgnoreCase) ||
            !textureTable.Columns.Contains("_TextureFileName", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var objectsByName = new Dictionary<string, CrossFireUccObjectAsset>(StringComparer.OrdinalIgnoreCase);
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CrossFireCftRow row in objectTable.Rows)
        {
            string name = GetString(row, "_ObjectName").Trim();
            string modelReference = NormalizePath(GetString(row, "_ModelFileName1"));
            if (name.Length == 0 || modelReference.Length == 0)
            {
                continue;
            }

            var allowedTextureIds = ParseIntegerSet(GetString(row, "_UseTextureIndex"));
            var asset = new CrossFireUccObjectAsset(row.RecordId, name, modelReference, allowedTextureIds);
            if (!objectsByName.TryAdd(name, asset) &&
                !Equals(objectsByName[name], asset))
            {
                ambiguousNames.Add(name);
            }
        }

        foreach (string ambiguousName in ambiguousNames)
        {
            objectsByName.Remove(ambiguousName);
        }

        var texturesById = new Dictionary<int, CrossFireUccTextureAsset>();
        var ambiguousTextureIds = new HashSet<int>();
        foreach (CrossFireCftRow row in textureTable.Rows)
        {
            string textureReference = NormalizePath(GetString(row, "_TextureFileName"));
            if (row.RecordId <= 0 || textureReference.Length == 0)
            {
                continue;
            }

            var texture = new CrossFireUccTextureAsset(row.RecordId, textureReference);
            if (!texturesById.TryAdd(row.RecordId, texture) &&
                !Equals(texturesById[row.RecordId], texture))
            {
                ambiguousTextureIds.Add(row.RecordId);
            }
        }

        foreach (int ambiguousTextureId in ambiguousTextureIds)
        {
            texturesById.Remove(ambiguousTextureId);
        }

        if (objectsByName.Count == 0 || texturesById.Count == 0)
        {
            return false;
        }

        catalog = new CrossFireUccAssetCatalog(objectsByName, texturesById);
        return true;
    }

    public static IReadOnlyList<CrossFireUccMapTextureMapping> ExtractMapTextureMappings(
        byte[] mapData,
        CrossFireUccAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(mapData);
        ArgumentNullException.ThrowIfNull(catalog);
        if (mapData.Length == 0 || mapData.Length > MaximumMapBytes)
        {
            return [];
        }

        int mapVersion = mapData.Length >= sizeof(int)
            ? BinaryPrimitives.ReadInt32LittleEndian(mapData.AsSpan(0, sizeof(int)))
            : 0;
        string serializedText = Encoding.Latin1.GetString(mapData);
        var mappings = new List<CrossFireUccMapTextureMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match instanceMatch in ObjectInstanceRegex.Matches(serializedText))
        {
            int instanceOffset = instanceMatch.Index;
            int assetOffset = instanceOffset + SerializedAssetFieldOffset;
            string serializedAssetName = ReadFixedAnsiField(mapData, assetOffset, ObjectNameFieldBytes);
            if (serializedAssetName.Length == 0 ||
                !TryResolveObjectAsset(serializedAssetName, catalog.ObjectsByName, out CrossFireUccObjectAsset? asset) ||
                asset is null)
            {
                continue;
            }

            var candidates = new Dictionary<int, int>();
            int searchStart = Math.Max(0, instanceOffset + PointerSearchStart);
            int searchEnd = Math.Min(mapData.Length - 3 * sizeof(int), instanceOffset + PointerSearchEnd);
            for (int offset = searchStart; offset <= searchEnd; offset++)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(mapData.AsSpan(offset, sizeof(int))) != ObjectClassId ||
                    BinaryPrimitives.ReadInt32LittleEndian(mapData.AsSpan(offset + sizeof(int), sizeof(int))) != asset.RecordId - 1)
                {
                    continue;
                }

                int serializedTextureSelector = BinaryPrimitives.ReadInt32LittleEndian(
                    mapData.AsSpan(offset + 2 * sizeof(int), sizeof(int)));
                if (TryResolveTextureSelector(serializedTextureSelector, catalog, out _))
                {
                    candidates.TryAdd(serializedTextureSelector, offset);
                }
            }

            bool hasPackedTextureSelection = TryReadVersionedTextureSelection(
                mapData,
                instanceOffset,
                mapVersion,
                out int packedTextureId);
            bool hasExplicitObjectReference;
            int textureSelectorValue;
            int textureRecordId;
            int referenceOffset;
            if (mapVersion == 16)
            {
                // Version 16 stores a zero-based texture selector in the upper
                // half of a packed 32-bit field. Other nearby triples can look
                // like an object-table reference, so the packed selector is
                // primary.
                if (!hasPackedTextureSelection ||
                    !TryResolveTextureSelector(packedTextureId, catalog, out textureRecordId))
                {
                    continue;
                }

                textureSelectorValue = packedTextureId;
                hasExplicitObjectReference = candidates.TryGetValue(textureSelectorValue, out referenceOffset);
                if (!hasExplicitObjectReference)
                {
                    referenceOffset = instanceOffset + 34;
                }
            }
            else if (candidates.Count == 1)
            {
                KeyValuePair<int, int> candidate = candidates.Single();
                textureSelectorValue = candidate.Key;
                if (!TryResolveTextureSelector(textureSelectorValue, catalog, out textureRecordId))
                {
                    continue;
                }

                referenceOffset = candidate.Value;
                hasExplicitObjectReference = true;

                if (hasPackedTextureSelection && packedTextureId != textureSelectorValue)
                {
                    continue;
                }
            }
            else if (candidates.Count == 0 &&
                     hasPackedTextureSelection &&
                     TryResolveTextureSelector(packedTextureId, catalog, out textureRecordId))
            {
                textureSelectorValue = packedTextureId;
                referenceOffset = instanceOffset + 32;
                hasExplicitObjectReference = false;
            }
            else
            {
                continue;
            }

            CrossFireUccTextureAsset? texture = textureRecordId > 0
                ? catalog.TexturesById.GetValueOrDefault(textureRecordId)
                : null;
            string instanceName = instanceMatch.Value;
            string mappingKey = $"{instanceName}\n{asset.RecordId}\n{textureSelectorValue}";
            if (seen.Add(mappingKey))
            {
                mappings.Add(new CrossFireUccMapTextureMapping(
                    instanceName,
                    serializedAssetName,
                    asset.Name,
                    asset.RecordId,
                    textureSelectorValue,
                    textureRecordId,
                    asset.ModelReference,
                    texture?.TextureReference,
                    mapVersion,
                    textureRecordId > 0 && asset.AllowedTextureIds.Contains(textureRecordId),
                    hasExplicitObjectReference,
                    referenceOffset));
            }
        }

        return mappings;
    }

    private static bool TryResolveTextureSelector(
        int selectorValue,
        CrossFireUccAssetCatalog catalog,
        out int textureRecordId)
    {
        textureRecordId = 0;
        if (selectorValue < 0 || selectorValue == ushort.MaxValue)
        {
            // Signed -1 / packed 0xFFFF are the no-selection sentinels. Zero
            // itself is a valid selector for CFT record ID 1.
            return selectorValue is -1 or ushort.MaxValue;
        }

        int resolvedRecordId = selectorValue + 1;
        if (!catalog.TexturesById.ContainsKey(resolvedRecordId))
        {
            return false;
        }

        textureRecordId = resolvedRecordId;
        return true;
    }

    private static bool TryReadVersionedTextureSelection(
        byte[] data,
        int instanceOffset,
        int mapVersion,
        out int textureRecordId)
    {
        textureRecordId = 0;
        if (mapVersion == 14)
        {
            int offset = instanceOffset + 32;
            if (offset < 0 || offset > data.Length - sizeof(int))
            {
                return false;
            }

            textureRecordId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
            return textureRecordId >= 0 || textureRecordId == -1;
        }

        if (mapVersion == 16)
        {
            int offset = instanceOffset + 34;
            if (offset < 0 || offset > data.Length - sizeof(ushort))
            {
                return false;
            }

            textureRecordId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
            return true;
        }

        return false;
    }

    private static bool TryResolveObjectAsset(
        string serializedAssetName,
        IReadOnlyDictionary<string, CrossFireUccObjectAsset> objectsByName,
        out CrossFireUccObjectAsset? asset)
    {
        if (objectsByName.TryGetValue(serializedAssetName, out asset))
        {
            return true;
        }

        int separator = serializedAssetName.LastIndexOf('_');
        if (separator <= 0 ||
            !int.TryParse(
                serializedAssetName.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            asset = null;
            return false;
        }

        return objectsByName.TryGetValue(serializedAssetName[..separator], out asset);
    }

    private static string ReadFixedAnsiField(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
        {
            return string.Empty;
        }

        int end = length;
        for (int index = 0; index < length; index++)
        {
            if (data[offset + index] is 0 or 0xFE or 0xFF)
            {
                end = index;
                break;
            }
        }

        return Encoding.Latin1.GetString(data, offset, end).Trim();
    }

    private static HashSet<int> ParseIntegerSet(string value)
    {
        var result = new HashSet<int>();
        foreach (string part in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static string GetString(CrossFireCftRow row, string column)
    {
        return row.Values.TryGetValue(column, out object? value) ? value as string ?? string.Empty : string.Empty;
    }

    private static string NormalizePath(string value)
    {
        return value.Trim().Replace('\\', '/').TrimStart('/');
    }
}
