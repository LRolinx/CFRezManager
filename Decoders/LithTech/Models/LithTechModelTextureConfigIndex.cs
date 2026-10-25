using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace CFRezManager;

internal static class LithTechModelTextureConfigIndex
{
    private sealed record VerifiedCftMotionTextureAlias(
        string ParentModelPath,
        string MotionModelPath,
        int SlotIndex,
        string MissingTexturePath,
        string ExistingTexturePath);

    private sealed record VerifiedCftTextureFieldSlotAlias(
        string TableFileName,
        int RowIndex,
        int RecordId,
        string ModelPath,
        string ModelColumn,
        string IdentityAlias,
        int SourceSlotIndex,
        int TargetSlotIndex,
        string[] TextureColumns,
        string MissingTexturePath,
        string ExistingTexturePath,
        string MeshName);

    private sealed record VerifiedCftModelTextureSlotAlias(
        string TargetModelPath,
        int TargetSlotIndex,
        string SourceModelPath,
        int SourceSlotIndex,
        string MeshName);

    private sealed record VerifiedSceneModelTextureSlotAlias(
        string TargetModelPath,
        int TargetSlotIndex,
        string TexturePath,
        IReadOnlyList<string> MeshNames);

    private sealed record VerifiedPlayerViewMeshTextureSlotAlias(
        string FamilyKey,
        string TargetModelPath,
        int TargetSlotIndex,
        string TexturePath,
        IReadOnlyList<string> MeshNames);

    private sealed record CftPlayerViewRoleSlotLayouts(
        CftPlayerViewRoleSlotMap HandOnly,
        CftPlayerViewRoleSlotMap ArmOnly,
        CftPlayerViewRoleSlotMap HandAndArm)
    {
        public CftPlayerViewRoleSlotMap ForReferences(bool hasHand, bool hasArm) => (hasHand, hasArm) switch
        {
            (true, true) => HandAndArm,
            (true, false) => HandOnly,
            (false, true) => ArmOnly,
            _ => CftPlayerViewRoleSlotMap.Empty
        };
    }

    private const int MaxConfigBytes = 1024 * 1024;
    private const int MaxCftBytes = 4 * 1024 * 1024;
    private const int MaxLtcBytes = 16 * 1024 * 1024;
    private const int MaxUccMapBytes = 4 * 1024 * 1024;
    private const int MaxClientShellBytes = 192 * 1024 * 1024;
    private const int MaxInferredTextureBytes = 64 * 1024 * 1024;
    private const int MaxTextureSlotIndex = byte.MaxValue;
    private static readonly ConditionalWeakTable<ExplorerItem, TextureConfigIndex> TextureConfigIndexCache = new();

    private static readonly string[] ConfigExtensions =
    [
        ".cfg",
        ".cft",
        ".enc",
        ".ini",
        ".txt"
    ];

    private static readonly string[] ModelExtensions =
    [
        ".ltb",
        ".lta",
        ".ltc",
        ".dat"
    ];

    private static readonly string[] TextureExtensions =
    [
        ".dtx",
        ".dds",
        ".tga",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".bin"
    ];

    private static readonly string[] LtcPlayerViewModelFieldPrefixes =
    [
        "WLeftHandModelFileName",
        "LeftHandModelFileName",
        "PreViewModelFileName",
        "PViewModelFileName"
    ];

    // The AI Princess Iron CFT points slot 2 at a missing AI_PrincessIron_HAIR
    // path. Its motion hair mesh shares 1,607 of 1,663 UV triangles with the
    // CFT-bound Princess Iron socket hair models; the replacement DTX is the
    // only physical resource at its exact path.
    private static readonly VerifiedCftMotionTextureAlias[] VerifiedCftMotionTextureAliases =
    [
        new(
            "Models/Character/AIMode/AI1_EvilDen/AI_Boss_PrincessIron",
            "Models/Character/AIMode/AI1_EvilDen/AI_Boss_PrincessIron_M-motion",
            2,
            "ModelTextures/Character/AIMode/AI1_EvilDen/AI_PrincessIron_HAIR.dtx",
            "ModelTextures/SOCKETITEM/BASIC/PrincessIron_pc/PrincessIron_PC_HAIR.dtx")
    ];

    // CharacterItem.CFT row 1809 names the SP hair texture in TexFile2, but
    // its only mesh has native slot 0. The same-stem DTX is unique and decodable.
    private static readonly VerifiedCftTextureFieldSlotAlias[] VerifiedCftTextureFieldSlotAliases =
    [
        new(
            "CharacterItem.CFT",
            1808,
            1809,
            "Models/SOCKETITEM/BASIC/Blitz_Buster/Blitz_Buster_Hair_SP",
            "_GRSocketItemModelFile",
            "Blitz_Buster_SP Head",
            1,
            0,
            ["_BLTexFile2", "_GRTexFile2"],
            "ModelTextures/SOCKETITEM/BASIC/Blitz_Buster/Blitz_Buster_HAT_SP.dtx",
            "ModelTextures/SOCKETITEM/BASIC/Blitz_Buster/Blitz_Buster_HAIR_SP.dtx",
            "V214_Blitz_Buster_GR_hiar")
    ];

    // CutScene02 uses the CFT-bound SandDragon materials on embedded dragon meshes.
    // Each target slot is tied to one donor slot by the normalized mesh name and exact UV-triangle layout.
    private static readonly VerifiedCftModelTextureSlotAlias[] VerifiedCftModelTextureSlotAliases =
    [
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark", 0,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon_socket", 0, "Dark_SandDragon_01"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark", 1,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon", 0, "Dark_SandDragon_02"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark", 2,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon", 1, "Dark_SandDragon_03"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark_M-motion", 0,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon_socket", 0, "Dark_SandDragon_01"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark_M-motion", 1,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon", 0, "Dark_SandDragon_02"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark_M-motion", 2,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_DarkSandDragon", 1, "Dark_SandDragon_03"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire", 0,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon_socket", 0, "Dark_SandDragon_01"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire", 1,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon", 0, "Dark_SandDragon_02"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire", 2,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon", 1, "Dark_SandDragon_03"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire_M-motion", 0,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon_socket", 0, "Dark_SandDragon_01"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire_M-motion", 1,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon", 0, "Dark_SandDragon_02"),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire_M-motion", 2,
            "Models/Character/AIMode/AI1_EvilDen/AI_BOSS_FireSandDragon", 1, "Dark_SandDragon_03")
    ];

    // The EvilDen cutscene's three stone meshes share slot 4. Their UV layouts
    // are consistent across physical model copies and sample the scene's stone-gate DTX.
    private static readonly VerifiedSceneModelTextureSlotAlias[] VerifiedSceneModelTextureSlotAliases =
    [
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark", 4,
            "ModelTextures/Character/AIMode/AI1_EvilDen/AI_Stone.dtx", ["stone01", "stone02", "stone03"]),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Dark_M-motion", 4,
            "ModelTextures/Character/AIMode/AI1_EvilDen/AI_Stone.dtx", ["stone01", "stone02", "stone03"]),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire", 4,
            "ModelTextures/Character/AIMode/AI1_EvilDen/AI_Stone.dtx", ["stone01", "stone02", "stone03"]),
        new("Models/Character/AIMode/AI1_EvilDen/AI_EvilDen_CutScene02_Fire_M-motion", 4,
            "ModelTextures/Character/AIMode/AI1_EvilDen/AI_Stone.dtx", ["stone01", "stone02", "stone03"])
    ];

    // These path-scoped aliases cover player-view meshes whose table paths are
    // present but whose slot layouts cannot be represented by the base model.
    // Verify every target mesh UV signature and DTX copy before binding them.
    private static readonly VerifiedPlayerViewMeshTextureSlotAlias[] VerifiedPlayerViewMeshTextureSlotAliases =
    [
        new("ai-shield-card", "Models/PLAYERVIEW/PV-ShieldCard_AI_NEW", 2,
            "TEX/UI/AIMode/AI1PreCrater/CARD.DTX", ["card"]),
        new("ai-shield-card", "Models/PLAYERVIEW/PV-ShieldCard_AI_NEW_BL", 2,
            "TEX/UI/AIMode/AI1PreCrater/CARD.DTX", ["card"]),
        new("ai-shield-card", "Models/PLAYERVIEW/PV-ShieldCard_AI_NEW_GR", 2,
            "TEX/UI/AIMode/AI1PreCrater/CARD.DTX", ["Box003"]),
        new("ai-shield-card", "Models/PLAYERVIEW/PV-ShieldCard_AI_NEW_WOMAN_BL", 2,
            "TEX/UI/AIMode/AI1PreCrater/CARD.DTX", ["Box003"]),
        new("ai-shield-card", "Models/PLAYERVIEW/PV-ShieldCard_AI_NEW_WOMAN_GR", 2,
            "TEX/UI/AIMode/AI1PreCrater/CARD.DTX", ["Box003"]),
        // BF005.LTC row 841 lists these two DTXs for PV-TAC15, but its base
        // model assigns both references to slot 2. The WOMAN side models have
        // stable, distinct body and scope slots; keep this mapping scoped to
        // their exact mesh layout.
        new("tac15-woman-body", "Models/WAVE/Hunter/PV-TAC15_WOMAN_BL", 2,
            "ModelTextures/PLAYERVIEW/PV-TAC15_01.DTX", ["body", "bolt", "Object001"]),
        new("tac15-woman-body", "Models/WAVE/Hunter/PV-TAC15_WOMAN_GR", 2,
            "ModelTextures/PLAYERVIEW/PV-TAC15_01.DTX", ["body", "bolt", "Object001"]),
        new("tac15-woman-scope", "Models/WAVE/Hunter/PV-TAC15_WOMAN_BL", 3,
            "ModelTextures/PLAYERVIEW/PV-TAC15_02.DTX", ["scope"]),
        new("tac15-woman-scope", "Models/WAVE/Hunter/PV-TAC15_WOMAN_GR", 3,
            "ModelTextures/PLAYERVIEW/PV-TAC15_02.DTX", ["scope"])
    ];

    public static Func<IEnumerable<string>, IReadOnlyList<string>>? CreateResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var textureCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return names =>
        {
            string cacheKey = string.Join("|", names.Where(name => !string.IsNullOrWhiteSpace(name)));
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = ResolveExistingTextureReferences(
                Resolve(index, names, textureCache),
                index.ResourcePaths,
                index.TexturePathsByBaseName);
            cache[cacheKey] = result;
            return result;
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            string cacheKey = $"{modelPath}|{slotIndex}";
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = ResolveModelTextureSlot(index, modelPath, slotIndex);
            cache[cacheKey] = result;
            return result;
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            string cacheKey = $"{modelPath}\0{contextKey}\0{slotIndex}";
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = ResolveModelTextureContextSlot(index, modelPath, contextKey, slotIndex);
            cache[cacheKey] = result;
            return result;
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateInferredTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            return modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex
                ? []
                : ResolveInferredModelTextureSlot(index, modelPath, slotIndex);
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateInferredTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            return modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex
                ? []
                : ResolveInferredModelTextureContextSlot(index, modelPath, contextKey, slotIndex);
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateLtcPreviewVariantTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            if (dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.LtcPreviewVariantByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateLtcPreviewVariantTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{modelPath}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.LtcPreviewVariantByModelContext.TryGetValue(key, out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreatePreviewNormalizedBasenameTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            if (dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.PreviewNormalizedBasenameByModelPath.TryGetValue(
                       modelPath,
                       out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateVariantSlotUvTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            if (dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.VariantSlotUvByModelPath.TryGetValue(
                       modelPath,
                       out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateMotionParentSlotTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            if (dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.MotionParentSlotByModelPath.TryGetValue(
                       modelPath,
                       out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateMotionParentContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{modelPath}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.MotionParentSlotByModelContext.TryGetValue(
                       key,
                       out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateCftIdentityModelAliasTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return [];
            }

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{NormalizeExtensionlessModelReference(modelPath)}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return dictionary.CftIdentityModelAliasByModelContext.TryGetValue(
                       key,
                       out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) &&
                   !string.IsNullOrWhiteSpace(texture)
                ? [texture]
                : [];
        };
    }

    public static Func<string, bool>? CreateModelTextureSlotMappingPresenceResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        return modelReference =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            return modelPath.Length > 0 &&
                   (dictionary.ModelsWithSlotSpecificBindings.Contains(modelPath) ||
                    dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? slots) &&
                    slots.Count > 0 ||
                    dictionary.CftPlayerViewRoleSlotsByModelPath.ContainsKey(modelPath) ||
                    dictionary.EquivalentArmByModelPath.ContainsKey(modelPath) ||
                    dictionary.LtcPreviewVariantByModelPath.ContainsKey(modelPath) ||
                    dictionary.PreviewNormalizedBasenameByModelPath.ContainsKey(modelPath) ||
                    dictionary.VariantSlotUvByModelPath.ContainsKey(modelPath) ||
                    dictionary.MotionParentSlotByModelPath.ContainsKey(modelPath) ||
                    dictionary.CftIdentityModelAliasByModelContext.Keys.Any(key =>
                        key.StartsWith(NormalizeExtensionlessModelReference(modelPath) + "\0", StringComparison.OrdinalIgnoreCase)) ||
                    dictionary.EquivalentArmByModelContext.Keys.Any(key =>
                        key.StartsWith(modelPath + "\0", StringComparison.OrdinalIgnoreCase)) ||
                    dictionary.LtcPreviewVariantByModelContext.Keys.Any(key =>
                        key.StartsWith(modelPath + "\0", StringComparison.OrdinalIgnoreCase)) ||
                    dictionary.MotionParentSlotByModelContext.Keys.Any(key =>
                        key.StartsWith(modelPath + "\0", StringComparison.OrdinalIgnoreCase)) ||
                    dictionary.WeaponPreviewVariantByModelContext.Keys.Any(key =>
                        key.StartsWith(modelPath + "\0", StringComparison.OrdinalIgnoreCase)));
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateEquivalentArmTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty) return null;
        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex) return [];
            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{modelPath}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) && directSlots.ContainsKey(slotIndex))
                return [];
            if (dictionary.EquivalentArmByModelContext.TryGetValue(key, out Dictionary<int, string?>? slots) &&
                slots.TryGetValue(slotIndex, out string? texture))
            {
                return !string.IsNullOrWhiteSpace(texture) ? [texture] : [];
            }

            return dictionary.EquivalentArmByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? pathSlots) &&
                   pathSlots.TryGetValue(slotIndex, out string? pathTexture) && !string.IsNullOrWhiteSpace(pathTexture)
                ? [pathTexture] : [];
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateCftPlayerViewRoleTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty) return null;
        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex)
                return [];

            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{modelPath}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) &&
                directSlots.ContainsKey(slotIndex))
            {
                return [];
            }

            return ResolveCftPlayerViewRoleTextureContextSlot(dictionary, modelPath, contextKey, slotIndex);
        };
    }

    public static Func<string, string, int, IReadOnlyList<string>>? CreateWeaponPreviewVariantTextureContextSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty) return null;
        return (modelReference, context, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            string contextKey = NormalizeLooseKey(context);
            if (modelPath.Length == 0 || contextKey.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex) return [];
            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            string key = $"{modelPath}\0{contextKey}";
            if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) && directSlots.ContainsKey(slotIndex))
                return [];
            return dictionary.WeaponPreviewVariantByModelContext.TryGetValue(key, out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) && !string.IsNullOrWhiteSpace(texture)
                ? [texture] : [];
        };
    }

    public static Func<string, int, IReadOnlyList<string>>? CreateWeaponPreviewVariantTextureSlotResolver(ExplorerItem root)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty) return null;
        return (modelReference, slotIndex) =>
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (modelPath.Length == 0 || slotIndex is < 0 or > MaxTextureSlotIndex) return [];
            ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
            return dictionary.WeaponPreviewVariantByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? slots) &&
                   slots.TryGetValue(slotIndex, out string? texture) && !string.IsNullOrWhiteSpace(texture)
                ? [texture] : [];
        };
    }

    public static (int ResourceCopies, int UniqueDocuments, int DecodedDocuments, int MotionReferences)
        WriteLtcMotionReferenceAuditReport(ExplorerItem root, string outputPath)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        var documentGroups = index.LtcItems
            .GroupBy(item => item.ArchiveFile?.Md5 is { Length: > 0 } md5
                ? "md5:" + md5
                : "path:" + NormalizeKey(item.OutputRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("source_resources\trow_index\trow_type\tmotion_field\tmotion_model\tpeer_models\ttexture_fields\tidentity_aliases");
        int decodedDocuments = 0;
        int motionReferences = 0;
        foreach (IGrouping<string, ExplorerItem> group in documentGroups)
        {
            ExplorerItem item = group.First();
            byte[]? data = TryReadItemBytes(item, MaxLtcBytes);
            if (data is null ||
                !CrossFireLtcDecoder.TryDecodeNativeText(
                    data,
                    out CrossFireLtcTextDocument? document,
                    out _) ||
                document is null)
            {
                continue;
            }

            decodedDocuments++;
            IReadOnlyList<CrossFireLtcMotionModelReferenceDiagnostic> references;
            try
            {
                references = CrossFireLtcModelTextureMappingDecoder.ExtractMotionModelReferences(document.Text);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (references.Count == 0)
            {
                continue;
            }

            string sources = string.Join(" || ", group.Select(source =>
            {
                string archive = source.Archive?.FilePath ?? "local";
                string resource = source.ArchiveFile?.FullPath ?? source.OutputRelativePath;
                string hash = source.ArchiveFile?.Md5 ?? "local";
                return $"{archive}:{resource}#{hash}";
            }).Order(StringComparer.OrdinalIgnoreCase));
            foreach (CrossFireLtcMotionModelReferenceDiagnostic reference in references)
            {
                motionReferences++;
                writer.WriteLine(string.Join("\t", [
                    EscapeTsv(sources),
                    reference.RowIndex.ToString(CultureInfo.InvariantCulture),
                    EscapeTsv(reference.RowType),
                    EscapeTsv(reference.MotionField),
                    EscapeTsv(reference.MotionModelReference),
                    EscapeTsv(string.Join(" || ", reference.PeerModelReferences)),
                    EscapeTsv(string.Join(" || ", reference.TextureReferences.Select(texture => $"{texture.FieldName}:{texture.Path}"))),
                    EscapeTsv(string.Join(" || ", reference.IdentityAliases))
                ]));
            }
        }

        return (index.LtcItems.Count, documentGroups.Length, decodedDocuments, motionReferences);
    }

    public static (int ResourceCopies, int UniqueDocuments, int DecodedDocuments, int ModelRows)
        WriteLtcModelTextureReferenceAuditReport(ExplorerItem root, string outputPath)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        IGrouping<string, ExplorerItem>[] documentGroups = index.LtcItems
            .GroupBy(item => item.ArchiveFile?.Md5 is { Length: > 0 } md5
                ? "md5:" + md5
                : "path:" + NormalizeKey(item.OutputRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("source_resources\trow_index\tlegacy_mapping_index\trow_type\tmodel_field\tmodel_reference\ttexture_fields\tauxiliary_texture_references\tidentity_aliases");
        int decodedDocuments = 0;
        int modelRows = 0;
        foreach (IGrouping<string, ExplorerItem> group in documentGroups)
        {
            ExplorerItem item = group.First();
            byte[]? data = TryReadItemBytes(item, MaxLtcBytes);
            if (data is null ||
                !CrossFireLtcDecoder.TryDecodeNativeText(
                    data,
                    out CrossFireLtcTextDocument? document,
                    out _) ||
                document is null)
            {
                continue;
            }

            decodedDocuments++;
            IReadOnlyList<CrossFireLtcModelTextureMapping> mappings;
            try
            {
                mappings = CrossFireLtcModelTextureMappingDecoder.Extract(document.Text);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            string sources = string.Join(" || ", group.Select(source =>
            {
                string archive = source.Archive?.FilePath ?? "local";
                string resource = source.ArchiveFile?.FullPath ?? source.OutputRelativePath;
                string hash = source.ArchiveFile?.Md5 ?? "local";
                return $"{archive}:{resource}#{hash}";
            }).Order(StringComparer.OrdinalIgnoreCase));
            foreach (CrossFireLtcModelTextureMapping mapping in mappings)
            {
                modelRows++;
                string textures = string.Join(" || ", mapping.TextureFieldReferences
                    .Select(texture => $"{texture.FieldName}:{texture.Path}"));
                writer.WriteLine(string.Join("\t", [
                    EscapeTsv(sources),
                    mapping.RowIndex.ToString(CultureInfo.InvariantCulture),
                    mapping.LegacyMappingIndex.ToString(CultureInfo.InvariantCulture),
                    EscapeTsv(mapping.RowType),
                    EscapeTsv(mapping.ModelField),
                    EscapeTsv(mapping.ModelReference),
                    EscapeTsv(textures),
                    EscapeTsv(string.Join(" || ", mapping.AuxiliaryTextureReferences)),
                    EscapeTsv(string.Join(" || ", mapping.IdentityAliases))
                ]));
            }
        }

        return (index.LtcItems.Count, documentGroups.Length, decodedDocuments, modelRows);
    }

    public static (int ResourceCopies, int UniqueDocuments, int DecodedDocuments, int MotionLinks)
        WriteCftMotionReferenceAuditReport(ExplorerItem root, string outputPath)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        IGrouping<string, TextureConfigItem>[] documentGroups = index.AllItems
            .Where(item => string.Equals(Path.GetExtension(item.Path), ".cft", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Item.ArchiveFile?.Md5 is { Length: > 0 } md5
                ? "md5:" + md5
                : "path:" + NormalizeKey(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int resourceCopies = documentGroups.Sum(group => group.Count());
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("source_resources\trow_index\trecord_id\tmodel_column\tparent_model\tmotion_model\ttexture_fields\tidentity_aliases");
        int decodedDocuments = 0;
        int motionLinks = 0;
        foreach (IGrouping<string, TextureConfigItem> group in documentGroups)
        {
            TextureConfigItem configItem = group.First();
            byte[]? data = TryReadItemBytes(configItem.Item, MaxCftBytes);
            if (data is null)
            {
                continue;
            }

            IReadOnlyList<CftModelTextureMapping> mappings;
            try
            {
                if (CrossFireCftTableDecoder.TryDecode(data, out _))
                {
                    decodedDocuments++;
                }

                mappings = CftModelTextureMappingDecoder.Extract(data);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            string sources = string.Join(" || ", group.Select(source =>
            {
                string archive = source.Item.Archive?.FilePath ?? "local";
                string resource = source.Item.ArchiveFile?.FullPath ?? source.Item.OutputRelativePath;
                string hash = source.Item.ArchiveFile?.Md5 ?? "local";
                return $"{archive}:{resource}#{hash}";
            }).Order(StringComparer.OrdinalIgnoreCase));
            foreach (CftModelTextureMapping mapping in mappings.Where(mapping =>
                         mapping.HasTypedRowContext && mapping.RelatedModelReferences is { Count: > 0 }))
            {
                foreach (string motionModel in mapping.RelatedModelReferences!
                             .Where(reference => Path.GetFileNameWithoutExtension(reference)
                                 .Contains("motion", StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    motionLinks++;
                    string recordId = mapping.RecordId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    string textures = string.Join(" || ", mapping.TextureReferences
                        .Select(texture => $"{texture.Column}:{texture.Path}"));
                    writer.WriteLine(string.Join("\t", [
                        EscapeTsv(sources),
                        mapping.RowIndex.ToString(CultureInfo.InvariantCulture),
                        recordId,
                        EscapeTsv(mapping.ModelColumn),
                        EscapeTsv(mapping.ModelReference),
                        EscapeTsv(motionModel),
                        EscapeTsv(textures),
                        EscapeTsv(string.Join(" || ", mapping.IdentityAliases))
                    ]));
                }
            }
        }

        return (resourceCopies, documentGroups.Length, decodedDocuments, motionLinks);
    }

    public static (
        int ResourceCopies,
        int UniqueDocuments,
        int DecodedDocuments,
        int ModelRows,
        int RelatedModelReferences)
        WriteCftModelTextureReferenceAuditReport(ExplorerItem root, string outputPath)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        IGrouping<string, TextureConfigItem>[] documentGroups = index.AllItems
            .Where(item => string.Equals(Path.GetExtension(item.Path), ".cft", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Item.ArchiveFile?.Md5 is { Length: > 0 } md5
                ? "md5:" + md5
                : "path:" + NormalizeKey(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int resourceCopies = documentGroups.Sum(group => group.Count());
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("source_resources\trow_index\trecord_id\tmodel_column\tmodel_reference\trelated_model_references\ttexture_fields\tidentity_aliases");
        int decodedDocuments = 0;
        int modelRows = 0;
        int relatedModelReferences = 0;
        foreach (IGrouping<string, TextureConfigItem> group in documentGroups)
        {
            TextureConfigItem configItem = group.First();
            byte[]? data = TryReadItemBytes(configItem.Item, MaxCftBytes);
            if (data is null)
            {
                continue;
            }

            IReadOnlyList<CftModelTextureMapping> mappings;
            try
            {
                if (CrossFireCftTableDecoder.TryDecode(data, out _))
                {
                    decodedDocuments++;
                }

                mappings = CftModelTextureMappingDecoder.Extract(data);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            string sources = string.Join(" || ", group.Select(source =>
            {
                string archive = source.Item.Archive?.FilePath ?? "local";
                string resource = source.Item.ArchiveFile?.FullPath ?? source.Item.OutputRelativePath;
                string hash = source.Item.ArchiveFile?.Md5 ?? "local";
                return $"{archive}:{resource}#{hash}";
            }).Order(StringComparer.OrdinalIgnoreCase));
            foreach (CftModelTextureMapping mapping in mappings.Where(mapping => mapping.HasTypedRowContext))
            {
                modelRows++;
                string[] relatedModels = mapping.RelatedModelReferences?
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [];
                relatedModelReferences += relatedModels.Length;
                string recordId = mapping.RecordId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                string textures = string.Join(" || ", mapping.TextureReferences
                    .Select(texture => $"{texture.Column}:{texture.Path}"));
                writer.WriteLine(string.Join("\t", [
                    EscapeTsv(sources),
                    mapping.RowIndex.ToString(CultureInfo.InvariantCulture),
                    recordId,
                    EscapeTsv(mapping.ModelColumn),
                    EscapeTsv(mapping.ModelReference),
                    EscapeTsv(string.Join(" || ", relatedModels)),
                    EscapeTsv(textures),
                    EscapeTsv(string.Join(" || ", mapping.IdentityAliases))
                ]));
            }
        }

        return (resourceCopies, documentGroups.Length, decodedDocuments, modelRows, relatedModelReferences);
    }

    public static (int LogicalPaths, int PhysicalCopies) WriteTextureResourcePathAuditReport(
        ExplorerItem root,
        string pathFilter,
        string outputPath)
    {
        var resourceCopies = new List<(string LogicalPath, ExplorerItem Item)>();
        var pending = new Stack<ExplorerItem>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ExplorerItem item = pending.Pop();
            if (item.IsFile && TextureExtensions.Contains(
                    "." + item.FileExtension.TrimStart('.'),
                    StringComparer.OrdinalIgnoreCase))
            {
                string logicalPath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
                if (logicalPath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                {
                    resourceCopies.Add((NormalizeKey(logicalPath), item));
                }
            }

            foreach (ExplorerItem child in item.Children)
            {
                pending.Push(child);
            }
        }

        IGrouping<string, (string LogicalPath, ExplorerItem Item)>[] matches = resourceCopies
            .GroupBy(copy => copy.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int physicalCopyCount = matches.Sum(group => group.Count());
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("logical_resource_path\tarchive_path\tphysical_resource_path\tmd5\tstored_size");
        foreach (IGrouping<string, (string LogicalPath, ExplorerItem Item)> group in matches)
        {
            foreach (var copy in group.OrderBy(copy => copy.Item.Archive?.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                ExplorerItem item = copy.Item;
                string archivePath = item.Archive?.FilePath ?? "local";
                string physicalPath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
                string md5 = item.ArchiveFile?.Md5 ?? string.Empty;
                string size = item.ArchiveFile?.Size.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                writer.WriteLine(string.Join("\t", [
                    EscapeTsv(group.Key),
                    EscapeTsv(archivePath),
                    EscapeTsv(physicalPath),
                    EscapeTsv(md5),
                    size
                ]));
            }
        }

        return (matches.Length, physicalCopyCount);
    }

    public static (
        int RowCount,
        int SlotMappings,
        int AmbiguousMappings,
        int InferredMappings,
        int MeshResolverMappings,
        int ExporterCandidateMappings,
        int SemanticCandidateMappings,
        int UnresolvedCandidateResourceRows,
        int OutOfScopeUnresolvedCandidateRows,
        int ExactTextureResourcePathsProbed,
        int ExactTextureDecodeAttempts,
        int ExactTextureResourcePathsDecoded,
        int ExactTextureResourcePathsFailed,
        int ExactTextureResourcePathsNotFound,
        int ModelGroupsWithDecodeFailures,
        int NonGeometryLtbResourceGroups,
        int ModelsWithEvidence,
        int ModelsWithoutEvidence)
        WriteSlotMappingAuditReport(ExplorerItem root, string outputPath)
    {
        TextureConfigIndex index = TextureConfigIndexCache.GetValue(root, BuildIndex);
        ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
        var mappedModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelsWithSlotEvidence = new HashSet<string>(dictionary.ModelsWithSlotSpecificBindings, StringComparer.OrdinalIgnoreCase);
        var rows = 0;
        var slotMappings = 0;
        var ambiguousMappings = 0;
        var inferredMappings = 0;
        var meshResolverMappings = 0;
        var exporterCandidateMappings = 0;
        var semanticCandidateMappings = 0;
        var unresolvedCandidateResourceRows = 0;
        var outOfScopeUnresolvedCandidateRows = 0;
        var exactTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelGroupsWithDecodeFailures = 0;
        var nonGeometryLtbResourceGroups = 0;

        foreach (string modelPath in dictionary.ByModelPath.Keys)
        {
            mappedModelPaths.Add(modelPath);
            modelsWithSlotEvidence.Add(modelPath);
        }

        foreach (string contextKey in dictionary.ByModelContext.Keys)
        {
            int separator = contextKey.IndexOf('\0');
            if (separator > 0)
            {
                mappedModelPaths.Add(contextKey[..separator]);
                modelsWithSlotEvidence.Add(contextKey[..separator]);
            }
        }

        foreach (string modelPath in dictionary.InferredByModelPath.Keys
                     .Concat(dictionary.LtcPreviewVariantByModelPath.Keys)
                     .Concat(dictionary.PreviewNormalizedBasenameByModelPath.Keys)
                     .Concat(dictionary.VariantSlotUvByModelPath.Keys)
                     .Concat(dictionary.MotionParentSlotByModelPath.Keys))
        {
            mappedModelPaths.Add(modelPath);
            modelsWithSlotEvidence.Add(modelPath);
        }

        foreach (string contextKey in dictionary.InferredByModelContext.Keys
                     .Concat(dictionary.EquivalentArmByModelContext.Keys)
                     .Concat(dictionary.LtcPreviewVariantByModelContext.Keys)
                     .Concat(dictionary.MotionParentSlotByModelContext.Keys)
                     .Concat(dictionary.CftIdentityModelAliasByModelContext.Keys)
                     .Concat(dictionary.WeaponPreviewVariantByModelContext.Keys))
        {
            int separator = contextKey.IndexOf('\0');
            if (separator > 0)
            {
                mappedModelPaths.Add(contextKey[..separator]);
                modelsWithSlotEvidence.Add(contextKey[..separator]);
            }
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(fullOutputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("model_path\tmesh_name\tscope\tcontext\ttexture_slot\ttexture_path\tmapping_kind\tstatus\tphysical_copies\tdetail");

        WriteSlotDictionary(dictionary.ByModelPath, "model", string.Empty, "direct-index", isInferred: false);
        WriteContextDictionary(dictionary.ByModelContext, "context", "direct-index", isInferred: false);
        WriteSlotDictionary(dictionary.InferredByModelPath, "model", string.Empty, "cft-body-face-derived", isInferred: true);
        WriteContextDictionary(dictionary.InferredByModelContext, "context", "cft-body-face-derived", isInferred: true);
        WriteSlotDictionary(dictionary.LtcPreviewVariantByModelPath, "model", string.Empty, "ltc-preview-variant-same-name-derived", isInferred: true);
        WriteContextDictionary(dictionary.LtcPreviewVariantByModelContext, "context", "ltc-preview-variant-same-name-derived", isInferred: true);
        WriteSlotDictionary(dictionary.PreviewNormalizedBasenameByModelPath, "model", string.Empty, "preview-normalized-basename-derived", isInferred: true);
        WriteSlotDictionary(dictionary.VariantSlotUvByModelPath, "model", string.Empty, "variant-slot-uv-derived", isInferred: true);
        WriteSlotDictionary(dictionary.MotionParentSlotByModelPath, "model", string.Empty, "motion-parent-slot-derived", isInferred: true);
        WriteContextDictionary(dictionary.MotionParentSlotByModelContext, "context", "motion-parent-slot-derived", isInferred: true);
        WriteContextDictionary(dictionary.CftIdentityModelAliasByModelContext, "context", "cft-identity-model-alias-derived", isInferred: true);
        WriteContextDictionary(dictionary.EquivalentArmByModelContext, "context", "cft-equivalent-arm-derived", isInferred: true);
        WriteContextDictionary(dictionary.WeaponPreviewVariantByModelContext, "context", "weapon-preview-variant-derived", isInferred: true);
        WriteRenderStyleContextDictionary(dictionary.RenderStyleByModelContext);

        var physicalModelGroups = index.ModelResourceItems
            .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, LogicalPath: GetAuditLogicalModelPath(GetModelResourcePath(item))))
            .Where(pair => pair.LogicalPath.Length > 0)
            .GroupBy(pair => pair.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var physicalModelGroupsByPath = physicalModelGroups.ToDictionary(
            group => group.Key,
            group => group,
            StringComparer.OrdinalIgnoreCase);
        var renderStyleCache = new Dictionary<string, (string Status, LithTechRenderStyleDocument? Document, int Copies, string Detail)>(
            StringComparer.OrdinalIgnoreCase);
        int modelsWithEvidence = 0;
        int modelsWithoutEvidence = 0;
        int processedModelGroups = 0;
        foreach (IGrouping<string, (ExplorerItem Item, string LogicalPath)> group in physicalModelGroups)
        {
            ExplorerItem[] groupItems = group.Select(pair => pair.Item).ToArray();
            string modelContextPrefix = group.Key + "\0";
            KeyValuePair<string, Dictionary<int, string?>>[] renderStyleContexts = dictionary.RenderStyleByModelContext
                .Where(pair => pair.Key.StartsWith(modelContextPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var storedBytesByItem = new Dictionary<ExplorerItem, byte[]?>();
            var headersByItem = new Dictionary<ExplorerItem, (byte FileType, ushort Version)?>();
            bool mayContainNonGeometryLtbResource = groupItems.Any(item =>
                LithTechRenderStyleDecoder.IsCandidate(GetModelResourcePath(item), "ltb"));
            if (mayContainNonGeometryLtbResource)
            {
                foreach (ExplorerItem modelItem in groupItems)
                {
                    byte[]? storedBytes = TryReadItemBytes(modelItem, 128 * 1024 * 1024);
                    storedBytesByItem[modelItem] = storedBytes;
                    headersByItem[modelItem] = storedBytes is not null && TryReadLtbHeader(storedBytes, out byte fileType, out ushort version)
                        ? (fileType, version)
                        : null;
                }
            }

            bool isNonGeometryLtbResource = groupItems.Length > 0 && groupItems.All(item =>
                headersByItem.TryGetValue(item, out (byte FileType, ushort Version)? header) &&
                header is { FileType: >= 5 and <= 7 });
            if (isNonGeometryLtbResource)
            {
                string[] resourceDetails = groupItems.Select(item =>
                {
                    (byte FileType, ushort Version) header = headersByItem[item]!.Value;
                    string physicalPath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
                    return $"{physicalPath}: file_type={header.FileType};version={header.Version}";
                }).ToArray();
                byte[] resourceTypes = groupItems.Select(item => headersByItem[item]!.Value.FileType).Distinct().ToArray();
                string resourceStatus = resourceTypes.Length == 1
                    ? resourceTypes[0] switch
                    {
                        5 => "d3d-render-style",
                        6 => "ps2-render-style",
                        7 => "d3d-render-object",
                        _ => "non-geometry-ltb-resource"
                    }
                    : "mixed-non-geometry-ltb-resources";
                WriteAuditRow(
                    group.Key,
                    string.Empty,
                    "ltb-resource-inventory",
                    string.Empty,
                    null,
                    string.Empty,
                    "ltb-header-type",
                    resourceStatus,
                    groupItems.Length,
                    string.Join(" | ", resourceDetails));
                nonGeometryLtbResourceGroups++;
                continue;
            }

            // Keep resolver query caches scoped to one logical model. A full-game
            // audit visits thousands of meshes and otherwise retains every query.
            Func<string, string, int, string, IReadOnlyList<string>>? meshTextureResolver =
                LithTechModelMeshTextureResolver.CreateResolver(root);
            Func<IEnumerable<string>, IReadOnlyList<string>>? textureReferenceResolver = TextureReferenceResolver.Combine(
                CreateResolver(root),
                LithTechDatTextureReferenceIndex.CreateResolver(root));
            Func<string, int, IReadOnlyList<string>>? textureSlotResolver = CreateTextureSlotResolver(root);
            Func<string, int, IReadOnlyList<string>>? inferredTextureSlotResolver = CreateInferredTextureSlotResolver(root);
            Func<string, bool>? slotMappingPresenceResolver = CreateModelTextureSlotMappingPresenceResolver(root);
            var perCopyMeshMappings = new List<Dictionary<string, HashSet<string>>>();
            var perCopyExporterMappings = new List<Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)>>();
            var perCopySemanticMappings = new List<Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)>>();
            var decodeFailures = new List<string>();
            bool hasDecodeFailure = false;
            var decodedModels = new Dictionary<string, (LithTechModelDocument? Document, string? Error)>(StringComparer.OrdinalIgnoreCase);
            foreach ((ExplorerItem modelItem, _) in group)
            {
                string modelResourcePath = GetModelResourcePath(modelItem);
                string cacheKey = modelItem.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
                    ? "md5:" + archiveHash
                    : NormalizeKey(modelResourcePath);
                if (!decodedModels.TryGetValue(cacheKey, out (LithTechModelDocument? Document, string? Error) decodeResult))
                {
                    byte[]? modelData = storedBytesByItem.TryGetValue(modelItem, out byte[]? storedModelBytes)
                        ? storedModelBytes
                        : TryReadItemBytes(modelItem, 128 * 1024 * 1024);
                    LithTechModelDocument? decodedDocument = null;
                    string? decodeError;
                    if (modelData is null)
                    {
                        decodeError = "LTB bytes unavailable or larger than the 128 MiB audit read limit.";
                    }
                    else if (!LithTechModelDecoder.TryDecode(
                                 modelData,
                                 modelResourcePath,
                                 "ltb",
                                 out decodedDocument,
                                 out decodeError))
                    {
                        decodedDocument = null;
                    }

                    decodeResult = (decodedDocument, decodeError);
                    decodedModels.Add(cacheKey, decodeResult);
                }

                LithTechModelDocument? document = decodeResult.Document;
                if (document is null)
                {
                    hasDecodeFailure = true;
                    string physicalPath = modelItem.ArchiveFile?.FullPath ?? modelItem.OutputRelativePath;
                    decodeFailures.Add($"{physicalPath}: {decodeResult.Error ?? "decoder returned no model"}");
                    perCopyMeshMappings.Add(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
                    perCopyExporterMappings.Add(new Dictionary<string, (string?, string?, int, string?, string?, string?)>(StringComparer.OrdinalIgnoreCase));
                    perCopySemanticMappings.Add(new Dictionary<string, (string?, string?, int, string?, string?, string?)>(StringComparer.OrdinalIgnoreCase));
                    continue;
                }

                var copyMappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var copyExporterMappings = new Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)>(StringComparer.OrdinalIgnoreCase);
                var copySemanticMappings = new Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)>(StringComparer.OrdinalIgnoreCase);
                string resolverModelPath = modelItem.OutputRelativePath.Length > 0
                    ? modelItem.OutputRelativePath
                    : modelItem.Name;
                Func<string, ImageSource?>? globalTextureResolver = LithTechModelTextureLoader.CreateGlobalResolver(root);
                Func<string, ImageSource?>? textureResolver =
                    LithTechObjExportCommand.CreateObjTextureResolver(modelItem, globalTextureResolver);
                var exportSource = new LithTechObjExportSource(
                    modelItem.Name,
                    resolverModelPath,
                    document,
                    textureResolver,
                    textureReferenceResolver,
                    textureSlotResolver,
                    meshTextureResolver,
                    null,
                    null,
                    slotMappingPresenceResolver,
                    inferredTextureSlotResolver,
                    WeaponPreviewVariantTextureSlotResolver: CreateWeaponPreviewVariantTextureSlotResolver(root));
                LithTechObjExportSource semanticSource = exportSource with { TextureConfigResolver = null };
                for (int meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
                {
                    LithTechMesh mesh = document.Meshes[meshIndex];
                    string[] exporterCandidates = LithTechObjExporter.EnumerateTextureCandidates(mesh, exportSource).ToArray();
                    string? selectedTexture = null;
                    string? selectedCandidate = null;
                    string? ambiguousCandidate = null;
                    string? undecodedTexturePath = null;
                    string? undecodedCandidate = null;
                    foreach (string candidate in exporterCandidates)
                    {
                        string? resolved = ResolveAuditTextureCandidate(index, candidate, out bool isAmbiguous);
                        if (textureResolver?.Invoke(candidate) is not null)
                        {
                            selectedTexture = resolved ?? NormalizeKey(candidate);
                            selectedCandidate = candidate;
                            break;
                        }

                        if (resolved is not null && undecodedTexturePath is null)
                        {
                            undecodedTexturePath = resolved;
                            undecodedCandidate = candidate;
                        }

                        if (isAmbiguous && ambiguousCandidate is null)
                        {
                            ambiguousCandidate = candidate;
                        }
                    }

                    string exportMeshKey = $"{meshIndex.ToString(CultureInfo.InvariantCulture)}\0{mesh.Name}";
                    copyExporterMappings[exportMeshKey] = (
                        selectedTexture,
                        selectedCandidate,
                        exporterCandidates.Length,
                        ambiguousCandidate,
                        selectedTexture is null ? undecodedTexturePath : null,
                        selectedTexture is null ? undecodedCandidate : null);

                    string[] semanticCandidates = LithTechObjExporter.EnumerateTextureCandidates(mesh, semanticSource).ToArray();
                    string? semanticTexture = null;
                    string? semanticCandidate = null;
                    string? ambiguousSemanticCandidate = null;
                    string? undecodedSemanticTexturePath = null;
                    string? undecodedSemanticCandidate = null;
                    foreach (string candidate in semanticCandidates)
                    {
                        string? resolved = ResolveAuditTextureCandidate(index, candidate, out bool isAmbiguous);
                        if (textureResolver?.Invoke(candidate) is not null)
                        {
                            semanticTexture = resolved ?? NormalizeKey(candidate);
                            semanticCandidate = candidate;
                            break;
                        }

                        if (resolved is not null && undecodedSemanticTexturePath is null)
                        {
                            undecodedSemanticTexturePath = resolved;
                            undecodedSemanticCandidate = candidate;
                        }

                        if (isAmbiguous && ambiguousSemanticCandidate is null)
                        {
                            ambiguousSemanticCandidate = candidate;
                        }
                    }

                    copySemanticMappings[exportMeshKey] = (
                        semanticTexture,
                        semanticCandidate,
                        semanticCandidates.Length,
                        ambiguousSemanticCandidate,
                        semanticTexture is null ? undecodedSemanticTexturePath : null,
                        semanticTexture is null ? undecodedSemanticCandidate : null);

                    if (renderStyleContexts.Length > 0)
                    {
                        WriteRenderStyleMeshStageRows(group.Key, mesh, meshIndex, modelItem, renderStyleContexts, group.Count());
                    }

                    int[] meshSlots = (mesh.TextureSlotIndices ?? []).Distinct().ToArray();
                    if (meshSlots.Length == 0)
                    {
                        if (mesh.HasAuthoritativeTextureBindings)
                        {
                            continue;
                        }

                        meshSlots = [-1];
                    }

                    string[] candidateReferences = new[] { string.Empty, mesh.Name }
                        .Concat(mesh.MaterialHints ?? [])
                        .Where(candidate => candidate is not null)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (int slot in meshSlots)
                    {
                        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (meshTextureResolver is not null)
                        {
                            foreach (string candidateReference in candidateReferences)
                            {
                                foreach (string texture in meshTextureResolver(
                                             resolverModelPath,
                                             mesh.Name,
                                             slot,
                                             candidateReference))
                                {
                                    textures.Add(ResolveAuditTextureCandidate(index, texture, out _) ?? texture);
                                }
                            }
                        }

                        string meshKey = $"{mesh.Name}\0{slot.ToString(CultureInfo.InvariantCulture)}";
                        if (!copyMappings.TryGetValue(meshKey, out HashSet<string>? existingTextures))
                        {
                            copyMappings.Add(meshKey, textures);
                        }
                        else
                        {
                            existingTextures.UnionWith(textures);
                        }
                    }
                }

                perCopyMeshMappings.Add(copyMappings);
                perCopyExporterMappings.Add(copyExporterMappings);
                perCopySemanticMappings.Add(copySemanticMappings);
            }

            if (hasDecodeFailure)
            {
                modelGroupsWithDecodeFailures++;
            }

            var allMeshKeys = perCopyMeshMappings
                .SelectMany(copy => copy.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool hasMeshResolverEvidence = false;
            foreach (string meshKey in allMeshKeys)
            {
                int separator = meshKey.LastIndexOf('\0');
                if (separator < 0 ||
                    !int.TryParse(meshKey[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                {
                    continue;
                }

                string meshName = meshKey[..separator];
                HashSet<string>? referenceSet = null;
                bool copiesAgree = !hasDecodeFailure && perCopyMeshMappings.Count == group.Count();
                var allTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, HashSet<string>> copy in perCopyMeshMappings)
                {
                    HashSet<string> currentSet = copy.TryGetValue(meshKey, out HashSet<string>? foundTextures)
                        ? foundTextures
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    referenceSet ??= new HashSet<string>(currentSet, StringComparer.OrdinalIgnoreCase);
                    if (!referenceSet.SetEquals(currentSet))
                    {
                        copiesAgree = false;
                    }

                    allTextures.UnionWith(currentSet);
                }

                string status = hasDecodeFailure
                    ? "model-copy-decode-failed"
                    : !copiesAgree
                        ? "physical-copy-conflict"
                        : allTextures.Count == 0
                            ? "no-mesh-resolver-match"
                            : allTextures.Count > 1
                                ? "ambiguous"
                                : !IsTextureResourcePresent(index, allTextures.Single())
                                    ? "texture-resource-missing"
                                    : "mapped";
                if (status == "mapped")
                {
                    hasMeshResolverEvidence = true;
                    meshResolverMappings++;
                }
                else if (status == "ambiguous")
                {
                    ambiguousMappings++;
                }

                if (allTextures.Count == 0)
                {
                    WriteAuditRow(
                        group.Key,
                        meshName,
                        "mesh-slot",
                        string.Empty,
                        slot,
                        string.Empty,
                        "mesh-resolver",
                        status,
                        group.Count());
                }
                else
                {
                    foreach (string texture in allTextures.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        WriteAuditRow(
                            group.Key,
                            meshName,
                            "mesh-slot",
                            string.Empty,
                            slot,
                            texture,
                            "mesh-resolver",
                            status,
                            group.Count());
                    }
                }
            }

            var allExporterMeshKeys = perCopyExporterMappings
                .SelectMany(copy => copy.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string meshKey in allExporterMeshKeys)
            {
                int separator = meshKey.IndexOf('\0');
                if (separator < 0 ||
                    !int.TryParse(meshKey[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshIndex))
                {
                    continue;
                }

                string meshName = meshKey[(separator + 1)..];
                (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)? reference = null;
                bool copiesAgree = !hasDecodeFailure && perCopyExporterMappings.Count == group.Count();
                var allTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allUndecodedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var perCopyDetails = new List<string>(perCopyExporterMappings.Count);
                for (int copyIndex = 0; copyIndex < perCopyExporterMappings.Count; copyIndex++)
                {
                    Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)> copy = perCopyExporterMappings[copyIndex];
                    var current = copy.TryGetValue(meshKey, out var found)
                        ? found
                        : (null, null, 0, null, null, null);
                    ExplorerItem physicalCopy = groupItems[copyIndex];
                    reference ??= current;
                    if (!string.Equals(reference.Value.TexturePath, current.TexturePath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(reference.Value.UndecodedTexturePath, current.UndecodedTexturePath, StringComparison.OrdinalIgnoreCase))
                    {
                        copiesAgree = false;
                    }

                    if (current.TexturePath is not null)
                    {
                        allTextures.Add(current.TexturePath);
                    }

                    if (current.UndecodedTexturePath is not null)
                    {
                        allUndecodedTextures.Add(current.UndecodedTexturePath);
                    }

                    string archivePath = physicalCopy.Archive?.FilePath ?? "local";
                    string modelResourcePath = physicalCopy.ArchiveFile?.FullPath ?? physicalCopy.OutputRelativePath;
                    string modelMd5 = physicalCopy.ArchiveFile?.Md5 ?? "local";
                    perCopyDetails.Add(
                        $"copy{(copyIndex + 1).ToString(CultureInfo.InvariantCulture)}={{archive={archivePath};model={modelResourcePath}#{modelMd5};candidate_count={current.CandidateCount.ToString(CultureInfo.InvariantCulture)};texture={current.TexturePath ?? "<none>"};candidate={current.Candidate ?? "<none>"};unresolved_texture={current.UndecodedTexturePath ?? "<none>"};unresolved_candidate={current.UndecodedCandidate ?? "<none>"}}}");
                }

                exactTexturePaths.UnionWith(allUndecodedTextures);

                string status = hasDecodeFailure
                    ? "model-copy-decode-failed"
                    : !copiesAgree
                        ? "physical-copy-conflict"
                        : allTextures.Count > 1
                            ? "ambiguous"
                            : allTextures.Count == 1
                                ? "candidate-texture-decodes"
                                : allUndecodedTextures.Count > 1
                                    ? allUndecodedTextures.All(IsTextureInModelDecodeScope)
                                        ? "ambiguous-unresolved-candidates"
                                        : "multiple-existing-candidates-not-resolved"
                                    : allUndecodedTextures.Count == 1
                                        ? IsTextureInModelDecodeScope(allUndecodedTextures.Single())
                                            ? "candidate-resource-present-unresolved"
                                            : "candidate-resource-present-unresolved-outside-model-texture-scope"
                                : reference?.AmbiguousCandidate is not null
                                    ? "ambiguous-candidate-resource"
                                    : "no-exporter-candidate-match";
                if (status == "candidate-texture-decodes")
                {
                    exporterCandidateMappings++;
                }
                else if (status == "candidate-resource-present-unresolved" || status == "ambiguous-unresolved-candidates")
                {
                    unresolvedCandidateResourceRows++;
                }
                else if (status == "candidate-resource-present-unresolved-outside-model-texture-scope" || status == "multiple-existing-candidates-not-resolved")
                {
                    outOfScopeUnresolvedCandidateRows++;
                }

                string exportDetail = $"mesh_index={meshIndex.ToString(CultureInfo.InvariantCulture)};candidate_count={(reference?.CandidateCount ?? 0).ToString(CultureInfo.InvariantCulture)}";
                if (reference?.Candidate is not null)
                {
                    exportDetail += $";selected_candidate={reference.Value.Candidate}";
                }
                else if (reference?.AmbiguousCandidate is not null)
                {
                    exportDetail += $";ambiguous_candidate={reference.Value.AmbiguousCandidate}";
                }
                if (reference?.UndecodedCandidate is not null)
                {
                    string decodeScope = IsTextureInModelDecodeScope(reference.Value.UndecodedTexturePath!)
                        ? "model-texture"
                        : "outside-model-texture";
                    exportDetail += $";unresolved_candidate={reference.Value.UndecodedCandidate};texture_resolver_invoked=true;resolver_result=unresolved;image_decode=unconfirmed;decode_scope={decodeScope}";
                }
                if (status == "physical-copy-conflict")
                {
                    exportDetail += $";physical_copy_details={string.Join("||", perCopyDetails)}";
                }

                var exportOutputTextures = new HashSet<string>(allTextures, StringComparer.OrdinalIgnoreCase);
                exportOutputTextures.UnionWith(allUndecodedTextures);
                if (exportOutputTextures.Count == 0)
                {
                    WriteAuditRow(group.Key, meshName, "mesh-export", string.Empty, null, string.Empty,
                        "exporter-first-existing-candidate", status, group.Count(), exportDetail);
                }
                else
                {
                    foreach (string texture in exportOutputTextures.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        WriteAuditRow(group.Key, meshName, "mesh-export", string.Empty, null, texture,
                            "exporter-first-existing-candidate", status, group.Count(), exportDetail);
                    }
                }
            }

            var allSemanticMeshKeys = perCopySemanticMappings
                .SelectMany(copy => copy.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool hasSemanticCandidateEvidence = false;
            foreach (string meshKey in allSemanticMeshKeys)
            {
                int separator = meshKey.IndexOf('\0');
                if (separator < 0 ||
                    !int.TryParse(meshKey[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshIndex))
                {
                    continue;
                }

                string meshName = meshKey[(separator + 1)..];
                (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)? reference = null;
                bool copiesAgree = !hasDecodeFailure && perCopySemanticMappings.Count == group.Count();
                var allTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allUndecodedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var perCopyDetails = new List<string>(perCopySemanticMappings.Count);
                for (int copyIndex = 0; copyIndex < perCopySemanticMappings.Count; copyIndex++)
                {
                    Dictionary<string, (string? TexturePath, string? Candidate, int CandidateCount, string? AmbiguousCandidate, string? UndecodedTexturePath, string? UndecodedCandidate)> copy = perCopySemanticMappings[copyIndex];
                    var current = copy.TryGetValue(meshKey, out var found)
                        ? found
                        : (null, null, 0, null, null, null);
                    ExplorerItem physicalCopy = groupItems[copyIndex];
                    reference ??= current;
                    if (!string.Equals(reference.Value.TexturePath, current.TexturePath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(reference.Value.UndecodedTexturePath, current.UndecodedTexturePath, StringComparison.OrdinalIgnoreCase))
                    {
                        copiesAgree = false;
                    }

                    if (current.TexturePath is not null)
                    {
                        allTextures.Add(current.TexturePath);
                    }

                    if (current.UndecodedTexturePath is not null)
                    {
                        allUndecodedTextures.Add(current.UndecodedTexturePath);
                    }

                    string archivePath = physicalCopy.Archive?.FilePath ?? "local";
                    string modelResourcePath = physicalCopy.ArchiveFile?.FullPath ?? physicalCopy.OutputRelativePath;
                    string modelMd5 = physicalCopy.ArchiveFile?.Md5 ?? "local";
                    perCopyDetails.Add(
                        $"copy{(copyIndex + 1).ToString(CultureInfo.InvariantCulture)}={{archive={archivePath};model={modelResourcePath}#{modelMd5};candidate_count={current.CandidateCount.ToString(CultureInfo.InvariantCulture)};texture={current.TexturePath ?? "<none>"};candidate={current.Candidate ?? "<none>"};unresolved_texture={current.UndecodedTexturePath ?? "<none>"};unresolved_candidate={current.UndecodedCandidate ?? "<none>"}}}");
                }

                exactTexturePaths.UnionWith(allUndecodedTextures);

                string status = hasDecodeFailure
                    ? "model-copy-decode-failed"
                    : !copiesAgree
                        ? "physical-copy-conflict"
                        : allTextures.Count > 1
                            ? "ambiguous"
                            : allTextures.Count == 1
                                ? "semantic-candidate-decodes"
                                : allUndecodedTextures.Count > 1
                                    ? allUndecodedTextures.All(IsTextureInModelDecodeScope)
                                        ? "ambiguous-unresolved-candidates"
                                        : "multiple-existing-candidates-not-resolved"
                                    : allUndecodedTextures.Count == 1
                                        ? IsTextureInModelDecodeScope(allUndecodedTextures.Single())
                                            ? "candidate-resource-present-unresolved"
                                            : "candidate-resource-present-unresolved-outside-model-texture-scope"
                                : reference?.AmbiguousCandidate is not null
                                    ? "ambiguous-candidate-resource"
                                    : "no-semantic-candidate-match";
                if (status == "semantic-candidate-decodes")
                {
                    hasSemanticCandidateEvidence = true;
                    semanticCandidateMappings++;
                }
                else if (status == "ambiguous" || status == "ambiguous-candidate-resource")
                {
                    ambiguousMappings++;
                }
                else if (status == "candidate-resource-present-unresolved" || status == "ambiguous-unresolved-candidates")
                {
                    unresolvedCandidateResourceRows++;
                }
                else if (status == "candidate-resource-present-unresolved-outside-model-texture-scope" || status == "multiple-existing-candidates-not-resolved")
                {
                    outOfScopeUnresolvedCandidateRows++;
                }

                string semanticDetail = $"mesh_index={meshIndex.ToString(CultureInfo.InvariantCulture)};candidate_count={(reference?.CandidateCount ?? 0).ToString(CultureInfo.InvariantCulture)};global_config_fallback=disabled";
                if (reference?.Candidate is not null)
                {
                    semanticDetail += $";selected_candidate={reference.Value.Candidate}";
                }
                else if (reference?.AmbiguousCandidate is not null)
                {
                    semanticDetail += $";ambiguous_candidate={reference.Value.AmbiguousCandidate}";
                }
                if (reference?.UndecodedCandidate is not null)
                {
                    string decodeScope = IsTextureInModelDecodeScope(reference.Value.UndecodedTexturePath!)
                        ? "model-texture"
                        : "outside-model-texture";
                    semanticDetail += $";unresolved_candidate={reference.Value.UndecodedCandidate};texture_resolver_invoked=true;resolver_result=unresolved;image_decode=unconfirmed;decode_scope={decodeScope}";
                }
                if (status == "physical-copy-conflict")
                {
                    semanticDetail += $";physical_copy_details={string.Join("||", perCopyDetails)}";
                }

                var semanticOutputTextures = new HashSet<string>(allTextures, StringComparer.OrdinalIgnoreCase);
                semanticOutputTextures.UnionWith(allUndecodedTextures);
                if (semanticOutputTextures.Count == 0)
                {
                    WriteAuditRow(group.Key, meshName, "mesh-semantic", string.Empty, null, string.Empty,
                        "exporter-candidate-without-global-config", status, group.Count(), semanticDetail);
                }
                else
                {
                    foreach (string texture in semanticOutputTextures.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        WriteAuditRow(group.Key, meshName, "mesh-semantic", string.Empty, null, texture,
                            "exporter-candidate-without-global-config", status, group.Count(), semanticDetail);
                    }
                }
            }

            bool hasSlotEvidence = modelsWithSlotEvidence.Contains(group.Key) || mappedModelPaths.Contains(group.Key);
            bool hasEvidence = hasSlotEvidence || hasMeshResolverEvidence || hasSemanticCandidateEvidence;
            if (hasEvidence)
            {
                modelsWithEvidence++;
            }
            else
            {
                modelsWithoutEvidence++;
            }

            string inventoryStatus = hasSlotEvidence
                ? "has-slot-dictionary-evidence"
                : hasMeshResolverEvidence
                    ? "has-mesh-resolver-evidence"
                    : hasSemanticCandidateEvidence
                        ? "has-semantic-candidate-evidence"
                    : hasDecodeFailure
                        ? "model-decode-failed"
                        : "no-resolver-evidence";
            string detail = hasDecodeFailure
                ? string.Join(" | ", decodeFailures.Distinct(StringComparer.OrdinalIgnoreCase))
                : string.Empty;
            WriteAuditRow(
                group.Key,
                string.Empty,
                "model-inventory",
                string.Empty,
                null,
                string.Empty,
                string.Empty,
                inventoryStatus,
                group.Count(),
                detail);

            LithTechModelTextureLoader.ClearDecodedTextureCaches(root, group.Select(pair => pair.Item.Archive));
            LithTechObjExporter.ClearTextureNameCandidateCache();
            processedModelGroups++;
            if (processedModelGroups % 64 == 0)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
        }

        var exactResourceItems = new Dictionary<string, List<ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (!TextureExtensions.Contains("." + item.FileExtension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string itemOutputPath = NormalizeKey(item.OutputRelativePath);
            string resourcePath = NormalizeKey(item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
            if (itemOutputPath.Length > 0 && exactTexturePaths.Contains(itemOutputPath))
            {
                AddExactResourceItem(itemOutputPath, item);
            }

            if (resourcePath.Length > 0 &&
                !string.Equals(resourcePath, itemOutputPath, StringComparison.OrdinalIgnoreCase) &&
                exactTexturePaths.Contains(resourcePath))
            {
                AddExactResourceItem(resourcePath, item);
            }
        }

        int exactCandidateResourceDecodeAttempts = 0;
        int exactCandidateResourcePathsDecoded = 0;
        int exactCandidateResourcePathsFailed = 0;
        int exactCandidateResourcePathsNotFound = 0;
        foreach (string texturePath in exactTexturePaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!exactResourceItems.TryGetValue(texturePath, out List<ExplorerItem>? items) || items.Count == 0)
            {
                exactCandidateResourcePathsNotFound++;
                WriteAuditRow(string.Empty, string.Empty, "texture-path-decode-probe", string.Empty, null, texturePath,
                    "exact-resource-path", "exact-resource-path-not-found", null, "texture_resource_indexed=true;physical_items=0");
                continue;
            }

            int decodableCopies = 0;
            foreach (ExplorerItem item in items)
            {
                exactCandidateResourceDecodeAttempts++;
                if (LithTechModelTextureLoader.TryDecodeTextureItem(item))
                {
                    decodableCopies++;
                }
            }

            string probeStatus = decodableCopies == 0
                ? "exact-resource-decode-failed-all-copies"
                : decodableCopies == items.Count
                    ? "exact-resource-decodes-all-copies"
                    : "exact-resource-decodes-some-copies";
            if (decodableCopies > 0)
            {
                exactCandidateResourcePathsDecoded++;
            }
            else
            {
                exactCandidateResourcePathsFailed++;
            }

            string copyDetails = string.Join('|', items.Select((item, index) =>
                $"copy{index + 1}={NormalizeKey(item.OutputRelativePath)}#{item.ArchiveFile?.Md5 ?? "local"}"));
            string detail = $"texture_resource_indexed=true;physical_items={items.Count.ToString(CultureInfo.InvariantCulture)};decode_attempts={items.Count.ToString(CultureInfo.InvariantCulture)};decodable_copies={decodableCopies.ToString(CultureInfo.InvariantCulture)};{copyDetails}";
            WriteAuditRow(string.Empty, string.Empty, "texture-path-decode-probe", string.Empty, null, texturePath,
                "exact-resource-path", probeStatus, items.Count, detail);
        }

        return (
            rows,
            slotMappings,
            ambiguousMappings,
            inferredMappings,
            meshResolverMappings,
            exporterCandidateMappings,
            semanticCandidateMappings,
            unresolvedCandidateResourceRows,
            outOfScopeUnresolvedCandidateRows,
            exactTexturePaths.Count,
            exactCandidateResourceDecodeAttempts,
            exactCandidateResourcePathsDecoded,
            exactCandidateResourcePathsFailed,
            exactCandidateResourcePathsNotFound,
            modelGroupsWithDecodeFailures,
            nonGeometryLtbResourceGroups,
            modelsWithEvidence,
            modelsWithoutEvidence);

        void AddExactResourceItem(string path, ExplorerItem item)
        {
            if (!exactResourceItems.TryGetValue(path, out List<ExplorerItem>? items))
            {
                items = [];
                exactResourceItems.Add(path, items);
            }

            if (!items.Contains(item))
            {
                items.Add(item);
            }
        }

        string? ResolveAuditTextureCandidate(TextureConfigIndex textureIndex, string candidate, out bool ambiguous)
        {
            ambiguous = false;
            string normalized = NormalizeKey(candidate);
            if (normalized.Length == 0)
            {
                return null;
            }

            foreach (string variant in LithTechModelTextureLoader.EnumerateTexturePathVariants(normalized))
            {
                string normalizedVariant = NormalizeKey(variant);
                string extension = Path.GetExtension(normalizedVariant);
                if (!TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (textureIndex.OutputPaths.Contains(normalizedVariant) || textureIndex.ResourcePaths.Contains(normalizedVariant))
                {
                    return normalizedVariant;
                }

                string? resolved = ResolveExistingTextureReference(
                    normalizedVariant,
                    textureIndex.ResourcePaths,
                    textureIndex.TexturePathsByBaseName);
                if (resolved is not null)
                {
                    return resolved;
                }

                string baseName = Path.GetFileNameWithoutExtension(normalizedVariant).Normalize(NormalizationForm.FormKC);
                ambiguous |= textureIndex.TexturePathsByBaseName.TryGetValue(
                                 GetTextureBaseNameKey(baseName, extension),
                                 out IReadOnlyList<string>? candidates) &&
                             candidates.Count > 1;
            }

            return null;
        }

        static bool IsTextureInModelDecodeScope(string texturePath)
        {
            return LithTechResourceHeuristics.IsLikelyModelTexturePath(
                texturePath,
                Path.GetExtension(texturePath));
        }

        void WriteSlotDictionary(
            IReadOnlyDictionary<string, Dictionary<int, string?>> mappings,
            string scope,
            string context,
            string mappingKind,
            bool isInferred)
        {
            foreach ((string modelPath, Dictionary<int, string?> slots) in mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach ((int slot, string? texturePath) in slots.OrderBy(pair => pair.Key))
                {
                    WriteMappingRow(modelPath, string.Empty, scope, context, slot, texturePath, mappingKind, isInferred);
                }
            }
        }

        void WriteContextDictionary(
            IReadOnlyDictionary<string, Dictionary<int, string?>> mappings,
            string scope,
            string mappingKind,
            bool isInferred)
        {
            foreach ((string key, Dictionary<int, string?> slots) in mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                int separator = key.IndexOf('\0');
                if (separator <= 0 || separator == key.Length - 1)
                {
                    continue;
                }

                string modelPath = key[..separator];
                string context = key[(separator + 1)..];
                foreach ((int slot, string? texturePath) in slots.OrderBy(pair => pair.Key))
                {
                    WriteMappingRow(modelPath, string.Empty, scope, context, slot, texturePath, mappingKind, isInferred);
                }
            }
        }

        void WriteRenderStyleContextDictionary(IReadOnlyDictionary<string, Dictionary<int, string?>> mappings)
        {
            foreach ((string key, Dictionary<int, string?> styles) in mappings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                int separator = key.IndexOf('\0');
                if (separator <= 0 || separator == key.Length - 1)
                {
                    continue;
                }

                string modelPath = key[..separator];
                string context = key[(separator + 1)..];
                foreach ((int renderStyleIndex, string? renderStylePath) in styles.OrderBy(pair => pair.Key))
                {
                    string status = string.IsNullOrWhiteSpace(renderStylePath)
                        ? "ambiguous-renderstyle-reference"
                        : index.ResourcePaths.Contains(NormalizeKey(renderStylePath))
                            ? "renderstyle-resource-present"
                            : "renderstyle-resource-missing";
                    WriteAuditRow(
                        modelPath,
                        string.Empty,
                        "renderstyle-context",
                        context,
                        renderStyleIndex,
                        renderStylePath,
                        "ltc-renderstyle-reference",
                        status,
                        null,
                        "texture_slot column contains the zero-based renderstyle file index for this scope");
                }
            }
        }

        void WriteRenderStyleMeshStageRows(
            string modelPath,
            LithTechMesh mesh,
            int meshIndex,
            ExplorerItem modelItem,
            IReadOnlyList<KeyValuePair<string, Dictionary<int, string?>>> contexts,
            int physicalCopies)
        {
            foreach ((string contextKey, Dictionary<int, string?> styleBindings) in contexts)
            {
                int separator = contextKey.IndexOf('\0');
                if (separator < 0 || separator == contextKey.Length - 1)
                {
                    continue;
                }

                string context = contextKey[(separator + 1)..];
                string modelPhysicalPath = modelItem.ArchiveFile?.FullPath ?? modelItem.OutputRelativePath;
                string modelArchivePath = modelItem.Archive?.FilePath ?? "local";
                string copyDetail = $"mesh_index={meshIndex.ToString(CultureInfo.InvariantCulture)};model_archive={modelArchivePath};model_copy={modelPhysicalPath}";
                if (mesh.RenderStyleIndex is not int renderStyleIndex)
                {
                    WriteAuditRow(
                        modelPath,
                        mesh.Name,
                        "mesh-renderstyle",
                        context,
                        null,
                        null,
                        "ltb-renderstyle-index",
                        "mesh-renderstyle-index-missing",
                        physicalCopies,
                        copyDetail);
                    continue;
                }

                if (!styleBindings.TryGetValue(renderStyleIndex, out string? renderStylePath))
                {
                    WriteAuditRow(
                        modelPath,
                        mesh.Name,
                        "mesh-renderstyle",
                        context,
                        renderStyleIndex,
                        null,
                        "ltb-renderstyle-index",
                        "ltc-renderstyle-reference-missing",
                        physicalCopies,
                        $"{copyDetail};declared_renderstyle_indices={string.Join(",", styleBindings.Keys.Order())}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(renderStylePath))
                {
                    WriteAuditRow(
                        modelPath,
                        mesh.Name,
                        "mesh-renderstyle",
                        context,
                        renderStyleIndex,
                        null,
                        "ltc-renderstyle-reference",
                        "ambiguous-renderstyle-reference",
                        physicalCopies,
                        copyDetail);
                    continue;
                }

                (string styleStatus, LithTechRenderStyleDocument? renderStyleDocument, int renderStyleCopies, string styleDetail) =
                    ResolveRenderStyleDocument(renderStylePath);
                if (renderStyleDocument is null)
                {
                    WriteAuditRow(
                        modelPath,
                        mesh.Name,
                        "mesh-renderstyle",
                        context,
                        renderStyleIndex,
                        renderStylePath,
                        "ltc-renderstyle-reference",
                        styleStatus,
                        renderStyleCopies,
                        $"{copyDetail};{styleDetail}");
                    continue;
                }

                if (renderStyleDocument.Styles.Count == 0)
                {
                    WriteAuditRow(
                        modelPath,
                        mesh.Name,
                        "mesh-renderstyle",
                        context,
                        renderStyleIndex,
                        renderStylePath,
                        "ltc-renderstyle-reference",
                        "renderstyle-file-has-no-styles",
                        renderStyleCopies,
                        $"{copyDetail};{styleDetail}");
                    continue;
                }

                for (int styleVariantIndex = 0; styleVariantIndex < renderStyleDocument.Styles.Count; styleVariantIndex++)
                {
                    LithTechRenderStyle styleVariant = renderStyleDocument.Styles[styleVariantIndex];
                    for (int passIndex = 0; passIndex < styleVariant.Passes.Count; passIndex++)
                    {
                        LithTechRenderPass pass = styleVariant.Passes[passIndex];
                        for (int stageIndex = 0; stageIndex < pass.TextureStages.Count; stageIndex++)
                        {
                            LithTechTextureStage stage = pass.TextureStages[stageIndex];
                            int localTextureIndex = stage.TextureParam - 1;
                            int? globalTextureSlot = null;
                            string? texturePath = null;
                            string stageStatus;
                            bool stageDisabled = stage.ColorOp == 0 && stage.AlphaOp == 0;
                            int? uvSetIndex = stage.UvSource is >= 0 and <= 3 ? stage.UvSource : null;
                            string uvSourceName = stage.UvSource switch
                            {
                                0 => "model-uv-set-1",
                                1 => "model-uv-set-2",
                                2 => "model-uv-set-3",
                                3 => "model-uv-set-4",
                                4 => "camera-space-normal",
                                5 => "camera-space-position",
                                6 => "camera-space-reflection",
                                7 => "world-space-normal",
                                8 => "world-space-position",
                                9 => "world-space-reflection",
                                _ => "unknown-uv-source"
                            };
                            string uvDataStatus = uvSetIndex is int modelUvSetIndex
                                ? mesh.HasTextureCoordinateSet(modelUvSetIndex) ? "model-uv-set-available" : "model-uv-set-unavailable"
                                : stage.UvSource is >= 4 and <= 9 ? "renderstyle-generated-uv-source" : "unknown-uv-source";
                            if (stage.TextureParam == 0)
                            {
                                stageStatus = "renderstyle-stage-no-texture";
                            }
                            else if (stage.TextureParam is < 1 or > 4)
                            {
                                stageStatus = "renderstyle-stage-unsupported-texture-param";
                            }
                            else if (localTextureIndex >= (mesh.TextureSlotIndices?.Count ?? 0))
                            {
                                stageStatus = "mesh-texture-list-index-out-of-range";
                            }
                            else
                            {
                                globalTextureSlot = mesh.TextureSlotIndices![localTextureIndex];
                                string textureContextKey = $"{modelPath}\0{context}";
                                if (!dictionary.LtcTextureByModelContext.TryGetValue(textureContextKey, out Dictionary<int, string?>? textures) ||
                                    !textures.TryGetValue(globalTextureSlot.Value, out texturePath))
                                {
                                    stageStatus = "ltc-context-texture-unresolved";
                                }
                                else if (string.IsNullOrWhiteSpace(texturePath))
                                {
                                    stageStatus = "ltc-context-texture-ambiguous";
                                }
                                else if (stageDisabled)
                                {
                                    stageStatus = "renderstyle-stage-disabled";
                                }
                                else if (!IsTextureResourcePresent(index, texturePath))
                                {
                                    stageStatus = "texture-resource-missing";
                                }
                                else if (uvDataStatus == "model-uv-set-unavailable")
                                {
                                    stageStatus = "renderstyle-stage-uv-set-unavailable";
                                }
                                else if (uvDataStatus == "unknown-uv-source")
                                {
                                    stageStatus = "renderstyle-stage-uv-source-unsupported";
                                }
                                else
                                {
                                    stageStatus = "renderstyle-stage-bound";
                                }
                            }

                            int? uvSourceStreamIndex = uvSetIndex is int sourceUvSetIndex
                                ? mesh.GetTextureCoordinateSourceStreamIndex(sourceUvSetIndex)
                                : null;
                            string detail = $"{copyDetail};renderstyle_path={renderStylePath};renderstyle_file_index={renderStyleIndex.ToString(CultureInfo.InvariantCulture)};style_variant_index={styleVariantIndex.ToString(CultureInfo.InvariantCulture)};pass_index={passIndex.ToString(CultureInfo.InvariantCulture)};stage_index={stageIndex.ToString(CultureInfo.InvariantCulture)};texture_param={stage.TextureParam.ToString(CultureInfo.InvariantCulture)};local_texture_index={(localTextureIndex >= 0 ? localTextureIndex.ToString(CultureInfo.InvariantCulture) : string.Empty)};uv_source={stage.UvSource.ToString(CultureInfo.InvariantCulture)};uv_source_name={uvSourceName};uv_set_index={(uvSetIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)};mesh_uv_set_count={mesh.TextureCoordinateSetCount.ToString(CultureInfo.InvariantCulture)};uv_source_stream_index={(uvSourceStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)};uv_data_status={uvDataStatus};color_op={stage.ColorOp.ToString(CultureInfo.InvariantCulture)};alpha_op={stage.AlphaOp.ToString(CultureInfo.InvariantCulture)};{styleDetail}";
                            WriteAuditRow(
                                modelPath,
                                mesh.Name,
                                "mesh-render-stage",
                                context,
                                globalTextureSlot,
                                texturePath,
                                "renderstyle-texture-param",
                                stageStatus,
                                physicalCopies,
                                detail);
                        }
                    }
                }
            }
        }

        (string Status, LithTechRenderStyleDocument? Document, int Copies, string Detail) ResolveRenderStyleDocument(string renderStylePath)
        {
            string logicalPath = NormalizeExtensionlessModelReference(renderStylePath);
            if (renderStyleCache.TryGetValue(logicalPath, out var cached))
            {
                return cached;
            }

            if (logicalPath.Length == 0 || !physicalModelGroupsByPath.TryGetValue(logicalPath, out IGrouping<string, (ExplorerItem Item, string LogicalPath)>? styleGroup))
            {
                var missing = ("renderstyle-resource-missing", (LithTechRenderStyleDocument?)null, 0, $"renderstyle_path={renderStylePath}");
                renderStyleCache[logicalPath] = missing;
                return missing;
            }

            var decodedCopies = new List<(ExplorerItem Item, LithTechRenderStyleDocument Document)>();
            var decodeErrors = new List<string>();
            foreach (ExplorerItem styleItem in styleGroup.Select(pair => pair.Item))
            {
                string physicalPath = GetModelResourcePath(styleItem);
                byte[]? data = TryReadItemBytes(styleItem, 128 * 1024 * 1024);
                if (data is null)
                {
                    decodeErrors.Add($"{physicalPath}: RenderStyle bytes unavailable or larger than the 128 MiB audit read limit.");
                    continue;
                }

                if (!LithTechRenderStyleDecoder.TryDecode(
                        data,
                        physicalPath,
                        styleItem.Archive?.FilePath ?? "local",
                        data.Length,
                        out LithTechRenderStyleDocument? document,
                        out string? error) || document is null)
                {
                    decodeErrors.Add($"{physicalPath}: {error ?? "RenderStyle bytes unavailable or decoding failed."}");
                    continue;
                }

                decodedCopies.Add((styleItem, document));
            }

            int copies = styleGroup.Count();
            if (decodeErrors.Count > 0 || decodedCopies.Count != copies)
            {
                var failed = ("renderstyle-copy-decode-failed", (LithTechRenderStyleDocument?)null, copies, string.Join(" | ", decodeErrors));
                renderStyleCache[logicalPath] = failed;
                return failed;
            }

            static string GetBindingSignature(LithTechRenderStyleDocument document) => string.Join("||", document.Styles.Select(style =>
                string.Join("|", style.Passes.Select(pass => string.Join(";", pass.TextureStages.Select(stage =>
                    $"{stage.TextureParam}:{stage.UvSource}:{stage.ColorOp}:{stage.AlphaOp}"))))));

            string expectedSignature = GetBindingSignature(decodedCopies[0].Document);
            if (decodedCopies.Skip(1).Any(copy => !string.Equals(GetBindingSignature(copy.Document), expectedSignature, StringComparison.Ordinal)))
            {
                string copiesDetail = string.Join(" | ", decodedCopies.Select(copy =>
                    $"{copy.Item.Archive?.FilePath ?? "local"}:{GetModelResourcePath(copy.Item)}#{copy.Item.ArchiveFile?.Md5 ?? "local"}"));
                var conflict = ("renderstyle-physical-copy-conflict", (LithTechRenderStyleDocument?)null, copies, copiesDetail);
                renderStyleCache[logicalPath] = conflict;
                return conflict;
            }

            var resolved = ("renderstyle-decoded", (LithTechRenderStyleDocument?)decodedCopies[0].Document, copies,
                $"renderstyle_path={renderStylePath};physical_copies={copies.ToString(CultureInfo.InvariantCulture)}");
            renderStyleCache[logicalPath] = resolved;
            return resolved;
        }

        void WriteMappingRow(
            string modelPath,
            string meshName,
            string scope,
            string context,
            int slot,
            string? texturePath,
            string mappingKind,
            bool isInferred)
        {
            string status;
            string? resolvedTexturePath = null;
            bool ambiguousTextureReference = false;
            if (!string.IsNullOrWhiteSpace(texturePath))
            {
                resolvedTexturePath = ResolveAuditTextureCandidate(index, texturePath, out ambiguousTextureReference);
                if (resolvedTexturePath is null &&
                    TextureExtensions.Contains(Path.GetExtension(texturePath), StringComparer.OrdinalIgnoreCase))
                {
                    exactTexturePaths.Add(texturePath);
                }
            }

            if (string.IsNullOrWhiteSpace(texturePath))
            {
                status = "ambiguous";
                ambiguousMappings++;
            }
            else if (resolvedTexturePath is null)
            {
                status = ambiguousTextureReference ? "ambiguous" : "texture-resource-missing";
                if (ambiguousTextureReference)
                {
                    ambiguousMappings++;
                }
            }
            else if (isInferred)
            {
                bool directBindingExists;
                if (scope == "context")
                {
                    directBindingExists = dictionary.ByModelContext.TryGetValue(
                                              $"{modelPath}\0{context}",
                                              out Dictionary<int, string?>? contextDirectSlots) &&
                                          contextDirectSlots.ContainsKey(slot);
                }
                else
                {
                    directBindingExists = dictionary.ByModelPath.TryGetValue(
                                              modelPath,
                                              out Dictionary<int, string?>? modelDirectSlots) &&
                                          modelDirectSlots.ContainsKey(slot);
                }

                status = directBindingExists ? "shadowed-by-direct-binding" : "inferred";
                inferredMappings++;
            }
            else
            {
                status = "mapped";
            }

            WriteAuditRow(modelPath, meshName, scope, context, slot, resolvedTexturePath ?? texturePath, mappingKind, status, null);
            if (!isInferred && resolvedTexturePath is not null && status == "mapped")
            {
                slotMappings++;
            }
        }

        void WriteAuditRow(
            string modelPath,
            string meshName,
            string scope,
            string context,
            int? slot,
            string? texturePath,
            string mappingKind,
            string status,
            int? physicalCopies,
            string? detail = null)
        {
            writer.WriteLine(string.Join('\t',
                EscapeTsv(modelPath),
                EscapeTsv(meshName),
                scope,
                EscapeTsv(context),
                slot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                EscapeTsv(texturePath),
                mappingKind,
                status,
                physicalCopies?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                EscapeTsv(detail)));
            rows++;
        }
    }

    private static string EscapeTsv(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static bool TryReadLtbHeader(byte[] storedBytes, out byte fileType, out ushort version)
    {
        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(storedBytes);
        ReadOnlySpan<byte> header = prepared ?? storedBytes;
        if (header.Length < 4)
        {
            fileType = 0;
            version = 0;
            return false;
        }

        fileType = header[0];
        version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2, sizeof(ushort)));
        return true;
    }

    private static bool IsTextureResourcePresent(TextureConfigIndex index, string texturePath)
    {
        string normalized = NormalizeKey(texturePath);
        return normalized.Length > 0 &&
               (index.ResourcePaths.Contains(normalized) || index.OutputPaths.Contains(normalized));
    }

    private static TextureConfigIndex BuildIndex(ExplorerItem root)
    {
        var byName = new Dictionary<string, List<TextureConfigItem>>(StringComparer.OrdinalIgnoreCase);
        var byLooseName = new Dictionary<string, List<TextureConfigItem>?>(StringComparer.OrdinalIgnoreCase);
        var allItems = new List<TextureConfigItem>();
        var resourceItems = new List<ExplorerItem>();
        var modelResourceItems = new List<ExplorerItem>();
        var uccMapItems = new List<ExplorerItem>();
        var objectLtoItems = new List<ExplorerItem>();
        var clientFxFxfItems = new List<ExplorerItem>();
        var clientShellItems = new List<ExplorerItem>();
        var ltcItems = new List<ExplorerItem>();
        var crossFireScriptItems = new List<ExplorerItem>();
        var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelPathsByBaseName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var texturePathsByBaseName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            resourceItems.Add(item);
            string outputPath = NormalizeKey(item.OutputRelativePath);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                outputPaths.Add(outputPath);
            }

            string resourcePath = NormalizeKey(item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
            if (!string.IsNullOrWhiteSpace(resourcePath))
            {
                resourcePaths.Add(resourcePath);
                if (ModelExtensions.Contains(Path.GetExtension(resourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    modelResourceItems.Add(item);
                    string baseName = Path.GetFileNameWithoutExtension(resourcePath)
                        .Normalize(NormalizationForm.FormKC);
                    if (!modelPathsByBaseName.TryGetValue(baseName, out HashSet<string>? modelPaths))
                    {
                        modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        modelPathsByBaseName.Add(baseName, modelPaths);
                    }

                    modelPaths.Add(resourcePath);
                }

                string resourceExtension = Path.GetExtension(resourcePath);
                if (TextureExtensions.Contains(resourceExtension, StringComparer.OrdinalIgnoreCase))
                {
                    string baseName = Path.GetFileNameWithoutExtension(resourcePath)
                        .Normalize(NormalizationForm.FormKC);
                    string key = GetTextureBaseNameKey(baseName, resourceExtension);
                    if (!texturePathsByBaseName.TryGetValue(key, out HashSet<string>? texturePaths))
                    {
                        texturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        texturePathsByBaseName.Add(key, texturePaths);
                    }

                    texturePaths.Add(resourcePath);
                }
            }

            if (IsUccPlacementMapCandidate(item))
            {
                uccMapItems.Add(item);
            }

            if (string.Equals(item.FileExtension, "lto", StringComparison.OrdinalIgnoreCase))
            {
                objectLtoItems.Add(item);
            }

            if (string.Equals(item.FileExtension, "fxf", StringComparison.OrdinalIgnoreCase))
            {
                clientFxFxfItems.Add(item);
            }

            if (IsClientShellMappingCandidate(item))
            {
                clientShellItems.Add(item);
            }

            if (string.Equals(item.FileExtension, "ltc", StringComparison.OrdinalIgnoreCase))
            {
                ltcItems.Add(item);
            }

            if (CrossFireScriptBinDecoder.IsCandidate(item.ArchiveFile?.FullPath ?? item.OutputRelativePath, item.FileExtension))
            {
                crossFireScriptItems.Add(item);
            }

            if (!IsTextureConfigCandidate(item))
            {
                continue;
            }

            string configName = Path.GetFileNameWithoutExtension(item.Name);
            if (string.IsNullOrWhiteSpace(configName))
            {
                continue;
            }

            var configItem = new TextureConfigItem(item.OutputRelativePath, configName, item);
            allItems.Add(configItem);
            AddIndexCandidate(byName, NormalizeKey(configName), configItem);
            AddIndexCandidate(byName, NormalizeKey(LithTechModelPartGrouper.GetNumberedPartBase(configName)), configItem);

            string looseKey = NormalizeLooseKey(configName);
            if (!string.IsNullOrWhiteSpace(looseKey))
            {
                AddLooseIndexCandidate(byLooseName, looseKey, configItem);
            }
        }

        return new TextureConfigIndex(
            byName,
            byLooseName,
            allItems,
            resourceItems,
            modelResourceItems,
            uccMapItems,
            objectLtoItems,
            clientFxFxfItems,
            clientShellItems,
            ltcItems,
            crossFireScriptItems,
            resourcePaths,
            outputPaths,
            modelPathsByBaseName.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            texturePathsByBaseName.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Resolve(
        TextureConfigIndex index,
        IEnumerable<string> names,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        var textures = new List<string>();
        var seenConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            foreach (TextureConfigItem configItem in EnumerateConfigItems(index, name))
            {
                if (!seenConfigs.Add(configItem.Path))
                {
                    continue;
                }

                IReadOnlyList<string> configTextures = GetConfigTextures(configItem, textureCache);
                foreach (string texture in configTextures)
                {
                    if (seenTextures.Add(texture))
                    {
                        textures.Add(texture);
                    }
                }
            }
        }

        if (textures.Count == 0)
        {
            foreach (string texture in ResolveFromModelTextureDictionary(index, names, textureCache))
            {
                if (seenTextures.Add(texture))
                {
                    textures.Add(texture);
                }
            }
        }

        return textures;
    }

    private static IReadOnlyList<string> ResolveExistingTextureReferences(
        IReadOnlyList<string> textureReferences,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var resolved = new List<string>(textureReferences.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string textureReference in textureReferences)
        {
            string texturePath = ResolveExistingTextureReference(
                textureReference,
                resourcePaths,
                texturePathsByBaseName) ?? textureReference;
            if (seen.Add(texturePath))
            {
                resolved.Add(texturePath);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<string> GetConfigTextures(
        TextureConfigItem configItem,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        if (textureCache.TryGetValue(configItem.Path, out IReadOnlyList<string>? cached))
        {
            return cached;
        }

        string? text = TryReadConfigText(configItem.Item);
        IReadOnlyList<string> textures = string.IsNullOrWhiteSpace(text)
            ? []
            : ExtractTextureReferences(text, configItem.Name);
        textureCache[configItem.Path] = textures;
        return textures;
    }

    private static IReadOnlyList<string> ResolveFromModelTextureDictionary(
        TextureConfigIndex index,
        IEnumerable<string> names,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        ModelTextureDictionary dictionary = GetModelTextureDictionary(index, textureCache);
        if (dictionary.IsEmpty)
        {
            return [];
        }

        var textures = new List<string>();
        var seenTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lookupNames = names.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

        // CFT tables can contain multiple models with the same basename in
        // different folders. If the exporter supplied the source model path,
        // prefer path-exact rows and do not merge unrelated basename aliases.
        foreach (string name in lookupNames)
        {
            string exactModelPath = NormalizeExactModelReference(name);
            if (exactModelPath.Length == 0 ||
                !dictionary.ByModelPath.TryGetValue(exactModelPath, out IReadOnlyList<string>? mappedTextures))
            {
                continue;
            }

            foreach (string texture in mappedTextures)
            {
                if (seenTextures.Add(texture))
                {
                    textures.Add(texture);
                }
            }
        }

        if (textures.Count > 0)
        {
            return textures;
        }

        // Some CFT rows store trusted model-root references without an
        // extension, while the archived resource itself is named with .ltb.
        // Only use this fallback after exact path matches have failed.
        foreach (string name in lookupNames)
        {
            string extensionlessModelPath = NormalizeExtensionlessModelReference(name);
            if (extensionlessModelPath.Length == 0 ||
                !dictionary.ByModelPath.TryGetValue(extensionlessModelPath, out IReadOnlyList<string>? mappedTextures))
            {
                continue;
            }

            foreach (string texture in mappedTextures)
            {
                if (seenTextures.Add(texture))
                {
                    textures.Add(texture);
                }
            }
        }

        if (textures.Count > 0)
        {
            return textures;
        }

        // Some CFTs point to a directory's canonical model stem, while the
        // actual LTB files in that same directory are gender/skin variants
        // named <stem>_<variant>. Keep this path-scoped and prefer the
        // longest matching stem so unrelated basename aliases cannot leak in.
        foreach (string name in lookupNames)
        {
            string actualModelPath = NormalizeExtensionlessModelReference(name);
            int actualSeparator = actualModelPath.LastIndexOf('/');
            if (actualSeparator < 0 || actualSeparator == actualModelPath.Length - 1)
            {
                continue;
            }

            string actualDirectory = actualModelPath[..(actualSeparator + 1)];
            string actualStem = actualModelPath[(actualSeparator + 1)..];
            int bestStemLength = 0;
            var familyMappings = new List<IReadOnlyList<string>>();
            foreach (KeyValuePair<string, IReadOnlyList<string>> pair in dictionary.ByModelPath)
            {
                string familyPath = NormalizeExtensionlessModelReference(pair.Key);
                int familySeparator = familyPath.LastIndexOf('/');
                if (familySeparator < 0 || familySeparator == familyPath.Length - 1 ||
                    !string.Equals(
                        familyPath[..(familySeparator + 1)],
                        actualDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string familyStem = familyPath[(familySeparator + 1)..];
                if (actualStem.Length <= familyStem.Length + 1 ||
                    !actualStem.StartsWith(familyStem, StringComparison.OrdinalIgnoreCase) ||
                    actualStem[familyStem.Length] != '_')
                {
                    continue;
                }

                if (familyStem.Length > bestStemLength)
                {
                    bestStemLength = familyStem.Length;
                    familyMappings.Clear();
                }

                if (familyStem.Length == bestStemLength)
                {
                    familyMappings.Add(pair.Value);
                }
            }

            foreach (IReadOnlyList<string> mappedTextures in familyMappings)
            {
                foreach (string texture in mappedTextures)
                {
                    if (seenTextures.Add(texture))
                    {
                        textures.Add(texture);
                    }
                }
            }
        }

        if (textures.Count > 0)
        {
            return textures;
        }

        foreach (string name in lookupNames)
        {
            string exactAlias = GetResourceLookupStem(name);
            if (exactAlias.Length == 0 ||
                !dictionary.ByExactAlias.TryGetValue(exactAlias, out IReadOnlyList<string>? mappedTextures))
            {
                continue;
            }

            return mappedTextures;
        }

        foreach (string name in lookupNames)
        {
            foreach (string lookupKey in EnumerateModelLookupKeys(name))
            {
                if (!dictionary.ByName.TryGetValue(lookupKey, out IReadOnlyList<string>? mappedTextures))
                {
                    continue;
                }

                foreach (string texture in mappedTextures)
                {
                    if (seenTextures.Add(texture))
                    {
                        textures.Add(texture);
                    }
                }
            }
        }

        return textures;
    }

    private static ModelTextureDictionary GetModelTextureDictionary(
        TextureConfigIndex index,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        lock (index.ModelTextureDictionarySync)
        {
            if (index.ModelTextureDictionaryBuilt)
            {
                return index.ModelTextureDictionary ?? ModelTextureDictionary.Empty;
            }

            index.ModelTextureDictionary = BuildModelTextureDictionary(
                index.AllItems,
                index.ModelResourceItems,
                index.UccMapItems,
                index.ObjectLtoItems,
                index.ClientFxFxfItems,
                index.ClientShellItems,
                index.LtcItems,
                index.ResourcePaths,
                index.ModelPathsByBaseName,
                index.TexturePathsByBaseName,
                textureCache);
            index.ModelTextureDictionaryBuilt = true;
            return index.ModelTextureDictionary;
        }
    }

    private static ModelTextureDictionary BuildModelTextureDictionary(
        IReadOnlyList<TextureConfigItem> configItems,
        IReadOnlyList<ExplorerItem> modelResourceItems,
        IReadOnlyList<ExplorerItem> uccMapItems,
        IReadOnlyList<ExplorerItem> objectLtoItems,
        IReadOnlyList<ExplorerItem> clientFxFxfItems,
        IReadOnlyList<ExplorerItem> clientShellItems,
        IReadOnlyList<ExplorerItem> ltcItems,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byModelPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byExactAlias = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var clientShellModelRegistrations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CrossFireWeaponSkinCatalog? weaponSkinCatalog = TryBuildWeaponSkinCatalog(configItems);
        foreach (TextureConfigItem configItem in configItems.Where(ShouldScanForModelTextureDictionary))
        {
            if (string.Equals(Path.GetExtension(configItem.Path), ".cft", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? cftData = TryReadItemBytes(configItem.Item, MaxCftBytes);
                if (cftData is not null)
                {
                    foreach (CftModelTextureMapping mapping in CftModelTextureMappingDecoder.Extract(cftData))
                    {
                        // Path-block matches do not retain row boundaries and can
                        // cross-pair adjacent models/textures. Keep them available
                        // to diagnostics, but never feed them to automatic exports.
                        if (!mapping.HasTypedRowContext)
                        {
                            continue;
                        }

                        IReadOnlyList<string> mappingTextures = mapping.TextureReferences
                            .Select(texture => ResolveExistingTextureReference(
                                texture.Path,
                                resourcePaths,
                                texturePathsByBaseName) ?? texture.Path)
                            .ToArray();
                        AddModelTextureMapping(
                            byName,
                            mapping.ModelReference,
                            mappingTextures);
                        foreach (string identityAlias in mapping.IdentityAliases)
                        {
                            AddModelTextureMapping(byName, identityAlias, mappingTextures);
                        }

                        AddModelPathTextureMapping(byModelPath, mapping.ModelReference, mappingTextures);
                        foreach (string existingModelAlias in EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                                     mapping.ModelReference,
                                     mapping.TextureReferences.Select(texture => texture.Path).ToArray(),
                                     resourcePaths,
                                     modelPathsByBaseName,
                                     texturePathsByBaseName))
                        {
                            AddModelTextureMapping(byName, existingModelAlias, mappingTextures);
                            AddModelPathTextureMapping(byModelPath, existingModelAlias, mappingTextures);
                        }
                    }
                }

                continue;
            }

            string? text = TryReadConfigText(configItem.Item);
            if (string.IsNullOrWhiteSpace(text) &&
                string.Equals(Path.GetExtension(configItem.Path), ".enc", StringComparison.OrdinalIgnoreCase))
            {
                text = TryReadEncScriptText(configItem.Item);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (CrossFireWeaponSkinShiftDecoder.TryParse(text, out CrossFireWeaponSkinShiftDocument? weaponSkinShift) &&
                weaponSkinShift is not null &&
                weaponSkinShift.Entries.Count > 0)
            {
                AddWeaponSkinShiftMappings(weaponSkinShift, weaponSkinCatalog, byName, byModelPath);
                continue;
            }

            bool isEncodedScript = string.Equals(
                Path.GetExtension(configItem.Path),
                ".enc",
                StringComparison.OrdinalIgnoreCase);
            foreach (ModelTextureMapping mapping in ExtractModelTextureMappings(text))
            {
                if (!isEncodedScript)
                {
                    AddModelTextureMapping(byName, mapping.ModelReference, mapping.TextureReferences);
                    AddModelPathTextureMapping(byModelPath, mapping.ModelReference, mapping.TextureReferences);
                    continue;
                }

                string[] existingTextures = mapping.TextureReferences
                    .Select(texture => ResolveExistingTextureReference(
                        texture,
                        resourcePaths,
                        texturePathsByBaseName))
                    .Where(texture => !string.IsNullOrWhiteSpace(texture))
                    .Select(texture => texture!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (existingTextures.Length != mapping.TextureReferences.Count)
                {
                    continue;
                }

                string modelReference = NormalizeKey(mapping.ModelReference);
                bool modelExists = resourcePaths.Contains(modelReference) ||
                                   EnumerateExistingModelReferenceAliases(
                                       modelReference,
                                       resourcePaths,
                                       modelPathsByBaseName).Any();
                if (modelExists)
                {
                    AddModelTextureMapping(byName, modelReference, existingTextures);
                    AddModelPathTextureMapping(byModelPath, modelReference, existingTextures);
                }
                else if (mapping.ModelReference.StartsWith("BoxModelData_", StringComparison.OrdinalIgnoreCase) &&
                         mapping.ModelReference["BoxModelData_".Length..] is { Length: > 0 } boxIndex &&
                         boxIndex.All(char.IsDigit))
                {
                    // The box-script row key is a stable identity for one mode
                    // variant even when several rows intentionally reuse a model.
                    AddExactAliasTextureMapping(byExactAlias, mapping.ModelReference, existingTextures);
                }
            }

            if (isEncodedScript)
            {
                foreach (CrossFireScriptModelTextureMapping mapping in
                         CrossFireScriptModelTextureMappingDecoder.Extract(text))
                {
                    string modelReference = NormalizeKey(mapping.ModelReference);
                    if (!resourcePaths.Contains(modelReference) &&
                        !IsArchivedModelReference(modelReference, resourcePaths))
                    {
                        continue;
                    }

                    string[] existingTextures = mapping.TextureReferences
                        .Select(texture => ResolveExistingTextureReference(
                            texture,
                            resourcePaths,
                            texturePathsByBaseName))
                        .Where(texture => !string.IsNullOrWhiteSpace(texture))
                        .Select(texture => texture!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (existingTextures.Length != mapping.TextureReferences.Count)
                    {
                        continue;
                    }

                    AddModelTextureMapping(byName, modelReference, existingTextures);
                    AddModelPathTextureMapping(byModelPath, modelReference, existingTextures);
                }
            }

            if (!IsGenericConfigName(configItem.Name))
            {
                IReadOnlyList<string> configTextures = GetConfigTextures(configItem, textureCache);
                if (configTextures.Count > 0)
                {
                    AddModelTextureMapping(byName, configItem.Name, configTextures);
                }
            }
        }

        CrossFireUccAssetCatalog? uccCatalog = TryBuildUccAssetCatalog(configItems);
        if (uccCatalog is not null)
        {
            foreach (ExplorerItem mapItem in uccMapItems)
            {
                byte[]? mapData = TryReadItemBytes(mapItem, MaxUccMapBytes);
                if (mapData is null)
                {
                    continue;
                }

                foreach (CrossFireUccMapTextureMapping mapping in
                         CrossFireUccResourceDecoder.ExtractMapTextureMappings(mapData, uccCatalog))
                {
                    if (string.IsNullOrWhiteSpace(mapping.TextureReference))
                    {
                        continue;
                    }

                    string[] mappedTexture = [mapping.TextureReference];
                    AddModelTextureMapping(byName, mapping.ModelReference, mappedTexture);
                    AddModelTextureMapping(byName, mapping.ObjectName, mappedTexture);
                    AddModelTextureMapping(byName, mapping.SerializedAssetName, mappedTexture);
                    AddModelPathTextureMapping(byModelPath, mapping.ModelReference, mappedTexture);
                    foreach (string modelAlias in EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                                 mapping.ModelReference,
                                 [mapping.TextureReference],
                                 resourcePaths,
                                 modelPathsByBaseName,
                                 texturePathsByBaseName))
                    {
                        AddModelTextureMapping(byName, modelAlias, mappedTexture);
                        AddModelPathTextureMapping(byModelPath, modelAlias, mappedTexture);
                    }
                }
            }
        }

        var pendingObjectLtoMappings = new List<(string ModelReference, string[] Textures, string[] ModelAliases)>();
        foreach (ExplorerItem ltoItem in objectLtoItems)
        {
            byte[]? ltoData = TryReadItemBytes(ltoItem, 8 * 1024 * 1024);
            if (ltoData is null)
            {
                continue;
            }

            foreach (CrossFireObjectLtoModelTextureMapping mapping in CrossFireObjectLtoDecoder.Extract(ltoData))
            {
                string[] existingTextures = mapping.TextureReferences
                    .Select(texture => ResolveExistingTextureReference(
                        texture,
                        resourcePaths,
                        texturePathsByBaseName))
                    .Where(texture => !string.IsNullOrWhiteSpace(texture))
                    .Select(texture => texture!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (existingTextures.Length != mapping.TextureReferences.Count)
                {
                    continue;
                }

                string[] existingModelAliases = EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                        mapping.ModelReference,
                        mapping.TextureReferences,
                        resourcePaths,
                        modelPathsByBaseName,
                        texturePathsByBaseName)
                    .ToArray();
                if (!resourcePaths.Contains(mapping.ModelReference) && existingModelAliases.Length == 0)
                {
                    continue;
                }

                pendingObjectLtoMappings.Add((mapping.ModelReference, existingTextures, existingModelAliases));
            }
        }

        foreach (ExplorerItem fxfItem in clientFxFxfItems)
        {
            byte[]? fxfData = TryReadItemBytes(fxfItem, 16 * 1024 * 1024);
            if (fxfData is null ||
                !CrossFireClientFxFxfDecoder.TryDecode(fxfData, out CrossFireClientFxFxfCatalog? catalog, out _) ||
                catalog is null)
            {
                continue;
            }

            foreach (CrossFireClientFxFxfModelTextureMapping mapping in catalog.Mappings)
            {
                var existingTextureSet = mapping.TextureReferences
                    .Select(texture => ResolveExistingTextureReference(texture, resourcePaths, texturePathsByBaseName))
                    .Where(texture => !string.IsNullOrWhiteSpace(texture))
                    .Select(texture => texture!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                string[] existingTextures = existingTextureSet.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (existingTextures.Length == 0)
                {
                    continue;
                }

                IReadOnlyList<string> modelAliases = EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                    mapping.ModelReference,
                    mapping.TextureReferences,
                    resourcePaths,
                    modelPathsByBaseName,
                    texturePathsByBaseName).ToArray();
                if (!resourcePaths.Contains(mapping.ModelReference) && modelAliases.Count == 0)
                {
                    continue;
                }

                AddModelTextureMapping(byName, mapping.ModelReference, existingTextures);
                AddModelPathTextureMapping(byModelPath, mapping.ModelReference, existingTextures);
                foreach (string existingModelAlias in modelAliases)
                {
                    AddModelTextureMapping(byName, existingModelAlias, existingTextures);
                    AddModelPathTextureMapping(byModelPath, existingModelAlias, existingTextures);
                }
            }
        }

        // ClientShell's compiled table has parallel model/texture fields. Decode
        // branch-local field pairs so multi-resource registration functions retain
        // explicit mappings without crossing mutually exclusive branches.
        foreach (ExplorerItem clientShellItem in clientShellItems)
        {
            byte[]? clientShellData = TryReadItemBytes(clientShellItem, MaxClientShellBytes);
            if (clientShellData is null ||
                !CrossFireClientShellAssetMappingDecoder.TryDecodeCandidateMappings(
                    clientShellData,
                    out IReadOnlyList<CrossFireClientShellAssetMapping> shellMappings,
                    out _) ||
                shellMappings.Count == 0)
            {
                continue;
            }

            foreach (IGrouping<string, CrossFireClientShellAssetMapping> modelMappings in shellMappings
                         .GroupBy(mapping => NormalizeExactModelReference(mapping.ModelReference), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Key.Length > 0))
            {
                string modelReference = modelMappings.First().ModelReference;
                string modelPath = modelMappings.Key;

                string[] modelAliases = EnumerateExistingModelReferenceAliases(
                        modelReference,
                        resourcePaths,
                        modelPathsByBaseName)
                    .Select(NormalizeExactModelReference)
                    .Where(alias => alias.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var resolvedModelAliases = modelAliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var resolvedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CrossFireClientShellAssetMapping mapping in modelMappings)
                {
                    resolvedModelAliases.UnionWith(
                        EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                            mapping.ModelReference,
                            [mapping.TextureReference],
                            resourcePaths,
                            modelPathsByBaseName,
                            texturePathsByBaseName));

                    if (TryResolveClientShellCategoryPair(
                            mapping.ModelReference,
                            mapping.TextureReference,
                            modelPathsByBaseName,
                            texturePathsByBaseName,
                            out string modelAlias,
                            out string textureAlias))
                    {
                        resolvedModelAliases.Add(modelAlias);
                        resolvedTextures.Add(textureAlias);
                        continue;
                    }

                    string? resolvedTexture = ResolveExistingTextureReference(
                        mapping.TextureReference,
                        resourcePaths,
                        texturePathsByBaseName);
                    if (!string.IsNullOrWhiteSpace(resolvedTexture))
                    {
                        resolvedTextures.Add(resolvedTexture);
                    }
                }

                modelAliases = resolvedModelAliases.ToArray();
                if (!resourcePaths.Contains(modelReference) && modelAliases.Length == 0)
                {
                    continue;
                }

                clientShellModelRegistrations.Add(modelPath);
                clientShellModelRegistrations.UnionWith(modelAliases);

                string[] mappedTextures = resolvedTextures.ToArray();
                if (mappedTextures.Length == 0)
                {
                    continue;
                }

                AddModelPathTextureMapping(byModelPath, modelReference, mappedTextures);
                foreach (string modelAlias in modelAliases)
                {
                    AddModelPathTextureMapping(byModelPath, modelAlias, mappedTextures);
                }
            }
        }

        var ltcMappingCache = new Dictionary<string, IReadOnlyList<CrossFireLtcModelTextureMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem ltcItem in ltcItems)
        {
            string cacheKey = ltcItem.ArchiveFile?.Md5 ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                cacheKey = ltcItem.OutputRelativePath;
            }

            if (!ltcMappingCache.TryGetValue(cacheKey, out IReadOnlyList<CrossFireLtcModelTextureMapping>? ltcMappings))
            {
                ltcMappings = [];
                byte[]? ltcData = TryReadItemBytes(ltcItem, MaxLtcBytes);
                if (ltcData is not null &&
                    CrossFireLtcDecoder.TryDecodeNativeText(
                        ltcData,
                        out CrossFireLtcTextDocument? ltcDocument,
                        out _) &&
                    ltcDocument is not null)
                {
                    try
                    {
                        ltcMappings = CrossFireLtcModelTextureMappingDecoder.Extract(ltcDocument.Text);
                    }
                    catch (InvalidDataException)
                    {
                        ltcMappings = [];
                    }
                }

                ltcMappingCache[cacheKey] = ltcMappings;
            }

            foreach (CrossFireLtcModelTextureMapping mapping in ltcMappings)
            {
                string[] mappingTextures = mapping.TextureReferences
                    .Select(texture => ResolveExistingTextureReference(
                        texture,
                        resourcePaths,
                        texturePathsByBaseName) ?? texture)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                AddModelTextureMapping(byName, mapping.ModelReference, mappingTextures);
                foreach (string identityAlias in mapping.IdentityAliases)
                {
                    AddModelTextureMapping(byName, identityAlias, mappingTextures);
                }

                AddModelPathTextureMapping(byModelPath, mapping.ModelReference, mappingTextures);
                foreach (string existingModelAlias in EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
                             mapping.ModelReference,
                             mapping.TextureReferences,
                             resourcePaths,
                             modelPathsByBaseName,
                             texturePathsByBaseName))
                {
                    AddModelTextureMapping(byName, existingModelAlias, mappingTextures);
                    AddModelPathTextureMapping(byModelPath, existingModelAlias, mappingTextures);
                }
            }
        }

        foreach (ModelMaterialComponentTextureMapping mapping in GetModelMaterialComponentTextureMappings(
                     modelResourceItems,
                     resourcePaths,
                     modelPathsByBaseName,
                     texturePathsByBaseName))
        {
            string[] mappedTexture = [mapping.TextureReference];
            AddModelTextureMapping(byName, mapping.ModelReference, mappedTexture);
            AddModelPathTextureMapping(byModelPath, mapping.ModelReference, mappedTexture);
            foreach (string modelAlias in EnumerateExistingModelReferenceAliases(
                         mapping.ModelReference,
                         resourcePaths,
                         modelPathsByBaseName))
            {
                AddModelTextureMapping(byName, modelAlias, mappedTexture);
                AddModelPathTextureMapping(byModelPath, modelAlias, mappedTexture);
            }
        }

        // Object.lto is a compiled image whose resource paths are only grouped by
        // adjacency. Treat those links as fallback evidence and do not let them
        // override typed tables, ClientFX skins, ClientShell registrations, or LTC rows.
        foreach ((string modelReference, string[] textures, string[] modelAliases) in pendingObjectLtoMappings)
        {
            string modelPath = NormalizeExactModelReference(modelReference);
            string[] aliasPaths = modelAliases
                .Select(NormalizeExactModelReference)
                .Where(alias => alias.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modelPath.Length == 0 ||
                byModelPath.ContainsKey(modelPath) ||
                clientShellModelRegistrations.Contains(modelPath) ||
                aliasPaths.Any(clientShellModelRegistrations.Contains) ||
                aliasPaths.Any(alias => byModelPath.ContainsKey(alias)))
            {
                continue;
            }

            AddModelTextureMapping(byName, modelReference, textures);
            AddModelPathTextureMapping(byModelPath, modelReference, textures);
            foreach (string modelAlias in modelAliases)
            {
                AddModelTextureMapping(byName, modelAlias, textures);
                AddModelPathTextureMapping(byModelPath, modelAlias, textures);
            }
        }

        return new ModelTextureDictionary(
            byName.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            byModelPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            byExactAlias.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    private static CrossFireWeaponSkinCatalog? TryBuildWeaponSkinCatalog(
        IReadOnlyList<TextureConfigItem> configItems)
    {
        var linksByScriptIndex = new Dictionary<int, CrossFireWeaponSkinShiftLink>();
        var ambiguousScriptIndices = new HashSet<int>();
        var baseItems = new List<CrossFireWeaponBaseItem>();
        var itemIdentities = new List<CrossFireWeaponItemIdentity>();
        bool foundShiftTable = false;

        foreach (TextureConfigItem item in configItems.Where(configItem =>
                     string.Equals(Path.GetExtension(configItem.Path), ".cft", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(item.Name, "VVIPSkinWeaponShift", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? data = TryReadItemBytes(item.Item, MaxCftBytes);
                if (data is null ||
                    !CrossFireWeaponSkinCatalogDecoder.TryDecodeShiftLinks(data, out IReadOnlyList<CrossFireWeaponSkinShiftLink> links))
                {
                    continue;
                }

                foundShiftTable = true;
                foreach (CrossFireWeaponSkinShiftLink link in links.Where(link => link.ScriptIndex > 0))
                {
                    if (ambiguousScriptIndices.Contains(link.ScriptIndex))
                    {
                        continue;
                    }

                    if (!linksByScriptIndex.TryAdd(link.ScriptIndex, link) &&
                        !Equals(linksByScriptIndex[link.ScriptIndex], link))
                    {
                        linksByScriptIndex.Remove(link.ScriptIndex);
                        ambiguousScriptIndices.Add(link.ScriptIndex);
                    }
                }

                continue;
            }

            if (!string.Equals(item.Name, "VVIPWeaponSkin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Name, "VVIPWeaponSkin2", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Name, "ITEM", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[]? itemData = TryReadItemBytes(item.Item, MaxCftBytes);
                if (itemData is not null &&
                    CrossFireWeaponSkinCatalogDecoder.TryDecodeItemIdentities(
                        itemData,
                        out IReadOnlyList<CrossFireWeaponItemIdentity> decodedItemIdentities))
                {
                    itemIdentities.AddRange(decodedItemIdentities);
                }

                continue;
            }

            byte[]? baseItemData = TryReadItemBytes(item.Item, MaxCftBytes);
            if (baseItemData is not null &&
                CrossFireWeaponSkinCatalogDecoder.TryDecodeBaseItems(
                    baseItemData,
                    out IReadOnlyList<CrossFireWeaponBaseItem> decodedBaseItems))
            {
                baseItems.AddRange(decodedBaseItems);
            }
        }

        if (!foundShiftTable || linksByScriptIndex.Count == 0)
        {
            return null;
        }

        return CrossFireWeaponSkinCatalogDecoder.CreateCatalog(
            linksByScriptIndex.Values,
            baseItems,
            itemIdentities,
            ambiguousScriptIndices);
    }

    private static CrossFireUccAssetCatalog? TryBuildUccAssetCatalog(
        IReadOnlyList<TextureConfigItem> configItems)
    {
        TextureConfigItem[] objectTables = configItems
            .Where(item => string.Equals(item.Name, "UCCAssetObject", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(Path.GetExtension(item.Path), ".cft", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TextureConfigItem[] textureTables = configItems
            .Where(item => string.Equals(item.Name, "UCCAssetObjectTextures", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(Path.GetExtension(item.Path), ".cft", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (objectTables.Length == 0 || textureTables.Length == 0)
        {
            return null;
        }

        CrossFireUccAssetCatalog? result = null;
        foreach (TextureConfigItem objectTable in objectTables)
        {
            byte[]? objectTableData = TryReadItemBytes(objectTable.Item, MaxCftBytes);
            if (objectTableData is null)
            {
                continue;
            }

            foreach (TextureConfigItem textureTable in textureTables)
            {
                byte[]? textureTableData = TryReadItemBytes(textureTable.Item, MaxCftBytes);
                if (textureTableData is null ||
                    !CrossFireUccResourceDecoder.TryDecodeAssetCatalog(
                        objectTableData,
                        textureTableData,
                        out CrossFireUccAssetCatalog? candidate) ||
                    candidate is null)
                {
                    continue;
                }

                if (result is null)
                {
                    result = candidate;
                }
                else if (!AreEquivalentUccCatalogs(result, candidate))
                {
                    // Different table revisions can reuse CFT row IDs. Do not
                    // select one silently when the mounted resources disagree.
                    return null;
                }
            }
        }

        return result;
    }

    private static bool AreEquivalentUccCatalogs(
        CrossFireUccAssetCatalog left,
        CrossFireUccAssetCatalog right)
    {
        if (left.ObjectsByName.Count != right.ObjectsByName.Count ||
            left.TexturesById.Count != right.TexturesById.Count)
        {
            return false;
        }

        foreach ((string name, CrossFireUccObjectAsset leftAsset) in left.ObjectsByName)
        {
            if (!right.ObjectsByName.TryGetValue(name, out CrossFireUccObjectAsset? rightAsset) ||
                leftAsset.RecordId != rightAsset.RecordId ||
                !string.Equals(leftAsset.ModelReference, rightAsset.ModelReference, StringComparison.OrdinalIgnoreCase) ||
                !leftAsset.AllowedTextureIds.SetEquals(rightAsset.AllowedTextureIds))
            {
                return false;
            }
        }

        foreach ((int id, CrossFireUccTextureAsset leftTexture) in left.TexturesById)
        {
            if (!right.TexturesById.TryGetValue(id, out CrossFireUccTextureAsset? rightTexture) ||
                !string.Equals(leftTexture.TextureReference, rightTexture.TextureReference, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddWeaponSkinShiftMappings(
        CrossFireWeaponSkinShiftDocument document,
        CrossFireWeaponSkinCatalog? catalog,
        Dictionary<string, List<string>> byName,
        Dictionary<string, List<string>> byModelPath)
    {
        IReadOnlyList<CrossFireWeaponSkinItemMapping> itemMappings = catalog is null
            ? []
            : CrossFireWeaponSkinCatalogDecoder.Join(catalog, document);
        var itemMappingsByScriptIndex = itemMappings.ToDictionary(
            mapping => mapping.ScriptEntry.ScriptIndex);

        foreach (CrossFireWeaponSkinShiftEntry entry in document.Entries)
        {
            string[] worldTextures = GetWeaponSkinShiftTextureReferences(entry, "SkinFileName");
            string[] playerViewTextures = GetWeaponSkinShiftTextureReferences(entry, "PViewSkinFileName");
            string[] projectileTextures = GetWeaponSkinShiftTextureReferences(entry, "ProjectileSkinFileName");
            string[] allSkinTextures = worldTextures
                .Concat(playerViewTextures)
                .Concat(projectileTextures)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string? standardName = entry.GetValue("StandardName");
            if (!string.IsNullOrWhiteSpace(standardName) && allSkinTextures.Length > 0)
            {
                AddModelTextureMapping(byName, standardName, allSkinTextures);
            }

            if (allSkinTextures.Length > 0 && itemMappingsByScriptIndex.TryGetValue(entry.ScriptIndex, out CrossFireWeaponSkinItemMapping? itemMapping))
            {
                CrossFireWeaponSkinShiftLink link = itemMapping.Link;
                AddModelTextureMapping(
                    byName,
                    string.Create(CultureInfo.InvariantCulture, $"{link.ItemIndex}:{link.ScriptIndex}"),
                    allSkinTextures);
                AddModelTextureMapping(
                    byName,
                    string.Create(CultureInfo.InvariantCulture, $"{link.ButeIndex}:{link.ScriptIndex}"),
                    allSkinTextures);
                AddModelTextureMapping(
                    byName,
                    link.ItemIndex.ToString(CultureInfo.InvariantCulture),
                    allSkinTextures);
                AddModelTextureMapping(
                    byName,
                    "item:" + link.ItemIndex.ToString(CultureInfo.InvariantCulture),
                    allSkinTextures);
                if (link.ButeIndex > 0)
                {
                    AddModelTextureMapping(
                        byName,
                        "bute:" + link.ButeIndex.ToString(CultureInfo.InvariantCulture),
                        allSkinTextures);
                }

                if (link.ChangeButeIndex > 0)
                {
                    AddModelTextureMapping(
                        byName,
                        "bute:" + link.ChangeButeIndex.ToString(CultureInfo.InvariantCulture),
                        allSkinTextures);
                }

                foreach (CrossFireWeaponBaseItem baseItem in itemMapping.MatchingBaseItems)
                {
                    AddModelTextureMapping(byName, baseItem.WeaponName, allSkinTextures);
                    AddModelTextureMapping(
                        byName,
                        baseItem.WeaponName + link.SkinSuffix,
                        allSkinTextures);
                }

                foreach (CrossFireWeaponItemIdentity itemIdentity in itemMapping.MatchingItemIdentities)
                {
                    AddModelTextureMapping(byName, itemIdentity.Name, allSkinTextures);
                    AddModelTextureMapping(byName, itemIdentity.Name + link.SkinSuffix, allSkinTextures);
                    if (!string.IsNullOrWhiteSpace(itemIdentity.ShortName))
                    {
                        AddModelTextureMapping(byName, itemIdentity.ShortName, allSkinTextures);
                        AddModelTextureMapping(byName, itemIdentity.ShortName + link.SkinSuffix, allSkinTextures);
                    }
                }
            }

            foreach (KeyValuePair<string, string> field in entry.Fields)
            {
                if (!field.Key.EndsWith("ModelFileName", StringComparison.OrdinalIgnoreCase) ||
                    !IsKnownModelPathRoot(NormalizeResourceReference(field.Value)))
                {
                    continue;
                }

                IReadOnlyList<string> textureReferences = field.Key.StartsWith("PView", StringComparison.OrdinalIgnoreCase)
                    ? playerViewTextures
                    : field.Key.StartsWith("Projectile", StringComparison.OrdinalIgnoreCase)
                        ? projectileTextures
                        : worldTextures;
                if (textureReferences.Count == 0)
                {
                    continue;
                }

                AddModelTextureMapping(byName, field.Value, textureReferences);
                AddModelPathTextureMapping(byModelPath, field.Value, textureReferences);
            }
        }
    }

    private static string[] GetWeaponSkinShiftTextureReferences(
        CrossFireWeaponSkinShiftEntry entry,
        string fieldPrefix)
    {
        return entry.Fields
            .Where(field => string.Equals(field.Key, fieldPrefix, StringComparison.OrdinalIgnoreCase) ||
                            field.Key.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase) &&
                            field.Key.Length > fieldPrefix.Length &&
                            field.Key[fieldPrefix.Length..].All(char.IsDigit))
            .Select(field => NormalizeResourceReference(field.Value))
            .Where(value => string.Equals(Path.GetExtension(value), ".dtx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ModelMaterialComponentTextureMapping> GetModelMaterialComponentTextureMappings(
        IReadOnlyList<ExplorerItem> modelResourceItems,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var modelItemsByPath = modelResourceItems
            .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => NormalizeKey(item.ArchiveFile?.FullPath ?? item.OutputRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var decodedModels = new Dictionary<string, LithTechModelDocument?>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, ModelMaterialComponentTextureMapping>(StringComparer.OrdinalIgnoreCase);

        foreach (string texturePath in resourcePaths.Where(path =>
                     string.Equals(Path.GetExtension(path), ".dtx", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryGetMaterialComponentTextureFamily(texturePath, out string modelBaseName, out string componentName) ||
                !modelPathsByBaseName.TryGetValue(modelBaseName.Normalize(NormalizationForm.FormKC), out IReadOnlyList<string>? modelPaths))
            {
                continue;
            }

            string? resolvedTexture = ResolveExistingTextureReference(
                texturePath,
                resourcePaths,
                texturePathsByBaseName);
            if (string.IsNullOrWhiteSpace(resolvedTexture))
            {
                continue;
            }

            foreach (string modelPath in modelPaths.Where(path =>
                         string.Equals(Path.GetExtension(path), ".ltb", StringComparison.OrdinalIgnoreCase)))
            {
                string normalizedModelPath = NormalizeKey(modelPath);
                if (!modelItemsByPath.TryGetValue(normalizedModelPath, out ExplorerItem? modelItem))
                {
                    continue;
                }

                string? archiveMd5 = modelItem.ArchiveFile?.Md5;
                string decodeCacheKey = string.IsNullOrWhiteSpace(archiveMd5)
                    ? normalizedModelPath
                    : archiveMd5;

                if (!decodedModels.TryGetValue(decodeCacheKey, out LithTechModelDocument? document))
                {
                    byte[]? modelData = TryReadItemBytes(modelItem, 64 * 1024 * 1024);
                    if (modelData is not null)
                    {
                        LithTechModelDecoder.TryDecode(modelData, modelPath, "ltb", out document, out _);
                    }

                    decodedModels[decodeCacheKey] = document;
                }

                if (document is null)
                {
                    continue;
                }

                foreach (LithTechMesh mesh in document.Meshes)
                {
                    if (!MeshNameContainsComponent(mesh, componentName) || mesh.TextureSlotIndices is null)
                    {
                        continue;
                    }

                    foreach (int slotIndex in mesh.TextureSlotIndices.Distinct())
                    {
                        var mapping = new ModelMaterialComponentTextureMapping(
                            modelPath,
                            slotIndex,
                            resolvedTexture);
                        mappings.TryAdd(
                            $"{NormalizeExtensionlessModelReference(modelPath)}\0{slotIndex}\0{resolvedTexture}",
                            mapping);
                    }
                }
            }
        }

        return mappings.Values.ToArray();
    }

    private static bool TryGetMaterialComponentTextureFamily(
        string texturePath,
        out string modelBaseName,
        out string componentName)
    {
        modelBaseName = string.Empty;
        componentName = string.Empty;
        string textureStem = Path.GetFileNameWithoutExtension(texturePath);
        string[] componentNames = ["body", "face", "hair"];
        foreach (string candidate in componentNames)
        {
            string suffix = "_" + candidate;
            if (!textureStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                textureStem.Length <= suffix.Length)
            {
                continue;
            }

            modelBaseName = textureStem[..^suffix.Length];
            componentName = candidate;
            return true;
        }

        return false;
    }

    private static bool MeshNameContainsComponent(LithTechMesh mesh, string componentName)
    {
        string normalizedComponent = NormalizeLooseKey(componentName);
        return NormalizeLooseKey(mesh.Name).Contains(normalizedComponent, StringComparison.Ordinal) ||
               mesh.MaterialHints?.Any(hint => NormalizeLooseKey(hint).Contains(normalizedComponent, StringComparison.Ordinal)) == true;
    }

    private static ModelTextureSlotDictionary BuildModelTextureSlotDictionary(
        IReadOnlyList<TextureConfigItem> configItems,
        IReadOnlyList<ExplorerItem> resourceItems,
        IReadOnlyList<ExplorerItem> ltcItems,
        IReadOnlyList<ExplorerItem> crossFireScriptItems,
        IReadOnlyList<ExplorerItem> modelResourceItems,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var byModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var byModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var inferredByModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var inferredByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var ltcPreviewVariantByModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var ltcPreviewVariantByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var previewNormalizedBasenameByModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var variantSlotUvByModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var motionParentSlotByModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var motionParentSlotByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var cftIdentityModelAliasByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var weaponPreviewVariantByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var renderStyleByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var ltcTextureByModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var modelsWithSlotSpecificBindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredCftMotionSkinBindings = new List<DeferredCftMotionSkinBinding>();
        var deferredCftIdentityModelAliasBindings = new List<DeferredCftIdentityModelAliasBinding>();
        var cftPlayerViewRoleTextureCandidates = new Dictionary<
            string,
            Dictionary<CftPlayerViewTextureRole, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var directPlayerViewModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typedLtcPlayerViewModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelItemsByPath = modelResourceItems
            .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, ResourcePath: GetModelResourcePath(item)))
            .Select(pair => (pair.Item, pair.ResourcePath, LogicalPath: GetLogicalModelResourcePath(pair.ResourcePath)))
            .Where(pair => pair.LogicalPath.Length > 0)
            .GroupBy(pair => pair.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Item).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var modelItemsByExtensionlessPath = modelResourceItems
            .Select(item => (Item: item, ResourcePath: GetModelResourcePath(item)))
            .Select(pair => (
                pair.Item,
                LogicalPath: GetLogicalModelResourcePath(pair.ResourcePath),
                Extension: Path.GetExtension(pair.ResourcePath)))
            .Where(pair => pair.LogicalPath.Length > 0 &&
                           ModelExtensions.Contains(pair.Extension, StringComparer.OrdinalIgnoreCase))
            .GroupBy(
                pair => NormalizeExtensionlessModelReference(pair.LogicalPath),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 &&
                            group.Select(pair => pair.Extension)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count() == 1 &&
                            string.Equals(group.First().Extension, ".ltb", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Item).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var modelItemsByPlayerViewRolePath = modelResourceItems
            .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, RolePath: NormalizeModelResourcePath(GetModelResourcePath(item))))
            .Where(pair => pair.RolePath.Length > 0)
            .GroupBy(pair => pair.RolePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Item).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var textureItemsByLogicalPath = resourceItems
            .Where(item => string.Equals(item.FileExtension, "dtx", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, LogicalPath: GetLogicalTextureResourcePath(GetModelResourcePath(item))))
            .Where(pair => pair.LogicalPath.Length > 0)
            .GroupBy(pair => pair.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Item).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var verifiedTexturePathCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var decodedModels = new Dictionary<string, LithTechModelDocument?>(StringComparer.OrdinalIgnoreCase);
        foreach (TextureConfigItem configItem in configItems.Where(configItem =>
                     string.Equals(Path.GetExtension(configItem.Path), ".cft", StringComparison.OrdinalIgnoreCase)))
        {
            byte[]? cftData = TryReadItemBytes(configItem.Item, MaxCftBytes);
            if (cftData is null)
            {
                continue;
            }

            foreach (CftModelTextureMapping mapping in CftModelTextureMappingDecoder.Extract(cftData))
            {
                // Only typed rows preserve the model/texture relationship. Numbered
                // fields are explicit engine slots. Player-view hand/arm fields are
                // resolved separately against decoded LTB mesh-slot bindings.
                if (!mapping.HasTypedRowContext)
                {
                    continue;
                }

                string modelResourcePath = NormalizeKey(mapping.ModelReference);
                string modelPath = NormalizeExtensionlessModelReference(modelResourcePath);
                if (modelPath.Length == 0)
                {
                    continue;
                }

                string[] contextKeys = GetCftContextKeys(configItem.Path, mapping);

                foreach (CftTextureReference texture in mapping.TextureReferences)
                {
                    if (!CftPlayerViewRoleSlotResolver.TryGetTextureRole(
                            texture.Column,
                            out CftPlayerViewTextureRole role) ||
                        !TryGetCftPlayerViewTextureSide(texture.Column, out string textureSide))
                    {
                        continue;
                    }

                    string? existingTexturePath = ResolveExistingCftTextureReference(
                        modelResourcePath,
                        texture.Path,
                        resourceItems,
                        resourcePaths,
                        texturePathsByBaseName);
                    bool hasConsistentTextureCopies = existingTexturePath is not null &&
                        HasConsistentPhysicalResourceCopies(resourceItems, existingTexturePath);
                    string candidate = hasConsistentTextureCopies
                        ? NormalizeKey(existingTexturePath!)
                        : "\u0001" + NormalizeKey(texture.Path);

                    foreach (string contextKey in GetCftSideContextKeys(
                                 configItem.Path,
                                 mapping,
                                 textureSide))
                    {
                        AddCftPlayerViewRoleTextureCandidate(
                            cftPlayerViewRoleTextureCandidates,
                            contextKey,
                            role,
                            candidate);
                    }
                }

                if (string.Equals(
                        Path.GetFileName(configItem.Path),
                        "Parts.CFT",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(mapping.ModelColumn, "_ModelFileName", StringComparison.OrdinalIgnoreCase) &&
                    mapping.TextureReferences is { Count: 1 } partsTextureReferences &&
                    string.Equals(
                        partsTextureReferences[0].Column.TrimStart('_'),
                        "ModelTexFileName",
                        StringComparison.OrdinalIgnoreCase) &&
                    modelItemsByPath.TryGetValue(
                        GetLogicalModelResourcePath(modelResourcePath),
                        out ExplorerItem[]? partsModelItems) &&
                    TryGetConsistentLtcModelSlots(partsModelItems, decodedModels, out int[] partsModelSlots) &&
                    partsModelSlots.Length == 1)
                {
                    CftTextureReference partsTexture = partsTextureReferences[0];
                    string? existingPartsTexturePath = ResolveExistingCftTextureReference(
                        modelResourcePath,
                        partsTexture.Path,
                        resourceItems,
                        resourcePaths,
                        texturePathsByBaseName);
                    bool hasExactPartsTexture = existingPartsTexturePath is not null &&
                        string.Equals(
                            NormalizeKey(existingPartsTexturePath),
                            NormalizeKey(partsTexture.Path),
                            StringComparison.OrdinalIgnoreCase);
                    int slotIndex = partsModelSlots[0];
                    AddModelTextureSlotMapping(
                        byModelPath,
                        modelPath,
                        slotIndex,
                        hasExactPartsTexture ? existingPartsTexturePath! : partsTexture.Path);
                    if (hasExactPartsTexture &&
                        HasConsistentPhysicalResourceCopies(resourceItems, existingPartsTexturePath!))
                    {
                        foreach (string contextKey in contextKeys)
                        {
                            AddModelTextureContextSlotMapping(
                                byModelContext,
                                modelPath,
                                contextKey,
                                slotIndex,
                                existingPartsTexturePath!);
                        }
                    }

                    modelsWithSlotSpecificBindings.Add(modelPath);
                    continue;
                }

                var rowSlotTextures = new Dictionary<int, string?>();
                var explicitTextureSlots = new HashSet<int>();
                var exactCftTextureSlots = new HashSet<int>();
                bool hasImplicitCharacterBodyTexture = false;
                string implicitCharacterBodySide = string.Empty;
                foreach (CftTextureReference texture in mapping.TextureReferences)
                {
                    if (CftModelTextureMappingDecoder.TryGetTextureSlotIndex(texture.Column, out int slotIndex))
                    {
                        string? existingTexturePath = ResolveExistingCftTextureReference(
                            modelResourcePath,
                            texture.Path,
                            resourceItems,
                            resourcePaths,
                            texturePathsByBaseName);
                        int resolvedSlotIndex = TryResolveVerifiedCftTextureFieldSlotAlias(
                            configItem.Path,
                            mapping,
                            texture,
                            modelResourcePath,
                            slotIndex,
                            existingTexturePath,
                            modelItemsByPath,
                            decodedModels,
                            textureItemsByLogicalPath,
                            verifiedTexturePathCache,
                            out int aliasSlotIndex)
                            ? aliasSlotIndex
                            : slotIndex;
                        explicitTextureSlots.Add(resolvedSlotIndex);
                        string texturePath = existingTexturePath ?? texture.Path;
                        AddModelTextureSlotMapping(byModelPath, modelPath, resolvedSlotIndex, texturePath);

                        if (existingTexturePath is not null)
                        {
                            if (string.Equals(
                                    NormalizeKey(existingTexturePath),
                                    NormalizeKey(texture.Path),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                exactCftTextureSlots.Add(resolvedSlotIndex);
                            }

                            foreach (string contextKey in contextKeys)
                            {
                                AddModelTextureContextSlotMapping(
                                    byModelContext,
                                    modelPath,
                                    contextKey,
                                    resolvedSlotIndex,
                                    existingTexturePath);
                            }
                        }

                        if (existingTexturePath is not null)
                        {
                            if (!rowSlotTextures.TryGetValue(resolvedSlotIndex, out string? existingRowTexture))
                            {
                                rowSlotTextures.Add(resolvedSlotIndex, existingTexturePath);
                            }
                            else if (!string.Equals(existingRowTexture, existingTexturePath, StringComparison.OrdinalIgnoreCase))
                            {
                                rowSlotTextures[resolvedSlotIndex] = null;
                            }
                        }
                    }
                    else if (IsPrimaryCftMotionSourceColumn(mapping.ModelColumn) &&
                             IsUnindexedCftSkinFileName(texture.Column) &&
                             mapping.RelatedModelReferences is { Count: > 0 } relatedModelReferences)
                    {
                        string? existingTexturePath = ResolveExistingCftTextureReference(
                            modelResourcePath,
                            texture.Path,
                            resourceItems,
                            resourcePaths,
                            texturePathsByBaseName);
                        if (existingTexturePath is null)
                        {
                            continue;
                        }

                        foreach (string relatedModelReference in relatedModelReferences
                                     .Where(reference => Path.GetFileNameWithoutExtension(reference)
                                         .Contains("motion", StringComparison.OrdinalIgnoreCase))
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            deferredCftMotionSkinBindings.Add(new DeferredCftMotionSkinBinding(
                                modelResourcePath,
                                relatedModelReference,
                                existingTexturePath,
                                contextKeys));
                        }
                    }
                    else if (TryGetCharacterCftBodySide(configItem.Path, mapping, texture, out string bodySide))
                    {
                        string? existingTexturePath = ResolveExistingCftTextureReference(
                            modelResourcePath,
                            texture.Path,
                            resourceItems,
                            resourcePaths,
                            texturePathsByBaseName);
                        if (existingTexturePath is null)
                        {
                            continue;
                        }

                        const int bodyTextureSlot = 0;
                        if (modelItemsByPath.TryGetValue(
                                GetLogicalModelResourcePath(modelResourcePath),
                                out ExplorerItem[]? characterBodyItems) &&
                            characterBodyItems is { Length: > 0 })
                        {
                            if (!TryGetConsistentLtcModelSlots(
                                    characterBodyItems,
                                    decodedModels,
                                    out int[] characterBodySlots) ||
                                !characterBodySlots.Contains(bodyTextureSlot))
                            {
                                continue;
                            }

                            AddModelTextureSlotMapping(byModelPath, modelPath, bodyTextureSlot, existingTexturePath);
                            if (TryGetConsistentCharacterHeadTextureSlot(
                                    characterBodyItems,
                                    decodedModels,
                                    out int headTextureSlot) &&
                                TryResolveCharacterFaceTextureReference(
                                    existingTexturePath,
                                    resourcePaths,
                                    texturePathsByBaseName,
                                    out string faceTexturePath))
                            {
                                AddModelTextureSlotMapping(
                                    inferredByModelPath,
                                    modelPath,
                                    headTextureSlot,
                                    faceTexturePath);
                                foreach (string contextKey in GetCftSideContextKeys(configItem.Path, mapping, bodySide))
                                {
                                    AddModelTextureContextSlotMapping(
                                        inferredByModelContext,
                                        modelPath,
                                        contextKey,
                                        headTextureSlot,
                                        faceTexturePath);
                                }
                            }

                            foreach (string contextKey in GetCftSideContextKeys(configItem.Path, mapping, bodySide))
                            {
                                AddModelTextureContextSlotMapping(
                                    byModelContext,
                                    modelPath,
                                    contextKey,
                                    bodyTextureSlot,
                                    existingTexturePath);
                            }
                        }

                        hasImplicitCharacterBodyTexture = true;
                        implicitCharacterBodySide = bodySide;
                        modelsWithSlotSpecificBindings.Add(modelPath);

                        if (!rowSlotTextures.TryGetValue(bodyTextureSlot, out string? existingRowTexture))
                        {
                            rowSlotTextures.Add(bodyTextureSlot, existingTexturePath);
                        }
                        else if (!string.Equals(existingRowTexture, existingTexturePath, StringComparison.OrdinalIgnoreCase))
                        {
                            rowSlotTextures[bodyTextureSlot] = null;
                        }
                    }
                }

                CftTextureReference[] playerViewRoleTextures = mapping.TextureReferences
                    .Where(texture => CftPlayerViewRoleSlotResolver.TryGetTextureRole(texture.Column, out _))
                    .ToArray();
                if (explicitTextureSlots.Count > 0)
                {
                    modelsWithSlotSpecificBindings.Add(modelPath);
                }

                if (playerViewRoleTextures.Length > 0 &&
                    modelItemsByPlayerViewRolePath.TryGetValue(
                        NormalizeModelResourcePath(modelResourcePath),
                        out ExplorerItem[]? playerViewModelItems) &&
                    playerViewModelItems.Length > 0)
                {
                    modelsWithSlotSpecificBindings.Add(modelPath);
                    directPlayerViewModels.Add(modelPath);
                    bool hasHandTextureReference = playerViewRoleTextures.Any(texture =>
                        CftPlayerViewRoleSlotResolver.TryGetTextureRole(texture.Column, out CftPlayerViewTextureRole role) &&
                        role == CftPlayerViewTextureRole.Hand);
                    bool hasArmTextureReference = playerViewRoleTextures.Any(texture =>
                        CftPlayerViewRoleSlotResolver.TryGetTextureRole(texture.Column, out CftPlayerViewTextureRole role) &&
                        role == CftPlayerViewTextureRole.Arm);
                    var playerViewDocuments = new List<LithTechModelDocument>(playerViewModelItems.Length);
                    foreach (ExplorerItem playerViewModelItem in playerViewModelItems)
                    {
                        LithTechModelDocument? playerViewDocument = TryDecodeSlotInheritanceModel(
                            playerViewModelItem,
                            GetModelResourcePath(playerViewModelItem),
                            decodedModels);
                        if (playerViewDocument is null)
                        {
                            playerViewDocuments.Clear();
                            break;
                        }

                        playerViewDocuments.Add(playerViewDocument);
                    }

                    CftPlayerViewRoleSlotMap roleSlots = CftPlayerViewRoleSlotResolver.Resolve(
                        playerViewDocuments,
                        hasHandTextureReference,
                        hasArmTextureReference);
                    foreach (CftTextureReference texture in playerViewRoleTextures)
                    {
                        if (!CftPlayerViewRoleSlotResolver.TryGetTextureRole(
                                texture.Column,
                                out CftPlayerViewTextureRole role))
                        {
                            continue;
                        }

                        IReadOnlyList<int> mappedSlots = roleSlots.GetSlots(role);
                        if (mappedSlots.Count == 0)
                        {
                            continue;
                        }

                        string? existingTexturePath = ResolveExistingCftTextureReference(
                            modelResourcePath,
                            texture.Path,
                            resourceItems,
                            resourcePaths,
                            texturePathsByBaseName);
                        string texturePath = existingTexturePath ?? texture.Path;
                        foreach (int slotIndex in mappedSlots)
                        {
                            // Explicit numbered CFT fields take precedence if both
                            // forms happen to describe the same material slot.
                            if (explicitTextureSlots.Contains(slotIndex))
                            {
                                continue;
                            }

                            AddModelTextureSlotMapping(byModelPath, modelPath, slotIndex, texturePath);
                            if (existingTexturePath is null)
                            {
                                continue;
                            }

                            foreach (string contextKey in contextKeys)
                            {
                                AddModelTextureContextSlotMapping(
                                    byModelContext,
                                    modelPath,
                                    contextKey,
                                    slotIndex,
                                    existingTexturePath);
                            }

                            if (!rowSlotTextures.TryGetValue(slotIndex, out string? existingRowTexture))
                            {
                                rowSlotTextures.Add(slotIndex, existingTexturePath);
                            }
                            else if (!string.Equals(existingRowTexture, existingTexturePath, StringComparison.OrdinalIgnoreCase))
                            {
                                rowSlotTextures[slotIndex] = null;
                            }
                        }
                    }
                }

                if (string.Equals(
                        Path.GetFileName(configItem.Path),
                        "WeaponAttachItemDoll.CFT",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(mapping.ModelColumn, "_PVItemModelFile", StringComparison.OrdinalIgnoreCase) &&
                    modelPath.StartsWith("Models/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) &&
                    modelPath.Contains("/ACC_SD/", StringComparison.OrdinalIgnoreCase) &&
                    mapping.IdentityAliases is { Count: > 0 })
                {
                    foreach ((int slotIndex, string? texturePath) in rowSlotTextures)
                    {
                        if (!exactCftTextureSlots.Contains(slotIndex) || string.IsNullOrWhiteSpace(texturePath))
                        {
                            continue;
                        }

                        deferredCftIdentityModelAliasBindings.Add(new DeferredCftIdentityModelAliasBinding(
                            modelPath,
                            mapping.IdentityAliases,
                            slotIndex,
                            texturePath,
                            contextKeys));
                    }
                }

                ExplorerItem[] baseModelItems = modelItemsByPath.TryGetValue(
                    GetLogicalModelResourcePath(modelResourcePath),
                    out ExplorerItem[]? foundBaseModelItems)
                    ? foundBaseModelItems
                    : [];
                bool hasPhysicalParentModels = baseModelItems.Length > 0;
                if (!IsPrimaryCftMotionSourceColumn(mapping.ModelColumn) ||
                    rowSlotTextures.Count == 0 ||
                    mapping.RelatedModelReferences is not { Count: > 0 } relatedModels ||
                    !hasPhysicalParentModels && !hasImplicitCharacterBodyTexture)
                {
                    continue;
                }

                foreach (string relatedModelReference in relatedModels)
                {
                    string relatedStem = Path.GetFileNameWithoutExtension(relatedModelReference);
                    if (!relatedStem.Contains("motion", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relatedResourcePath = NormalizeKey(relatedModelReference);
                    ExplorerItem[]? relatedModelItems;
                    bool hasRelatedModel = Path.HasExtension(relatedResourcePath)
                        ? modelItemsByPath.TryGetValue(
                            GetLogicalModelResourcePath(relatedResourcePath),
                            out relatedModelItems)
                        : modelItemsByExtensionlessPath.TryGetValue(
                            NormalizeExtensionlessModelReference(relatedResourcePath),
                            out relatedModelItems);
                    if (!hasRelatedModel || relatedModelItems is null)
                    {
                        continue;
                    }

                    if (hasImplicitCharacterBodyTexture &&
                        (!TryGetConsistentLtcModelSlots(relatedModelItems, decodedModels, out int[] relatedSlots) ||
                         hasPhysicalParentModels && !relatedSlots.Contains(0) ||
                         !hasPhysicalParentModels && !relatedSlots.SequenceEqual([0])))
                    {
                        // Character body fields imply slot 0. With a physical
                        // parent, require that slot to exist on every decoded
                        // motion copy; without one, only a single-slot child is
                        // strong enough evidence for direct same-row binding.
                        // Other slots on multi-slot motion models stay unmapped.
                        continue;
                    }

                    bool allRelatedCopiesValidated = relatedModelItems.Length > 0;
                    HashSet<int> validatedSlotSet = rowSlotTextures.Keys.ToHashSet();
                    var parentUsedSlots = new HashSet<int>();
                    var childUsedSlots = new HashSet<int>();
                    IEnumerable<(ExplorerItem Item, string Path, bool IsChild)> modelsToValidate =
                        (hasPhysicalParentModels
                            ? baseModelItems.Select(item => (item, modelResourcePath, false))
                            : Enumerable.Empty<(ExplorerItem Item, string Path, bool IsChild)>())
                        .Concat(relatedModelItems.Select(item => (item, relatedResourcePath, true)));
                    foreach ((ExplorerItem mappedModelItem, string mappedModelPath, bool isChild) in modelsToValidate)
                    {
                        LithTechModelDocument? mappedDocument = TryDecodeSlotInheritanceModel(
                            mappedModelItem,
                            mappedModelPath,
                            decodedModels);
                        int[] modelSlotSet = mappedDocument?.Meshes
                            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                            .Distinct()
                            .ToArray() ?? [];
                        // A typed CFT row explicitly relates the parent and
                        // child model references to these texture fields. Keep
                        // only CFT slots used by both logical models; physical
                        // revisions may omit unused slots and animation geometry
                        // or mesh ordering may differ.
                        if (mappedDocument is null)
                        {
                            allRelatedCopiesValidated = false;
                            break;
                        }

                        (isChild ? childUsedSlots : parentUsedSlots).UnionWith(modelSlotSet);
                    }

                    if (hasPhysicalParentModels)
                    {
                        validatedSlotSet.IntersectWith(parentUsedSlots);
                    }
                    validatedSlotSet.IntersectWith(childUsedSlots);
                    if (!allRelatedCopiesValidated)
                    {
                        continue;
                    }

                    bool hasConsistentRelatedSlotLayout = TryGetConsistentLtcModelSlots(
                        relatedModelItems,
                        decodedModels,
                        out int[] consistentRelatedSlots);
                    if (hasPhysicalParentModels && hasConsistentRelatedSlotLayout)
                    {
                        // A numbered CFT field names an engine skin slot even
                        // when that slot is used only by the row's motion model.
                        // Require an exact texture path, identical physical DTX
                        // copies, and the same child slot layout in every copy.
                        foreach ((int slotIndex, string? texturePath) in rowSlotTextures)
                        {
                            if (!exactCftTextureSlots.Contains(slotIndex) ||
                                parentUsedSlots.Contains(slotIndex) ||
                                !childUsedSlots.Contains(slotIndex) ||
                                !consistentRelatedSlots.Contains(slotIndex) ||
                                string.IsNullOrWhiteSpace(texturePath) ||
                                !HasConsistentPhysicalResourceCopies(resourceItems, texturePath))
                            {
                                continue;
                            }

                            validatedSlotSet.Add(slotIndex);
                        }
                    }

                    foreach (VerifiedCftMotionTextureAlias alias in VerifiedCftMotionTextureAliases)
                    {
                        if (!IsVerifiedCftMotionTextureAlias(
                                modelResourcePath,
                                relatedResourcePath,
                                alias.SlotIndex,
                                rowSlotTextures.GetValueOrDefault(alias.SlotIndex)) ||
                            parentUsedSlots.Contains(alias.SlotIndex) ||
                            !childUsedSlots.Contains(alias.SlotIndex) ||
                            !hasConsistentRelatedSlotLayout ||
                            !consistentRelatedSlots.Contains(alias.SlotIndex))
                        {
                            continue;
                        }

                        // The verified Princess Iron hair slot is present on
                        // the related motion model only, so it cannot pass the
                        // shared-parent-slot rule above.
                        validatedSlotSet.Add(alias.SlotIndex);
                    }

                    if (validatedSlotSet.Count == 0)
                    {
                        continue;
                    }

                    string relatedModelPath = NormalizeExtensionlessModelReference(relatedResourcePath);
                    string[] relatedContextKeys = hasImplicitCharacterBodyTexture
                        ? GetCftSideContextKeys(configItem.Path, mapping, implicitCharacterBodySide)
                        : contextKeys;
                    foreach (int slotIndex in validatedSlotSet)
                    {
                        string? texturePath = rowSlotTextures[slotIndex];
                        if (!string.IsNullOrWhiteSpace(texturePath))
                        {
                            modelsWithSlotSpecificBindings.Add(relatedModelPath);
                            AddModelTextureSlotMapping(byModelPath, relatedModelPath, slotIndex, texturePath);
                            foreach (string contextKey in relatedContextKeys)
                            {
                                AddModelTextureContextSlotMapping(
                                    byModelContext,
                                    relatedModelPath,
                                    contextKey,
                                    slotIndex,
                                    texturePath);
                            }
                        }
                    }
                }
            }
        }

        var ltcMappingCache = new Dictionary<string, IReadOnlyList<CrossFireLtcModelTextureMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem ltcItem in ltcItems)
        {
            string cacheKey = ltcItem.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
                ? "md5:" + archiveHash
                : NormalizeKey(ltcItem.OutputRelativePath);
            if (ltcMappingCache.ContainsKey(cacheKey))
            {
                continue;
            }

            byte[]? ltcData = TryReadItemBytes(ltcItem, MaxLtcBytes);
            IReadOnlyList<CrossFireLtcModelTextureMapping> mappings = ltcData is not null &&
                       CrossFireLtcDecoder.TryDecodeNativeText(
                           ltcData,
                           out CrossFireLtcTextDocument? ltcDocument,
                           out _) &&
                       ltcDocument is not null
                ? CrossFireLtcModelTextureMappingDecoder.Extract(ltcDocument.Text)
                : [];
            ltcMappingCache[cacheKey] = mappings;
        }

        var ltcWeaponIndexIds = new HashSet<uint>();
        foreach (CrossFireLtcModelTextureMapping mapping in ltcMappingCache.Values.SelectMany(mappings => mappings))
        {
            foreach (string identityAlias in mapping.IdentityAliases)
            {
                const string weaponIndexPrefix = "WeaponIndex:";
                if (identityAlias.StartsWith(weaponIndexPrefix, StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(
                        identityAlias[weaponIndexPrefix.Length..],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint weaponIndex))
                {
                    ltcWeaponIndexIds.Add(weaponIndex);
                }
            }
        }

        (CrossFireWeaponPreviewItemAliasIndex itemAliases, IReadOnlyList<WeaponPreviewSkinAsset> weaponPreviewSkinAssets) =
            BuildWeaponPreviewItemAliases(crossFireScriptItems, ltcWeaponIndexIds, ltcMappingCache.Values);
        Dictionary<string, Dictionary<string, LtcPhysicalModelTextureBinding>> ltcPhysicalBindingAliases =
            BuildLtcPhysicalBindingAliases(
                ltcItems,
                ltcMappingCache,
                modelItemsByPath,
                resourcePaths,
                texturePathsByBaseName,
                decodedModels);
        foreach (ExplorerItem ltcItem in ltcItems)
        {
            string cacheKey = ltcItem.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
                ? "md5:" + archiveHash
                : NormalizeKey(ltcItem.OutputRelativePath);
            if (!ltcMappingCache.TryGetValue(cacheKey, out IReadOnlyList<CrossFireLtcModelTextureMapping>? mappings))
            {
                byte[]? ltcData = TryReadItemBytes(ltcItem, MaxLtcBytes);
                mappings = ltcData is not null &&
                           CrossFireLtcDecoder.TryDecodeNativeText(
                               ltcData,
                               out CrossFireLtcTextDocument? ltcDocument,
                               out _) &&
                           ltcDocument is not null
                    ? CrossFireLtcModelTextureMappingDecoder.Extract(ltcDocument.Text)
                    : [];
                ltcMappingCache[cacheKey] = mappings;
            }

            foreach (CrossFireLtcModelTextureMapping mapping in mappings)
            {
                string modelPath = NormalizeExtensionlessModelReference(mapping.ModelReference);
                string roleModelPath = NormalizeModelResourcePath(mapping.ModelReference);
                if (modelPath.Length == 0)
                {
                    continue;
                }

                string[] contextKeys = GetLtcContextKeys(ltcItem, mapping)
                    .Concat(GetWeaponItemContextAliases(mapping, itemAliases))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (CrossFireLtcRenderStyleFieldReference renderStyleReference in mapping.RenderStyleFieldReferences)
                {
                    if (!TryGetLtcRenderStyleIndex(renderStyleReference.FieldName, out int renderStyleIndex))
                    {
                        continue;
                    }

                    foreach (string contextKey in contextKeys)
                    {
                        AddModelTextureContextSlotMapping(
                            renderStyleByModelContext,
                            modelPath,
                            contextKey,
                            renderStyleIndex,
                            NormalizeKey(renderStyleReference.Path));
                    }
                }

                if (!modelItemsByPlayerViewRolePath.TryGetValue(roleModelPath, out ExplorerItem[]? modelItems) ||
                    modelItems.Length == 0)
                {
                    continue;
                }

                if (mapping.TextureFieldReferences.Any(textureReference =>
                        TryGetLtcPlayerViewTextureOrdinal(
                            mapping.ModelField,
                            textureReference.FieldName,
                            out _)))
                {
                    modelsWithSlotSpecificBindings.Add(modelPath);
                }

                var modelDocuments = new List<LithTechModelDocument>(modelItems.Length);
                foreach (ExplorerItem modelItem in modelItems)
                {
                    LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                        modelItem,
                        GetModelResourcePath(modelItem),
                        decodedModels);
                    if (document is null)
                    {
                        modelDocuments.Clear();
                        break;
                    }

                    modelDocuments.Add(document);
                }

                if (modelDocuments.Count == 0)
                {
                    continue;
                }

                if (mapping.ModelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase))
                {
                    // A typed LTC player-view role is direct evidence that this
                    // model and its side/gender variants can carry CFT hand/arm skins.
                    typedLtcPlayerViewModels.Add(modelPath);
                    foreach (string variantSuffix in new[]
                             {
                                 "_BL", "_GR", "_SP",
                                 "_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP"
                             })
                    {
                        string variantPath = modelPath + variantSuffix;
                        if (modelItemsByPlayerViewRolePath.ContainsKey(NormalizeModelResourcePath(variantPath)))
                        {
                            typedLtcPlayerViewModels.Add(variantPath);
                        }
                    }
                }

                IReadOnlyDictionary<string, LithTechModelDocument>? playerViewArmDocuments =
                    mapping.ModelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase)
                        ? TryGetLtcPlayerViewArmDocuments(
                            roleModelPath,
                            modelItemsByPlayerViewRolePath,
                            decodedModels)
                        : null;
                int[] equivalentPlayerViewArmSlots =
                    mapping.ModelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase) &&
                    playerViewArmDocuments is { Count: > 0 } &&
                    modelItems.Length == modelDocuments.Count
                        ? ResolveEquivalentPlayerViewArmSlots(modelItems, modelDocuments, playerViewArmDocuments)
                        : [];

                foreach (CrossFireLtcTextureFieldReference textureReference in mapping.TextureFieldReferences)
                {
                    if (!TryGetLtcPlayerViewTextureOrdinal(
                            mapping.ModelField,
                            textureReference.FieldName,
                            out int textureOrdinal))
                    {
                        continue;
                    }

                    modelsWithSlotSpecificBindings.Add(modelPath);
                    string? existingTexturePath = ResolveExistingTextureReference(
                        textureReference.Path,
                        resourcePaths,
                        texturePathsByBaseName);

                    int[] mappedSlots = ResolveLtcPlayerViewTextureSlots(
                        modelDocuments,
                        mapping.ModelField,
                        textureOrdinal,
                        equivalentPlayerViewArmSlots);
                    if (mappedSlots.Length == 0)
                    {
                        continue;
                    }

                    string texturePath = existingTexturePath ?? textureReference.Path;
                    foreach (int slotIndex in mappedSlots)
                    {
                        AddModelTextureSlotMapping(byModelPath, modelPath, slotIndex, texturePath);
                        foreach (string contextKey in contextKeys)
                        {
                            AddModelTextureContextSlotMapping(
                                ltcTextureByModelContext,
                                modelPath,
                                contextKey,
                                slotIndex,
                                texturePath);
                        }

                        if (existingTexturePath is null)
                        {
                            continue;
                        }

                        foreach (string contextKey in contextKeys)
                        {
                            AddModelTextureContextSlotMapping(
                                byModelContext,
                                modelPath,
                                contextKey,
                                slotIndex,
                                existingTexturePath);
                        }
                    }

                    if (existingTexturePath is not null &&
                        mappedSlots.Length == 1 &&
                        mapping.ModelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase))
                    {
                        AddLtcPreviewVariantSameNameTextureBindings(
                            modelPath,
                            mapping.ModelField,
                            existingTexturePath,
                            textureOrdinal,
                            mappedSlots,
                            contextKeys,
                            typedLtcPlayerViewModels,
                            modelItemsByPlayerViewRolePath,
                            decodedModels,
                            textureItemsByLogicalPath,
                            verifiedTexturePathCache,
                            byModelPath,
                            byModelContext,
                            ltcPreviewVariantByModelPath,
                            ltcPreviewVariantByModelContext,
                            modelsWithSlotSpecificBindings);
                    }
                }
            }
        }

        (Dictionary<string, Dictionary<int, string?>> equivalentArmByModelContext,
            Dictionary<string, Dictionary<int, string?>> equivalentArmByModelPath) = BuildEquivalentArmContextBindings(
            directPlayerViewModels,
            typedLtcPlayerViewModels,
            modelItemsByPlayerViewRolePath,
            byModelContext,
            byModelPath);
        modelsWithSlotSpecificBindings.UnionWith(equivalentArmByModelPath.Keys);
        foreach (string contextBindingKey in equivalentArmByModelContext.Keys)
        {
            int contextSeparator = contextBindingKey.IndexOf('\0');
            if (contextSeparator > 0)
            {
                modelsWithSlotSpecificBindings.Add(contextBindingKey[..contextSeparator]);
            }
        }

        foreach (ExplorerItem ltcItem in ltcItems)
        {
            string cacheKey = ltcItem.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
                ? "md5:" + archiveHash
                : NormalizeKey(ltcItem.OutputRelativePath);
            if (!ltcMappingCache.TryGetValue(cacheKey, out IReadOnlyList<CrossFireLtcModelTextureMapping>? mappings))
            {
                continue;
            }

            foreach (CrossFireLtcModelTextureMapping mapping in mappings)
            {
                if (mapping.TextureFieldReferences.Count == 0 ||
                    mapping.TextureFieldReferences.Any(textureReference =>
                        TryGetLtcPlayerViewTextureOrdinal(mapping.ModelField, textureReference.FieldName, out _)))
                {
                    continue;
                }

                string logicalModelPath = GetLogicalModelResourcePath(NormalizeKey(mapping.ModelReference));
                string modelPath = NormalizeExtensionlessModelReference(mapping.ModelReference);
                CrossFireLtcTextureFieldReference[] textureReferences = mapping.TextureFieldReferences
                    .OrderBy(reference => GetLtcTextureFieldOrdinal(reference.FieldName))
                    .ThenBy(reference => reference.FieldName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (logicalModelPath.Length == 0 || textureReferences.Length == 0)
                {
                    continue;
                }

                string[] contextKeys = GetLtcContextKeys(ltcItem, mapping)
                    .Concat(GetWeaponItemContextAliases(mapping, itemAliases))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!modelItemsByPath.TryGetValue(logicalModelPath, out ExplorerItem[]? modelItems) ||
                    modelItems.Length == 0)
                {
                    if (!TryResolveLtcPhysicalBindingAliases(
                            mapping,
                            textureReferences,
                            ltcPhysicalBindingAliases,
                            out LtcPhysicalModelTextureBinding[] aliasedBindings))
                    {
                        continue;
                    }

                    foreach (LtcPhysicalModelTextureBinding binding in aliasedBindings)
                    {
                        AddModelTextureSlotMapping(
                            byModelPath,
                            binding.ModelPath,
                            binding.SlotIndex,
                            binding.TexturePath);
                        modelsWithSlotSpecificBindings.Add(binding.ModelPath);
                        foreach (string contextKey in contextKeys)
                        {
                            AddModelTextureContextSlotMapping(
                                byModelContext,
                                binding.ModelPath,
                                contextKey,
                                binding.SlotIndex,
                                binding.TexturePath);
                        }
                    }

                    continue;
                }

                if (modelPath.Length == 0 ||
                    !TryGetConsistentLtcModelSlots(modelItems, decodedModels, out int[] mappedSlots) ||
                    mappedSlots.Length != textureReferences.Length)
                {
                    continue;
                }

                for (int index = 0; index < mappedSlots.Length; index++)
                {
                    string? existingTexturePath = ResolveExistingTextureReference(
                        textureReferences[index].Path,
                        resourcePaths,
                        texturePathsByBaseName);
                    string texturePath = existingTexturePath ?? textureReferences[index].Path;
                    int slotIndex = mappedSlots[index];
                    foreach (string contextKey in contextKeys)
                    {
                        AddModelTextureContextSlotMapping(
                            ltcTextureByModelContext,
                            modelPath,
                            contextKey,
                            slotIndex,
                            texturePath);
                    }

                    if (existingTexturePath is null)
                    {
                        continue;
                    }

                    AddModelTextureSlotMapping(byModelPath, modelPath, slotIndex, existingTexturePath);
                    modelsWithSlotSpecificBindings.Add(modelPath);
                    foreach (string contextKey in contextKeys)
                    {
                        AddModelTextureContextSlotMapping(
                            byModelContext,
                            modelPath,
                            contextKey,
                            slotIndex,
                            existingTexturePath);
                    }
                }
            }
        }

        foreach (ModelMaterialComponentTextureMapping mapping in GetModelMaterialComponentTextureMappings(
                     modelResourceItems,
                     resourcePaths,
                     modelPathsByBaseName,
                     texturePathsByBaseName))
        {
            string modelPath = NormalizeExtensionlessModelReference(mapping.ModelReference);
            if (modelPath.Length == 0 ||
                byModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? existingSlots) &&
                existingSlots.ContainsKey(mapping.TextureSlotIndex))
            {
                continue;
            }

            AddModelTextureSlotMapping(
                byModelPath,
                modelPath,
                mapping.TextureSlotIndex,
                mapping.TextureReference);
            modelsWithSlotSpecificBindings.Add(modelPath);
        }

        foreach (DeferredCftMotionSkinBinding binding in deferredCftMotionSkinBindings)
        {
            string parentResourcePath = NormalizeKey(binding.ParentModelPath);
            ExplorerItem[]? parentModelItems;
            bool hasParentModel = Path.HasExtension(parentResourcePath)
                ? modelItemsByPath.TryGetValue(
                    GetLogicalModelResourcePath(parentResourcePath),
                    out parentModelItems)
                : modelItemsByExtensionlessPath.TryGetValue(
                    NormalizeExtensionlessModelReference(parentResourcePath),
                    out parentModelItems);
            string parentModelPath = NormalizeExtensionlessModelReference(parentResourcePath);
            if (!hasParentModel || parentModelItems is null || parentModelItems.Length == 0 ||
                !TryGetConsistentLtcModelSlots(parentModelItems, decodedModels, out int[] parentSlots) ||
                !parentSlots.Contains(0) ||
                !HasMatchingModelTextureSlotMapping(
                    byModelPath,
                    byModelContext,
                    parentModelPath,
                    0,
                    binding.TexturePath))
            {
                continue;
            }

            string relatedResourcePath = NormalizeKey(binding.MotionModelReference);
            ExplorerItem[]? relatedModelItems;
            bool hasRelatedModel = Path.HasExtension(relatedResourcePath)
                ? modelItemsByPath.TryGetValue(
                    GetLogicalModelResourcePath(relatedResourcePath),
                    out relatedModelItems)
                : modelItemsByExtensionlessPath.TryGetValue(
                    NormalizeExtensionlessModelReference(relatedResourcePath),
                    out relatedModelItems);
            if (!hasRelatedModel || relatedModelItems is null || relatedModelItems.Length == 0 ||
                !TryGetConsistentLtcModelSlots(relatedModelItems, decodedModels, out int[] relatedSlots) ||
                !relatedSlots.Contains(0))
            {
                continue;
            }

            string relatedModelPath = NormalizeExtensionlessModelReference(relatedResourcePath);
            AddModelTextureSlotMapping(byModelPath, relatedModelPath, 0, binding.TexturePath);
            foreach (string contextKey in binding.ContextKeys)
            {
                AddModelTextureContextSlotMapping(
                    byModelContext,
                    relatedModelPath,
                    contextKey,
                    0,
                    binding.TexturePath);
            }

            modelsWithSlotSpecificBindings.Add(relatedModelPath);
        }

        AddWeaponPreviewVariantTextureContextBindings(
            weaponPreviewSkinAssets,
            resourceItems,
            modelItemsByPath,
            decodedModels,
            byModelPath,
            byModelContext,
            weaponPreviewVariantByModelContext,
            modelsWithSlotSpecificBindings,
            resourcePaths,
            texturePathsByBaseName);

        Dictionary<string, Dictionary<int, string?>> weaponPreviewVariantByModelPath =
            BuildWeaponPreviewVariantTexturePathBindings(
                weaponPreviewVariantByModelContext,
                byModelPath,
                byModelContext);

        AddVerifiedCftModelTextureSlotAliases(
            resourceItems,
            modelItemsByPath,
            decodedModels,
            byModelPath,
            modelsWithSlotSpecificBindings);

        AddVerifiedSceneModelTextureSlotAliases(
            resourceItems,
            modelItemsByPath,
            decodedModels,
            byModelPath,
            modelsWithSlotSpecificBindings);

        AddPreviewNormalizedBasenameTextureBindings(
            modelItemsByPlayerViewRolePath,
            textureItemsByLogicalPath,
            verifiedTexturePathCache,
            decodedModels,
            byModelPath,
            byModelContext,
            previewNormalizedBasenameByModelPath,
            modelsWithSlotSpecificBindings);

        AddVerifiedVariantSlotUvTextureBindings(
            modelItemsByPlayerViewRolePath,
            textureItemsByLogicalPath,
            verifiedTexturePathCache,
            decodedModels,
            byModelPath,
            byModelContext,
            variantSlotUvByModelPath,
            modelsWithSlotSpecificBindings);

        AddVerifiedPlayerViewMeshTextureSlotAliases(
            modelItemsByPlayerViewRolePath,
            textureItemsByLogicalPath,
            verifiedTexturePathCache,
            decodedModels,
            byModelPath,
            byModelContext,
            variantSlotUvByModelPath,
            modelsWithSlotSpecificBindings);

        AddVerifiedMotionParentSlotTextureBindings(
            modelItemsByPlayerViewRolePath,
            textureItemsByLogicalPath,
            verifiedTexturePathCache,
            decodedModels,
            byModelPath,
            byModelContext,
            [inferredByModelPath, ltcPreviewVariantByModelPath, previewNormalizedBasenameByModelPath,
                variantSlotUvByModelPath, weaponPreviewVariantByModelPath, equivalentArmByModelPath],
            [inferredByModelContext, ltcPreviewVariantByModelContext, weaponPreviewVariantByModelContext,
                equivalentArmByModelContext],
            motionParentSlotByModelPath,
            motionParentSlotByModelContext,
            modelsWithSlotSpecificBindings);

        AddVerifiedCftIdentityModelAliasTextureBindings(
            deferredCftIdentityModelAliasBindings,
            modelItemsByPlayerViewRolePath,
            textureItemsByLogicalPath,
            verifiedTexturePathCache,
            decodedModels,
            byModelPath,
            byModelContext,
            [inferredByModelPath, ltcPreviewVariantByModelPath, previewNormalizedBasenameByModelPath,
                variantSlotUvByModelPath, motionParentSlotByModelPath, weaponPreviewVariantByModelPath,
                equivalentArmByModelPath],
            [inferredByModelContext, ltcPreviewVariantByModelContext, motionParentSlotByModelContext,
                renderStyleByModelContext, ltcTextureByModelContext, weaponPreviewVariantByModelContext,
                equivalentArmByModelContext],
            cftIdentityModelAliasByModelContext,
            modelsWithSlotSpecificBindings);

        Dictionary<string, CftPlayerViewRoleSlotLayouts> cftPlayerViewRoleSlotsByModelPath =
            BuildCftPlayerViewRoleSlotLayouts(
                typedLtcPlayerViewModels,
                modelItemsByPlayerViewRolePath,
                decodedModels);

        return new ModelTextureSlotDictionary(
            byModelPath,
            byModelContext,
            inferredByModelPath,
            inferredByModelContext,
            ltcPreviewVariantByModelPath,
            ltcPreviewVariantByModelContext,
            previewNormalizedBasenameByModelPath,
            variantSlotUvByModelPath,
            motionParentSlotByModelPath,
            motionParentSlotByModelContext,
            cftIdentityModelAliasByModelContext,
            renderStyleByModelContext,
            ltcTextureByModelContext,
            weaponPreviewVariantByModelContext,
            weaponPreviewVariantByModelPath,
            modelsWithSlotSpecificBindings,
            equivalentArmByModelContext,
            equivalentArmByModelPath,
            cftPlayerViewRoleTextureCandidates,
            cftPlayerViewRoleSlotsByModelPath);
    }

    private static void AddLtcPreviewVariantSameNameTextureBindings(
        string modelPath,
        string modelField,
        string textureReferencePath,
        int textureOrdinal,
        IReadOnlyList<int> sourceMappedSlots,
        IReadOnlyList<string> contextKeys,
        IReadOnlySet<string> typedLtcPlayerViewModels,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        Dictionary<string, Dictionary<int, string?>> ltcPreviewVariantByModelPath,
        Dictionary<string, Dictionary<int, string?>> ltcPreviewVariantByModelContext,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        if (sourceMappedSlots.Count != 1 ||
            !modelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string modelStem = Path.GetFileName(modelPath);
        string[] variantSuffixes = ["_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP", "_BL", "_GR", "_SP"];
        if (variantSuffixes.Any(suffix => modelStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string textureExtension = Path.GetExtension(textureReferencePath);
        string textureDirectory = Path.GetDirectoryName(NormalizeKey(textureReferencePath)) ?? string.Empty;
        if (!string.Equals(textureExtension, ".dtx", StringComparison.OrdinalIgnoreCase) ||
            textureDirectory.Length == 0)
        {
            return;
        }

        string sameNameTexturePath = NormalizeKey(Path.Combine(textureDirectory, modelStem + textureExtension));
        string sourceModelPath = NormalizeModelResourcePath(modelPath);
        int sourceSlotIndex = sourceMappedSlots[0];
        // Do not let a same-name fallback replace a different texture bound to the source slot.
        if (byModelPath.TryGetValue(sourceModelPath, out Dictionary<int, string?>? sourceDirectSlots) &&
            sourceDirectSlots.TryGetValue(sourceSlotIndex, out string? sourceDirectTexture) &&
            !string.IsNullOrWhiteSpace(sourceDirectTexture) &&
            !string.Equals(NormalizeKey(sourceDirectTexture), sameNameTexturePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string contextKey in contextKeys)
        {
            string normalizedContext = NormalizeLooseKey(contextKey);
            string sourceContextKey = $"{sourceModelPath}\0{normalizedContext}";
            if (normalizedContext.Length > 0 &&
                byModelContext.TryGetValue(sourceContextKey, out Dictionary<int, string?>? sourceContextSlots) &&
                sourceContextSlots.TryGetValue(sourceSlotIndex, out string? sourceContextTexture) &&
                !string.IsNullOrWhiteSpace(sourceContextTexture) &&
                !string.Equals(NormalizeKey(sourceContextTexture), sameNameTexturePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (!HasConsistentDecodableDtxCopies(
                sameNameTexturePath,
                textureItemsByLogicalPath,
                verifiedTexturePathCache))
        {
            return;
        }

        foreach (string suffix in variantSuffixes)
        {
            string targetPath = NormalizeModelResourcePath(modelPath + suffix);
            if (!typedLtcPlayerViewModels.Contains(targetPath) ||
                !modelItemsByPlayerViewRolePath.TryGetValue(targetPath, out ExplorerItem[]? targetItems) ||
                targetItems.Length == 0)
            {
                continue;
            }

            var targetDocuments = new List<LithTechModelDocument>(targetItems.Length);
            foreach (ExplorerItem targetItem in targetItems)
            {
                LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                    targetItem,
                    GetModelResourcePath(targetItem),
                    decodedModels);
                if (document is null)
                {
                    targetDocuments.Clear();
                    break;
                }

                targetDocuments.Add(document);
            }

            if (targetDocuments.Count != targetItems.Length ||
                CftPlayerViewArmEquivalence.GetConsistentTextureSlotLayoutSignatures(targetDocuments) is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, LithTechModelDocument>? armDocuments = TryGetLtcPlayerViewArmDocuments(
                targetPath,
                modelItemsByPlayerViewRolePath,
                decodedModels);
            int[] equivalentArmSlots = armDocuments is { Count: > 0 }
                ? ResolveEquivalentPlayerViewArmSlots(targetItems, targetDocuments, armDocuments)
                : [];
            int[] targetSlots = ResolveLtcPlayerViewTextureSlots(
                targetDocuments,
                modelField,
                textureOrdinal,
                equivalentArmSlots);
            if (targetSlots.Length != 1 || targetSlots[0] != sourceMappedSlots[0])
            {
                continue;
            }

            int slotIndex = targetSlots[0];
            if (byModelPath.TryGetValue(targetPath, out Dictionary<int, string?>? directPathSlots) &&
                directPathSlots.ContainsKey(slotIndex))
            {
                continue;
            }

            AddModelTextureSlotMapping(ltcPreviewVariantByModelPath, targetPath, slotIndex, sameNameTexturePath);
            foreach (string contextKey in contextKeys)
            {
                string normalizedContext = NormalizeLooseKey(contextKey);
                string directContextKey = $"{targetPath}\0{normalizedContext}";
                if (normalizedContext.Length == 0 ||
                    byModelContext.TryGetValue(directContextKey, out Dictionary<int, string?>? directContextSlots) &&
                    directContextSlots.ContainsKey(slotIndex))
                {
                    continue;
                }

                AddModelTextureContextSlotMapping(
                    ltcPreviewVariantByModelContext,
                    targetPath,
                    normalizedContext,
                    slotIndex,
                    sameNameTexturePath);
            }

            modelsWithSlotSpecificBindings.Add(targetPath);
        }
    }

    private static bool HasConsistentDecodableDtxCopies(
        string texturePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache)
    {
        string normalizedPath = NormalizeKey(texturePath);
        if (verifiedTexturePathCache.TryGetValue(normalizedPath, out bool cached))
        {
            return cached;
        }

        if (!textureItemsByLogicalPath.TryGetValue(normalizedPath, out ExplorerItem[]? copies) || copies.Length == 0)
        {
            verifiedTexturePathCache[normalizedPath] = false;
            return false;
        }

        string? expectedHash = null;
        foreach (ExplorerItem copy in copies)
        {
            byte[]? data = TryReadItemBytes(copy, MaxInferredTextureBytes);
            if (data is null || data.Length == 0 || !LithTechModelTextureLoader.TryDecodeTextureItem(copy))
            {
                verifiedTexturePathCache[normalizedPath] = false;
                return false;
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(data));
            if (expectedHash is not null && !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                verifiedTexturePathCache[normalizedPath] = false;
                return false;
            }

            expectedHash = actualHash;
        }

        verifiedTexturePathCache[normalizedPath] = expectedHash is not null;
        return expectedHash is not null;
    }

    private static void AddPreviewNormalizedBasenameTextureBindings(
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        Dictionary<string, Dictionary<int, string?>> previewNormalizedBasenameByModelPath,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        const string modelRoot = "Models/PLAYERVIEW";
        const string textureRoot = "ModelTextures/PLAYERVIEW";
        string[] variantSuffixes = ["_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP", "_BL", "_GR", "_SP"];
        var texturePathsByLooseStem = textureItemsByLogicalPath.Keys
            .Where(path => path.StartsWith(textureRoot + "/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => NormalizeLooseKey(Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var contextSlotsByModelPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, Dictionary<int, string?> slots) in byModelContext)
        {
            int separator = key.IndexOf('\0');
            if (separator <= 0)
            {
                continue;
            }

            string modelPath = key[..separator];
            if (!contextSlotsByModelPath.TryGetValue(modelPath, out HashSet<int>? contextSlots))
            {
                contextSlotsByModelPath[modelPath] = contextSlots = [];
            }

            contextSlots.UnionWith(slots.Keys);
        }

        foreach (string baseModelPath in modelItemsByPlayerViewRolePath.Keys)
        {
            if (!baseModelPath.StartsWith(modelRoot + "/", StringComparison.OrdinalIgnoreCase) ||
                variantSuffixes.Any(suffix => baseModelPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string looseStem = NormalizeLooseKey(Path.GetFileName(baseModelPath));
            if (!texturePathsByLooseStem.TryGetValue(looseStem, out string[]? texturePaths) || texturePaths.Length != 1)
            {
                continue;
            }

            string texturePath = texturePaths[0];
            if (!string.Equals(
                    GetRelativePlayerViewDirectory(baseModelPath, modelRoot),
                    GetRelativePlayerViewDirectory(texturePath, textureRoot),
                    StringComparison.OrdinalIgnoreCase) ||
                !HasConsistentDecodableDtxCopies(texturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
            {
                continue;
            }

            if (!modelItemsByPlayerViewRolePath.TryGetValue(baseModelPath, out ExplorerItem[]? baseItems) ||
                !TryDecodeConsistentPlayerViewModelDocuments(baseItems, decodedModels, out List<LithTechModelDocument> baseDocuments) ||
                !TryGetUniquePlayerViewNonArmTextureSlot(baseDocuments, out int slotIndex) ||
                contextSlotsByModelPath.TryGetValue(baseModelPath, out HashSet<int>? baseContextSlots) && baseContextSlots.Contains(slotIndex))
            {
                continue;
            }

            bool baseHasDirectSlot = byModelPath.TryGetValue(baseModelPath, out Dictionary<int, string?>? baseDirectSlots) &&
                                     baseDirectSlots.ContainsKey(slotIndex);
            if (baseHasDirectSlot &&
                (string.IsNullOrWhiteSpace(baseDirectSlots![slotIndex]) ||
                 !string.Equals(baseDirectSlots[slotIndex], texturePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!baseHasDirectSlot)
            {
                AddModelTextureSlotMapping(previewNormalizedBasenameByModelPath, baseModelPath, slotIndex, texturePath);
                modelsWithSlotSpecificBindings.Add(baseModelPath);
            }

            foreach (string suffix in variantSuffixes)
            {
                string targetPath = NormalizeModelResourcePath(baseModelPath + suffix);
                if (!modelItemsByPlayerViewRolePath.TryGetValue(targetPath, out ExplorerItem[]? targetItems) ||
                    !TryDecodeConsistentPlayerViewModelDocuments(targetItems, decodedModels, out List<LithTechModelDocument> targetDocuments) ||
                    targetDocuments.Any(document => !document.Meshes.Any(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))) ||
                    byModelPath.TryGetValue(targetPath, out Dictionary<int, string?>? targetDirectSlots) && targetDirectSlots.ContainsKey(slotIndex) ||
                    contextSlotsByModelPath.TryGetValue(targetPath, out HashSet<int>? targetContextSlots) && targetContextSlots.Contains(slotIndex))
                {
                    continue;
                }

                AddModelTextureSlotMapping(previewNormalizedBasenameByModelPath, targetPath, slotIndex, texturePath);
                modelsWithSlotSpecificBindings.Add(targetPath);
            }
        }
    }

    private static bool TryDecodeConsistentPlayerViewModelDocuments(
        IReadOnlyList<ExplorerItem> modelItems,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        out List<LithTechModelDocument> documents)
    {
        documents = new List<LithTechModelDocument>(modelItems.Count);
        foreach (ExplorerItem modelItem in modelItems)
        {
            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                modelItem,
                GetModelResourcePath(modelItem),
                decodedModels);
            if (document is null)
            {
                documents.Clear();
                return false;
            }

            documents.Add(document);
        }

        return documents.Count == modelItems.Count &&
               CftPlayerViewArmEquivalence.GetConsistentTextureSlotLayoutSignatures(documents) is not null;
    }

    private static bool TryGetUniquePlayerViewNonArmTextureSlot(
        IReadOnlyList<LithTechModelDocument> documents,
        out int slotIndex)
    {
        slotIndex = -1;
        foreach (LithTechModelDocument document in documents)
        {
            int[] modelSlots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (modelSlots.Length == 0)
            {
                return false;
            }

            if (!TryGetPlayerViewHandArmTextureSlots(document, out HashSet<int> handArmSlots))
            {
                return false;
            }

            int[] nonArmSlots = modelSlots.Except(handArmSlots).Order().ToArray();
            if (nonArmSlots.Length != 1 || slotIndex >= 0 && slotIndex != nonArmSlots[0])
            {
                return false;
            }

            slotIndex = nonArmSlots[0];
        }

        return slotIndex >= 0;
    }

    private static bool IsPlayerViewHandOrArmTextureSlot(
        IReadOnlyList<LithTechModelDocument> documents,
        int slotIndex)
    {
        foreach (LithTechModelDocument document in documents)
        {
            if (!TryGetPlayerViewHandArmTextureSlots(document, out HashSet<int> handArmSlots) ||
                handArmSlots.Contains(slotIndex))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPlayerViewHandArmTextureSlots(
        LithTechModelDocument document,
        out HashSet<int> handArmSlots)
    {
        handArmSlots = new HashSet<int>();
        bool hasPairedHandArmSuffixes = document.Meshes.Any(mesh => mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase)) &&
                                        document.Meshes.Any(mesh => mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase));
        foreach (LithTechMesh mesh in document.Meshes)
        {
            string normalizedName = NormalizeLooseKey(mesh.Name);
            bool isHand = normalizedName.StartsWith("fviewhand", StringComparison.Ordinal) ||
                          normalizedName == "hand" ||
                          hasPairedHandArmSuffixes && mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase);
            bool isArm = normalizedName.StartsWith("fviewarm", StringComparison.Ordinal) ||
                         normalizedName.StartsWith("fviewam", StringComparison.Ordinal) ||
                         normalizedName == "arm" ||
                         hasPairedHandArmSuffixes && mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase);
            if (isHand && isArm)
            {
                handArmSlots.Clear();
                return false;
            }

            if (isHand || isArm)
            {
                handArmSlots.UnionWith(mesh.TextureSlotIndices ?? []);
            }
        }

        return true;
    }

    private static void AddVerifiedVariantSlotUvTextureBindings(
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        Dictionary<string, Dictionary<int, string?>> variantSlotUvByModelPath,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        // Motion LTBs and player-view variants may reuse a direct texture after the slot/UV checks below.
        string[] variantSuffixes = ["_M-motion", "_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP", "_BL", "_GR", "_SP"];
        var baseModelPathsByStem = modelItemsByPlayerViewRolePath.Keys
            .Where(path => !variantSuffixes.Any(suffix => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(path => NormalizeLooseKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var contextTexturesByModelPath = new Dictionary<string, Dictionary<int, HashSet<string?>>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, Dictionary<int, string?> slots) in byModelContext)
        {
            int separator = key.IndexOf('\0');
            if (separator <= 0)
            {
                continue;
            }

            string modelPath = key[..separator];
            if (!contextTexturesByModelPath.TryGetValue(modelPath, out Dictionary<int, HashSet<string?>>? contextSlots))
            {
                contextTexturesByModelPath[modelPath] = contextSlots = new Dictionary<int, HashSet<string?>>();
            }

            foreach ((int slotIndex, string? texturePath) in slots)
            {
                if (!contextSlots.TryGetValue(slotIndex, out HashSet<string?>? textures))
                {
                    contextSlots[slotIndex] = textures = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
                }

                textures.Add(texturePath);
            }
        }

        var slotUvTriangleSignaturesByModelPath = new Dictionary<string, IReadOnlyDictionary<int, string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (string targetModelPath in modelItemsByPlayerViewRolePath.Keys)
        {
            string targetStem = GetPlayerViewBaseModelStem(targetModelPath, variantSuffixes);
            if (targetStem.Length == 0 ||
                !baseModelPathsByStem.TryGetValue(targetStem, out string[]? donorModelPaths))
            {
                continue;
            }

            if (!modelItemsByPlayerViewRolePath.TryGetValue(targetModelPath, out ExplorerItem[]? targetItems) ||
                !TryDecodeConsistentPlayerViewModelDocuments(targetItems, decodedModels, out List<LithTechModelDocument> targetDocuments) ||
                targetDocuments.Count == 0)
            {
                continue;
            }

            IReadOnlyDictionary<int, string>? targetSignatures = GetConsistentTextureSlotUvTriangleSignatures(targetDocuments);
            slotUvTriangleSignaturesByModelPath[targetModelPath] = targetSignatures;
            if (targetSignatures is null)
            {
                continue;
            }

            var candidateTexturesBySlot = new Dictionary<int, HashSet<string>>();
            const string motionSuffix = "_M-motion";
            string? exactMotionParentPath = targetModelPath.EndsWith(motionSuffix, StringComparison.OrdinalIgnoreCase)
                ? NormalizeModelResourcePath(targetModelPath[..^motionSuffix.Length])
                : null;
            foreach (string donorModelPath in donorModelPaths)
            {
                if (string.Equals(donorModelPath, targetModelPath, StringComparison.OrdinalIgnoreCase) ||
                    (exactMotionParentPath is not null &&
                     !string.Equals(donorModelPath, exactMotionParentPath, StringComparison.OrdinalIgnoreCase)) ||
                    !modelItemsByPlayerViewRolePath.TryGetValue(donorModelPath, out ExplorerItem[]? donorItems) ||
                    !TryDecodeConsistentPlayerViewModelDocuments(donorItems, decodedModels, out List<LithTechModelDocument> donorDocuments) ||
                    !byModelPath.TryGetValue(donorModelPath, out Dictionary<int, string?>? donorDirectSlots))
                {
                    continue;
                }

                IReadOnlyDictionary<int, string>? donorSignatures = GetSlotUvTriangleSignatures(donorModelPath);
                if (donorSignatures is null)
                {
                    continue;
                }

                foreach ((int donorSlotIndex, string? directTextureReference) in donorDirectSlots)
                {
                    if (string.IsNullOrWhiteSpace(directTextureReference) ||
                        IsPlayerViewHandOrArmTextureSlot(donorDocuments, donorSlotIndex) ||
                        IsPlayerViewHandOrArmTextureSlot(targetDocuments, donorSlotIndex) ||
                        !targetSignatures.TryGetValue(donorSlotIndex, out string? targetSignature) ||
                        !donorSignatures.TryGetValue(donorSlotIndex, out string? donorSignature) ||
                        !string.Equals(donorSignature, targetSignature, StringComparison.Ordinal) ||
                        byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetDirectSlots) &&
                        targetDirectSlots.ContainsKey(donorSlotIndex) ||
                        contextTexturesByModelPath.TryGetValue(targetModelPath, out Dictionary<int, HashSet<string?>>? targetContextSlots) &&
                        targetContextSlots.ContainsKey(donorSlotIndex))
                    {
                        continue;
                    }

                    string texturePath = NormalizeKey(directTextureReference);
                    bool isPlayerViewOrWeaponTexture =
                        texturePath.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) ||
                        texturePath.StartsWith("ModelTextures/WEAPONS/", StringComparison.OrdinalIgnoreCase);
                    bool isExactMotionModelTexture = exactMotionParentPath is not null &&
                                                    texturePath.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase);
                    if ((!isPlayerViewOrWeaponTexture && !isExactMotionModelTexture) ||
                        !HasConsistentDecodableDtxCopies(texturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                    {
                        continue;
                    }

                    if (contextTexturesByModelPath.TryGetValue(donorModelPath, out Dictionary<int, HashSet<string?>>? donorContextSlots) &&
                        donorContextSlots.TryGetValue(donorSlotIndex, out HashSet<string?>? donorContextTextures) &&
                        donorContextTextures.Any(contextTexture =>
                            !string.Equals(contextTexture, texturePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (!candidateTexturesBySlot.TryGetValue(donorSlotIndex, out HashSet<string>? candidateTextures))
                    {
                        candidateTexturesBySlot[donorSlotIndex] = candidateTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    candidateTextures.Add(texturePath);
                }
            }

            foreach ((int slotIndex, HashSet<string> candidateTextures) in candidateTexturesBySlot)
            {
                if (candidateTextures.Count != 1)
                {
                    continue;
                }

                AddModelTextureSlotMapping(
                    variantSlotUvByModelPath,
                    targetModelPath,
                    slotIndex,
                    candidateTextures.Single());
                modelsWithSlotSpecificBindings.Add(targetModelPath);
            }
        }

        AddVerifiedSkinDtxBindingsForIdenticalModelCopies();

        // Identical skin variants may reuse a table-bound slot from a prefix
        // model or same-family sibling when the matching main, numbered, or
        // skin-suffixed alias DTX is unique and the slot UVs agree.
        void AddVerifiedSkinDtxBindingsForIdenticalModelCopies()
        {
            var playerViewTexturePathsByBaseName = textureItemsByLogicalPath.Keys
                .Where(path => path.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase))
                .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var tableMappedTexturesByModelSlot = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string modelPath, Dictionary<int, string?> slots) in byModelPath)
            {
                foreach ((int slot, string? texturePath) in slots)
                {
                    AddTableMappedTexturePath(modelPath, slot, texturePath);
                }
            }

            foreach ((string contextKey, Dictionary<int, string?> slots) in byModelContext)
            {
                int separator = contextKey.IndexOf('\0');
                if (separator <= 0)
                {
                    continue;
                }

                string modelPath = contextKey[..separator];
                foreach ((int slot, string? texturePath) in slots)
                {
                    AddTableMappedTexturePath(modelPath, slot, texturePath);
                }
            }

            var candidateDonorsBySlot = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var candidateTexturesBySlot = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var playerViewModelPathsByHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string modelPath, ExplorerItem[] modelItems) in modelItemsByPlayerViewRolePath)
            {
                string? modelHash = GetUniqueArchivedModelHash(modelItems);
                if (modelHash is null)
                {
                    continue;
                }

                if (!playerViewModelPathsByHash.TryGetValue(modelHash, out List<string>? modelPaths))
                {
                    playerViewModelPathsByHash[modelHash] = modelPaths = [];
                }

                modelPaths.Add(modelPath);
            }

            foreach (string targetModelPath in modelItemsByPlayerViewRolePath.Keys)
            {
                if (!targetModelPath.StartsWith("Models/PLAYERVIEW/PV-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetStem = Path.GetFileName(targetModelPath);
                string targetTexturePath = "ModelTextures/PLAYERVIEW/" + targetStem + ".dtx";
                if (!playerViewTexturePathsByBaseName.TryGetValue(targetStem, out string[]? targetTexturePaths) ||
                    targetTexturePaths.Length != 1 ||
                    !string.Equals(targetTexturePaths[0], targetTexturePath, StringComparison.OrdinalIgnoreCase) ||
                    !HasConsistentDecodableDtxCopies(targetTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache) ||
                    !modelItemsByPlayerViewRolePath.TryGetValue(targetModelPath, out ExplorerItem[]? targetItems) ||
                    !TryDecodeConsistentPlayerViewModelDocuments(targetItems, decodedModels, out List<LithTechModelDocument> targetDocuments) ||
                    targetDocuments.Count == 0)
                {
                    continue;
                }

                string? targetModelHash = GetUniqueArchivedModelHash(targetItems);
                if (targetModelHash is null)
                {
                    continue;
                }

                if (!playerViewModelPathsByHash.TryGetValue(targetModelHash, out List<string>? identicalModelPaths))
                {
                    continue;
                }

                IReadOnlyDictionary<int, string>? targetSignatures = GetSlotUvTriangleSignatures(targetModelPath);
                if (targetSignatures is null)
                {
                    continue;
                }

                string targetDirectory = Path.GetDirectoryName(targetModelPath)?.Replace('\\', '/') ?? string.Empty;
                int separatorIndex = targetStem.LastIndexOf('_');
                while (separatorIndex > 0)
                {
                    string donorModelPath = targetDirectory + "/" + targetStem[..separatorIndex];
                    separatorIndex = targetStem.LastIndexOf('_', separatorIndex - 1);
                    if (!tableMappedTexturesByModelSlot.TryGetValue(donorModelPath, out Dictionary<int, HashSet<string>>? donorBindings) ||
                        !modelItemsByPlayerViewRolePath.TryGetValue(donorModelPath, out ExplorerItem[]? donorItems) ||
                        !string.Equals(targetModelHash, GetUniqueArchivedModelHash(donorItems), StringComparison.OrdinalIgnoreCase) ||
                        !TryDecodeConsistentPlayerViewModelDocuments(donorItems, decodedModels, out List<LithTechModelDocument> donorDocuments) ||
                        donorDocuments.Count == 0)
                    {
                        continue;
                    }

                    string donorStem = Path.GetFileName(donorModelPath);
                    string donorTexturePath = "ModelTextures/PLAYERVIEW/" + donorStem + ".dtx";
                    IReadOnlyDictionary<int, string>? donorSignatures = GetSlotUvTriangleSignatures(donorModelPath);
                    if (donorSignatures is null)
                    {
                        continue;
                    }

                    if (HasConsistentDecodableDtxCopies(donorTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                    {
                        foreach ((int slot, HashSet<string> textures) in donorBindings)
                        {
                            if (textures.Contains(donorTexturePath) &&
                                IsVerifiedUnmappedSlot(targetModelPath, slot, targetDocuments, donorDocuments, targetSignatures, donorSignatures))
                            {
                                AddCandidateBinding(targetModelPath, slot, donorModelPath, targetTexturePath);
                            }
                        }
                    }

                    string skinPrefix = donorStem + "_";
                    if (!targetStem.StartsWith(skinPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string skinSuffix = targetStem[skinPrefix.Length..];
                    foreach ((int slot, HashSet<string> sourceTexturePaths) in donorBindings)
                    {
                        if (!IsVerifiedUnmappedSlot(targetModelPath, slot, targetDocuments, donorDocuments, targetSignatures, donorSignatures))
                        {
                            continue;
                        }

                        foreach (string sourceTexturePath in sourceTexturePaths)
                        {
                            if (string.Equals(sourceTexturePath, donorTexturePath, StringComparison.OrdinalIgnoreCase) ||
                                !TryGetNumberedPartSuffix(sourceTexturePath, donorStem, out string partSuffix) ||
                                !HasConsistentDecodableDtxCopies(sourceTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                            {
                                continue;
                            }

                            string sourceTextureStem = Path.GetFileNameWithoutExtension(sourceTexturePath);
                            var candidateTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                targetStem + partSuffix,
                                targetStem + "_" + partSuffix.TrimStart('_'),
                                sourceTextureStem + "_" + skinSuffix
                            };
                            var matchingTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string candidateTextureName in candidateTextureNames)
                            {
                                string candidateTexturePath = "ModelTextures/PLAYERVIEW/" + candidateTextureName + ".dtx";
                                if (!playerViewTexturePathsByBaseName.TryGetValue(candidateTextureName, out string[]? candidatePaths) ||
                                    candidatePaths.Length != 1 ||
                                    !string.Equals(candidatePaths[0], candidateTexturePath, StringComparison.OrdinalIgnoreCase) ||
                                    !HasConsistentDecodableDtxCopies(candidateTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                                {
                                    continue;
                                }

                                matchingTexturePaths.Add(candidateTexturePath);
                            }

                            if (matchingTexturePaths.Count == 1)
                            {
                                AddCandidateBinding(targetModelPath, slot, donorModelPath, matchingTexturePaths.Single());
                            }
                        }
                    }
                }

                AddSameHashSkinSiblingBindings(
                    targetModelPath,
                    targetStem,
                    targetTexturePath,
                    targetModelHash,
                    identicalModelPaths,
                    targetDocuments,
                    targetSignatures);
            }

            void AddSameHashSkinSiblingBindings(
                string targetModelPath,
                string targetStem,
                string targetTexturePath,
                string targetModelHash,
                IReadOnlyList<string> identicalModelPaths,
                IReadOnlyList<LithTechModelDocument> targetDocuments,
                IReadOnlyDictionary<int, string> targetSignatures)
            {
                string targetDirectory = Path.GetDirectoryName(targetModelPath)?.Replace('\\', '/') ?? string.Empty;
                foreach (string donorModelPath in identicalModelPaths)
                {
                    if (string.Equals(donorModelPath, targetModelPath, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            Path.GetDirectoryName(donorModelPath)?.Replace('\\', '/') ?? string.Empty,
                            targetDirectory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string donorStem = Path.GetFileName(donorModelPath);
                    string? familyRootStem = FindExistingSkinFamilyRoot(targetDirectory, targetStem, donorStem);
                    if (familyRootStem is null ||
                        !tableMappedTexturesByModelSlot.TryGetValue(donorModelPath, out Dictionary<int, HashSet<string>>? donorBindings) ||
                        !modelItemsByPlayerViewRolePath.TryGetValue(donorModelPath, out ExplorerItem[]? donorItems) ||
                        !string.Equals(targetModelHash, GetUniqueArchivedModelHash(donorItems), StringComparison.OrdinalIgnoreCase) ||
                        !TryDecodeConsistentPlayerViewModelDocuments(donorItems, decodedModels, out List<LithTechModelDocument> donorDocuments) ||
                        donorDocuments.Count == 0)
                    {
                        continue;
                    }

                    string donorTexturePath = "ModelTextures/PLAYERVIEW/" + donorStem + ".dtx";
                    string familyRootPath = targetDirectory + "/" + familyRootStem;
                    IReadOnlyDictionary<int, string>? donorSignatures = GetSlotUvTriangleSignatures(donorModelPath);
                    if (donorSignatures is null)
                    {
                        continue;
                    }

                    bool hasDecodableMainTexture = HasConsistentDecodableDtxCopies(
                        donorTexturePath,
                        textureItemsByLogicalPath,
                        verifiedTexturePathCache);
                    if (hasDecodableMainTexture)
                    {
                        foreach ((int slot, HashSet<string> textures) in donorBindings)
                        {
                            if (textures.Contains(donorTexturePath) &&
                                IsVerifiedUnmappedSlot(targetModelPath, slot, targetDocuments, donorDocuments, targetSignatures, donorSignatures))
                            {
                                AddCandidateBinding(targetModelPath, slot, familyRootPath, targetTexturePath);
                            }
                        }
                    }

                    string targetSkinSuffix = targetStem.Length > familyRootStem.Length
                        ? targetStem[(familyRootStem.Length + 1)..]
                        : string.Empty;
                    foreach ((int slot, HashSet<string> sourceTexturePaths) in donorBindings)
                    {
                        if (!IsVerifiedUnmappedSlot(targetModelPath, slot, targetDocuments, donorDocuments, targetSignatures, donorSignatures))
                        {
                            continue;
                        }

                        foreach (string sourceTexturePath in sourceTexturePaths)
                        {
                            if (string.Equals(sourceTexturePath, donorTexturePath, StringComparison.OrdinalIgnoreCase) ||
                                !HasConsistentDecodableDtxCopies(sourceTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                            {
                                continue;
                            }

                            var candidateTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (TryGetSiblingNumberedPartSuffix(sourceTexturePath, familyRootStem, donorStem, out string partSuffix))
                            {
                                string partNumber = partSuffix.TrimStart('_');
                                candidateTextureNames.Add(targetStem + partSuffix);
                                candidateTextureNames.Add(targetStem + "_" + partNumber);
                                candidateTextureNames.Add(familyRootStem + partNumber + "_" + targetSkinSuffix);
                                candidateTextureNames.Add(familyRootStem + "_" + partNumber + "_" + targetSkinSuffix);
                            }

                            if (!string.IsNullOrEmpty(targetSkinSuffix) &&
                                TryGetSiblingSkinTextureAlias(sourceTexturePath, familyRootStem, donorStem, out string aliasPrefix))
                            {
                                // Some skins bind a shared texture alias whose name ends with the skin suffix.
                                candidateTextureNames.Add(aliasPrefix + "_" + targetSkinSuffix);
                            }

                            if (candidateTextureNames.Count == 0)
                            {
                                continue;
                            }

                            var matchingTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string candidateTextureName in candidateTextureNames)
                            {
                                string candidateTexturePath = "ModelTextures/PLAYERVIEW/" + candidateTextureName + ".dtx";
                                if (!playerViewTexturePathsByBaseName.TryGetValue(candidateTextureName, out string[]? candidatePaths) ||
                                    candidatePaths.Length != 1 ||
                                    !string.Equals(candidatePaths[0], candidateTexturePath, StringComparison.OrdinalIgnoreCase) ||
                                    !HasConsistentDecodableDtxCopies(candidateTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                                {
                                    continue;
                                }

                                matchingTexturePaths.Add(candidateTexturePath);
                            }

                            if (matchingTexturePaths.Count == 1)
                            {
                                AddCandidateBinding(targetModelPath, slot, familyRootPath, matchingTexturePaths.Single());
                            }
                        }
                    }
                }
            }

            string? FindExistingSkinFamilyRoot(string directory, string targetStem, string donorStem)
            {
                if (donorStem.StartsWith(targetStem + "_", StringComparison.OrdinalIgnoreCase) &&
                    modelItemsByPlayerViewRolePath.ContainsKey(directory + "/" + targetStem))
                {
                    return targetStem;
                }

                int separatorIndex = targetStem.LastIndexOf('_');
                while (separatorIndex > 0)
                {
                    string candidateRoot = targetStem[..separatorIndex];
                    if (donorStem.StartsWith(candidateRoot + "_", StringComparison.OrdinalIgnoreCase) &&
                        modelItemsByPlayerViewRolePath.ContainsKey(directory + "/" + candidateRoot))
                    {
                        return candidateRoot;
                    }

                    separatorIndex = targetStem.LastIndexOf('_', separatorIndex - 1);
                }

                return null;
            }

            static bool TryGetSiblingNumberedPartSuffix(
                string texturePath,
                string familyRootStem,
                string donorStem,
                out string partSuffix)
            {
                if (TryGetNumberedPartSuffix(texturePath, donorStem, out partSuffix))
                {
                    return true;
                }

                partSuffix = string.Empty;
                string textureDirectory = Path.GetDirectoryName(texturePath)?.Replace('\\', '/') ?? string.Empty;
                string familyPrefix = familyRootStem + "_";
                if (!string.Equals(textureDirectory, "ModelTextures/PLAYERVIEW", StringComparison.OrdinalIgnoreCase) ||
                    !donorStem.StartsWith(familyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string donorSkinSuffix = donorStem[familyPrefix.Length..];
                string textureStem = Path.GetFileNameWithoutExtension(texturePath);
                if (!textureStem.StartsWith(familyRootStem, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string remainder = textureStem[familyRootStem.Length..];
                int digitStart = remainder.StartsWith('_') ? 1 : 0;
                int digitEnd = digitStart;
                while (digitEnd < remainder.Length && char.IsAsciiDigit(remainder[digitEnd]))
                {
                    digitEnd++;
                }

                if (digitEnd == digitStart ||
                    digitEnd >= remainder.Length ||
                    remainder[digitEnd] != '_' ||
                    !string.Equals(remainder[(digitEnd + 1)..], donorSkinSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                partSuffix = remainder[..digitEnd];
                return true;
            }

            static bool TryGetSiblingSkinTextureAlias(
                string texturePath,
                string familyRootStem,
                string donorStem,
                out string aliasPrefix)
            {
                aliasPrefix = string.Empty;
                string textureDirectory = Path.GetDirectoryName(texturePath)?.Replace('\\', '/') ?? string.Empty;
                string familyPrefix = familyRootStem + "_";
                if (!string.Equals(textureDirectory, "ModelTextures/PLAYERVIEW", StringComparison.OrdinalIgnoreCase) ||
                    !donorStem.StartsWith(familyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string donorSkinSuffix = donorStem[familyPrefix.Length..];
                string textureStem = Path.GetFileNameWithoutExtension(texturePath);
                string expectedSuffix = "_" + donorSkinSuffix;
                if (textureStem.Length <= expectedSuffix.Length ||
                    !textureStem.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                aliasPrefix = textureStem[..^expectedSuffix.Length];
                return aliasPrefix.Length > 0;
            }

            void AddCandidateBinding(string targetModelPath, int slot, string donorModelPath, string texturePath)
            {
                if (!candidateDonorsBySlot.TryGetValue(targetModelPath, out Dictionary<int, HashSet<string>>? targetDonors))
                {
                    candidateDonorsBySlot[targetModelPath] = targetDonors = [];
                }

                if (!targetDonors.TryGetValue(slot, out HashSet<string>? donors))
                {
                    targetDonors[slot] = donors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                donors.Add(donorModelPath);

                if (!candidateTexturesBySlot.TryGetValue(targetModelPath, out Dictionary<int, HashSet<string>>? targetTextures))
                {
                    candidateTexturesBySlot[targetModelPath] = targetTextures = [];
                }

                if (!targetTextures.TryGetValue(slot, out HashSet<string>? textures))
                {
                    targetTextures[slot] = textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                textures.Add(texturePath);
            }

            void AddTableMappedTexturePath(string modelPath, int slot, string? texturePath)
            {
                if (string.IsNullOrWhiteSpace(texturePath))
                {
                    return;
                }

                if (!tableMappedTexturesByModelSlot.TryGetValue(modelPath, out Dictionary<int, HashSet<string>>? modelSlots))
                {
                    tableMappedTexturesByModelSlot[modelPath] = modelSlots = [];
                }

                if (!modelSlots.TryGetValue(slot, out HashSet<string>? textures))
                {
                    modelSlots[slot] = textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                textures.Add(NormalizeKey(texturePath));
            }

            bool IsVerifiedUnmappedSlot(
                string targetModelPath,
                int slot,
                IReadOnlyList<LithTechModelDocument> targetDocuments,
                IReadOnlyList<LithTechModelDocument> donorDocuments,
                IReadOnlyDictionary<int, string> targetUvSignatures,
                IReadOnlyDictionary<int, string> donorUvSignatures)
            {
                return slot >= 0 &&
                    !IsPlayerViewHandOrArmTextureSlot(donorDocuments, slot) &&
                    !IsPlayerViewHandOrArmTextureSlot(targetDocuments, slot) &&
                    targetUvSignatures.TryGetValue(slot, out string? targetSignature) &&
                    donorUvSignatures.TryGetValue(slot, out string? donorSignature) &&
                    string.Equals(targetSignature, donorSignature, StringComparison.Ordinal) &&
                    !(byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetPathSlots) && targetPathSlots.ContainsKey(slot)) &&
                    !(contextTexturesByModelPath.TryGetValue(targetModelPath, out Dictionary<int, HashSet<string?>>? targetContextSlots) && targetContextSlots.ContainsKey(slot)) &&
                    !(variantSlotUvByModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? inferredTargetSlots) && inferredTargetSlots.ContainsKey(slot));
            }

            static bool TryGetNumberedPartSuffix(string texturePath, string modelStem, out string partSuffix)
            {
                partSuffix = string.Empty;
                string textureDirectory = Path.GetDirectoryName(texturePath)?.Replace('\\', '/') ?? string.Empty;
                if (!string.Equals(textureDirectory, "ModelTextures/PLAYERVIEW", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string textureStem = Path.GetFileNameWithoutExtension(texturePath);
                if (!textureStem.StartsWith(modelStem, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                partSuffix = textureStem[modelStem.Length..];
                string digits = partSuffix.StartsWith('_') ? partSuffix[1..] : partSuffix;
                return digits.Length > 0 && digits.All(char.IsAsciiDigit);
            }

            foreach ((string targetModelPath, Dictionary<int, HashSet<string>> slotCandidates) in candidateDonorsBySlot)
            {
                foreach ((int slot, HashSet<string> donorPaths) in slotCandidates)
                {
                    if (donorPaths.Count != 1 ||
                        !candidateTexturesBySlot.TryGetValue(targetModelPath, out Dictionary<int, HashSet<string>>? targetTextures) ||
                        !targetTextures.TryGetValue(slot, out HashSet<string>? textures) ||
                        textures.Count != 1)
                    {
                        continue;
                    }

                    string texturePath = textures.Single();
                    AddModelTextureSlotMapping(variantSlotUvByModelPath, targetModelPath, slot, texturePath);
                    modelsWithSlotSpecificBindings.Add(targetModelPath);
                }
            }

            static string? GetUniqueArchivedModelHash(IReadOnlyList<ExplorerItem> items)
            {
                if (items.Count == 0)
                {
                    return null;
                }

                string?[] hashes = items.Select(item => item.ArchiveFile?.Md5).ToArray();
                if (hashes.Any(string.IsNullOrWhiteSpace))
                {
                    return null;
                }

                string[] unique = hashes
                    .Select(hash => hash!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return unique.Length == 1 ? unique[0] : null;
            }
        }

        IReadOnlyDictionary<int, string>? GetSlotUvTriangleSignatures(string modelPath)
        {
            if (slotUvTriangleSignaturesByModelPath.TryGetValue(modelPath, out IReadOnlyDictionary<int, string>? cached))
            {
                return cached;
            }

            if (!modelItemsByPlayerViewRolePath.TryGetValue(modelPath, out ExplorerItem[]? modelItems) ||
                !TryDecodeConsistentPlayerViewModelDocuments(modelItems, decodedModels, out List<LithTechModelDocument> documents))
            {
                slotUvTriangleSignaturesByModelPath[modelPath] = null;
                return null;
            }

            IReadOnlyDictionary<int, string>? signatures = GetConsistentTextureSlotUvTriangleSignatures(documents);
            slotUvTriangleSignaturesByModelPath[modelPath] = signatures;
            return signatures;
        }

        static string GetPlayerViewBaseModelStem(string modelPath, IReadOnlyList<string> suffixes)
        {
            string stem = Path.GetFileName(modelPath);
            foreach (string suffix in suffixes)
            {
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^suffix.Length];
                    break;
                }
            }

            return NormalizeLooseKey(stem);
        }
    }

    private static void AddVerifiedPlayerViewMeshTextureSlotAliases(
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        Dictionary<string, Dictionary<int, string?>> variantSlotUvByModelPath,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        foreach (IGrouping<string, VerifiedPlayerViewMeshTextureSlotAlias> family in
                 VerifiedPlayerViewMeshTextureSlotAliases.GroupBy(alias => alias.FamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            VerifiedPlayerViewMeshTextureSlotAlias[] aliases = family.ToArray();
            int[] slots = aliases.Select(alias => alias.TargetSlotIndex).Distinct().ToArray();
            string[] texturePaths = aliases.Select(alias => NormalizeKey(alias.TexturePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (aliases.Length == 0 || slots.Length != 1 || texturePaths.Length != 1 ||
                !HasConsistentDecodableDtxCopies(texturePaths[0], textureItemsByLogicalPath, verifiedTexturePathCache))
            {
                continue;
            }

            int slotIndex = slots[0];
            string texturePath = aliases[0].TexturePath;
            string? expectedFamilySignature = null;
            var pendingBindings = new List<string>();
            bool validFamily = true;

            foreach (VerifiedPlayerViewMeshTextureSlotAlias alias in aliases)
            {
                string targetModelPath = NormalizeExtensionlessModelReference(alias.TargetModelPath);
                if (targetModelPath.Length == 0 ||
                    !modelItemsByPlayerViewRolePath.TryGetValue(targetModelPath, out ExplorerItem[]? modelCopies) ||
                    modelCopies.Length == 0 ||
                    !TryDecodeConsistentPlayerViewModelDocuments(modelCopies, decodedModels, out List<LithTechModelDocument> documents) ||
                    documents.Count == 0 ||
                    IsPlayerViewHandOrArmTextureSlot(documents, slotIndex) ||
                    byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? directSlots) && directSlots.ContainsKey(slotIndex) ||
                    variantSlotUvByModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? existingInferredSlots) && existingInferredSlots.ContainsKey(slotIndex) ||
                    byModelContext.Any(pair =>
                        pair.Key.StartsWith(targetModelPath + "\0", StringComparison.OrdinalIgnoreCase) &&
                        pair.Value.ContainsKey(slotIndex)))
                {
                    validFamily = false;
                    break;
                }

                string? expectedModelSignature = null;
                string? expectedModelTextureLayoutSignature = null;
                foreach (LithTechModelDocument document in documents)
                {
                    LithTechMesh[] slotMeshes = document.Meshes
                        .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                        .ToArray();
                    string[] expectedMeshNames = alias.MeshNames
                        .Select(meshName => meshName.Trim())
                        .OrderBy(meshName => meshName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    string[] actualMeshNames = slotMeshes
                        .Select(mesh => mesh.Name.Trim())
                        .OrderBy(meshName => meshName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (slotMeshes.Length == 0 ||
                        slotMeshes.Length != expectedMeshNames.Length ||
                        !actualMeshNames.SequenceEqual(expectedMeshNames, StringComparer.OrdinalIgnoreCase))
                    {
                        validFamily = false;
                        break;
                    }

                    var meshSignatures = new List<string>(slotMeshes.Length);
                    var meshTextureLayoutSignatures = new List<string>(slotMeshes.Length);
                    foreach (LithTechMesh mesh in slotMeshes)
                    {
                        if (!mesh.HasAuthoritativeTextureBindings ||
                            mesh.TextureSlotIndices is not { Count: 1 } meshSlots || meshSlots[0] != slotIndex ||
                            mesh.RenderStyleIndex is not int renderStyleIndex ||
                            !string.IsNullOrWhiteSpace(mesh.TexturePath) ||
                            GetVerifiedTextureUvTriangleSignature(mesh) is not string uvSignature)
                        {
                            validFamily = false;
                            break;
                        }

                        meshSignatures.Add(
                            $"{mesh.Name.Trim().ToUpperInvariant()}\0{renderStyleIndex}\0{uvSignature}");
                        meshTextureLayoutSignatures.Add($"{renderStyleIndex}\0{uvSignature}");
                    }

                    if (!validFamily)
                    {
                        break;
                    }

                    string modelSignature = string.Join('|', meshSignatures.Order(StringComparer.Ordinal));
                    string modelTextureLayoutSignature = string.Join(
                        '|', meshTextureLayoutSignatures.Order(StringComparer.Ordinal));
                    if ((expectedModelSignature is not null &&
                         !string.Equals(expectedModelSignature, modelSignature, StringComparison.Ordinal)) ||
                        (expectedModelTextureLayoutSignature is not null &&
                         !string.Equals(
                             expectedModelTextureLayoutSignature,
                             modelTextureLayoutSignature,
                             StringComparison.Ordinal)))
                    {
                        validFamily = false;
                        break;
                    }

                    expectedModelSignature ??= modelSignature;
                    expectedModelTextureLayoutSignature ??= modelTextureLayoutSignature;
                }

                if (!validFamily || expectedModelSignature is null || expectedModelTextureLayoutSignature is null ||
                    expectedFamilySignature is not null &&
                    !string.Equals(
                        expectedFamilySignature,
                        expectedModelTextureLayoutSignature,
                        StringComparison.Ordinal))
                {
                    validFamily = false;
                    break;
                }

                expectedFamilySignature ??= expectedModelTextureLayoutSignature;
                pendingBindings.Add(targetModelPath);
            }

            if (!validFamily || pendingBindings.Count != aliases.Length)
            {
                continue;
            }

            foreach (string targetModelPath in pendingBindings)
            {
                AddModelTextureSlotMapping(variantSlotUvByModelPath, targetModelPath, slotIndex, texturePath);
                modelsWithSlotSpecificBindings.Add(targetModelPath);
            }
        }
    }

    private static void AddVerifiedCftIdentityModelAliasTextureBindings(
        IReadOnlyList<DeferredCftIdentityModelAliasBinding> bindings,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> otherPathMappings,
        IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> otherContextMappings,
        Dictionary<string, Dictionary<int, string?>> cftIdentityModelAliasByModelContext,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        var candidates = new List<(string ModelPath, string Context, int Slot, string SourceModelPath, string TexturePath)>();

        foreach (DeferredCftIdentityModelAliasBinding binding in bindings)
        {
            string sourceModelPath = NormalizeExtensionlessModelReference(binding.SourceModelPath);
            string sourceDirectory = NormalizeModelResourcePath(Path.GetDirectoryName(sourceModelPath) ?? string.Empty);
            string texturePath = NormalizeKey(binding.TexturePath);
            if (sourceModelPath.Length == 0 || sourceDirectory.Length == 0 ||
                !texturePath.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(texturePath), ".dtx", StringComparison.OrdinalIgnoreCase) ||
                !HasConsistentDecodableDtxCopies(texturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
            {
                continue;
            }

            string[] identityAliases = binding.IdentityAliases
                .Select(NormalizeCftIdentityModelAlias)
                .Where(alias => alias.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (identityAliases.Length == 0)
            {
                continue;
            }

            string[] targetModelPaths = modelItemsByPlayerViewRolePath.Keys
                .Where(path => IsAccSdPreviewModelPath(path) &&
                               string.Equals(
                                   NormalizeModelResourcePath(Path.GetDirectoryName(path) ?? string.Empty),
                                   sourceDirectory,
                                   StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(
                                   NormalizeExtensionlessModelReference(path),
                                   sourceModelPath,
                                   StringComparison.OrdinalIgnoreCase) &&
                               identityAliases.Contains(NormalizeCftIdentityModelAlias(path), StringComparer.OrdinalIgnoreCase))
                .Select(NormalizeExtensionlessModelReference)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (targetModelPaths.Length != 1 ||
                !modelItemsByPlayerViewRolePath.TryGetValue(targetModelPaths[0], out ExplorerItem[]? targetModelItems) ||
                !modelItemsByPlayerViewRolePath.TryGetValue(sourceModelPath, out ExplorerItem[]? sourceModelItems) ||
                !TryDecodeConsistentPlayerViewModelDocuments(sourceModelItems, decodedModels, out List<LithTechModelDocument> sourceDocuments) ||
                !TryDecodeConsistentPlayerViewModelDocuments(targetModelItems, decodedModels, out List<LithTechModelDocument> targetDocuments) ||
                !HaveIdenticalAuthoritativePlayerViewLayouts(sourceDocuments, targetDocuments) ||
                IsPlayerViewHandOrArmTextureSlot(sourceDocuments, binding.SlotIndex) ||
                IsPlayerViewHandOrArmTextureSlot(targetDocuments, binding.SlotIndex) ||
                !HasAuthoritativePlayerViewTextureSlot(sourceDocuments, binding.SlotIndex) ||
                !HasAuthoritativePlayerViewTextureSlot(targetDocuments, binding.SlotIndex))
            {
                continue;
            }

            string targetModelPath = targetModelPaths[0];
            foreach (string context in binding.ContextKeys
                         .Select(NormalizeLooseKey)
                         .Where(context => context.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string contextKey = $"{targetModelPath}\0{context}";
                if (HasSlot(byModelPath, targetModelPath, binding.SlotIndex) ||
                    HasSlotInAnyMapping(otherPathMappings, targetModelPath, binding.SlotIndex) ||
                    HasSlot(byModelContext, contextKey, binding.SlotIndex) ||
                    HasSlotInAnyMapping(otherContextMappings, contextKey, binding.SlotIndex) ||
                    HasSlot(cftIdentityModelAliasByModelContext, contextKey, binding.SlotIndex))
                {
                    continue;
                }

                candidates.Add((targetModelPath, context, binding.SlotIndex, sourceModelPath, texturePath));
            }
        }

        foreach (IGrouping<string, (string ModelPath, string Context, int Slot, string SourceModelPath, string TexturePath)> group in
                 candidates.GroupBy(
                     candidate => $"{candidate.ModelPath}\0{candidate.Context}\0{candidate.Slot.ToString(CultureInfo.InvariantCulture)}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var firstCandidate = group.First();
            string modelPath = firstCandidate.ModelPath;
            string context = firstCandidate.Context;
            int slot = firstCandidate.Slot;
            string[] sourceModels = group.Select(candidate => candidate.SourceModelPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] textures = group.Select(candidate => candidate.TexturePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string contextKey = $"{modelPath}\0{context}";
            if (sourceModels.Length != 1 || textures.Length != 1 ||
                HasSlot(byModelPath, modelPath, slot) ||
                HasSlotInAnyMapping(otherPathMappings, modelPath, slot) ||
                HasSlot(byModelContext, contextKey, slot) ||
                HasSlotInAnyMapping(otherContextMappings, contextKey, slot) ||
                HasSlot(cftIdentityModelAliasByModelContext, contextKey, slot))
            {
                continue;
            }

            AddModelTextureContextSlotMapping(
                cftIdentityModelAliasByModelContext,
                modelPath,
                context,
                slot,
                textures[0]);
            modelsWithSlotSpecificBindings.Add(modelPath);
        }

        static bool IsAccSdPreviewModelPath(string path) =>
            path.StartsWith("Models/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/ACC_SD/", StringComparison.OrdinalIgnoreCase);

        static string NormalizeCftIdentityModelAlias(string value)
        {
            string stem = Path.GetFileNameWithoutExtension(NormalizeKey(value));
            if (stem.StartsWith("PV-", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("PV_", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[3..];
            }

            return NormalizeLooseKey(stem);
        }

        static bool HasAuthoritativePlayerViewTextureSlot(
            IReadOnlyList<LithTechModelDocument> documents,
            int slotIndex)
        {
            if (slotIndex is < 0 or > MaxTextureSlotIndex)
            {
                return false;
            }

            foreach (LithTechModelDocument document in documents)
            {
                LithTechMesh[] slotMeshes = document.Meshes
                    .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                    .ToArray();
                if (slotMeshes.Length == 0 || slotMeshes.Any(mesh =>
                        !mesh.HasAuthoritativeTextureBindings ||
                        mesh.RenderStyleIndex is null ||
                        !string.IsNullOrWhiteSpace(mesh.TexturePath)))
                {
                    return false;
                }
            }

            return documents.Count > 0;
        }

        static bool HaveIdenticalAuthoritativePlayerViewLayouts(
            IReadOnlyList<LithTechModelDocument> sourceDocuments,
            IReadOnlyList<LithTechModelDocument> targetDocuments)
        {
            if (sourceDocuments.Count == 0 || targetDocuments.Count == 0)
            {
                return false;
            }

            foreach (LithTechModelDocument sourceDocument in sourceDocuments)
            {
                foreach (LithTechModelDocument targetDocument in targetDocuments)
                {
                    if (!LithTechModelMeshTextureResolver.HaveIdenticalMeshLayouts(sourceDocument, targetDocument) ||
                        sourceDocument.Meshes.Where((mesh, index) =>
                            mesh.RenderStyleIndex != targetDocument.Meshes[index].RenderStyleIndex ||
                            mesh.HasAuthoritativeTextureBindings != targetDocument.Meshes[index].HasAuthoritativeTextureBindings)
                            .Any())
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        static bool HasSlot(
            IReadOnlyDictionary<string, Dictionary<int, string?>> mappings,
            string key,
            int slotIndex) => mappings.TryGetValue(key, out Dictionary<int, string?>? slots) &&
                              slots.ContainsKey(slotIndex);

        static bool HasSlotInAnyMapping(
            IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> mappings,
            string key,
            int slotIndex) => mappings.Any(mapping => HasSlot(mapping, key, slotIndex));
    }

    private static void AddVerifiedMotionParentSlotTextureBindings(
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> otherPathMappings,
        IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> otherContextMappings,
        Dictionary<string, Dictionary<int, string?>> motionParentSlotByModelPath,
        Dictionary<string, Dictionary<int, string?>> motionParentSlotByModelContext,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        const string motionSuffix = "_M-motion";
        var contextsByModelPath = new Dictionary<string, List<(string Context, Dictionary<int, string?> Slots)>>(
            StringComparer.OrdinalIgnoreCase);
        var contextSlotsByModelPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var contextMappings = new List<IReadOnlyDictionary<string, Dictionary<int, string?>>>(otherContextMappings.Count + 1)
        {
            byModelContext
        };
        contextMappings.AddRange(otherContextMappings);

        foreach (IReadOnlyDictionary<string, Dictionary<int, string?>> mappings in contextMappings)
        {
            foreach ((string key, Dictionary<int, string?> slots) in mappings)
            {
                int separator = key.IndexOf('\0');
                if (separator <= 0 || separator == key.Length - 1)
                {
                    continue;
                }

                string modelPath = key[..separator];
                string context = key[(separator + 1)..];
                if (!contextSlotsByModelPath.TryGetValue(modelPath, out HashSet<int>? modelSlots))
                {
                    contextSlotsByModelPath[modelPath] = modelSlots = [];
                }

                modelSlots.UnionWith(slots.Keys);
                if (ReferenceEquals(mappings, byModelContext))
                {
                    if (!contextsByModelPath.TryGetValue(modelPath, out List<(string Context, Dictionary<int, string?> Slots)>? contexts))
                    {
                        contextsByModelPath[modelPath] = contexts = [];
                    }

                    contexts.Add((context, slots));
                }
            }
        }

        foreach (string targetModelPath in modelItemsByPlayerViewRolePath.Keys)
        {
            if (!targetModelPath.EndsWith(motionSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string parentModelPath = NormalizeModelResourcePath(targetModelPath[..^motionSuffix.Length]);
            if (parentModelPath.Length == 0 ||
                !modelItemsByPlayerViewRolePath.TryGetValue(targetModelPath, out ExplorerItem[]? targetItems) ||
                !modelItemsByPlayerViewRolePath.TryGetValue(parentModelPath, out ExplorerItem[]? parentItems) ||
                !TryDecodeConsistentPlayerViewModelDocuments(targetItems, decodedModels, out List<LithTechModelDocument> targetDocuments) ||
                !TryDecodeConsistentPlayerViewModelDocuments(parentItems, decodedModels, out List<LithTechModelDocument> parentDocuments))
            {
                continue;
            }

            Dictionary<int, string?>? parentDirectSlots = byModelPath.TryGetValue(parentModelPath, out Dictionary<int, string?>? directSlots)
                ? directSlots
                : null;
            contextsByModelPath.TryGetValue(parentModelPath, out List<(string Context, Dictionary<int, string?> Slots)>? parentContexts);
            var candidateSlots = new HashSet<int>(parentDirectSlots is null
                ? Enumerable.Empty<int>()
                : parentDirectSlots.Keys);
            if (parentContexts is not null)
            {
                foreach ((_, Dictionary<int, string?> contextSlots) in parentContexts)
                {
                    candidateSlots.UnionWith(contextSlots.Keys);
                }
            }

            foreach (int slotIndex in candidateSlots)
            {
                if (slotIndex is < 0 or > MaxTextureSlotIndex ||
                    IsPlayerViewHandOrArmTextureSlot(parentDocuments, slotIndex) ||
                    IsPlayerViewHandOrArmTextureSlot(targetDocuments, slotIndex) ||
                    !HasMatchingMotionParentSlotUsage(parentDocuments, targetDocuments, slotIndex))
                {
                    continue;
                }

                bool targetHasDirectPathSlot = byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetDirectSlots) &&
                                               targetDirectSlots.ContainsKey(slotIndex);
                bool targetHasOtherPathSlot = HasSlotInAnyMapping(otherPathMappings, targetModelPath, slotIndex);

                if (parentDirectSlots is not null &&
                    parentDirectSlots.TryGetValue(slotIndex, out string? parentTextureReference) &&
                    !string.IsNullOrWhiteSpace(parentTextureReference))
                {
                    string texturePath = NormalizeKey(parentTextureReference);
                    if (IsPlayerViewTexturePath(texturePath) &&
                        HasConsistentDecodableDtxCopies(texturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                    {
                        bool targetHasContextSlot = contextSlotsByModelPath.TryGetValue(targetModelPath, out HashSet<int>? targetContextSlots) &&
                                                    targetContextSlots.Contains(slotIndex);
                        if (!targetHasContextSlot && !targetHasDirectPathSlot && !targetHasOtherPathSlot &&
                            (!motionParentSlotByModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? existingPathSlots) ||
                             !existingPathSlots.ContainsKey(slotIndex)))
                        {
                            AddModelTextureSlotMapping(motionParentSlotByModelPath, targetModelPath, slotIndex, texturePath);
                            modelsWithSlotSpecificBindings.Add(targetModelPath);
                        }
                    }
                }

                if (parentContexts is null)
                {
                    continue;
                }

                foreach ((string context, Dictionary<int, string?> parentContextSlots) in parentContexts)
                {
                    if (!parentContextSlots.TryGetValue(slotIndex, out string? contextTextureReference) ||
                        string.IsNullOrWhiteSpace(contextTextureReference))
                    {
                        continue;
                    }

                    string contextKey = $"{targetModelPath}\0{context}";
                    if (HasSlotInAnyMapping(contextMappings, contextKey, slotIndex))
                    {
                        continue;
                    }

                    string contextTexturePath = NormalizeKey(contextTextureReference);
                    if (!IsPlayerViewTexturePath(contextTexturePath) ||
                        !HasConsistentDecodableDtxCopies(contextTexturePath, textureItemsByLogicalPath, verifiedTexturePathCache))
                    {
                        continue;
                    }

                    AddModelTextureContextSlotMapping(
                        motionParentSlotByModelContext,
                        targetModelPath,
                        context,
                        slotIndex,
                        contextTexturePath);
                    modelsWithSlotSpecificBindings.Add(targetModelPath);
                }
            }
        }

        static bool IsPlayerViewTexturePath(string path) =>
            path.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ModelTextures/WEAPONS/", StringComparison.OrdinalIgnoreCase);

        static bool HasSlotInAnyMapping(
            IReadOnlyList<IReadOnlyDictionary<string, Dictionary<int, string?>>> mappings,
            string key,
            int slotIndex) =>
            mappings.Any(mapping => mapping.TryGetValue(key, out Dictionary<int, string?>? slots) &&
                                    slots.ContainsKey(slotIndex));
    }

    private static bool HasMatchingMotionParentSlotUsage(
        IReadOnlyList<LithTechModelDocument> parentDocuments,
        IReadOnlyList<LithTechModelDocument> targetDocuments,
        int slotIndex)
    {
        string? parentSignature = GetConsistentMotionParentSlotUsageSignature(parentDocuments, slotIndex);
        string? targetSignature = GetConsistentMotionParentSlotUsageSignature(targetDocuments, slotIndex);
        return parentSignature is not null &&
               string.Equals(parentSignature, targetSignature, StringComparison.Ordinal);
    }

    private static string? GetConsistentMotionParentSlotUsageSignature(
        IReadOnlyList<LithTechModelDocument> documents,
        int slotIndex)
    {
        string? expectedSignature = null;
        foreach (LithTechModelDocument document in documents)
        {
            LithTechMesh[] slotMeshes = document.Meshes
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                .ToArray();
            if (slotMeshes.Length == 0)
            {
                return null;
            }

            var meshSignatures = new string[slotMeshes.Length];
            for (int meshIndex = 0; meshIndex < slotMeshes.Length; meshIndex++)
            {
                LithTechMesh mesh = slotMeshes[meshIndex];
                if (!mesh.HasAuthoritativeTextureBindings ||
                    string.IsNullOrWhiteSpace(mesh.Name) ||
                    mesh.TextureSlotIndices is not { Count: > 0 } meshSlots ||
                    meshSlots.Any(slot => slot < 0) ||
                    mesh.RenderStyleIndex is not int renderStyleIndex ||
                    !string.IsNullOrWhiteSpace(mesh.TexturePath))
                {
                    return null;
                }

                meshSignatures[meshIndex] = string.Join("\0", [
                    mesh.Name.Trim().ToUpperInvariant(),
                    string.Join(",", meshSlots.Select(slot => slot.ToString(CultureInfo.InvariantCulture))),
                    renderStyleIndex.ToString(CultureInfo.InvariantCulture)
                ]);
            }

            string currentSignature = string.Join("|", meshSignatures.Order(StringComparer.Ordinal));
            if (expectedSignature is not null &&
                !string.Equals(expectedSignature, currentSignature, StringComparison.Ordinal))
            {
                return null;
            }

            expectedSignature ??= currentSignature;
        }

        return expectedSignature;
    }

    private static IReadOnlyDictionary<int, string>? GetConsistentTextureSlotUvTriangleSignatures(
        IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0)
        {
            return null;
        }

        Dictionary<int, string>? expected = null;
        foreach (LithTechModelDocument document in documents)
        {
            int[] slots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (slots.Length == 0)
            {
                return null;
            }

            var current = new Dictionary<int, string>();
            foreach (int slotIndex in slots)
            {
                string?[] meshSignatures = document.Meshes
                    .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                    .Select(GetVerifiedTextureUvTriangleSignature)
                    .ToArray();
                if (meshSignatures.Length == 0 || meshSignatures.Any(string.IsNullOrWhiteSpace))
                {
                    return null;
                }

                string signature = string.Join('|', meshSignatures.Select(value => value!).Order(StringComparer.Ordinal));
                current[slotIndex] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
            }

            if (expected is not null &&
                (expected.Count != current.Count || expected.Any(pair =>
                    !current.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            {
                return null;
            }

            expected ??= current;
        }

        return expected;
    }

    private static string GetRelativePlayerViewDirectory(string resourcePath, string resourceRoot)
    {
        string directory = (Path.GetDirectoryName(resourcePath) ?? string.Empty).Replace('\\', '/');
        return directory.Length <= resourceRoot.Length
            ? string.Empty
            : directory[(resourceRoot.Length + 1)..];
    }

    private static (Dictionary<string, Dictionary<int, string?>> ByContext,
        Dictionary<string, Dictionary<int, string?>> ByPath) BuildEquivalentArmContextBindings(
        IReadOnlySet<string> directModels,
        IReadOnlySet<string> typedLtcPlayerViewModels,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItems,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directContexts,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directPaths)
    {
        var result = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var pathTextureCandidates = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var compatibleSourcePaths = new Dictionary<string, Dictionary<int, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var signatures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var textureSlotSignatures = new Dictionary<string, IReadOnlyDictionary<int, string>?>(StringComparer.OrdinalIgnoreCase);
        var geometryUvTextureSlotSignatures = new Dictionary<string, IReadOnlyDictionary<int, string>?>(StringComparer.OrdinalIgnoreCase);
        var uvTriangleTextureSlotSignatures = new Dictionary<string, IReadOnlyDictionary<int, string>?>(StringComparer.OrdinalIgnoreCase);
        var textureSlotDocuments = new Dictionary<string, IReadOnlyList<LithTechModelDocument>?>(StringComparer.OrdinalIgnoreCase);
        var contextsBySourceModel = new Dictionary<string, List<KeyValuePair<string, Dictionary<int, string?>>>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<int, string?>> binding in directContexts)
        {
            int separator = binding.Key.IndexOf('\0');
            if (separator <= 0) continue;
            string sourcePath = binding.Key[..separator];
            if (!contextsBySourceModel.TryGetValue(sourcePath, out List<KeyValuePair<string, Dictionary<int, string?>>>? modelBindings))
                contextsBySourceModel[sourcePath] = modelBindings = [];
            modelBindings.Add(binding);
        }

        var armDonorsBySlotSignature = new Dictionary<(int Slot, string Signature), List<string>>();
        var armDonorsBySlotGeometryUvSignature = new Dictionary<(int Slot, string Signature), List<string>>();
        var armDonorsBySlotUvTriangleSignature = new Dictionary<(int Slot, string Signature), List<string>>();
        foreach (string sourcePath in directModels.Where(CftPlayerViewArmEquivalence.IsPlayerViewArmModel))
        {
            if (!contextsBySourceModel.ContainsKey(sourcePath)) continue;
            IReadOnlyDictionary<int, string>? sourceSlots = GetTextureSlotSignatures(sourcePath);
            if (sourceSlots is not null)
            {
                foreach ((int slot, string signature) in sourceSlots)
                {
                    var key = (slot, signature);
                    if (!armDonorsBySlotSignature.TryGetValue(key, out List<string>? paths))
                        armDonorsBySlotSignature[key] = paths = [];
                    paths.Add(sourcePath);
                }
            }

            IReadOnlyDictionary<int, string>? sourceGeometryUvSlots = GetGeometryUvSlotSignatures(sourcePath);
            if (sourceGeometryUvSlots is not null)
            {
                foreach ((int slot, string signature) in sourceGeometryUvSlots)
                {
                    var key = (slot, signature);
                    if (!armDonorsBySlotGeometryUvSignature.TryGetValue(key, out List<string>? paths))
                        armDonorsBySlotGeometryUvSignature[key] = paths = [];
                    paths.Add(sourcePath);
                }
            }

            IReadOnlyDictionary<int, string>? sourceUvTriangleSlots = GetUvTriangleSlotSignatures(sourcePath);
            if (sourceUvTriangleSlots is not null)
            {
                foreach ((int slot, string signature) in sourceUvTriangleSlots)
                {
                    var key = (slot, signature);
                    if (!armDonorsBySlotUvTriangleSignature.TryGetValue(key, out List<string>? paths))
                        armDonorsBySlotUvTriangleSignature[key] = paths = [];
                    paths.Add(sourcePath);
                }
            }
        }

        foreach ((string targetPath, ExplorerItem[] _) in modelItems)
        {
            bool isEmbeddedPreview = CftPlayerViewArmEquivalence.IsEmbeddedPreviewModel(targetPath);
            bool isTypedLtcPlayerViewModel = typedLtcPlayerViewModels.Contains(targetPath);
            if (!isEmbeddedPreview && !isTypedLtcPlayerViewModel && directModels.Contains(targetPath)) continue;
            if (isEmbeddedPreview || isTypedLtcPlayerViewModel)
            {
                var strongerMatches = new HashSet<int>();
                IReadOnlyDictionary<int, string>? targetSlots = GetTextureSlotSignatures(targetPath);
                if (targetSlots is not null)
                {
                    foreach ((int slot, string signature) in targetSlots)
                    {
                        if (directPaths.TryGetValue(targetPath, out Dictionary<int, string?>? targetDirectSlots) &&
                            targetDirectSlots.ContainsKey(slot))
                        {
                            continue;
                        }

                        if (armDonorsBySlotSignature.TryGetValue((slot, signature), out List<string>? sourcePaths))
                        {
                            AddEquivalentArmDonors(targetPath, slot, sourcePaths);
                            strongerMatches.Add(slot);
                        }
                    }
                }

                IReadOnlyDictionary<int, string>? targetGeometryUvSlots = GetGeometryUvSlotSignatures(targetPath);
                if (targetGeometryUvSlots is not null)
                {
                    foreach ((int slot, string signature) in targetGeometryUvSlots)
                    {
                        if (directPaths.TryGetValue(targetPath, out Dictionary<int, string?>? targetDirectSlots) &&
                            targetDirectSlots.ContainsKey(slot))
                        {
                            continue;
                        }

                        if (armDonorsBySlotGeometryUvSignature.TryGetValue((slot, signature), out List<string>? sourcePaths))
                        {
                            AddEquivalentArmDonors(targetPath, slot, sourcePaths);
                            strongerMatches.Add(slot);
                        }
                    }
                }

                if (isEmbeddedPreview)
                {
                    IReadOnlyDictionary<int, string>? targetUvTriangleSlots = GetUvTriangleSlotSignatures(targetPath);
                    if (targetUvTriangleSlots is not null)
                    {
                        foreach ((int slot, string signature) in targetUvTriangleSlots)
                        {
                            if (strongerMatches.Contains(slot) ||
                                directPaths.TryGetValue(targetPath, out Dictionary<int, string?>? targetDirectSlots) &&
                                targetDirectSlots.ContainsKey(slot))
                            {
                                continue;
                            }

                            if (armDonorsBySlotUvTriangleSignature.TryGetValue((slot, signature), out List<string>? sourcePaths))
                                AddEquivalentArmDonors(targetPath, slot, sourcePaths);
                        }
                    }
                }

                continue;
            }

            foreach (string sourcePath in directModels)
            {
                if (!CftPlayerViewArmEquivalence.IsFamilyVariantOf(targetPath, sourcePath) ||
                    !string.Equals(GetSignature(targetPath), GetSignature(sourcePath), StringComparison.Ordinal))
                    continue;
                if (!contextsBySourceModel.TryGetValue(sourcePath, out List<KeyValuePair<string, Dictionary<int, string?>>>? sourceBindings))
                    continue;
                foreach (KeyValuePair<string, Dictionary<int, string?>> sourceBinding in sourceBindings)
                {
                    foreach ((int slot, string? texture) in sourceBinding.Value)
                    {
                        AddContextBinding(targetPath, sourcePath, sourceBinding, slot, texture);
                    }
                }
            }
        }

        var byPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string targetPath, Dictionary<int, HashSet<string>> candidateSlots) in pathTextureCandidates)
        {
            foreach ((int slot, HashSet<string> candidates) in candidateSlots)
            {
                if (!byPath.TryGetValue(targetPath, out Dictionary<int, string?>? targetSlots))
                    byPath[targetPath] = targetSlots = [];

                // Context dictionaries include only resolvable DTX files. The model-path map also
                // retains missing and conflicting CFT references, so validate every donor there.
                bool isUniqueAcrossEveryDonor = candidates.Count == 1;
                HashSet<string>? donorPaths = null;
                if (isUniqueAcrossEveryDonor &&
                    (!compatibleSourcePaths.TryGetValue(targetPath, out Dictionary<int, HashSet<string>>? donorSlots) ||
                     !donorSlots.TryGetValue(slot, out donorPaths) ||
                     donorPaths.Count == 0))
                {
                    isUniqueAcrossEveryDonor = false;
                }

                if (isUniqueAcrossEveryDonor)
                {
                    string candidate = candidates.Single();
                    foreach (string donorPath in donorPaths!)
                    {
                        if (!directPaths.TryGetValue(donorPath, out Dictionary<int, string?>? donorTextureSlots) ||
                            !donorTextureSlots.TryGetValue(slot, out string? donorTexture) ||
                            string.IsNullOrWhiteSpace(donorTexture) ||
                            !string.Equals(donorTexture, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            isUniqueAcrossEveryDonor = false;
                            break;
                        }
                    }

                    if (isUniqueAcrossEveryDonor) targetSlots[slot] = candidate;
                    else targetSlots[slot] = null;
                }
                else
                {
                    targetSlots[slot] = null;
                }
            }
        }

        return (result, byPath);

        void AddContextBinding(
            string targetPath,
            string sourcePath,
            KeyValuePair<string, Dictionary<int, string?>> sourceBinding,
            int slot,
            string? texture)
        {
            string targetKey = targetPath + "\0" + sourceBinding.Key[(sourcePath.Length + 1)..];
            if (!result.TryGetValue(targetKey, out Dictionary<int, string?>? targetSlots))
                result[targetKey] = targetSlots = [];
            if (!targetSlots.TryGetValue(slot, out string? previous)) targetSlots[slot] = texture;
            else if (!string.Equals(previous, texture, StringComparison.OrdinalIgnoreCase)) targetSlots[slot] = null;
        }

        void AddPathTextureCandidate(string targetPath, int slot, string texture)
        {
            if (!pathTextureCandidates.TryGetValue(targetPath, out Dictionary<int, HashSet<string>>? candidateSlots))
                pathTextureCandidates[targetPath] = candidateSlots = [];
            if (!candidateSlots.TryGetValue(slot, out HashSet<string>? candidates))
                candidateSlots[slot] = candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            candidates.Add(texture);
        }

        void AddCompatibleSourcePath(string targetPath, int slot, string sourcePath)
        {
            if (!compatibleSourcePaths.TryGetValue(targetPath, out Dictionary<int, HashSet<string>>? donorSlots))
                compatibleSourcePaths[targetPath] = donorSlots = [];
            if (!donorSlots.TryGetValue(slot, out HashSet<string>? donorPaths))
                donorSlots[slot] = donorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            donorPaths.Add(sourcePath);
        }

        void AddEquivalentArmDonors(string targetPath, int slot, IEnumerable<string> sourcePaths)
        {
            foreach (string sourcePath in sourcePaths)
            {
                AddCompatibleSourcePath(targetPath, slot, sourcePath);
                if (!contextsBySourceModel.TryGetValue(sourcePath, out List<KeyValuePair<string, Dictionary<int, string?>>>? sourceBindings))
                    continue;
                foreach (KeyValuePair<string, Dictionary<int, string?>> sourceBinding in sourceBindings)
                {
                    if (!sourceBinding.Value.TryGetValue(slot, out string? texture)) continue;
                    AddContextBinding(targetPath, sourcePath, sourceBinding, slot, texture);
                    if (!string.IsNullOrWhiteSpace(texture)) AddPathTextureCandidate(targetPath, slot, texture);
                }
            }
        }

        string? GetSignature(string path)
        {
            if (signatures.TryGetValue(path, out string? cached)) return cached;
            var documents = new List<LithTechModelDocument>();
            if (modelItems.TryGetValue(path, out ExplorerItem[]? copies))
            {
                foreach (ExplorerItem copy in copies)
                {
                    // Verify every physical copy, independently of archive MD5 metadata.
                    byte[]? data = TryReadItemBytes(copy, 64 * 1024 * 1024);
                    if (data is null || !LithTechModelDecoder.TryDecode(data, GetModelResourcePath(copy), "ltb",
                            out LithTechModelDocument? document, out _) || document is null)
                        return signatures[path] = null;
                    documents.Add(document);
                }
            }
            return signatures[path] = CftPlayerViewArmEquivalence.GetConsistentLayoutSignature(documents);
        }

        IReadOnlyDictionary<int, string>? GetTextureSlotSignatures(string path)
        {
            if (textureSlotSignatures.TryGetValue(path, out IReadOnlyDictionary<int, string>? cached)) return cached;
            IReadOnlyList<LithTechModelDocument>? documents = GetTextureSlotDocuments(path);
            return textureSlotSignatures[path] = documents is null
                ? null
                : CftPlayerViewArmEquivalence.GetConsistentTextureSlotLayoutSignatures(documents);
        }

        IReadOnlyDictionary<int, string>? GetGeometryUvSlotSignatures(string path)
        {
            if (geometryUvTextureSlotSignatures.TryGetValue(path, out IReadOnlyDictionary<int, string>? cached)) return cached;
            IReadOnlyList<LithTechModelDocument>? documents = GetTextureSlotDocuments(path);
            return geometryUvTextureSlotSignatures[path] = documents is null
                ? null
                : CftPlayerViewArmEquivalence.GetConsistentTextureSlotGeometryUvSignatures(documents);
        }

        IReadOnlyDictionary<int, string>? GetUvTriangleSlotSignatures(string path)
        {
            if (uvTriangleTextureSlotSignatures.TryGetValue(path, out IReadOnlyDictionary<int, string>? cached)) return cached;
            IReadOnlyList<LithTechModelDocument>? documents = GetTextureSlotDocuments(path);
            return uvTriangleTextureSlotSignatures[path] = documents is null
                ? null
                : CftPlayerViewArmEquivalence.GetConsistentTextureSlotUvTriangleSignatures(documents);
        }

        IReadOnlyList<LithTechModelDocument>? GetTextureSlotDocuments(string path)
        {
            if (textureSlotDocuments.TryGetValue(path, out IReadOnlyList<LithTechModelDocument>? cached)) return cached;
            if (!modelItems.TryGetValue(path, out ExplorerItem[]? copies))
            {
                // CharacterType.CFT refers to player-view arms as ArmModel/Arm_*.
                // Their physical LTB resources are stored below Models/PLAYERVIEW.
                string normalizedPath = NormalizeExtensionlessModelReference(path);
                if (!normalizedPath.StartsWith("ArmModel/Arm_", StringComparison.OrdinalIgnoreCase) ||
                    !modelItems.TryGetValue("Models/PLAYERVIEW/" + normalizedPath, out copies))
                {
                    return textureSlotDocuments[path] = null;
                }
            }

            if (copies.Length == 0)
                return textureSlotDocuments[path] = null;

            var documents = new List<LithTechModelDocument>(copies.Length);
            foreach (ExplorerItem copy in copies)
            {
                // Decode each physical copy once for both strict and canonical layout checks.
                byte[]? data = TryReadItemBytes(copy, 64 * 1024 * 1024);
                if (data is null || !LithTechModelDecoder.TryDecode(data, GetModelResourcePath(copy), "ltb",
                        out LithTechModelDocument? document, out _) || document is null)
                    return textureSlotDocuments[path] = null;
                documents.Add(document);
            }

            return textureSlotDocuments[path] = documents;
        }
    }

    private static string[] GetCftContextKeys(string tablePath, CftModelTextureMapping mapping)
    {
        string rowIdentity = mapping.RowIndex.ToString(CultureInfo.InvariantCulture);
        string normalizedTablePath = NormalizeKey(tablePath);
        string tableFileName = Path.GetFileName(tablePath);
        var contexts = new List<string>(mapping.IdentityAliases)
        {
            $"{normalizedTablePath}#{rowIdentity}",
            $"row:{normalizedTablePath}#{rowIdentity}",
            $"row:{tableFileName}#{rowIdentity}",
            // Keep the old basename-plus-row selector, but never reuse its
            // `record:` namespace for RecordId; those number domains overlap.
            $"record:{tableFileName}:{rowIdentity}"
        };
        if (mapping.RecordId is int recordId && recordId > 0)
        {
            string recordIdentity = recordId.ToString(CultureInfo.InvariantCulture);
            contexts.Add($"recordid:{normalizedTablePath}#{recordIdentity}");
            contexts.Add($"recordid:{tableFileName}#{recordIdentity}");
        }

        return contexts
            .Where(context => NormalizeLooseKey(context).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetCftSideContextKeys(
        string tablePath,
        CftModelTextureMapping mapping,
        string side)
    {
        string[] contextKeys = GetCftContextKeys(tablePath, mapping);
        return contextKeys
            .Concat(contextKeys.Select(context => $"{context}:{side}"))
            .Append($"record:{NormalizeKey(tablePath)}#{mapping.RowIndex.ToString(CultureInfo.InvariantCulture)}:{side}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetCftPlayerViewTextureSide(string columnName, out string side)
    {
        side = string.Empty;
        if (!CftPlayerViewRoleSlotResolver.TryGetTextureRole(columnName, out CftPlayerViewTextureRole role))
        {
            return false;
        }

        string normalizedColumn = columnName.TrimStart('_');
        string roleField = role == CftPlayerViewTextureRole.Hand
            ? "PVHandTextureFile"
            : "PVArmTextureFile";
        int roleFieldIndex = normalizedColumn.IndexOf(roleField, StringComparison.OrdinalIgnoreCase);
        if (roleFieldIndex <= 0)
        {
            return false;
        }

        string prefix = normalizedColumn[..roleFieldIndex].TrimEnd('_');
        foreach (string candidateSide in new[] { "BL", "GR" })
        {
            if (string.Equals(prefix, candidateSide, StringComparison.OrdinalIgnoreCase) ||
                prefix.EndsWith("_" + candidateSide, StringComparison.OrdinalIgnoreCase))
            {
                side = candidateSide;
                return true;
            }
        }

        return false;
    }

    private static void AddCftPlayerViewRoleTextureCandidate(
        Dictionary<string, Dictionary<CftPlayerViewTextureRole, HashSet<string>>> candidatesByContext,
        string context,
        CftPlayerViewTextureRole role,
        string candidate)
    {
        string contextKey = NormalizeLooseKey(context);
        if (contextKey.Length == 0)
        {
            return;
        }

        if (!candidatesByContext.TryGetValue(contextKey, out Dictionary<CftPlayerViewTextureRole, HashSet<string>>? roleCandidates))
        {
            roleCandidates = new Dictionary<CftPlayerViewTextureRole, HashSet<string>>();
            candidatesByContext.Add(contextKey, roleCandidates);
        }

        if (!roleCandidates.TryGetValue(role, out HashSet<string>? textureCandidates))
        {
            textureCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roleCandidates.Add(role, textureCandidates);
        }

        textureCandidates.Add(candidate);
    }

    private static Dictionary<string, CftPlayerViewRoleSlotLayouts> BuildCftPlayerViewRoleSlotLayouts(
        IReadOnlySet<string> typedLtcPlayerViewModels,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        Dictionary<string, LithTechModelDocument?> decodedModels)
    {
        var layoutsByModelPath = new Dictionary<string, CftPlayerViewRoleSlotLayouts>(StringComparer.OrdinalIgnoreCase);
        foreach (string modelReference in typedLtcPlayerViewModels)
        {
            string modelPath = NormalizeModelResourcePath(modelReference);
            if (!modelPath.StartsWith("Models/PLAYERVIEW/PV-", StringComparison.OrdinalIgnoreCase) ||
                !modelItemsByPlayerViewRolePath.TryGetValue(modelPath, out ExplorerItem[]? modelItems) ||
                modelItems.Length == 0)
            {
                continue;
            }

            var documents = new List<LithTechModelDocument>(modelItems.Length);
            foreach (ExplorerItem modelItem in modelItems)
            {
                LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                    modelItem,
                    GetModelResourcePath(modelItem),
                    decodedModels);
                if (document is null)
                {
                    documents.Clear();
                    break;
                }

                documents.Add(document);
            }

            if (documents.Count != modelItems.Length ||
                !documents.SelectMany(document => document.Meshes).Any(mesh =>
                    mesh.Name.Contains("hand", StringComparison.OrdinalIgnoreCase) ||
                    mesh.Name.Contains("arm", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            CftPlayerViewRoleSlotMap handOnly = CftPlayerViewRoleSlotResolver.Resolve(documents, true, false);
            CftPlayerViewRoleSlotMap armOnly = CftPlayerViewRoleSlotResolver.Resolve(documents, false, true);
            CftPlayerViewRoleSlotMap handAndArm = CftPlayerViewRoleSlotResolver.Resolve(documents, true, true);
            if (handOnly.HandSlots.Count == 0 && handOnly.ArmSlots.Count == 0 &&
                armOnly.HandSlots.Count == 0 && armOnly.ArmSlots.Count == 0 &&
                handAndArm.HandSlots.Count == 0 && handAndArm.ArmSlots.Count == 0)
            {
                continue;
            }

            layoutsByModelPath[modelPath] = new CftPlayerViewRoleSlotLayouts(handOnly, armOnly, handAndArm);
        }

        return layoutsByModelPath;
    }

    private static IReadOnlyList<string> ResolveCftPlayerViewRoleTextureContextSlot(
        ModelTextureSlotDictionary dictionary,
        string modelPath,
        string contextKey,
        int slotIndex)
    {
        if (!dictionary.CftPlayerViewRoleSlotsByModelPath.TryGetValue(
                modelPath,
                out CftPlayerViewRoleSlotLayouts? layouts) ||
            !dictionary.CftPlayerViewRoleTexturesByContext.TryGetValue(
                contextKey,
                out Dictionary<CftPlayerViewTextureRole, HashSet<string>>? roleCandidates))
        {
            return [];
        }

        bool hasHand = TryGetUniqueCftPlayerViewRoleTextureCandidate(
            roleCandidates,
            CftPlayerViewTextureRole.Hand,
            out string handTexture);
        bool hasArm = TryGetUniqueCftPlayerViewRoleTextureCandidate(
            roleCandidates,
            CftPlayerViewTextureRole.Arm,
            out string armTexture);
        if (!hasHand && !hasArm)
        {
            return [];
        }

        CftPlayerViewRoleSlotMap roleSlots = layouts.ForReferences(hasHand, hasArm);
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasHand && roleSlots.HandSlots.Contains(slotIndex))
        {
            matches.Add(handTexture);
        }

        if (hasArm && roleSlots.ArmSlots.Contains(slotIndex))
        {
            matches.Add(armTexture);
        }

        return matches.Count == 1 ? matches.ToArray() : [];
    }

    private static bool TryGetUniqueCftPlayerViewRoleTextureCandidate(
        IReadOnlyDictionary<CftPlayerViewTextureRole, HashSet<string>> candidates,
        CftPlayerViewTextureRole role,
        out string texturePath)
    {
        texturePath = string.Empty;
        if (!candidates.TryGetValue(role, out HashSet<string>? roleCandidates) || roleCandidates.Count != 1)
        {
            return false;
        }

        string candidate = roleCandidates.Single();
        if (candidate.StartsWith("\u0001", StringComparison.Ordinal))
        {
            return false;
        }

        texturePath = candidate;
        return true;
    }

    private static bool TryGetCharacterCftBodySide(
        string tablePath,
        CftModelTextureMapping mapping,
        CftTextureReference texture,
        out string side)
    {
        side = string.Empty;
        if (!string.Equals(Path.GetFileName(tablePath), "CharacterType.CFT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string modelColumn = mapping.ModelColumn;
        const string battleRoyalPrefix = "_BattleRoyal_";
        if (modelColumn.StartsWith(battleRoyalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            modelColumn = "_" + modelColumn[battleRoyalPrefix.Length..];
        }

        const string modelSuffix = "ModelFile";
        int suffixIndex = modelColumn.LastIndexOf(modelSuffix, StringComparison.OrdinalIgnoreCase);
        if (!modelColumn.StartsWith('_') || suffixIndex < 0 ||
            modelColumn[(suffixIndex + modelSuffix.Length)..].Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        string candidateSide = modelColumn[1..suffixIndex];
        if (candidateSide is not ("GR" or "BL") ||
            !string.Equals(texture.Column, $"_{candidateSide}TextureFile", StringComparison.OrdinalIgnoreCase) ||
            !NormalizeModelResourcePath(mapping.ModelReference).StartsWith(
                "Models/Character/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        side = candidateSide;
        return true;
    }

    private static string[] GetLtcContextKeys(
        ExplorerItem ltcItem,
        CrossFireLtcModelTextureMapping mapping)
    {
        string tablePath = NormalizeKey(ltcItem.OutputRelativePath);
        return mapping.IdentityAliases
            .Append($"{tablePath}#{mapping.RowIndex.ToString(CultureInfo.InvariantCulture)}")
            .Where(context => NormalizeLooseKey(context).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetLtcTextureFieldOrdinal(string fieldName)
    {
        int suffixStart = fieldName.Length;
        while (suffixStart > 0 && char.IsAsciiDigit(fieldName[suffixStart - 1]))
        {
            suffixStart--;
        }

        if (suffixStart == fieldName.Length)
        {
            return 1;
        }

        return int.TryParse(
            fieldName[suffixStart..],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int ordinal) && ordinal > 0
            ? ordinal
            : int.MaxValue;
    }

    private static Dictionary<string, Dictionary<string, LtcPhysicalModelTextureBinding>> BuildLtcPhysicalBindingAliases(
        IReadOnlyList<ExplorerItem> ltcItems,
        IReadOnlyDictionary<string, IReadOnlyList<CrossFireLtcModelTextureMapping>> ltcMappingCache,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPath,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName,
        Dictionary<string, LithTechModelDocument?> decodedModels)
    {
        var aliases = new Dictionary<string, Dictionary<string, LtcPhysicalModelTextureBinding>>(
            StringComparer.OrdinalIgnoreCase);
        var unresolvedAliasKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem ltcItem in ltcItems)
        {
            string cacheKey = ltcItem.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
                ? "md5:" + archiveHash
                : NormalizeKey(ltcItem.OutputRelativePath);
            if (!ltcMappingCache.TryGetValue(cacheKey, out IReadOnlyList<CrossFireLtcModelTextureMapping>? mappings))
            {
                continue;
            }

            foreach (CrossFireLtcModelTextureMapping mapping in mappings)
            {
                string logicalModelPath = GetLogicalModelResourcePath(NormalizeKey(mapping.ModelReference));
                if (mapping.IdentityAliases.Count == 0 ||
                    mapping.TextureFieldReferences.Count == 0 ||
                    logicalModelPath.Length == 0 || modelItemsByPath.ContainsKey(logicalModelPath) ||
                    mapping.TextureFieldReferences.Any(textureReference =>
                        TryGetLtcPlayerViewTextureOrdinal(mapping.ModelField, textureReference.FieldName, out _)))
                {
                    continue;
                }

                CrossFireLtcTextureFieldReference[] textureReferences = mapping.TextureFieldReferences
                    .OrderBy(reference => GetLtcTextureFieldOrdinal(reference.FieldName))
                    .ThenBy(reference => reference.FieldName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (CrossFireLtcTextureFieldReference textureReference in textureReferences)
                {
                    foreach (string identityAlias in mapping.IdentityAliases.Where(alias => !string.IsNullOrWhiteSpace(alias)))
                    {
                        unresolvedAliasKeys.Add(GetLtcPhysicalBindingAliasKey(mapping, textureReference, identityAlias));
                    }
                }
            }
        }

        foreach (CrossFireLtcModelTextureMapping mapping in ltcMappingCache.Values
                     .SelectMany(mappings => mappings)
                     .DistinctBy(
                         mapping => string.Join(
                             '\u001f',
                             mapping.LegacyDeduplicationKey,
                             string.Join('\u001e', mapping.IdentityAliases)),
                         StringComparer.OrdinalIgnoreCase))
        {
            if (mapping.IdentityAliases.Count == 0 ||
                mapping.TextureFieldReferences.Count == 0 ||
                mapping.TextureFieldReferences.Any(textureReference =>
                    TryGetLtcPlayerViewTextureOrdinal(mapping.ModelField, textureReference.FieldName, out _)))
            {
                continue;
            }

            string logicalModelPath = GetLogicalModelResourcePath(NormalizeKey(mapping.ModelReference));
            string modelPath = NormalizeExtensionlessModelReference(mapping.ModelReference);
            if (logicalModelPath.Length == 0 || modelPath.Length == 0 ||
                !modelItemsByPath.TryGetValue(logicalModelPath, out ExplorerItem[]? modelItems))
            {
                continue;
            }

            CrossFireLtcTextureFieldReference[] textureReferences = mapping.TextureFieldReferences
                .OrderBy(reference => GetLtcTextureFieldOrdinal(reference.FieldName))
                .ThenBy(reference => reference.FieldName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!mapping.IdentityAliases.Any(identityAlias => textureReferences.Any(textureReference =>
                    unresolvedAliasKeys.Contains(GetLtcPhysicalBindingAliasKey(mapping, textureReference, identityAlias)))) ||
                !TryGetConsistentLtcModelSlots(modelItems, decodedModels, out int[] mappedSlots) ||
                mappedSlots.Length != textureReferences.Length)
            {
                continue;
            }

            for (int index = 0; index < mappedSlots.Length; index++)
            {
                CrossFireLtcTextureFieldReference textureReference = textureReferences[index];
                string? existingTexturePath = ResolveExistingTextureReference(
                    textureReference.Path,
                    resourcePaths,
                    texturePathsByBaseName);
                if (existingTexturePath is null)
                {
                    continue;
                }

                var binding = new LtcPhysicalModelTextureBinding(
                    modelPath,
                    mappedSlots[index],
                    existingTexturePath);
                string bindingKey = GetLtcPhysicalBindingValueKey(binding);
                foreach (string identityAlias in mapping.IdentityAliases)
                {
                    if (string.IsNullOrWhiteSpace(identityAlias))
                    {
                        continue;
                    }

                    string aliasKey = GetLtcPhysicalBindingAliasKey(mapping, textureReference, identityAlias);
                    if (!unresolvedAliasKeys.Contains(aliasKey))
                    {
                        continue;
                    }

                    if (!aliases.TryGetValue(aliasKey, out Dictionary<string, LtcPhysicalModelTextureBinding>? candidates))
                    {
                        candidates = new Dictionary<string, LtcPhysicalModelTextureBinding>(StringComparer.OrdinalIgnoreCase);
                        aliases.Add(aliasKey, candidates);
                    }

                    candidates[bindingKey] = binding;
                }
            }
        }

        return aliases;
    }

    private static bool TryGetConsistentLtcModelSlots(
        IReadOnlyList<ExplorerItem> modelItems,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        out int[] slots)
    {
        int[]? mappedSlots = null;
        foreach (ExplorerItem modelItem in modelItems)
        {
            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                modelItem,
                GetModelResourcePath(modelItem),
                decodedModels);
            if (document is null)
            {
                slots = [];
                return false;
            }

            int[] currentSlots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (currentSlots.Length == 0 ||
                mappedSlots is not null && !mappedSlots.SequenceEqual(currentSlots))
            {
                slots = [];
                return false;
            }

            mappedSlots ??= currentSlots;
        }

        slots = mappedSlots ?? [];
        return slots.Length > 0;
    }

    private static bool TryGetConsistentCharacterHeadTextureSlot(
        IReadOnlyList<ExplorerItem> modelItems,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        out int headTextureSlot)
    {
        int? expectedHeadTextureSlot = null;
        int[]? expectedTextureSlots = null;
        foreach (ExplorerItem modelItem in modelItems)
        {
            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                modelItem,
                GetModelResourcePath(modelItem),
                decodedModels);
            if (document is null)
            {
                headTextureSlot = -1;
                return false;
            }

            int[] currentTextureSlots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (currentTextureSlots.Length == 0 ||
                expectedTextureSlots is not null && !expectedTextureSlots.SequenceEqual(currentTextureSlots))
            {
                headTextureSlot = -1;
                return false;
            }

            expectedTextureSlots ??= currentTextureSlots;
            LithTechMesh[] headOrFaceMeshes = document.Meshes
                .Where(IsCharacterHeadOrFaceMesh)
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Count > 0)
                .ToArray();
            int[] roleTextureSlots = headOrFaceMeshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (headOrFaceMeshes.Length == 0 ||
                roleTextureSlots.Length != 1 ||
                roleTextureSlots[0] is <= 0 or >= 31 ||
                document.Meshes.Any(mesh =>
                    (mesh.TextureSlotIndices ?? []).Contains(roleTextureSlots[0]) &&
                    !IsCharacterHeadOrFaceMesh(mesh)) ||
                expectedHeadTextureSlot.HasValue && expectedHeadTextureSlot.Value != roleTextureSlots[0])
            {
                headTextureSlot = -1;
                return false;
            }

            expectedHeadTextureSlot ??= roleTextureSlots[0];
        }

        if (!expectedHeadTextureSlot.HasValue)
        {
            headTextureSlot = -1;
            return false;
        }

        headTextureSlot = expectedHeadTextureSlot.Value;
        return true;
    }

    private static bool IsCharacterHeadOrFaceMesh(LithTechMesh mesh)
    {
        return mesh.Name.Contains("head", StringComparison.OrdinalIgnoreCase) ||
               mesh.Name.Contains("face", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveCharacterFaceTextureReference(
        string bodyTexturePath,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName,
        out string faceTexturePath)
    {
        faceTexturePath = string.Empty;
        string normalizedBodyPath = NormalizeKey(bodyTexturePath);
        if (!normalizedBodyPath.StartsWith("ModelTextures/Character/", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(normalizedBodyPath).Equals(".dtx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(normalizedBodyPath);
        var faceStem = new StringBuilder(stem.Length);
        bool replacedBodyToken = false;
        for (int index = 0; index < stem.Length;)
        {
            bool isBodyToken = index + 4 <= stem.Length &&
                               stem.AsSpan(index, 4).Equals("BODY", StringComparison.OrdinalIgnoreCase) &&
                               (index == 0 || !char.IsLetterOrDigit(stem[index - 1])) &&
                               (index + 4 == stem.Length || !char.IsLetterOrDigit(stem[index + 4]));
            if (isBodyToken)
            {
                if (replacedBodyToken)
                {
                    return false;
                }

                faceStem.Append("FACE");
                index += 4;
                replacedBodyToken = true;
                continue;
            }

            faceStem.Append(stem[index]);
            index++;
        }

        if (!replacedBodyToken)
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(normalizedBodyPath)?.Replace('\\', '/');
        string candidate = (string.IsNullOrWhiteSpace(directory) ? string.Empty : directory + "/") +
                           faceStem + Path.GetExtension(normalizedBodyPath);
        string? existingFacePath = ResolveExistingTextureReference(
            candidate,
            resourcePaths,
            texturePathsByBaseName);
        if (string.IsNullOrWhiteSpace(existingFacePath))
        {
            return false;
        }

        faceTexturePath = existingFacePath;
        return true;
    }

    private static bool TryResolveLtcPhysicalBindingAliases(
        CrossFireLtcModelTextureMapping mapping,
        IReadOnlyList<CrossFireLtcTextureFieldReference> textureReferences,
        IReadOnlyDictionary<string, Dictionary<string, LtcPhysicalModelTextureBinding>> aliases,
        out LtcPhysicalModelTextureBinding[] bindings)
    {
        var resolved = new List<LtcPhysicalModelTextureBinding>(textureReferences.Count);
        foreach (CrossFireLtcTextureFieldReference textureReference in textureReferences)
        {
            var candidates = new Dictionary<string, LtcPhysicalModelTextureBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (string identityAlias in mapping.IdentityAliases)
            {
                string aliasKey = GetLtcPhysicalBindingAliasKey(mapping, textureReference, identityAlias);
                if (aliases.TryGetValue(aliasKey, out Dictionary<string, LtcPhysicalModelTextureBinding>? matches))
                {
                    foreach ((string bindingKey, LtcPhysicalModelTextureBinding binding) in matches)
                    {
                        candidates[bindingKey] = binding;
                    }
                }
            }

            if (candidates.Count != 1)
            {
                bindings = [];
                return false;
            }

            resolved.Add(candidates.Values.Single());
        }

        if (resolved.Count == 0 ||
            resolved.Select(binding => binding.ModelPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            resolved.Select(binding => binding.SlotIndex).Distinct().Count() != resolved.Count)
        {
            bindings = [];
            return false;
        }

        bindings = resolved.ToArray();
        return true;
    }

    private static string GetLtcPhysicalBindingAliasKey(
        CrossFireLtcModelTextureMapping mapping,
        CrossFireLtcTextureFieldReference textureReference,
        string identityAlias)
    {
        string modelStem = Path.GetFileNameWithoutExtension(NormalizeKey(mapping.ModelReference))
            .Normalize(NormalizationForm.FormKC);
        string textureStem = Path.GetFileNameWithoutExtension(NormalizeKey(textureReference.Path))
            .Normalize(NormalizationForm.FormKC);
        return string.Join(
            '\u001f',
            mapping.RowType.Trim(),
            mapping.ModelField.Trim(),
            modelStem,
            identityAlias.Trim().Normalize(NormalizationForm.FormKC),
            textureReference.FieldName.Trim(),
            textureStem);
    }

    private static string GetLtcPhysicalBindingValueKey(LtcPhysicalModelTextureBinding binding)
    {
        return string.Join(
            '\u001f',
            NormalizeKey(binding.ModelPath),
            binding.SlotIndex.ToString(CultureInfo.InvariantCulture),
            NormalizeKey(binding.TexturePath));
    }

    private static IReadOnlyList<string> GetWeaponItemContextAliases(
        CrossFireLtcModelTextureMapping mapping,
        CrossFireWeaponPreviewItemAliasIndex itemAliases)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string identityAlias in mapping.IdentityAliases)
        {
            const string weaponIndexPrefix = "WeaponIndex:";
            if (!identityAlias.StartsWith(weaponIndexPrefix, StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(
                    identityAlias[weaponIndexPrefix.Length..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out uint weaponIndex) ||
                !itemAliases.ByWeaponIndex.TryGetValue(weaponIndex, out IReadOnlyList<string>? weaponItemAliases))
            {
                continue;
            }

            aliases.UnionWith(weaponItemAliases);
        }

        if (mapping.LegacyMappingIndex >= 0 &&
            itemAliases.ByLtcLegacyMappingIndex.TryGetValue((uint)mapping.LegacyMappingIndex, out IReadOnlyList<string>? mappingItemAliases))
        {
            aliases.UnionWith(mappingItemAliases);
        }

        return aliases.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (CrossFireWeaponPreviewItemAliasIndex Aliases, IReadOnlyList<WeaponPreviewSkinAsset> SkinAssets) BuildWeaponPreviewItemAliases(
        IReadOnlyList<ExplorerItem> scriptItems,
        IReadOnlySet<uint> ltcWeaponIndexIds,
        IEnumerable<IReadOnlyList<CrossFireLtcModelTextureMapping>> ltcTables)
    {
        var previews = new List<CrossFireWeaponPreviewEntry>();
        var models = new List<CrossFireWeaponModelEntry>();
        var skinAssets = new List<WeaponPreviewSkinAsset>();
        foreach (ExplorerItem item in scriptItems)
        {
            byte[]? data = TryReadItemBytes(item, 8 * 1024 * 1024);
            if (data is null)
            {
                continue;
            }

            string resourcePath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
            if (CrossFireWeaponPreviewModelIndexDecoder.TryDecodePreview(data, resourcePath, out CrossFireWeaponPreviewEntry? preview) &&
                preview is not null)
            {
                previews.Add(preview);
                string normalizedResourcePath = resourcePath.Replace('\\', '/');
                if (normalizedResourcePath.Contains("/WeaponPreview/Skin/", StringComparison.OrdinalIgnoreCase) &&
                    preview.ItemId > 0 && preview.ModelGroupId > 0)
                {
                    skinAssets.Add(new WeaponPreviewSkinAsset(
                        Path.GetFileNameWithoutExtension(normalizedResourcePath),
                        preview.ItemId,
                        preview.ModelGroupId,
                        preview.PreviewSlot,
                        preview.ModelId));
                }
                continue;
            }

            if (CrossFireWeaponPreviewModelIndexDecoder.TryDecodeWeaponModelTable(data, resourcePath, out IReadOnlyList<CrossFireWeaponModelEntry> decodedModels))
            {
                models.AddRange(decodedModels);
            }
        }

        IReadOnlySet<uint> ltcWeaponLegacyMappingIndexIds = GetUnambiguousWeaponLtcLegacyMappingIndices(ltcTables);
        CrossFireWeaponPreviewItemAliasIndex aliases = CrossFireWeaponPreviewModelIndexDecoder.BuildItemAliases(
            previews,
            models,
            ltcWeaponIndexIds,
            ltcWeaponLegacyMappingIndexIds);
        return (aliases, skinAssets);
    }

    private static void AddWeaponPreviewVariantTextureContextBindings(
        IReadOnlyList<WeaponPreviewSkinAsset> skinAssets,
        IReadOnlyList<ExplorerItem> resourceItems,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPath,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directByModelContext,
        Dictionary<string, Dictionary<int, string?>> variantByModelContext,
        HashSet<string> modelsWithSlotSpecificBindings,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var signatureCache = new Dictionary<(string ModelPath, int Slot, string MeshName), string?>();
        foreach (IGrouping<string, WeaponPreviewSkinAsset> group in skinAssets
                     .Where(asset => asset.ItemId > 0 && asset.ModelGroupId > 0 && asset.ModelStem.Length > 0)
                     .GroupBy(asset => NormalizeLooseKey(asset.ModelStem), StringComparer.OrdinalIgnoreCase))
        {
            WeaponPreviewSkinAsset[] matchingAssets = group
                .DistinctBy(asset => (asset.ItemId, asset.ModelGroupId, asset.PreviewSlot, asset.ModelId))
                .ToArray();
            if (matchingAssets.Length != 1)
            {
                continue;
            }

            WeaponPreviewSkinAsset asset = matchingAssets[0];
            string previewContext = NormalizeLooseKey("preview:" + asset.ModelGroupId.ToString(CultureInfo.InvariantCulture) + ":" +
                                                       asset.ItemId.ToString(CultureInfo.InvariantCulture));
            string targetModelReference = $"Models/PLAYERVIEW/PV-{asset.ModelStem}.ltb";
            string targetModelPath = NormalizeExtensionlessModelReference(targetModelReference);
            string targetLogicalPath = GetLogicalModelResourcePath(targetModelReference);
            if (targetModelPath.Length == 0 || targetLogicalPath.Length == 0 ||
                !modelItemsByPath.TryGetValue(targetLogicalPath, out ExplorerItem[]? targetCopies) || targetCopies.Length == 0)
            {
                continue;
            }

            string variantTextureReference = $"ModelTextures/PLAYERVIEW/PV-{asset.ModelStem}.dtx";
            string? variantTexturePath = ResolveExistingTextureReference(
                variantTextureReference,
                resourcePaths,
                texturePathsByBaseName);
            string variantTextureKey = GetTextureBaseNameKey(
                Path.GetFileNameWithoutExtension(variantTextureReference),
                Path.GetExtension(variantTextureReference));
            if (variantTexturePath is null ||
                !string.Equals(variantTexturePath, NormalizeKey(variantTextureReference), StringComparison.OrdinalIgnoreCase) ||
                !texturePathsByBaseName.TryGetValue(variantTextureKey, out IReadOnlyList<string>? texturePaths) ||
                texturePaths.Count != 1 ||
                !string.Equals(texturePaths[0], variantTexturePath, StringComparison.OrdinalIgnoreCase) ||
                !HasConsistentPhysicalResourceCopies(resourceItems, variantTexturePath))
            {
                continue;
            }

            var candidatesBySlot = new Dictionary<int, HashSet<string>>();
            string[] sourceModelPaths = directByModelContext.Keys
                .Select(key =>
                {
                    int separator = key.IndexOf('\0');
                    return separator > 0 ? key[..separator] : string.Empty;
                })
                .Where(sourcePath => sourcePath.StartsWith("Models/PLAYERVIEW/PV-", StringComparison.OrdinalIgnoreCase) &&
                                     targetModelPath.StartsWith(sourcePath + "_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string sourceModelPath in sourceModelPaths)
            {
                string sourceModelName = Path.GetFileName(sourceModelPath);
                if (sourceModelName.Length <= 3)
                {
                    continue;
                }

                string weaponMeshName = sourceModelName[3..];
                string sourceLogicalPath = GetLogicalModelResourcePath(sourceModelPath + ".ltb");
                if (!modelItemsByPath.TryGetValue(sourceLogicalPath, out ExplorerItem[]? sourceCopies) || sourceCopies.Length == 0)
                {
                    continue;
                }

                int[] sourceSlots = directByModelContext
                    .Where(binding => binding.Key.StartsWith(sourceModelPath + "\0", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(binding => binding.Value
                        .Where(slotBinding => !string.IsNullOrWhiteSpace(slotBinding.Value))
                        .Select(slotBinding => slotBinding.Key))
                    .Distinct()
                    .ToArray();
                foreach (int slot in sourceSlots)
                {
                    if (slot is < 0 or > MaxTextureSlotIndex ||
                        directByModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetPathSlots) && targetPathSlots.ContainsKey(slot) ||
                        HasContextSlot(directByModelContext, targetModelPath, previewContext, slot))
                    {
                        continue;
                    }

                    string? sourceSignature = GetWeaponMeshTextureSlotSignature(
                        sourceCopies,
                        decodedModels,
                        slot,
                        weaponMeshName,
                        signatureCache,
                        sourceModelPath);
                    string? targetSignature = GetWeaponMeshTextureSlotSignature(
                        targetCopies,
                        decodedModels,
                        slot,
                        weaponMeshName,
                        signatureCache,
                        targetModelPath);
                    if (sourceSignature is null || targetSignature is null ||
                        !string.Equals(sourceSignature, targetSignature, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!candidatesBySlot.TryGetValue(slot, out HashSet<string>? sourcePaths))
                    {
                        sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        candidatesBySlot.Add(slot, sourcePaths);
                    }

                    sourcePaths.Add(sourceModelPath);
                }
            }

            foreach (KeyValuePair<int, HashSet<string>> candidate in candidatesBySlot)
            {
                if (candidate.Value.Count != 1 ||
                    directByModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetPathSlots) && targetPathSlots.ContainsKey(candidate.Key) ||
                    HasContextSlot(directByModelContext, targetModelPath, previewContext, candidate.Key))
                {
                    continue;
                }

                AddModelTextureContextSlotMapping(
                    variantByModelContext,
                    targetModelPath,
                    "preview:" + asset.ModelGroupId.ToString(CultureInfo.InvariantCulture) + ":" +
                    asset.ItemId.ToString(CultureInfo.InvariantCulture),
                    candidate.Key,
                    variantTexturePath);
                modelsWithSlotSpecificBindings.Add(targetModelPath);
            }
        }

        static bool HasContextSlot(
            IReadOnlyDictionary<string, Dictionary<int, string?>> contexts,
            string modelPath,
            string contextKey,
            int slot)
        {
            return contexts.TryGetValue(modelPath + "\0" + contextKey, out Dictionary<int, string?>? slots) &&
                   slots.ContainsKey(slot);
        }
    }

    private static Dictionary<string, Dictionary<int, string?>> BuildWeaponPreviewVariantTexturePathBindings(
        IReadOnlyDictionary<string, Dictionary<int, string?>> variantByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> directByModelContext)
    {
        var candidatesByModelSlot = new Dictionary<string, List<(string ModelPath, int Slot, string? Texture)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<int, string?>> modelContext in variantByModelContext)
        {
            int separator = modelContext.Key.IndexOf('\0');
            if (separator <= 0)
            {
                continue;
            }

            string modelPath = modelContext.Key[..separator];
            foreach (KeyValuePair<int, string?> slot in modelContext.Value)
            {
                string key = modelPath + "\0" + slot.Key.ToString(CultureInfo.InvariantCulture);
                if (!candidatesByModelSlot.TryGetValue(key, out List<(string ModelPath, int Slot, string? Texture)>? candidates))
                {
                    candidates = [];
                    candidatesByModelSlot.Add(key, candidates);
                }

                candidates.Add((modelPath, slot.Key, slot.Value));
            }
        }

        var result = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (List<(string ModelPath, int Slot, string? Texture)> candidates in candidatesByModelSlot.Values)
        {
            if (candidates.Count != 1)
            {
                continue;
            }

            (string modelPath, int slot, string? texture) = candidates[0];
            if (string.IsNullOrWhiteSpace(texture) ||
                directByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directPathSlots) && directPathSlots.ContainsKey(slot) ||
                directByModelContext.Any(binding =>
                    binding.Key.StartsWith(modelPath + "\0", StringComparison.OrdinalIgnoreCase) &&
                    binding.Value.ContainsKey(slot)))
            {
                continue;
            }

            if (!result.TryGetValue(modelPath, out Dictionary<int, string?>? pathSlots))
            {
                pathSlots = [];
                result.Add(modelPath, pathSlots);
            }

            pathSlots[slot] = texture;
        }

        return result;
    }

    private static string? GetWeaponMeshTextureSlotSignature(
        IReadOnlyList<ExplorerItem> modelCopies,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        int slotIndex,
        string expectedMeshName,
        Dictionary<(string ModelPath, int Slot, string MeshName), string?> signatureCache,
        string modelPath)
    {
        var cacheKey = (modelPath, slotIndex, expectedMeshName);
        if (signatureCache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        string? signature = null;
        foreach (ExplorerItem copy in modelCopies)
        {
            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                copy,
                GetModelResourcePath(copy),
                decodedModels);
            if (document is null)
            {
                return signatureCache[cacheKey] = null;
            }

            LithTechMesh[] slotMeshes = document.Meshes
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                .ToArray();
            if (slotMeshes.Length != 1 ||
                !string.Equals(slotMeshes[0].Name, expectedMeshName, StringComparison.OrdinalIgnoreCase) ||
                slotMeshes[0].TextureSlotIndices is not { Count: 1 })
            {
                return signatureCache[cacheKey] = null;
            }

            IReadOnlyDictionary<int, string>? signatures = CftPlayerViewArmEquivalence
                .GetConsistentTextureSlotGeometryUvSignatures([document]);
            if (signatures is null || !signatures.TryGetValue(slotIndex, out string? current) ||
                signature is not null && !string.Equals(signature, current, StringComparison.Ordinal))
            {
                return signatureCache[cacheKey] = null;
            }

            signature = current;
        }

        return signatureCache[cacheKey] = signature;
    }

    private static bool HasConsistentPhysicalResourceCopies(
        IReadOnlyList<ExplorerItem> resourceItems,
        string resourcePath)
    {
        string normalizedPath = NormalizeKey(resourcePath);
        ExplorerItem[] copies = resourceItems
            .Where(item => string.Equals(
                NormalizeKey(item.ArchiveFile?.FullPath ?? item.OutputRelativePath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (copies.Length == 0)
        {
            return false;
        }

        string? expectedHash = null;
        foreach (ExplorerItem copy in copies)
        {
            byte[]? bytes = TryReadItemBytes(copy, 64 * 1024 * 1024);
            if (bytes is null)
            {
                return false;
            }

            string hash = Convert.ToHexString(MD5.HashData(bytes));

            if (expectedHash is not null && !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            expectedHash = hash;
        }

        return expectedHash is not null;
    }

    private static void AddVerifiedCftModelTextureSlotAliases(
        IReadOnlyList<ExplorerItem> resourceItems,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPath,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        Dictionary<string, Dictionary<int, string?>> byModelPath,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        var layoutCache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (VerifiedCftModelTextureSlotAlias alias in VerifiedCftModelTextureSlotAliases)
        {
            string targetModelPath = NormalizeExtensionlessModelReference(alias.TargetModelPath);
            string sourceModelPath = NormalizeExtensionlessModelReference(alias.SourceModelPath);
            string targetKey = GetLogicalModelResourcePath(targetModelPath + ".ltb");
            string sourceKey = GetLogicalModelResourcePath(sourceModelPath + ".ltb");
            if (targetModelPath.Length == 0 || sourceModelPath.Length == 0 ||
                !modelItemsByPath.TryGetValue(targetKey, out ExplorerItem[]? targetCopies) || targetCopies.Length == 0 ||
                !modelItemsByPath.TryGetValue(sourceKey, out ExplorerItem[]? sourceCopies) || sourceCopies.Length == 0 ||
                byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetSlots) &&
                targetSlots.ContainsKey(alias.TargetSlotIndex) ||
                !byModelPath.TryGetValue(sourceModelPath, out Dictionary<int, string?>? sourceSlots) ||
                !sourceSlots.TryGetValue(alias.SourceSlotIndex, out string? texturePath) ||
                string.IsNullOrWhiteSpace(texturePath) ||
                !HasConsistentPhysicalResourceCopies(resourceItems, texturePath))
            {
                continue;
            }

            string[]? targetLayouts = GetVerifiedTextureSlotMeshLayouts(
                targetModelPath,
                targetCopies,
                alias.TargetSlotIndex,
                alias.MeshName);
            string[]? sourceLayouts = GetVerifiedTextureSlotMeshLayouts(
                sourceModelPath,
                sourceCopies,
                alias.SourceSlotIndex,
                alias.MeshName);
            if (targetLayouts is null || sourceLayouts is null || sourceLayouts.Length == 0 ||
                targetLayouts.Length == 0 ||
                targetLayouts.Any(layout => !sourceLayouts.Contains(layout, StringComparer.Ordinal)))
            {
                continue;
            }

            AddModelTextureSlotMapping(byModelPath, targetModelPath, alias.TargetSlotIndex, texturePath);
            modelsWithSlotSpecificBindings.Add(targetModelPath);
        }

        string[]? GetVerifiedTextureSlotMeshLayouts(
            string modelPath,
            IReadOnlyList<ExplorerItem> copies,
            int slotIndex,
            string expectedMeshName)
        {
            string cacheKey = $"{modelPath}\0{slotIndex}\0{expectedMeshName}";
            if (layoutCache.TryGetValue(cacheKey, out string[]? cached))
            {
                return cached;
            }

            string[]? expectedLayouts = null;
            foreach (ExplorerItem copy in copies)
            {
                LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                    copy,
                    GetModelResourcePath(copy),
                    decodedModels);
                if (document is null)
                {
                    return layoutCache[cacheKey] = null;
                }

                LithTechMesh[] slotMeshes = document.Meshes
                    .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slotIndex))
                    .ToArray();
                if (slotMeshes.Length == 0)
                {
                    return layoutCache[cacheKey] = null;
                }

                var layouts = new List<string>(slotMeshes.Length);
                foreach (LithTechMesh mesh in slotMeshes)
                {
                    if (!mesh.HasAuthoritativeTextureBindings ||
                        mesh.TextureSlotIndices is not { Count: 1 } meshSlots || meshSlots[0] != slotIndex ||
                        mesh.RenderStyleIndex is not int renderStyleIndex ||
                        !string.IsNullOrWhiteSpace(mesh.TexturePath) ||
                        !string.Equals(
                            NormalizeVerifiedSandDragonMeshName(mesh.Name),
                            expectedMeshName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return layoutCache[cacheKey] = null;
                    }

                    string? uvSignature = GetVerifiedTextureUvTriangleSignature(mesh);
                    if (uvSignature is null)
                    {
                        return layoutCache[cacheKey] = null;
                    }

                    layouts.Add($"{renderStyleIndex}\0{uvSignature}");
                }

                string[] currentLayouts = layouts.Order(StringComparer.Ordinal).ToArray();
                if (expectedLayouts is not null && !expectedLayouts.SequenceEqual(currentLayouts, StringComparer.Ordinal))
                {
                    return layoutCache[cacheKey] = null;
                }

                expectedLayouts ??= currentLayouts;
            }

            return layoutCache[cacheKey] = expectedLayouts;
        }
    }

    private static void AddVerifiedSceneModelTextureSlotAliases(
        IReadOnlyList<ExplorerItem> resourceItems,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPath,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        Dictionary<string, Dictionary<int, string?>> byModelPath,
        HashSet<string> modelsWithSlotSpecificBindings)
    {
        foreach (VerifiedSceneModelTextureSlotAlias alias in VerifiedSceneModelTextureSlotAliases)
        {
            string targetModelPath = NormalizeExtensionlessModelReference(alias.TargetModelPath);
            string targetKey = GetLogicalModelResourcePath(targetModelPath + ".ltb");
            string[] expectedMeshNames = alias.MeshNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targetModelPath.Length == 0 || expectedMeshNames.Length != alias.MeshNames.Count ||
                !modelItemsByPath.TryGetValue(targetKey, out ExplorerItem[]? targetCopies) || targetCopies.Length == 0 ||
                byModelPath.TryGetValue(targetModelPath, out Dictionary<int, string?>? targetSlots) &&
                targetSlots.ContainsKey(alias.TargetSlotIndex) ||
                !HasConsistentPhysicalResourceCopies(resourceItems, alias.TexturePath))
            {
                continue;
            }

            var expectedNames = expectedMeshNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[]? expectedLayouts = null;
            foreach (ExplorerItem copy in targetCopies)
            {
                LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                    copy,
                    GetModelResourcePath(copy),
                    decodedModels);
                if (document is null)
                {
                    expectedLayouts = null;
                    break;
                }

                LithTechMesh[] slotMeshes = document.Meshes
                    .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(alias.TargetSlotIndex))
                    .ToArray();
                if (slotMeshes.Length != expectedMeshNames.Length)
                {
                    expectedLayouts = null;
                    break;
                }

                var observedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var layouts = new List<string>(slotMeshes.Length);
                foreach (LithTechMesh mesh in slotMeshes)
                {
                    if (!mesh.HasAuthoritativeTextureBindings ||
                        mesh.TextureSlotIndices is not { Count: 1 } meshSlots || meshSlots[0] != alias.TargetSlotIndex ||
                        mesh.RenderStyleIndex is not int renderStyleIndex ||
                        !string.IsNullOrWhiteSpace(mesh.TexturePath) ||
                        !expectedNames.Contains(mesh.Name) || !observedNames.Add(mesh.Name))
                    {
                        layouts.Clear();
                        break;
                    }

                    string? uvSignature = GetVerifiedTextureUvTriangleSignature(mesh);
                    if (uvSignature is null)
                    {
                        layouts.Clear();
                        break;
                    }

                    layouts.Add($"{mesh.Name.Trim().ToUpperInvariant()}\0{renderStyleIndex}\0{uvSignature}");
                }

                if (layouts.Count != slotMeshes.Length || observedNames.Count != expectedMeshNames.Length)
                {
                    expectedLayouts = null;
                    break;
                }

                string[] currentLayouts = layouts.Order(StringComparer.Ordinal).ToArray();
                if (expectedLayouts is not null && !expectedLayouts.SequenceEqual(currentLayouts, StringComparer.Ordinal))
                {
                    expectedLayouts = null;
                    break;
                }

                expectedLayouts ??= currentLayouts;
            }

            if (expectedLayouts is null || expectedLayouts.Length != expectedMeshNames.Length)
            {
                continue;
            }

            AddModelTextureSlotMapping(byModelPath, targetModelPath, alias.TargetSlotIndex, alias.TexturePath);
            modelsWithSlotSpecificBindings.Add(targetModelPath);
        }
    }

    private static string NormalizeVerifiedSandDragonMeshName(string meshName)
    {
        string normalized = meshName.Trim();
        if (normalized.StartsWith("02_", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("03_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[3..];
        }

        return normalized;
    }

    private static string? GetVerifiedTextureUvTriangleSignature(LithTechMesh mesh)
    {
        IReadOnlyList<LithTechVector2>? uvCoordinates = mesh.TextureCoordinates;
        if (mesh.TextureCoordinateSetCount != 1 || uvCoordinates is null ||
            uvCoordinates.Count != mesh.Vertices.Count || mesh.Vertices.Count == 0 ||
            mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0)
        {
            return null;
        }

        string Number(double value) => (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture);
        var triangles = new List<string>(mesh.TriangleIndices.Count / 3);
        for (int offset = 0; offset < mesh.TriangleIndices.Count; offset += 3)
        {
            var corners = new string[3];
            for (int corner = 0; corner < 3; corner++)
            {
                int index = mesh.TriangleIndices[offset + corner];
                if (index < 0 || index >= uvCoordinates.Count)
                {
                    return null;
                }

                LithTechVector2 uv = uvCoordinates[index];
                if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y))
                {
                    return null;
                }

                corners[corner] = $"{Number(uv.X)},{Number(uv.Y)}";
            }

            Array.Sort(corners, StringComparer.Ordinal);
            triangles.Add(string.Join('|', corners));
        }

        triangles.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', triangles))));
    }

    private static IReadOnlySet<uint> GetUnambiguousWeaponLtcLegacyMappingIndices(
        IEnumerable<IReadOnlyList<CrossFireLtcModelTextureMapping>> ltcTables)
    {
        var signaturesByLegacyMappingIndex = new Dictionary<int, Dictionary<string, bool>>();
        foreach (IReadOnlyList<CrossFireLtcModelTextureMapping> ltcTable in ltcTables)
        {
            foreach (CrossFireLtcModelTextureMapping mapping in ltcTable.Where(mapping => mapping.LegacyMappingIndex >= 0))
            {
                string signature = mapping.LegacyDeduplicationKey;

                if (!signaturesByLegacyMappingIndex.TryGetValue(mapping.LegacyMappingIndex, out Dictionary<string, bool>? signatures))
                {
                    signatures = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    signaturesByLegacyMappingIndex.Add(mapping.LegacyMappingIndex, signatures);
                }

                bool isWeaponMapping = string.Equals(mapping.RowType, "Weapon", StringComparison.OrdinalIgnoreCase);
                signatures[signature] = signatures.TryGetValue(signature, out bool previousIsWeaponMapping)
                    ? previousIsWeaponMapping || isWeaponMapping
                    : isWeaponMapping;
            }
        }

        return signaturesByLegacyMappingIndex
            .Where(pair => pair.Value.Count == 1 && pair.Value.Single().Value)
            .Select(pair => (uint)pair.Key)
            .ToHashSet();
    }

    private static bool TryGetLtcPlayerViewTextureOrdinal(
        string modelField,
        string textureField,
        out int textureOrdinal)
    {
        textureOrdinal = -1;
        string? modelPrefix = LtcPlayerViewModelFieldPrefixes.FirstOrDefault(prefix =>
            modelField.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        const string texturePrefix = "PViewSkinFileName";
        if (modelPrefix is null || !textureField.StartsWith(texturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string modelSuffix = modelField[modelPrefix.Length..];
        string textureSuffix = textureField[texturePrefix.Length..];
        if (modelSuffix.Length > 0)
        {
            // A matching suffix on the model and texture field names denotes a
            // paired model variant, not the second material slot.
            if (!string.Equals(modelSuffix, textureSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            textureOrdinal = 0;
            return true;
        }

        if (textureSuffix.Length == 0)
        {
            textureOrdinal = 0;
            return true;
        }

        if (!int.TryParse(
                textureSuffix,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int numberedField) || numberedField < 2)
        {
            return false;
        }

        textureOrdinal = numberedField - 1;
        return true;
    }

    private static int[] ResolveLtcPlayerViewTextureSlots(
        IReadOnlyList<LithTechModelDocument> documents,
        string modelField,
        int textureOrdinal,
        IReadOnlyList<int> equivalentArmSlots)
    {
        int[]? resolvedSlots = null;
        bool excludeHandArmSlots = modelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase);
        foreach (LithTechModelDocument document in documents)
        {
            int[] modelSlots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
                .Distinct()
                .Order()
                .ToArray();
            if (modelSlots.Length == 0)
            {
                return [];
            }

            var handArmSlots = new HashSet<int>();
            if (excludeHandArmSlots)
            {
                bool hasPairedHandArmSuffixes = document.Meshes.Any(mesh => mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase)) &&
                                                document.Meshes.Any(mesh => mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase));
                foreach (LithTechMesh mesh in document.Meshes)
                {
                    string normalizedName = NormalizeLooseKey(mesh.Name);
                    bool isHand = normalizedName.StartsWith("fviewhand", StringComparison.Ordinal) ||
                                  normalizedName == "hand" ||
                                  hasPairedHandArmSuffixes && mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase);
                    bool isArm = normalizedName.StartsWith("fviewarm", StringComparison.Ordinal) ||
                                 normalizedName.StartsWith("fviewam", StringComparison.Ordinal) ||
                                 normalizedName == "arm" ||
                                 hasPairedHandArmSuffixes && mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase);
                    if (isHand && isArm)
                    {
                        return [];
                    }

                    if (isHand || isArm)
                    {
                        handArmSlots.UnionWith(mesh.TextureSlotIndices ?? []);
                    }
                }

                handArmSlots.UnionWith(equivalentArmSlots);
            }

            int[] eligibleSlots = modelSlots.Except(handArmSlots).Order().ToArray();
            // Numbered LTC player-view fields can describe the same skin on a
            // standalone hand model or the sole weapon slot of a full preview
            // model. In that case, the only eligible LTB slot is unambiguous;
            // keep ordinal selection strict when multiple slots are available.
            int resolvedOrdinal = eligibleSlots.Length == 1 ? 0 : textureOrdinal;
            if (resolvedOrdinal < 0 || resolvedOrdinal >= eligibleSlots.Length)
            {
                return [];
            }

            int[] currentSlots = [eligibleSlots[resolvedOrdinal]];
            if (resolvedSlots is null)
            {
                resolvedSlots = currentSlots;
            }
            else if (!resolvedSlots.SequenceEqual(currentSlots))
            {
                return [];
            }
        }

        return resolvedSlots ?? [];
    }

    private static IReadOnlyDictionary<string, LithTechModelDocument>? TryGetLtcPlayerViewArmDocuments(
        string modelPath,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath,
        Dictionary<string, LithTechModelDocument?> decodedModels)
    {
        string? armModelPath = GetLtcPlayerViewArmModelPath(modelPath, modelItemsByPlayerViewRolePath);
        if (armModelPath is null ||
            !CftPlayerViewArmEquivalence.IsPlayerViewArmModel(armModelPath) ||
            !modelItemsByPlayerViewRolePath.TryGetValue(armModelPath, out ExplorerItem[]? armModelItems) ||
            armModelItems.Length == 0)
        {
            return null;
        }

        var documentsByArchiveRoot = new Dictionary<string, LithTechModelDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (ExplorerItem armModelItem in armModelItems)
        {
            string archiveRoot = GetModelArchiveRoot(GetModelResourcePath(armModelItem));
            if (archiveRoot.Length == 0 || documentsByArchiveRoot.ContainsKey(archiveRoot))
            {
                return null;
            }

            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                armModelItem,
                GetModelResourcePath(armModelItem),
                decodedModels);
            if (document is null)
            {
                return null;
            }

            documentsByArchiveRoot.Add(archiveRoot, document);
        }

        return documentsByArchiveRoot;
    }

    private static string? GetLtcPlayerViewArmModelPath(
        string modelPath,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPlayerViewRolePath)
    {
        string normalizedPath = NormalizeExtensionlessModelReference(modelPath);
        const string playerViewPrefix = "Models/PLAYERVIEW/PV-";
        if (!normalizedPath.StartsWith(playerViewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string modelName = Path.GetFileName(normalizedPath);
        string[] nameParts = modelName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int index = nameParts.Length - 2; index >= 0; index--)
        {
            string side = nameParts[index + 1];
            if (!side.Equals("BL", StringComparison.OrdinalIgnoreCase) &&
                !side.Equals("GR", StringComparison.OrdinalIgnoreCase) &&
                !side.Equals("SP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string armModelPath = $"ArmModel/Arm_{nameParts[index]}_{side}";
            if (modelItemsByPlayerViewRolePath.ContainsKey(armModelPath))
            {
                return armModelPath;
            }
        }

        return null;
    }

    private static int[] ResolveEquivalentPlayerViewArmSlots(
        IReadOnlyList<ExplorerItem> targetItems,
        IReadOnlyList<LithTechModelDocument> targetDocuments,
        IReadOnlyDictionary<string, LithTechModelDocument> armDocumentsByArchiveRoot)
    {
        if (targetItems.Count == 0 || targetItems.Count != targetDocuments.Count)
        {
            return [];
        }

        int[]? resolvedSlots = null;
        for (int documentIndex = 0; documentIndex < targetDocuments.Count; documentIndex++)
        {
            LithTechModelDocument targetDocument = targetDocuments[documentIndex];
            // Pair copies by resource layer so unrelated archive versions cannot supply the arm layout.
            string targetArchiveRoot = GetModelArchiveRoot(GetModelResourcePath(targetItems[documentIndex]));
            if (targetArchiveRoot.Length == 0 ||
                !armDocumentsByArchiveRoot.TryGetValue(targetArchiveRoot, out LithTechModelDocument? armDocument))
            {
                return [];
            }

            CftPlayerViewRoleSlotMap armRoles = CftPlayerViewRoleSlotResolver.Resolve(
                [armDocument],
                hasHandTextureReference: true,
                hasArmTextureReference: true);
            if (armRoles.ArmSlots.Count == 0)
            {
                return [];
            }

            IReadOnlyDictionary<int, string>? targetSignatures =
                CftPlayerViewArmEquivalence.GetConsistentTextureSlotLayoutSignatures([targetDocument]);
            IReadOnlyDictionary<int, string>? armSignatures =
                CftPlayerViewArmEquivalence.GetConsistentTextureSlotLayoutSignatures([armDocument]);
            if (targetSignatures is null || armSignatures is null)
            {
                return [];
            }

            int[] matchingArmSlots = targetSignatures
                .Where(targetSlot => armRoles.ArmSlots.Any(armSlot =>
                    armSignatures.TryGetValue(armSlot, out string? armSignature) &&
                    string.Equals(targetSlot.Value, armSignature, StringComparison.Ordinal)))
                .Select(targetSlot => targetSlot.Key)
                .Order()
                .ToArray();
            if (matchingArmSlots.Length != 1 ||
                resolvedSlots is not null && !resolvedSlots.SequenceEqual(matchingArmSlots))
            {
                return [];
            }

            resolvedSlots = matchingArmSlots.ToArray();
        }

        return resolvedSlots ?? [];
    }

    private static void AddModelTextureContextSlotMapping(
        Dictionary<string, Dictionary<int, string?>> byModelContext,
        string modelPath,
        string context,
        int slotIndex,
        string texturePath)
    {
        string normalizedContext = NormalizeLooseKey(context);
        if (normalizedContext.Length == 0)
        {
            return;
        }

        AddModelTextureSlotMapping(
            byModelContext,
            $"{modelPath}\0{normalizedContext}",
            slotIndex,
            texturePath);
    }

    private static bool IsUnindexedCftSkinFileName(string columnName)
    {
        return string.Equals(
            columnName.TrimStart('_'),
            "SkinFileName",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetLtcRenderStyleIndex(string fieldName, out int renderStyleIndex)
    {
        renderStyleIndex = -1;
        string? prefix = new[]
            {
                "WLeftHandRenderStyleFileName",
                "LeftHandRenderStyleFileName",
                "PreViewRenderStyleFileName",
                "PViewRenderStyleFileName"
            }
            .FirstOrDefault(candidate => fieldName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        string suffix = fieldName[prefix.Length..];
        if (suffix.Length == 0)
        {
            renderStyleIndex = 0;
            return true;
        }

        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int fieldOrdinal) || fieldOrdinal < 1)
        {
            return false;
        }

        renderStyleIndex = fieldOrdinal - 1;
        return true;
    }

    private static bool IsPrimaryCftMotionSourceColumn(string columnName)
    {
        string normalized = columnName.TrimStart('_');
        return normalized.Contains("model", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("motion", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("socket", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("arm", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("hand", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("pview", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("playerview", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("weapon", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("projectile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingModelTextureSlotMapping(
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> byModelContext,
        string modelPath,
        int slotIndex,
        string texturePath)
    {
        if (byModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? modelSlots) &&
            modelSlots.TryGetValue(slotIndex, out string? modelTexture) &&
            string.Equals(modelTexture, texturePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string contextPrefix = modelPath + "\0";
        return byModelContext.Any(pair =>
            pair.Key.StartsWith(contextPrefix, StringComparison.OrdinalIgnoreCase) &&
            pair.Value.TryGetValue(slotIndex, out string? contextTexture) &&
            string.Equals(contextTexture, texturePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetLogicalModelResourcePath(string resourcePath)
    {
        string normalized = NormalizeKey(resourcePath);
        int modelPathIndex = normalized.IndexOf("Models/", StringComparison.OrdinalIgnoreCase);
        return modelPathIndex >= 0 ? normalized[modelPathIndex..] : string.Empty;
    }

    private static string GetLogicalTextureResourcePath(string resourcePath)
    {
        string normalized = NormalizeKey(resourcePath);
        int texturePathIndex = normalized.IndexOf("ModelTextures/", StringComparison.OrdinalIgnoreCase);
        if (texturePathIndex >= 0)
        {
            return normalized[texturePathIndex..];
        }

        texturePathIndex = normalized.IndexOf("TEX/", StringComparison.OrdinalIgnoreCase);
        return texturePathIndex >= 0 ? normalized[texturePathIndex..] : string.Empty;
    }

    private static string GetAuditLogicalModelPath(string resourcePath)
    {
        string normalized = NormalizeKey(resourcePath);
        foreach (string root in new[] { "Models/", "ArmModel/", "UCC/ModelAssets/" })
        {
            int rootIndex = normalized.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (rootIndex >= 0)
            {
                return NormalizeExtensionlessModelReference(normalized[rootIndex..]);
            }
        }

        string[] pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int segmentIndex = 0; segmentIndex < pathSegments.Length; segmentIndex++)
        {
            if (!string.Equals(pathSegments[segmentIndex], "RS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pathSegments[segmentIndex], "RenderStyles", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string renderStylePath = string.Join('/', pathSegments.Skip(segmentIndex));
            string extension = Path.GetExtension(renderStylePath);
            return string.Equals(extension, ".ltb", StringComparison.OrdinalIgnoreCase)
                ? renderStylePath[..^extension.Length]
                : string.Empty;
        }

        if (LithTechRenderStyleDecoder.IsCandidate(normalized, "ltb"))
        {
            int effectsRootIndex = Array.FindIndex(
                pathSegments,
                segment => string.Equals(segment, "FX", StringComparison.OrdinalIgnoreCase));
            string renderStylePath = effectsRootIndex >= 0
                ? string.Join('/', pathSegments.Skip(effectsRootIndex))
                : normalized;
            string extension = Path.GetExtension(renderStylePath);
            return string.Equals(extension, ".ltb", StringComparison.OrdinalIgnoreCase)
                ? renderStylePath[..^extension.Length]
                : string.Empty;
        }

        return NormalizeExtensionlessModelReference(normalized);
    }

    private static string GetModelResourcePath(ExplorerItem item)
    {
        return NormalizeKey(!string.IsNullOrWhiteSpace(item.OutputRelativePath)
            ? item.OutputRelativePath
            : item.ArchiveFile?.FullPath ?? string.Empty);
    }

    private static string GetModelArchiveRoot(string resourcePath)
    {
        string normalized = NormalizeKey(resourcePath);
        int modelPathIndex = normalized.IndexOf("Models/", StringComparison.OrdinalIgnoreCase);
        return modelPathIndex > 0 ? normalized[..modelPathIndex].TrimEnd('/') : string.Empty;
    }

    private static LithTechModelDocument? TryDecodeSlotInheritanceModel(
        ExplorerItem item,
        string modelPath,
        Dictionary<string, LithTechModelDocument?> decodedModels)
    {
        string cacheKey = item.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
            ? "md5:" + archiveHash
            : NormalizeKey(modelPath);
        if (decodedModels.TryGetValue(cacheKey, out LithTechModelDocument? cached))
        {
            return cached;
        }

        byte[]? modelData = TryReadItemBytes(item, 64 * 1024 * 1024);
        LithTechModelDocument? document = null;
        if (modelData is not null)
        {
            LithTechModelDecoder.TryDecode(modelData, modelPath, "ltb", out document, out _);
        }

        decodedModels[cacheKey] = document;
        return document;
    }

    private static IReadOnlyList<string> ResolveModelTextureSlot(
        TextureConfigIndex index,
        string modelPath,
        int slotIndex)
    {
        ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
        return dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? slots) &&
               slots.TryGetValue(slotIndex, out string? texture) &&
               !string.IsNullOrWhiteSpace(texture)
            ? [texture]
            : [];
    }

    private static IReadOnlyList<string> ResolveModelTextureContextSlot(
        TextureConfigIndex index,
        string modelPath,
        string contextKey,
        int slotIndex)
    {
        ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
        string key = $"{modelPath}\0{contextKey}";
        return dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? slots) &&
               slots.TryGetValue(slotIndex, out string? texture) &&
               !string.IsNullOrWhiteSpace(texture)
            ? [texture]
            : [];
    }

    private static IReadOnlyList<string> ResolveInferredModelTextureSlot(
        TextureConfigIndex index,
        string modelPath,
        int slotIndex)
    {
        ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
        if (dictionary.ByModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? directSlots) &&
            directSlots.ContainsKey(slotIndex))
        {
            return [];
        }

        return ResolveInferredTextureFromSources(
            modelPath,
            slotIndex,
            dictionary.InferredByModelPath,
            dictionary.LtcPreviewVariantByModelPath,
            dictionary.PreviewNormalizedBasenameByModelPath,
            dictionary.VariantSlotUvByModelPath);
    }

    private static IReadOnlyList<string> ResolveInferredModelTextureContextSlot(
        TextureConfigIndex index,
        string modelPath,
        string contextKey,
        int slotIndex)
    {
        ModelTextureSlotDictionary dictionary = GetModelTextureSlotDictionary(index);
        string key = $"{modelPath}\0{contextKey}";
        if (dictionary.ByModelContext.TryGetValue(key, out Dictionary<int, string?>? directSlots) &&
            directSlots.ContainsKey(slotIndex))
        {
            return [];
        }

        return ResolveInferredTextureFromSources(
            key,
            slotIndex,
            dictionary.InferredByModelContext,
            dictionary.LtcPreviewVariantByModelContext);
    }

    private static IReadOnlyList<string> ResolveInferredTextureFromSources(
        string key,
        int slotIndex,
        params IReadOnlyDictionary<string, Dictionary<int, string?>>[] sources)
    {
        string? resolved = null;
        foreach (IReadOnlyDictionary<string, Dictionary<int, string?>> source in sources)
        {
            if (!source.TryGetValue(key, out Dictionary<int, string?>? slots) || !slots.TryGetValue(slotIndex, out string? texture))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(texture) ||
                resolved is not null && !string.Equals(resolved, texture, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            resolved = texture;
        }

        return resolved is not null ? [resolved] : [];
    }

    private static ModelTextureSlotDictionary GetModelTextureSlotDictionary(TextureConfigIndex index)
    {
        lock (index.ModelTextureSlotDictionarySync)
        {
            if (index.ModelTextureSlotDictionaryBuilt)
            {
                return index.ModelTextureSlotDictionary ?? ModelTextureSlotDictionary.Empty;
            }

            index.ModelTextureSlotDictionary = BuildModelTextureSlotDictionary(
                index.AllItems,
                index.ResourceItems,
                index.LtcItems,
                index.CrossFireScriptItems,
                index.ModelResourceItems,
                index.ResourcePaths,
                index.ModelPathsByBaseName,
                index.TexturePathsByBaseName);
            index.ModelTextureSlotDictionaryBuilt = true;
            return index.ModelTextureSlotDictionary;
        }
    }

    private static void AddModelTextureSlotMapping(
        Dictionary<string, Dictionary<int, string?>> byModelPath,
        string modelPath,
        int slotIndex,
        string texturePath)
    {
        if (!byModelPath.TryGetValue(modelPath, out Dictionary<int, string?>? slots))
        {
            slots = [];
            byModelPath[modelPath] = slots;
        }

        if (!slots.TryGetValue(slotIndex, out string? existingTexture))
        {
            slots[slotIndex] = texturePath;
        }
        else if (!string.IsNullOrWhiteSpace(existingTexture) &&
                 !string.Equals(existingTexture, texturePath, StringComparison.OrdinalIgnoreCase))
        {
            // A model/slot collision across CFT rows is ambiguous. Preserve that
            // ambiguity instead of choosing whichever table happened to load first.
            slots[slotIndex] = null;
        }
    }

    private static string NormalizeModelResourcePath(string value)
    {
        string normalized = NormalizeKey(value);
        string[] modelRoots = ["/Models/", "/ArmModel/", "/UCC/ModelAssets/"];
        foreach (string modelRoot in modelRoots)
        {
            int rootIndex = normalized.IndexOf(modelRoot, StringComparison.OrdinalIgnoreCase);
            if (rootIndex >= 0)
            {
                normalized = normalized[(rootIndex + 1)..];
                break;
            }
        }

        return NormalizeExtensionlessModelReference(normalized);
    }

    private static void AddModelPathTextureMapping(
        Dictionary<string, List<string>> byModelPath,
        string modelReference,
        IReadOnlyList<string> textures)
    {
        string key = NormalizeExactModelReference(modelReference);
        if (key.Length == 0)
        {
            return;
        }

        if (!byModelPath.TryGetValue(key, out List<string>? mappedTextures))
        {
            mappedTextures = [];
            byModelPath[key] = mappedTextures;
        }

        foreach (string texture in textures)
        {
            if (!mappedTextures.Contains(texture, StringComparer.OrdinalIgnoreCase))
            {
                mappedTextures.Add(texture);
            }
        }
    }

    private static string NormalizeExactModelReference(string value)
    {
        string normalized = NormalizeCanonicalModelReference(value);
        string extension = Path.GetExtension(normalized);
        return ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               (string.IsNullOrEmpty(extension) && IsKnownModelPathRoot(normalized))
            ? normalized
            : string.Empty;
    }

    private static string NormalizeExtensionlessModelReference(string value)
    {
        string normalized = NormalizeCanonicalModelReference(value);
        if (!IsKnownModelPathRoot(normalized))
        {
            return string.Empty;
        }

        string extension = Path.GetExtension(normalized);
        if (ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            normalized = normalized[..^extension.Length];
        }
        else if (!string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string NormalizeCanonicalModelReference(string value)
    {
        string normalized = NormalizeKey(value);
        const string playerViewArmModelRoot = "Models/PLAYERVIEW/ArmModel/";
        if (normalized.StartsWith(playerViewArmModelRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "ArmModel/" + normalized[playerViewArmModelRoot.Length..];
        }

        return normalized;
    }

    private static IEnumerable<string> EnumerateExistingModelReferenceAliases(
        string modelReference,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName)
    {
        string normalized = NormalizeKey(modelReference);
        if (normalized.Length == 0 || normalized.EndsWith('/'))
        {
            yield break;
        }

        if (IsArchivedModelReference(normalized, resourcePaths))
        {
            yield break;
        }

        string extension = Path.GetExtension(normalized);
        if (!ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension = string.Empty;
        }

        string stem = extension.Length == 0 ? normalized : normalized[..^extension.Length];
        var candidateStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingStems = new Queue<string>();
        candidateStems.Add(stem);
        pendingStems.Enqueue(stem);
        while (pendingStems.Count > 0)
        {
            string candidateStem = pendingStems.Dequeue();
            string? collapsedPrefixStem = CollapseDuplicatedLeadingPrefix(candidateStem);
            if (!string.IsNullOrWhiteSpace(collapsedPrefixStem) &&
                !string.Equals(collapsedPrefixStem, candidateStem, StringComparison.OrdinalIgnoreCase))
            {
                AddCandidateStem(collapsedPrefixStem);
            }

            if (candidateStem.EndsWith("_INDEXED", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidateStem(candidateStem[..^"_INDEXED".Length]);
                string withoutIndexed = candidateStem[..^"_INDEXED".Length];
                foreach (string side in new[] { "_GR", "_BL", "_SP" })
                {
                    if (withoutIndexed.EndsWith(side, StringComparison.OrdinalIgnoreCase))
                    {
                        AddCandidateStem(withoutIndexed[..^side.Length]);
                    }
                }
            }

            if (candidateStem.EndsWith(".INDEXED", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidateStem(candidateStem[..^".INDEXED".Length]);
            }

            if (candidateStem.EndsWith("_BR", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidateStem(candidateStem[..^"_BR".Length]);
            }

            foreach (string side in new[] { "_GR", "_BL", "_SP" })
            {
                if (candidateStem.EndsWith(side, StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidateStem(candidateStem[..^side.Length]);
                }
            }

            if (candidateStem.EndsWith(".ltb", StringComparison.OrdinalIgnoreCase) && extension.Length > 0)
            {
                AddCandidateStem(candidateStem[..^".ltb".Length]);
            }
        }

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidateStem in candidateStems)
        {
            IEnumerable<string> candidatePaths = extension.Length == 0
                ? ModelExtensions.Select(modelExtension => candidateStem + modelExtension)
                : [candidateStem + extension];
            foreach (string candidatePath in candidatePaths)
            {
                string compatibilityNormalized = candidatePath.Normalize(NormalizationForm.FormKC);
                foreach (string probe in string.Equals(
                             compatibilityNormalized,
                             candidatePath,
                             StringComparison.Ordinal)
                         ? [candidatePath]
                         : new[] { candidatePath, compatibilityNormalized })
                {
                    if (string.Equals(probe, normalized, StringComparison.OrdinalIgnoreCase) ||
                        !yielded.Add(probe) ||
                        !IsArchivedModelReference(probe, resourcePaths))
                    {
                        continue;
                    }

                    yield return probe;
                }
            }

            string baseName = Path.GetFileName(candidateStem).Normalize(NormalizationForm.FormKC);
            if (modelPathsByBaseName.TryGetValue(baseName, out IReadOnlyList<string>? sameNamePaths) &&
                sameNamePaths.Count == 1)
            {
                string sameNamePath = sameNamePaths[0];
                if (!string.Equals(sameNamePath, normalized, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        GetModelPathCategory(sameNamePath),
                        GetModelPathCategory(normalized),
                        StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(sameNamePath) &&
                    IsArchivedModelReference(sameNamePath, resourcePaths))
                {
                    yield return sameNamePath;
                }
            }
        }

        void AddCandidateStem(string candidateStem)
        {
            if (candidateStems.Add(candidateStem))
            {
                pendingStems.Enqueue(candidateStem);
            }
        }
    }

    private static IEnumerable<string> EnumerateExistingModelReferenceAliasesFromPairedTextureEvidence(
        string modelReference,
        IReadOnlyList<string> textureReferences,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string alias in EnumerateExistingModelReferenceAliases(
                     modelReference,
                     resourcePaths,
                     modelPathsByBaseName))
        {
            if (yielded.Add(alias))
            {
                yield return alias;
            }
        }

        string normalizedModel = NormalizeKey(modelReference);
        string modelExtension = Path.GetExtension(normalizedModel);
        string modelBaseName = Path.GetFileNameWithoutExtension(normalizedModel)
            .Normalize(NormalizationForm.FormKC);
        if (!ModelExtensions.Contains(modelExtension, StringComparer.OrdinalIgnoreCase) ||
            modelBaseName.Length == 0 ||
            !modelPathsByBaseName.TryGetValue(modelBaseName, out IReadOnlyList<string>? sameNameModels))
        {
            yield break;
        }

        string[] physicalModelCandidates = sameNameModels
            .Where(path => string.Equals(Path.GetExtension(path), modelExtension, StringComparison.OrdinalIgnoreCase) &&
                           IsArchivedModelReference(path, resourcePaths))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (physicalModelCandidates.Length != 1)
        {
            yield break;
        }

        string physicalModel = physicalModelCandidates[0];
        if (string.Equals(physicalModel, normalizedModel, StringComparison.OrdinalIgnoreCase) ||
            yielded.Contains(physicalModel))
        {
            yield break;
        }

        foreach (string textureReference in textureReferences)
        {
            string normalizedTexture = NormalizeKey(textureReference);
            string textureBaseName = Path.GetFileNameWithoutExtension(normalizedTexture)
                .Normalize(NormalizationForm.FormKC);
            if (!string.Equals(modelBaseName, textureBaseName, StringComparison.OrdinalIgnoreCase) ||
                ResolveExistingTextureReference(
                    normalizedTexture,
                    resourcePaths,
                    texturePathsByBaseName) is null)
            {
                continue;
            }

            yield return physicalModel;
            yield break;
        }
    }

    private static string? ResolveExistingTextureReference(
        string textureReference,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        string normalized = NormalizeKey(textureReference);
        if (normalized.Length == 0 || resourcePaths.Contains(normalized))
        {
            return normalized.Length == 0 ? null : normalized;
        }

        string extension = Path.GetExtension(normalized);
        if (!TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string baseName = Path.GetFileNameWithoutExtension(normalized).Normalize(NormalizationForm.FormKC);
        if (normalized.Contains('/') &&
            texturePathsByBaseName.TryGetValue(
                GetTextureBaseNameKey(baseName, extension),
                out IReadOnlyList<string>? pathCandidates))
        {
            string relativeSuffix = "/" + normalized.TrimStart('/');
            string[] suffixMatches = pathCandidates
                .Where(candidate => candidate.EndsWith(relativeSuffix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (suffixMatches.Length == 1)
            {
                return suffixMatches[0];
            }
        }

        string[] candidates =
        [
            baseName,
            ReplaceIgnoreCase(baseName, "FVEIW", "FVIEW"),
            ReplaceIgnoreCase(baseName, "BDOY", "BODY"),
            ReplaceIgnoreCase(baseName, "THIGHT", "THIGH"),
            ReplaceIgnoreCase(baseName, "WEIST", "WAIST"),
            ReplaceIgnoreCase(baseName, "Blitz_Buster_HAT_SP", "Blitz_Buster_HAIR_SP"),
            ReplaceIgnoreCase(baseName, "BullsEye_HAT_SP", "Bullseye_hair_SP"),
            ReplaceIgnoreCase(baseName, "Natalya_HAT_BL", "Natalya_hair_BL"),
            ReplaceIgnoreCase(baseName, "Venus_CAB_BL", "Venus_HAIR_BL"),
            ReplaceIgnoreCase(baseName, "Venus_Ribon_GR", "Venus_HAIR_GR"),
            ReplaceIgnoreCase(baseName, "PV-SixEyed_Sword_Gold", "PV-SixEyedSword_Gold"),
            // The LTC player-view texture is missing; this model's geometry and UVs match the full-layout PV-QBZ95_Volcano_BR model.
            ReplaceIgnoreCase(baseName, "PV-QBZ95_Silencer_Volcano", "PV-QBZ95_Volcano"),
            // WeaponAttachItemDoll pairs these QV shells with PV Baihu; their PV/QV layouts match and QV Baihu is the unique physical counterpart.
            ReplaceIgnoreCase(baseName, "QV-ACC_Interaction_Joy", "QV-ACC_Baihu"),
            ReplaceIgnoreCase(baseName, "QV-ACC_Interaction_PartyDoggy", "QV-ACC_Baihu"),
            CollapseDuplicatedLeadingPrefix(baseName) ?? baseName,
            baseName.StartsWith("GSG9_BODY_", StringComparison.OrdinalIgnoreCase)
                ? "GSG9_CH_Body_" + baseName["GSG9_BODY_".Length..]
                : baseName,
            baseName.StartsWith("GSG9_FACE_", StringComparison.OrdinalIgnoreCase)
                ? "GSG9_CH_Face_" + baseName["GSG9_FACE_".Length..]
                : baseName
        ];
        var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidateBaseName in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!texturePathsByBaseName.TryGetValue(
                    GetTextureBaseNameKey(candidateBaseName, extension),
                    out IReadOnlyList<string>? candidatePaths) ||
                candidatePaths.Count != 1)
            {
                continue;
            }

            resolvedPaths.Add(candidatePaths[0]);
        }

        return resolvedPaths.Count == 1 ? resolvedPaths.First() : null;
    }

    private static string? ResolveExistingCftTextureReference(
        string modelReference,
        string textureReference,
        IReadOnlyList<ExplorerItem> resourceItems,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        string? existingTexturePath = ResolveExistingTextureReference(
            textureReference,
            resourcePaths,
            texturePathsByBaseName);
        if (existingTexturePath is not null)
        {
            return existingTexturePath;
        }

        string normalizedModelPath = NormalizeExtensionlessModelReference(modelReference);
        string normalizedTexturePath = NormalizeKey(textureReference);
        foreach (VerifiedCftMotionTextureAlias alias in VerifiedCftMotionTextureAliases)
        {
            if (!string.Equals(
                    normalizedModelPath,
                    NormalizeExtensionlessModelReference(alias.ParentModelPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(normalizedTexturePath, NormalizeKey(alias.MissingTexturePath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            existingTexturePath = ResolveExistingTextureReference(
                alias.ExistingTexturePath,
                resourcePaths,
                texturePathsByBaseName);
            if (existingTexturePath is not null &&
                HasConsistentPhysicalResourceCopies(resourceItems, existingTexturePath))
            {
                return existingTexturePath;
            }
        }

        return null;
    }

    private static bool TryResolveVerifiedCftTextureFieldSlotAlias(
        string tablePath,
        CftModelTextureMapping mapping,
        CftTextureReference texture,
        string modelResourcePath,
        int sourceSlotIndex,
        string? existingTexturePath,
        IReadOnlyDictionary<string, ExplorerItem[]> modelItemsByPath,
        Dictionary<string, LithTechModelDocument?> decodedModels,
        IReadOnlyDictionary<string, ExplorerItem[]> textureItemsByLogicalPath,
        Dictionary<string, bool> verifiedTexturePathCache,
        out int targetSlotIndex)
    {
        targetSlotIndex = -1;
        VerifiedCftTextureFieldSlotAlias? alias = VerifiedCftTextureFieldSlotAliases.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(tablePath), candidate.TableFileName, StringComparison.OrdinalIgnoreCase) &&
            mapping.RowIndex == candidate.RowIndex &&
            mapping.RecordId == candidate.RecordId &&
            string.Equals(mapping.ModelColumn, candidate.ModelColumn, StringComparison.OrdinalIgnoreCase) &&
            mapping.IdentityAliases.Contains(candidate.IdentityAlias, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(
                NormalizeExtensionlessModelReference(modelResourcePath),
                NormalizeExtensionlessModelReference(candidate.ModelPath),
                StringComparison.OrdinalIgnoreCase) &&
            sourceSlotIndex == candidate.SourceSlotIndex &&
            candidate.TextureColumns.Contains(texture.Column, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(NormalizeKey(texture.Path), NormalizeKey(candidate.MissingTexturePath), StringComparison.OrdinalIgnoreCase) &&
            existingTexturePath is not null &&
            string.Equals(NormalizeKey(existingTexturePath), NormalizeKey(candidate.ExistingTexturePath), StringComparison.OrdinalIgnoreCase));
        if (alias is null)
        {
            return false;
        }

        string logicalModelPath = GetLogicalModelResourcePath(alias.ModelPath + ".ltb");
        if (!modelItemsByPath.TryGetValue(logicalModelPath, out ExplorerItem[]? modelCopies) ||
            modelCopies.Length == 0 ||
            !TryGetConsistentLtcModelSlots(modelCopies, decodedModels, out int[] modelSlots) ||
            !modelSlots.SequenceEqual([alias.TargetSlotIndex]))
        {
            return false;
        }

        string? expectedLayout = null;
        foreach (ExplorerItem modelCopy in modelCopies)
        {
            LithTechModelDocument? document = TryDecodeSlotInheritanceModel(
                modelCopy,
                GetModelResourcePath(modelCopy),
                decodedModels);
            if (document is null || document.Meshes.Count != 1)
            {
                return false;
            }

            LithTechMesh mesh = document.Meshes[0];
            if (!mesh.HasAuthoritativeTextureBindings ||
                mesh.TextureSlotIndices is not { Count: 1 } meshSlots ||
                meshSlots[0] != alias.TargetSlotIndex ||
                !string.Equals(mesh.Name, alias.MeshName, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(mesh.TexturePath) ||
                GetVerifiedTextureUvTriangleSignature(mesh) is null ||
                mesh.RenderStyleIndex is not int ||
                CftPlayerViewArmEquivalence.GetConsistentTextureSlotGeometryUvSignatures([document]) is not { } signatures ||
                !signatures.TryGetValue(alias.TargetSlotIndex, out string? layout) ||
                expectedLayout is not null && !string.Equals(expectedLayout, layout, StringComparison.Ordinal))
            {
                return false;
            }

            expectedLayout = layout;
        }

        if (!HasConsistentDecodableDtxCopies(
                alias.ExistingTexturePath,
                textureItemsByLogicalPath,
                verifiedTexturePathCache))
        {
            return false;
        }

        targetSlotIndex = alias.TargetSlotIndex;
        return true;
    }

    private static bool IsVerifiedCftMotionTextureAlias(
        string parentModelPath,
        string motionModelPath,
        int slotIndex,
        string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return false;
        }

        string normalizedParentPath = NormalizeExtensionlessModelReference(parentModelPath);
        string normalizedMotionPath = NormalizeExtensionlessModelReference(motionModelPath);
        string normalizedTexturePath = NormalizeKey(texturePath);
        return VerifiedCftMotionTextureAliases.Any(alias =>
            alias.SlotIndex == slotIndex &&
            string.Equals(
                normalizedParentPath,
                NormalizeExtensionlessModelReference(alias.ParentModelPath),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                normalizedMotionPath,
                NormalizeExtensionlessModelReference(alias.MotionModelPath),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                normalizedTexturePath,
                NormalizeKey(alias.ExistingTexturePath),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveClientShellCategoryPair(
        string modelReference,
        string textureReference,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName,
        out string modelAlias,
        out string textureAlias)
    {
        // Resolve stale ClientShell folder paths only when the model and texture
        // share a category and source directory, each basename has one candidate
        // in that category, and the current physical pair shares one directory.
        modelAlias = string.Empty;
        textureAlias = string.Empty;
        string normalizedModel = NormalizeKey(modelReference);
        string normalizedTexture = NormalizeKey(textureReference);
        if (!TryGetCategoryRoot(normalizedModel, "Models/", out string modelCategory, out string modelRoot) ||
            !TryGetCategoryRoot(normalizedTexture, "ModelTextures/", out string textureCategory, out string textureRoot) ||
            !string.Equals(modelCategory, textureCategory, StringComparison.OrdinalIgnoreCase) ||
            !ModelExtensions.Contains(Path.GetExtension(normalizedModel), StringComparer.OrdinalIgnoreCase) ||
            !TextureExtensions.Contains(Path.GetExtension(normalizedTexture), StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(
                GetRelativeAssetDirectory(normalizedModel, modelRoot),
                GetRelativeAssetDirectory(normalizedTexture, textureRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string modelBaseName = Path.GetFileNameWithoutExtension(normalizedModel).Normalize(NormalizationForm.FormKC);
        string modelExtension = Path.GetExtension(normalizedModel);
        if (!modelPathsByBaseName.TryGetValue(modelBaseName, out IReadOnlyList<string>? sameNameModels))
        {
            return false;
        }

        string[] categoryModels = sameNameModels
            .Where(path => path.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(Path.GetExtension(path), modelExtension, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (categoryModels.Length != 1)
        {
            return false;
        }

        string textureBaseName = Path.GetFileNameWithoutExtension(normalizedTexture).Normalize(NormalizationForm.FormKC);
        string textureExtension = Path.GetExtension(normalizedTexture);
        if (!texturePathsByBaseName.TryGetValue(
                GetTextureBaseNameKey(textureBaseName, textureExtension),
                out IReadOnlyList<string>? sameNameTextures))
        {
            return false;
        }

        string[] categoryTextures = sameNameTextures
            .Where(path => path.StartsWith(textureRoot, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(Path.GetExtension(path), textureExtension, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (categoryTextures.Length != 1 ||
            !string.Equals(
                GetRelativeAssetDirectory(categoryModels[0], modelRoot),
                GetRelativeAssetDirectory(categoryTextures[0], textureRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        modelAlias = categoryModels[0];
        textureAlias = categoryTextures[0];
        return true;
    }

    private static bool TryGetCategoryRoot(
        string path,
        string assetRoot,
        out string category,
        out string categoryRoot)
    {
        category = string.Empty;
        categoryRoot = string.Empty;
        if (!path.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relativePath = path[assetRoot.Length..];
        int separator = relativePath.IndexOf('/');
        if (separator <= 0)
        {
            return false;
        }

        category = relativePath[..separator];
        categoryRoot = assetRoot + category + "/";
        return true;
    }

    private static string GetModelPathCategory(string modelReference)
    {
        string normalized = NormalizeKey(modelReference);
        string root = normalized.StartsWith("Models/", StringComparison.OrdinalIgnoreCase)
            ? "Models/"
            : normalized.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase)
                ? "ArmModel/"
                : normalized.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase)
                    ? "UCC/ModelAssets/"
                    : string.Empty;
        if (root.Length == 0 || !normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string relativePath = normalized[root.Length..];
        int separator = relativePath.IndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private static string GetRelativeAssetDirectory(string path, string categoryRoot)
    {
        if (!path.StartsWith(categoryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string relativePath = path[categoryRoot.Length..];
        int separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
    {
        return value.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CollapseDuplicatedLeadingPrefix(string value)
    {
        int separator = value.LastIndexOf('/');
        string fileName = separator < 0 ? value : value[(separator + 1)..];
        for (int prefixLength = fileName.Length / 2; prefixLength >= 4; prefixLength--)
        {
            if (!string.Equals(
                    fileName[..prefixLength],
                    fileName.Substring(prefixLength, prefixLength),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string collapsed = fileName[..prefixLength] + fileName[(prefixLength * 2)..];
            return separator < 0 ? collapsed : value[..(separator + 1)] + collapsed;
        }

        return null;
    }

    private static string GetTextureBaseNameKey(string baseName, string extension)
    {
        return baseName.Normalize(NormalizationForm.FormKC) + "|" + extension;
    }

    private static bool IsArchivedModelReference(
        string modelReference,
        IReadOnlySet<string> resourcePaths)
    {
        if (resourcePaths.Contains(modelReference))
        {
            return true;
        }

        const string armModelRoot = "ArmModel/";
        return modelReference.StartsWith(armModelRoot, StringComparison.OrdinalIgnoreCase) &&
               resourcePaths.Contains("Models/PLAYERVIEW/" + modelReference);
    }

    private static bool IsKnownModelPathRoot(string value)
    {
        return value.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ModelTextureMapping> ExtractModelTextureMappings(string text)
    {
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            List<string> textures = ExtractTextureReferencesFromValue(line)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (textures.Count == 0)
            {
                continue;
            }

            var modelReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                AddPotentialModelReference(modelReferences, line[..equals]);
            }

            foreach (string modelReference in ExtractModelReferencesFromValue(line))
            {
                AddPotentialModelReference(modelReferences, modelReference);
            }

            foreach (string modelReference in modelReferences)
            {
                yield return new ModelTextureMapping(modelReference, textures);
            }
        }
    }

    private static void AddModelTextureMapping(
        Dictionary<string, List<string>> byName,
        string modelReference,
        IReadOnlyList<string> textures)
    {
        foreach (string lookupKey in EnumerateModelLookupKeys(modelReference))
        {
            if (!byName.TryGetValue(lookupKey, out List<string>? existingTextures))
            {
                existingTextures = [];
                byName[lookupKey] = existingTextures;
            }

            foreach (string texture in textures)
            {
                if (!existingTextures.Contains(texture, StringComparer.OrdinalIgnoreCase))
                {
                    existingTextures.Add(texture);
                }
            }
        }
    }

    private static void AddExactAliasTextureMapping(
        Dictionary<string, List<string>> byAlias,
        string alias,
        IReadOnlyList<string> textures)
    {
        string key = GetResourceLookupStem(alias);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!byAlias.TryGetValue(key, out List<string>? existingTextures))
        {
            existingTextures = [];
            byAlias[key] = existingTextures;
        }

        foreach (string texture in textures)
        {
            if (!existingTextures.Contains(texture, StringComparer.OrdinalIgnoreCase))
            {
                existingTextures.Add(texture);
            }
        }
    }

    private static IEnumerable<string> EnumerateModelLookupKeys(string value)
    {
        string normalized = GetResourceLookupStem(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        string fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !string.Equals(fileName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return fileName;
        }

        string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(normalized);
        if (!string.Equals(numberedBase, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return numberedBase;
        }

        foreach (string familyBase in LithTechModelPartGrouper.EnumerateModelFamilyBaseCandidates(normalized))
        {
            if (!string.Equals(familyBase, normalized, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(familyBase, numberedBase, StringComparison.OrdinalIgnoreCase))
            {
                yield return familyBase;
            }
        }
    }

    private static IEnumerable<string> ExtractModelReferencesFromValue(string value)
    {
        foreach (string extension in ModelExtensions)
        {
            int searchStart = 0;
            while (searchStart < value.Length)
            {
                int extensionIndex = value.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    break;
                }

                int start = extensionIndex;
                while (start > 0 && IsResourcePathCharacter(value[start - 1]))
                {
                    start--;
                }

                int end = extensionIndex + extension.Length;
                string reference = NormalizeResourceReference(value[start..end]);
                if (!string.IsNullOrWhiteSpace(reference) && reference.Length > extension.Length)
                {
                    yield return reference;
                }

                searchStart = end;
            }
        }
    }

    private static void AddPotentialModelReference(HashSet<string> modelReferences, string value)
    {
        string reference = NormalizeResourceReference(value);
        if (string.IsNullOrWhiteSpace(reference) || IsGenericMappingKey(reference))
        {
            return;
        }

        modelReferences.Add(reference);
    }

    private static IEnumerable<TextureConfigItem> EnumerateConfigItems(TextureConfigIndex index, string name)
    {
        string normalized = GetResourceLookupStem(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string lookupKey in EnumerateConfigLookupKeys(normalized))
        {
            foreach (TextureConfigItem candidate in LookupExact(index, lookupKey))
            {
                if (yielded.Add(candidate.Path))
                {
                    yield return candidate;
                }
            }

            string looseKey = NormalizeLooseKey(lookupKey);
            if (string.IsNullOrWhiteSpace(looseKey) ||
                !index.ByLooseName.TryGetValue(looseKey, out List<TextureConfigItem>? looseCandidates) ||
                looseCandidates is null)
            {
                continue;
            }

            foreach (TextureConfigItem candidate in looseCandidates)
            {
                if (yielded.Add(candidate.Path))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string GetResourceLookupStem(string value)
    {
        string normalized = NormalizeKey(Path.GetFileName(value));
        string extension = Path.GetExtension(normalized);
        if (ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            ConfigExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            normalized = normalized[..^extension.Length];
        }

        return normalized;
    }

    private static IEnumerable<string> EnumerateConfigLookupKeys(string normalized)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in EnumerateConfigLookupKeysCore(normalized))
        {
            if (yielded.Add(candidate))
            {
                yield return candidate;
            }
        }

        string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(normalized);
        if (!string.Equals(numberedBase, normalized, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string candidate in EnumerateConfigLookupKeysCore(numberedBase))
            {
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateConfigLookupKeysCore(string value)
    {
        yield return value;

        string fileName = Path.GetFileName(value);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !string.Equals(fileName, value, StringComparison.OrdinalIgnoreCase))
        {
            yield return fileName;
        }

        string[] prefixes = ["PV-", "PV_", "QV-", "QV_", "TV-", "TV_", "WV-", "WV_"];
        foreach (string prefix in prefixes)
        {
            if (value.Length > prefix.Length &&
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return value[prefix.Length..];
            }
        }
    }

    private static IEnumerable<TextureConfigItem> LookupExact(TextureConfigIndex index, string name)
    {
        return index.ByName.TryGetValue(name, out List<TextureConfigItem>? candidates)
            ? candidates
            : [];
    }

    private static void AddIndexCandidate(
        Dictionary<string, List<TextureConfigItem>> index,
        string key,
        TextureConfigItem candidate)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out List<TextureConfigItem>? candidates))
        {
            candidates = [];
            index[key] = candidates;
        }

        if (!candidates.Any(existing => string.Equals(existing.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(candidate);
        }
    }

    private static void AddLooseIndexCandidate(
        Dictionary<string, List<TextureConfigItem>?> index,
        string key,
        TextureConfigItem candidate)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!index.TryGetValue(key, out List<TextureConfigItem>? candidates))
        {
            index[key] = [candidate];
            return;
        }

        if (candidates is null)
        {
            return;
        }

        if (candidates.Any(existing => string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(candidate);
            return;
        }

        index[key] = null;
    }

    private static List<string> ExtractTextureReferences(string text, string configName)
    {
        var candidates = new List<ScoredTextureReference>();
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            string key = equals > 0 ? line[..equals].Trim() : string.Empty;
            string value = equals >= 0 ? line[(equals + 1)..] : line;
            foreach (string texture in ExtractTextureReferencesFromValue(value))
            {
                candidates.Add(new ScoredTextureReference(texture, ScoreTextureReference(key, texture, configName)));
            }
        }

        if (candidates.Count == 0)
        {
            foreach (string texture in ExtractTextureReferencesFromValue(text))
            {
                candidates.Add(new ScoredTextureReference(texture, ScoreTextureReference(string.Empty, texture, configName)));
            }
        }

        return candidates
            .GroupBy(candidate => candidate.Texture, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Texture, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Texture)
            .ToList();
    }

    private static int ScoreTextureReference(string key, string texture, string configName)
    {
        string lowerKey = key.ToLowerInvariant();
        string textureStem = Path.GetFileNameWithoutExtension(texture).ToLowerInvariant();
        string configStem = configName.ToLowerInvariant();
        int score = 50;

        if (lowerKey.Contains("diffuse") ||
            lowerKey.Contains("albedo") ||
            lowerKey.Contains("base") ||
            lowerKey.Contains("color") ||
            lowerKey.Contains("main") ||
            lowerKey == "texturename" ||
            lowerKey == "texture")
        {
            score += 100;
        }

        if (textureStem.Contains(configStem, StringComparison.OrdinalIgnoreCase) ||
            configStem.Contains(textureStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (LooksLikeAuxiliaryTexture(lowerKey, textureStem))
        {
            score -= 80;
        }

        if (textureStem.Contains("lobbycube") ||
            textureStem.Contains("gold_map") ||
            textureStem.Contains("black_shader"))
        {
            score -= 120;
        }

        return score;
    }

    private static bool LooksLikeAuxiliaryTexture(string lowerKey, string textureStem)
    {
        return lowerKey.Contains("normal") ||
               lowerKey.Contains("specular") ||
               lowerKey.Contains("env") ||
               lowerKey.Contains("cube") ||
               lowerKey.Contains("alpha") ||
               lowerKey.Contains("mask") ||
               lowerKey.Contains("bump") ||
               lowerKey.Contains("glow") ||
               textureStem.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_s", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_sp", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_alpha", StringComparison.OrdinalIgnoreCase) ||
               textureStem.EndsWith("_mask", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextureConfigCandidate(ExplorerItem item)
    {
        string extension = "." + item.FileExtension.TrimStart('.');
        if (!ConfigExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(extension, ".cft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(item.OutputRelativePath, item.FileExtension))
        {
            return true;
        }

        if (LithTechResourceHeuristics.IsLowValueMappingPath(item.OutputRelativePath))
        {
            return false;
        }

        return string.Equals(extension, ".enc", StringComparison.OrdinalIgnoreCase) ||
               (string.Equals(extension, ".cfg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ini", StringComparison.OrdinalIgnoreCase)) &&
               !IsGenericConfigName(Path.GetFileNameWithoutExtension(item.Name));
    }

    private static bool IsClientShellMappingCandidate(ExplorerItem item)
    {
        return item.Kind == ExplorerItemKind.LocalFile &&
               string.Equals(item.FileExtension, "dll", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileName(item.SourcePath), "CShell_x64.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUccPlacementMapCandidate(ExplorerItem item)
    {
        string extension = item.FileExtension.TrimStart('.');
        if (!extension.Equals("ugc", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals("ucc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = item.OutputRelativePath.Replace('\\', '/');
        return path.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldScanForModelTextureDictionary(TextureConfigItem configItem)
    {
        string extension = Path.GetExtension(configItem.Path).TrimStart('.');
        if (string.Equals(extension, "cft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(extension, "enc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(configItem.Path, extension) ||
               !IsGenericConfigName(configItem.Name);
    }

    private static bool IsGenericConfigName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Length < 3 ||
               lower.Contains("texture") ||
               lower.Contains("material") ||
               lower.Contains("shader") ||
               lower.Contains("model") ||
               lower.Contains("skin") ||
               lower.Contains("table") ||
               lower.Contains("list") ||
               lower.Contains("config") ||
               lower.Contains("effect") ||
               lower.Contains("common") ||
               lower.Contains("default");
    }

    private static bool IsGenericMappingKey(string value)
    {
        string stem = GetResourceLookupStem(value).ToLowerInvariant();
        return stem.Length < 3 ||
               stem is "texture" or "texturename" or "diffuse" or "albedo" or "base" or "main" or "color" or "material" or "shader" or "model" or "path" or "file" or "name" ||
               stem.Contains("texture") ||
               stem.Contains("diffuse") ||
               stem.Contains("normal") ||
               stem.Contains("specular") ||
               stem.Contains("material") ||
               stem.Contains("shader");
    }

    private static IEnumerable<string> ExtractTextureReferencesFromValue(string value)
    {
        foreach (string extension in TextureExtensions)
        {
            int searchStart = 0;
            while (searchStart < value.Length)
            {
                int extensionIndex = value.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    break;
                }

                int start = extensionIndex;
                while (start > 0 && IsResourcePathCharacter(value[start - 1]))
                {
                    start--;
                }

                int end = extensionIndex + extension.Length;
                string reference = NormalizeResourceReference(value[start..end]);
                if (!string.IsNullOrWhiteSpace(reference) && reference.Length > extension.Length)
                {
                    yield return reference;
                }

                searchStart = end;
            }
        }
    }

    private static bool IsResourcePathCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '_' or '-' or '.' or '/' or '\\' or ' ';
    }

    private static string NormalizeResourceReference(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', ':')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string? TryReadConfigText(ExplorerItem item)
    {
        byte[]? data = TryReadItemBytes(item);
        if (data is null || data.Length == 0)
        {
            return null;
        }

        return CfgTextDecoder.TryDecode(data, item.Name, MaxConfigBytes, out CfgTextDocument? document) &&
               document is not null
            ? document.Text
            : null;
    }

    private static string? TryReadEncScriptText(ExplorerItem item)
    {
        byte[]? data = TryReadItemBytes(item);
        return data is not null &&
               EncTextDecoder.TryDecode(data, out byte[] decoded) &&
               TextPreviewDecoder.TryDecode(decoded, preferKorean: true, out string text, out _)
            ? text
            : null;
    }

    private static byte[]? TryReadItemBytes(ExplorerItem item, int maxBytes = MaxConfigBytes)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                if (!info.Exists || info.Length <= 0 || info.Length > maxBytes || info.Length > int.MaxValue)
                {
                    return null;
                }

                return File.ReadAllBytes(item.SourcePath);
            }

            if (item.Kind == ExplorerItemKind.RezFile &&
                item.Archive is not null &&
                item.ArchiveFile is not null &&
                item.ArchiveFile.Size > 0 &&
                item.ArchiveFile.Size <= maxBytes)
            {
                byte[] data = new byte[item.ArchiveFile.Size];
                using Stream source = RezArchiveReader.OpenFileRead(item.Archive, item.ArchiveFile);
                source.ReadExactly(data);
                return data;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<ExplorerItem> EnumerateFiles(ExplorerItem item)
    {
        if (item.IsFile)
        {
            yield return item;
        }

        foreach (ExplorerItem child in item.Children)
        {
            foreach (ExplorerItem file in EnumerateFiles(child))
            {
                yield return file;
            }
        }
    }

    private static string NormalizeKey(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string NormalizeLooseKey(string value)
    {
        var builder = new StringBuilder();
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private sealed record TextureConfigIndex(
        Dictionary<string, List<TextureConfigItem>> ByName,
        Dictionary<string, List<TextureConfigItem>?> ByLooseName,
        IReadOnlyList<TextureConfigItem> AllItems,
        IReadOnlyList<ExplorerItem> ResourceItems,
        IReadOnlyList<ExplorerItem> ModelResourceItems,
        IReadOnlyList<ExplorerItem> UccMapItems,
        IReadOnlyList<ExplorerItem> ObjectLtoItems,
        IReadOnlyList<ExplorerItem> ClientFxFxfItems,
        IReadOnlyList<ExplorerItem> ClientShellItems,
        IReadOnlyList<ExplorerItem> LtcItems,
        IReadOnlyList<ExplorerItem> CrossFireScriptItems,
        IReadOnlySet<string> ResourcePaths,
        IReadOnlySet<string> OutputPaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ModelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TexturePathsByBaseName)
    {
        public object ModelTextureDictionarySync { get; } = new();
        public bool ModelTextureDictionaryBuilt { get; set; }
        public ModelTextureDictionary? ModelTextureDictionary { get; set; }
        public object ModelTextureSlotDictionarySync { get; } = new();
        public bool ModelTextureSlotDictionaryBuilt { get; set; }
        public ModelTextureSlotDictionary? ModelTextureSlotDictionary { get; set; }
        public bool IsEmpty => ByName.Count == 0 &&
                               ByLooseName.Count == 0 &&
                               AllItems.Count == 0 &&
                               ResourceItems.Count == 0 &&
                               ModelResourceItems.Count == 0 &&
                               UccMapItems.Count == 0 &&
                               ObjectLtoItems.Count == 0 &&
                               ClientFxFxfItems.Count == 0 &&
                               ClientShellItems.Count == 0 &&
                               LtcItems.Count == 0 &&
                               CrossFireScriptItems.Count == 0 &&
                               ResourcePaths.Count == 0 &&
                               OutputPaths.Count == 0 &&
                               ModelPathsByBaseName.Count == 0 &&
                               TexturePathsByBaseName.Count == 0;
    }

    private sealed record TextureConfigItem(string Path, string Name, ExplorerItem Item);

    private sealed record WeaponPreviewSkinAsset(
        string ModelStem,
        uint ItemId,
        uint ModelGroupId,
        uint PreviewSlot,
        uint ModelId);

    private sealed record LtcPhysicalModelTextureBinding(string ModelPath, int SlotIndex, string TexturePath);

    private sealed record ModelMaterialComponentTextureMapping(
        string ModelReference,
        int TextureSlotIndex,
        string TextureReference);

    private sealed record DeferredCftMotionSkinBinding(
        string ParentModelPath,
        string MotionModelReference,
        string TexturePath,
        IReadOnlyList<string> ContextKeys);

    private sealed record DeferredCftIdentityModelAliasBinding(
        string SourceModelPath,
        IReadOnlyList<string> IdentityAliases,
        int SlotIndex,
        string TexturePath,
        IReadOnlyList<string> ContextKeys);

    private sealed record ModelTextureDictionary(
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByModelPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByExactAlias)
    {
        public static ModelTextureDictionary Empty { get; } = new(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        public bool IsEmpty => ByName.Count == 0 && ByModelPath.Count == 0 && ByExactAlias.Count == 0;
    }

    private sealed record ModelTextureSlotDictionary(
        IReadOnlyDictionary<string, Dictionary<int, string?>> ByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> ByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> InferredByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> InferredByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> LtcPreviewVariantByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> LtcPreviewVariantByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> PreviewNormalizedBasenameByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> VariantSlotUvByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> MotionParentSlotByModelPath,
        IReadOnlyDictionary<string, Dictionary<int, string?>> MotionParentSlotByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> CftIdentityModelAliasByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> RenderStyleByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> LtcTextureByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> WeaponPreviewVariantByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> WeaponPreviewVariantByModelPath,
        IReadOnlySet<string> ModelsWithSlotSpecificBindings,
        IReadOnlyDictionary<string, Dictionary<int, string?>> EquivalentArmByModelContext,
        IReadOnlyDictionary<string, Dictionary<int, string?>> EquivalentArmByModelPath,
        IReadOnlyDictionary<string, Dictionary<CftPlayerViewTextureRole, HashSet<string>>> CftPlayerViewRoleTexturesByContext,
        IReadOnlyDictionary<string, CftPlayerViewRoleSlotLayouts> CftPlayerViewRoleSlotsByModelPath)
    {
        public static ModelTextureSlotDictionary Empty { get; } = new(
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<CftPlayerViewTextureRole, HashSet<string>>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, CftPlayerViewRoleSlotLayouts>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record ModelTextureMapping(string ModelReference, IReadOnlyList<string> TextureReferences);

    private readonly record struct ScoredTextureReference(string Texture, int Score);
}
