using System.Buffers.Binary;
using System.IO;

namespace CFRezManager;

internal sealed record CrossFireWeaponPreviewEntry(
    uint ItemId,
    uint PreviewSlot,
    uint ModelGroupId,
    uint ModelId);

internal sealed record CrossFireWeaponModelEntry(
    uint ModelId,
    uint ItemId,
    uint ModelGroupId);

internal sealed record CrossFireWeaponPreviewItemAliasIndex(
    IReadOnlyDictionary<uint, IReadOnlyList<string>> ByWeaponIndex,
    IReadOnlyDictionary<uint, IReadOnlyList<string>> ByLtcLegacyMappingIndex);

/// <summary>
/// Reads the explicit item/group relation between WeaponPreview and WeaponModel
/// BINs. WeaponModel's first and second uint32 fields are model and item IDs;
/// its final field is the model group key used by WeaponPreview.
/// </summary>
internal static class CrossFireWeaponPreviewModelIndexDecoder
{
    private const int MaxDecodedBytes = 8 * 1024 * 1024;
    private const int StandardRecordBytes = 72;
    private const int ShopRecordBytes = 68;
    private const int MaximumPreviewFields = 64;

    public static bool TryDecodePreview(
        byte[] source,
        string resourcePath,
        out CrossFireWeaponPreviewEntry? entry)
    {
        entry = null;
        string normalizedPath = resourcePath.Replace('\\', '/');
        if (!string.Equals(Path.GetExtension(normalizedPath), ".bin", StringComparison.OrdinalIgnoreCase) ||
            !normalizedPath.Contains("/WeaponPreview/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[]? data = LzmaAloneDecoder.TryPrepareData(source, MaxDecodedBytes);
        if (data is null ||
            data.Length < 1 + sizeof(uint) * 4 ||
            (data.Length - 1) % sizeof(uint) != 0)
        {
            return false;
        }

        int fieldCount = (data.Length - 1) / sizeof(uint);
        if (fieldCount is < 4 or > MaximumPreviewFields)
        {
            return false;
        }

        entry = new CrossFireWeaponPreviewEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(13, sizeof(uint))));
        return true;
    }

    public static bool TryDecodeWeaponModelTable(
        byte[] source,
        string resourcePath,
        out IReadOnlyList<CrossFireWeaponModelEntry> entries)
    {
        entries = [];
        string normalizedPath = resourcePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalizedPath);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(stem, "WeaponModel", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("WeaponModel", StringComparison.OrdinalIgnoreCase) &&
              stem["WeaponModel".Length..].All(char.IsDigit)))
        {
            return false;
        }

        byte[]? data = LzmaAloneDecoder.TryPrepareData(source, MaxDecodedBytes);
        if (data is null)
        {
            return false;
        }

        if (data.Length == sizeof(uint))
        {
            return true;
        }

        bool hasHeader = data.Length >= sizeof(uint) &&
                         (data.Length - sizeof(uint)) % StandardRecordBytes == 0;
        bool isShopLayout = !hasHeader && data.Length % ShopRecordBytes == 0;
        if (!hasHeader && !isShopLayout)
        {
            return false;
        }

        int headerBytes = hasHeader ? sizeof(uint) : 0;
        int recordBytes = hasHeader ? StandardRecordBytes : ShopRecordBytes;
        int fieldCount = recordBytes / sizeof(uint);
        int recordCount = (data.Length - headerBytes) / recordBytes;
        var decoded = new CrossFireWeaponModelEntry[recordCount];
        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            int recordOffset = headerBytes + recordIndex * recordBytes;
            decoded[recordIndex] = new CrossFireWeaponModelEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset, sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset + sizeof(uint), sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset + (fieldCount - 1) * sizeof(uint), sizeof(uint))));
        }

        entries = decoded;
        return true;
    }

    public static CrossFireWeaponPreviewItemAliasIndex BuildItemAliases(
        IEnumerable<CrossFireWeaponPreviewEntry> previews,
        IEnumerable<CrossFireWeaponModelEntry> models,
        IReadOnlySet<uint> ltcWeaponIndexIds,
        IReadOnlySet<uint> ltcWeaponLegacyMappingIndexIds)
    {
        CrossFireWeaponPreviewEntry[] previewEntries = previews.ToArray();
        CrossFireWeaponModelEntry[] modelEntries = models.ToArray();
        var modelsByGroupAndItem = modelEntries
            .Where(model => model.ModelGroupId > 0 && model.ItemId > 0 && model.ModelId > 0)
            .GroupBy(model => (model.ModelGroupId, model.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(model => model.ModelId).Distinct().ToArray());
        var modelIdsByItem = modelEntries
            .Where(model => model.ItemId > 0 && model.ModelId > 0)
            .GroupBy(model => model.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(model => model.ModelId).Distinct().ToArray());
        var modelTableGroups = modelEntries
            .Where(model => model.ModelGroupId > 0)
            .Select(model => model.ModelGroupId)
            .ToHashSet();
        var previewModelIdsByGroupAndItem = previewEntries
            .Where(preview => preview.ModelGroupId > 0 && preview.ItemId > 0 && preview.ModelId > 0)
            .GroupBy(preview => (preview.ModelGroupId, preview.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(preview => preview.ModelId).Distinct().ToArray());
        var aliasesByWeaponIndex = new Dictionary<uint, HashSet<string>>();
        var aliasesByLtcLegacyMappingIndex = new Dictionary<uint, HashSet<string>>();

        static void AddAlias(
            IDictionary<uint, HashSet<string>> aliasesByTarget,
            uint target,
            uint itemId)
        {
            if (!aliasesByTarget.TryGetValue(target, out HashSet<string>? aliases))
            {
                aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                aliasesByTarget.Add(target, aliases);
            }

            aliases.Add("item:" + itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (CrossFireWeaponPreviewEntry preview in previewEntries)
        {
            var key = (preview.ModelGroupId, preview.ItemId);
            if (preview.ItemId == 0 || preview.ModelGroupId == 0 ||
                !modelsByGroupAndItem.TryGetValue(key, out uint[]? modelIds) ||
                modelIds.Length != 1)
            {
                continue;
            }

            if (modelIds[0] != preview.ItemId &&
                previewModelIdsByGroupAndItem.TryGetValue(key, out uint[]? previewModelIds) &&
                previewModelIds.Any(previewModelId => previewModelId != modelIds[0]))
            {
                continue;
            }

            uint modelId = modelIds[0];
            if (ltcWeaponIndexIds.Contains(modelId))
            {
                AddAlias(aliasesByWeaponIndex, modelId, preview.ItemId);
            }
            else if (ltcWeaponLegacyMappingIndexIds.Contains(modelId))
            {
                if (ltcWeaponIndexIds.Contains(preview.ItemId))
                {
                    AddAlias(aliasesByWeaponIndex, preview.ItemId, preview.ItemId);
                }
                else
                {
                    AddAlias(aliasesByLtcLegacyMappingIndex, modelId, preview.ItemId);
                }
            }
        }

        foreach (CrossFireWeaponPreviewEntry preview in previewEntries)
        {
            if (preview.ItemId == 0 || preview.ModelGroupId == 0 ||
                modelTableGroups.Contains(preview.ModelGroupId) ||
                !ltcWeaponIndexIds.Contains(preview.ItemId))
            {
                continue;
            }

            if (modelIdsByItem.TryGetValue(preview.ItemId, out uint[]? modelIds) &&
                modelIds.Any(modelId =>
                    modelId != preview.ItemId &&
                    (ltcWeaponIndexIds.Contains(modelId) || ltcWeaponLegacyMappingIndexIds.Contains(modelId))))
            {
                continue;
            }

            AddAlias(aliasesByWeaponIndex, preview.ItemId, preview.ItemId);
        }

        return new CrossFireWeaponPreviewItemAliasIndex(
            Freeze(aliasesByWeaponIndex),
            Freeze(aliasesByLtcLegacyMappingIndex));
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<string>> Freeze(
        IDictionary<uint, HashSet<string>> aliases)
    {
        return aliases.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
