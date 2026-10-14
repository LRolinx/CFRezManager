namespace CFRezManager;

internal sealed record CrossFireWeaponSkinShiftLink(
    int CftRecordId,
    int ItemIndex,
    int ButeIndex,
    int SkinType,
    string SkinSuffix,
    int ScriptIndex,
    int UnableVfx,
    int TagIndex,
    int UseCheckWeapon,
    int ChangeButeIndex,
    int UnableSound);

internal sealed record CrossFireWeaponBaseItem(
    int CftRecordId,
    int ItemIndex,
    string WeaponName,
    int VvipIndex,
    int ButeIndex);

internal sealed record CrossFireWeaponItemIdentity(
    int CftRecordId,
    int ItemIndex,
    string Name,
    string ShortName,
    int ItemButeType,
    int ItemIndexInBute,
    string ItemCode);

internal sealed record CrossFireWeaponSkinCatalog(
    IReadOnlyDictionary<int, CrossFireWeaponSkinShiftLink> LinksByScriptIndex,
    IReadOnlySet<int> AmbiguousScriptIndices,
    IReadOnlyDictionary<int, IReadOnlyList<CrossFireWeaponBaseItem>> BaseItemsByItemIndex,
    IReadOnlyDictionary<int, IReadOnlyList<CrossFireWeaponItemIdentity>> ItemIdentitiesByItemIndex);

internal sealed record CrossFireWeaponSkinItemMapping(
    CrossFireWeaponSkinShiftEntry ScriptEntry,
    CrossFireWeaponSkinShiftLink Link,
    IReadOnlyList<CrossFireWeaponBaseItem> MatchingBaseItems,
    IReadOnlyList<CrossFireWeaponItemIdentity> MatchingItemIdentities);

/// <summary>
/// Joins the VVIP skin-shift CFT row (item/bute/suffix/script index) to the
/// decoded skin script and the VVIP base-item CFTs. Script references are
/// joined by the explicit script index; item and bute indexes are retained as
/// game-side identity keys rather than inferred from localized names.
/// </summary>
internal static class CrossFireWeaponSkinCatalogDecoder
{
    private static readonly string[] RequiredShiftColumns =
    [
        "_ITEM_INDEX",
        "_BUTE_INDEX",
        "_SKIN_TYPE",
        "_SKIN_SUFFIX",
        "_SCRIPT_INDEX",
        "_UNABLE_VFX",
        "_TAG_INDEX",
        "_USE_CHECK_WEAPON",
        "_CHANGE_BUTE_INDEX",
        "_UNABLE_SOUND"
    ];

    private static readonly string[] RequiredBaseItemColumns =
    [
        "_ITEM_INDEX",
        "_WEAPON_NAME",
        "_VVIP_INDEX",
        "_BUTE_INDEX"
    ];

    private static readonly string[] RequiredItemIdentityColumns =
    [
        "_ITEM_INDEX",
        "_NAME",
        "_SHORT_NAME",
        "_ItemButeType",
        "_ItemIndexInBute",
        "_ITEM_CODE"
    ];

    public static bool TryDecodeShiftLinks(
        byte[] data,
        out IReadOnlyList<CrossFireWeaponSkinShiftLink> links)
    {
        links = [];
        if (!CrossFireCftTableDecoder.TryDecode(data, out CrossFireCftTable? table) ||
            table is null ||
            !HasColumns(table, RequiredShiftColumns))
        {
            return false;
        }

        var decoded = new List<CrossFireWeaponSkinShiftLink>(table.Rows.Count);
        foreach (CrossFireCftRow row in table.Rows)
        {
            decoded.Add(new CrossFireWeaponSkinShiftLink(
                row.RecordId,
                GetInt32(row, "_ITEM_INDEX"),
                GetInt32(row, "_BUTE_INDEX"),
                GetInt32(row, "_SKIN_TYPE"),
                GetString(row, "_SKIN_SUFFIX"),
                GetInt32(row, "_SCRIPT_INDEX"),
                GetInt32(row, "_UNABLE_VFX"),
                GetInt32(row, "_TAG_INDEX"),
                GetInt32(row, "_USE_CHECK_WEAPON"),
                GetInt32(row, "_CHANGE_BUTE_INDEX"),
                GetInt32(row, "_UNABLE_SOUND")));
        }

        links = decoded;
        return true;
    }

    public static bool TryDecodeBaseItems(
        byte[] data,
        out IReadOnlyList<CrossFireWeaponBaseItem> baseItems)
    {
        baseItems = [];
        if (!CrossFireCftTableDecoder.TryDecode(data, out CrossFireCftTable? table) ||
            table is null ||
            !HasColumns(table, RequiredBaseItemColumns))
        {
            return false;
        }

        var decoded = new List<CrossFireWeaponBaseItem>(table.Rows.Count);
        foreach (CrossFireCftRow row in table.Rows)
        {
            int itemIndex = GetInt32(row, "_ITEM_INDEX");
            string weaponName = GetString(row, "_WEAPON_NAME").Trim();
            if (itemIndex <= 0 || weaponName.Length == 0)
            {
                continue;
            }

            decoded.Add(new CrossFireWeaponBaseItem(
                row.RecordId,
                itemIndex,
                weaponName,
                GetInt32(row, "_VVIP_INDEX"),
                GetInt32(row, "_BUTE_INDEX")));
        }

        baseItems = decoded;
        return true;
    }

    public static bool TryDecodeItemIdentities(
        byte[] data,
        out IReadOnlyList<CrossFireWeaponItemIdentity> itemIdentities)
    {
        itemIdentities = [];
        if (!CrossFireCftTableDecoder.TryDecode(data, out CrossFireCftTable? table) ||
            table is null ||
            !HasColumns(table, RequiredItemIdentityColumns))
        {
            return false;
        }

        var decoded = new List<CrossFireWeaponItemIdentity>(table.Rows.Count);
        foreach (CrossFireCftRow row in table.Rows)
        {
            int itemIndex = GetInt32(row, "_ITEM_INDEX");
            string name = GetString(row, "_NAME").Trim();
            if (itemIndex <= 0 || name.Length == 0)
            {
                continue;
            }

            decoded.Add(new CrossFireWeaponItemIdentity(
                row.RecordId,
                itemIndex,
                name,
                GetString(row, "_SHORT_NAME").Trim(),
                GetInt32(row, "_ItemButeType"),
                GetInt32(row, "_ItemIndexInBute"),
                GetString(row, "_ITEM_CODE").Trim()));
        }

        itemIdentities = decoded;
        return true;
    }

    public static CrossFireWeaponSkinCatalog CreateCatalog(
        IEnumerable<CrossFireWeaponSkinShiftLink> links,
        IEnumerable<CrossFireWeaponBaseItem> baseItems,
        IEnumerable<CrossFireWeaponItemIdentity>? itemIdentities = null,
        IEnumerable<int>? ambiguousScriptIndices = null)
    {
        var ambiguous = ambiguousScriptIndices is null
            ? new HashSet<int>()
            : new HashSet<int>(ambiguousScriptIndices);
        var linksByScriptIndex = new Dictionary<int, CrossFireWeaponSkinShiftLink>();
        foreach (CrossFireWeaponSkinShiftLink link in links)
        {
            if (link.ScriptIndex <= 0 || ambiguous.Contains(link.ScriptIndex))
            {
                continue;
            }

            if (!linksByScriptIndex.TryAdd(link.ScriptIndex, link) &&
                !Equals(linksByScriptIndex[link.ScriptIndex], link))
            {
                linksByScriptIndex.Remove(link.ScriptIndex);
                ambiguous.Add(link.ScriptIndex);
            }
        }

        var baseItemsByItemIndex = baseItems
            .Where(item => item.ItemIndex > 0)
            .GroupBy(item => item.ItemIndex)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CrossFireWeaponBaseItem>)group
                    .GroupBy(item => (item.ButeIndex, item.VvipIndex, item.WeaponName), BaseItemIdentityComparer.Instance)
                    .Select(identityGroup => identityGroup.First())
                    .OrderBy(item => item.ButeIndex)
                    .ThenBy(item => item.WeaponName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

        var itemIdentitiesByItemIndex = (itemIdentities ?? [])
            .Where(item => item.ItemIndex > 0)
            .GroupBy(item => item.ItemIndex)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CrossFireWeaponItemIdentity>)group
                    .GroupBy(
                        item => (item.Name, item.ShortName, item.ItemButeType, item.ItemIndexInBute, item.ItemCode),
                        ItemIdentityComparer.Instance)
                    .Select(identityGroup => identityGroup.First())
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ItemIndexInBute)
                    .ToArray());

        return new CrossFireWeaponSkinCatalog(
            linksByScriptIndex,
            ambiguous,
            baseItemsByItemIndex,
            itemIdentitiesByItemIndex);
    }

    public static IReadOnlyList<CrossFireWeaponSkinItemMapping> Join(
        CrossFireWeaponSkinCatalog catalog,
        CrossFireWeaponSkinShiftDocument scriptDocument)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scriptDocument);

        var mappings = new List<CrossFireWeaponSkinItemMapping>(scriptDocument.Entries.Count);
        foreach (CrossFireWeaponSkinShiftEntry scriptEntry in scriptDocument.Entries)
        {
            if (!catalog.LinksByScriptIndex.TryGetValue(scriptEntry.ScriptIndex, out CrossFireWeaponSkinShiftLink? link))
            {
                continue;
            }

            IReadOnlyList<CrossFireWeaponBaseItem> matchingBaseItems =
                catalog.BaseItemsByItemIndex.TryGetValue(link.ItemIndex, out IReadOnlyList<CrossFireWeaponBaseItem>? candidates)
                    ? candidates.Where(item => item.ButeIndex == link.ButeIndex).ToArray()
                    : [];
            IReadOnlyList<CrossFireWeaponItemIdentity> matchingItemIdentities =
                catalog.ItemIdentitiesByItemIndex.TryGetValue(link.ItemIndex, out IReadOnlyList<CrossFireWeaponItemIdentity>? identities)
                    ? identities
                    : [];

            mappings.Add(new CrossFireWeaponSkinItemMapping(
                scriptEntry,
                link,
                matchingBaseItems,
                matchingItemIdentities));
        }

        return mappings;
    }

    private static bool HasColumns(CrossFireCftTable table, IReadOnlyList<string> requiredColumns)
    {
        return requiredColumns.All(required => table.Columns.Contains(required, StringComparer.OrdinalIgnoreCase));
    }

    private static int GetInt32(CrossFireCftRow row, string column)
    {
        return row.Values.TryGetValue(column, out object? value) &&
               value is int integer
            ? integer
            : 0;
    }

    private static string GetString(CrossFireCftRow row, string column)
    {
        return row.Values.TryGetValue(column, out object? value) ? value as string ?? string.Empty : string.Empty;
    }

    private sealed class BaseItemIdentityComparer : IEqualityComparer<(int ButeIndex, int VvipIndex, string WeaponName)>
    {
        public static BaseItemIdentityComparer Instance { get; } = new();

        public bool Equals(
            (int ButeIndex, int VvipIndex, string WeaponName) left,
            (int ButeIndex, int VvipIndex, string WeaponName) right)
        {
            return left.ButeIndex == right.ButeIndex &&
                   left.VvipIndex == right.VvipIndex &&
                   string.Equals(left.WeaponName, right.WeaponName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((int ButeIndex, int VvipIndex, string WeaponName) value)
        {
            return HashCode.Combine(
                value.ButeIndex,
                value.VvipIndex,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.WeaponName));
        }
    }

    private sealed class ItemIdentityComparer : IEqualityComparer<(string Name, string ShortName, int ItemButeType, int ItemIndexInBute, string ItemCode)>
    {
        public static ItemIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string Name, string ShortName, int ItemButeType, int ItemIndexInBute, string ItemCode) left,
            (string Name, string ShortName, int ItemButeType, int ItemIndexInBute, string ItemCode) right)
        {
            return left.ItemButeType == right.ItemButeType &&
                   left.ItemIndexInBute == right.ItemIndexInBute &&
                   string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ShortName, right.ShortName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ItemCode, right.ItemCode, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(
            (string Name, string ShortName, int ItemButeType, int ItemIndexInBute, string ItemCode) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ShortName),
                value.ItemButeType,
                value.ItemIndexInBute,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ItemCode));
        }
    }
}
