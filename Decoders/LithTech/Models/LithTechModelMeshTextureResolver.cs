using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CFRezManager;

/// <summary>
/// Resolves mesh-level texture candidates when native or table references are
/// absent. General fallback candidates require an exact basename and unique
/// logical texture path; first-person arm models use a role-aware resolver
/// restricted to consistent DTX copies in the player-view texture tree.
/// </summary>
internal static class LithTechModelMeshTextureResolver
{
    private static readonly string[] TextureExtensions = ["dtx", "dds", "tga", "png", "jpg", "jpeg", "bmp", "bin"];
    private static readonly Regex MapTexturePathPattern = new(@"^MAP\d+/", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedTexturePattern = new(@"^(?<base>.+?)[_-]?(?<ordinal>\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedMapMeshNamePattern = new(@"^[A-Za-z][A-Za-z0-9_-]*\d{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] ModelVariantSuffixes =
    [
        "_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP",
        "_FEMALE_BL", "_FEMALE_GR", "_FEMALE_SP",
        "_CHANGEMODE", "_CHANGE_MODE", "_PREVIEW", "_REMOT",
        "_BL", "_GR", "_SP", "_F", "_M", "_W"
    ];
    private static readonly string[] PlayerViewArmModelVariantSuffixes =
    [
        "_WOMAN_BL", "_WOMAN_GR", "_WOMAN_SP",
        "_FEMALE_BL", "_FEMALE_GR", "_FEMALE_SP",
        "_BL", "_GR", "_SP"
    ];
    private static readonly HashSet<string> PvInkPlayerviewModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV-INK", "PV-INK_BL", "PV-INK_UP", "PV-INK_UP_BL",
        "PV-INK_Hook", "PV-INK_Hook_BL", "PV-INK_UP_Hook", "PV-INK_UP_Hook_BL",
        "PV-InkHook_Devil", "PV-InkHook_Devil_BL", "PV-InkHook_Devil_UP", "PV-InkHook_Devil_UP_BL",
        "PV-INK_Axehook", "PV-INK_Axehook_BL", "PV-INK_Axehook_UP", "PV-INK_Axehook_UP_BL"
    };
    private static readonly HashSet<string> PvDeAngelicBeastTeamModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV-D.E_AngelicBeast_BL", "PV-D.E_AngelicBeast_GR",
        "PV-D.E_AngelicBeast_WOMAN_BL", "PV-D.E_AngelicBeast_WOMAN_GR",
        "PV-D.E_AngelicBeast_GunFu_BL", "PV-D.E_AngelicBeast_GunFu_GR",
        "PV-D.E_AngelicBeast_GunFu_WOMAN_BL", "PV-D.E_AngelicBeast_GunFu_WOMAN_GR"
    };
    private static readonly HashSet<string> PvScarLightAngelicBeastModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV-ScarLight_S_AngelicBeast", "PV-ScarLight_S_AngelicBeast_BL", "PV-ScarLight_S_AngelicBeast_GR",
        "PV-ScarLight_S_AngelicBeast_WOMAN_BL", "PV-ScarLight_S_AngelicBeast_WOMAN_GR",
        "PV-ScarLight_S_AngelicBeast_Sprint", "PV-ScarLight_S_AngelicBeast_Sprint_BL", "PV-ScarLight_S_AngelicBeast_Sprint_GR",
        "PV-ScarLight_S_AngelicBeast_Sprint_WOMAN_BL", "PV-ScarLight_S_AngelicBeast_Sprint_WOMAN_GR"
    };
    private static readonly HashSet<string> PvAnniv18thHkg36cModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV-Anniv18th_BL_HKG36C", "PV-Anniv18th_BL_HKG36C_BL", "PV-Anniv18th_BL_HKG36C_GR",
        "PV-Anniv18th_BL_HKG36C_WOMAN_BL", "PV-Anniv18th_BL_HKG36C_WOMAN_GR",
        "PV-Anniv18th_HKG36C_GR", "PV-Anniv18th_HKG36C_WOMAN_BL", "PV-Anniv18th_HKG36C_WOMAN_GR"
    };
    private static readonly HashSet<string> Ai2BattleShipOpeningBigCrawlerModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "AI2_BattleShip_Opening_CHAR2", "AI2_BattleShip_Opening_CHAR2_M-motion"
    };
    private static readonly HashSet<string> ZumaMachineDragonOpening2MeshNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AI_MachineDragon body", "grinder cube", "Object001", "Object002",
        "Object019", "Object020", "grinder cube007", "Object021", "Object022",
        "grinder cube008", "Object023", "Object024", "grinder cube009", "Object025",
        "Object026", "grinder cube010", "Object027", "Object028", "grinder cube011",
        "Object029", "Object030", "grinder cube012"
    };
    private static readonly HashSet<string> EliteMechanicalDeviceVariantModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "AI_Boss_UFO", "AI_Boss_UFO_M-motion",
        "EliteBoss_UnknownDestructor", "EliteBoss_UnknownDestructor_M-motion"
    };
    private static readonly HashSet<string> EliteMechanicalDeviceMeshNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mechanical device_boy", "Mechanical device_01", "Mechanical device_03_1",
        "Mechanical device_01_1", "Mechanical device_02_1", "Mechanical device_01_2",
        "Mechanical device_03_2", "Mechanical device_02_2", "Mechanical device_01_3",
        "Mechanical device_03_3", "Mechanical device_02_3", "Mechanical device_03",
        "Mechanical device_02", "Mechanical device_weapon"
    };
    private static readonly HashSet<string> PvBoxCubeTeamModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV-BoxCube_BL", "PV-BoxCube_GR",
        "PV-BoxCube_WOMAN_BL", "PV-BoxCube_WOMAN_GR"
    };
    private static readonly HashSet<string> Ai3HellTowerElevatorCeilingModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "AI3_Tower_elevator_1F_Ceiling",
        "AI3_Tower_elevator_2F_Ceiling",
        "AI3_Tower_elevator_Middle_Ceiling"
    };
    private static readonly HashSet<string> AiGigantPredatorMotionRegMeshNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssdsd011", "ssdsd012", "ssdsd005", "ssdsd010"
    };
    private static readonly HashSet<string> PvNanoAssassinKnifeModelStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "PV_NANO_ASSASSIN_KNIFE", "PV_NANO_ASSASSIN_KNIFE_BL"
    };
    private static readonly HashSet<string> IgnoredContextTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODEL", "MODELS", "CHARACTER", "ITEM", "MAP", "REALITYMODEL"
    };
    private static readonly Regex ContextTokenPattern = new("[A-Za-z]{5,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonAlphaNumericPattern = new("[^A-Za-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConditionalWeakTable<ExplorerItem, MeshTextureIndex> IndexCache = new();

    public static Func<string, string, int, string, IReadOnlyList<string>>? CreateResolver(ExplorerItem root)
    {
        MeshTextureIndex index = IndexCache.GetValue(root, BuildIndex);
        if (index.UniqueDtxByBaseName.Count == 0 &&
            index.UniquePlayerViewDtxByBaseName.Count == 0 &&
            index.NumberedModelDtxByFamily.Count == 0 &&
            index.UniqueUccModelDtxByBaseName.Count == 0 &&
            index.UniqueUccObjectDtxByBaseName.Count == 0)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return (modelReference, meshName, slotIndex, candidateReference) =>
        {
            string modelPath = NormalizePath(modelReference);
            string normalizedMeshName = NormalizeMeshName(meshName);
            string candidateBaseName = GetCandidateBaseName(candidateReference);
            if (modelPath.Length == 0 || (normalizedMeshName.Length == 0 && candidateBaseName.Length == 0))
            {
                return [];
            }

            string key = $"{modelPath}|{normalizedMeshName}|{slotIndex}|{candidateBaseName}";
            if (cache.TryGetValue(key, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> result = ResolveMeshTexture(index, modelPath, normalizedMeshName, slotIndex, candidateBaseName);
            cache[key] = result;
            return result;
        };
    }

    private static MeshTextureIndex BuildIndex(ExplorerItem root)
    {
        var texturesByBaseName = new Dictionary<string, Dictionary<string, ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        var texturePhysicalCopiesByLogicalPath = new Dictionary<string, List<ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        var modelItemsByPath = new Dictionary<string, List<ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ExplorerItem>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ExplorerItem item = pending.Pop();
            if (item.IsFile && item.FileExtension.Equals("ltb", StringComparison.OrdinalIgnoreCase))
            {
                string modelPath = NormalizePath(item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
                if (modelPath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!modelItemsByPath.TryGetValue(modelPath, out List<ExplorerItem>? modelItems))
                    {
                        modelItems = [];
                        modelItemsByPath.Add(modelPath, modelItems);
                    }

                    modelItems.Add(item);
                }
            }

            if (item.IsFile && TextureExtensions.Contains(item.FileExtension.Trim().TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            {
                string logicalPath = NormalizePath(item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
                string baseName = Path.GetFileNameWithoutExtension(logicalPath);
                if (logicalPath.Length > 0 && baseName.Length > 0)
                {
                    if (logicalPath.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) &&
                        item.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase) &&
                        !texturePhysicalCopiesByLogicalPath.TryGetValue(logicalPath, out _))
                    {
                        texturePhysicalCopiesByLogicalPath.Add(logicalPath, []);
                    }

                    if (texturePhysicalCopiesByLogicalPath.TryGetValue(logicalPath, out List<ExplorerItem>? physicalCopies))
                    {
                        physicalCopies.Add(item);
                    }

                    if (!texturesByBaseName.TryGetValue(baseName, out Dictionary<string, ExplorerItem>? logicalVersions))
                    {
                        logicalVersions = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
                        texturesByBaseName.Add(baseName, logicalVersions);
                    }

                    logicalVersions.TryAdd(logicalPath, item);
                }
            }

            foreach (ExplorerItem child in item.Children)
            {
                pending.Push(child);
            }
        }

        var uniquePlayerViewDtxByBaseName = new Dictionary<string, DtxCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var textureName in texturesByBaseName)
        {
            var playerViewVersions = textureName.Value
                .Where(version =>
                    version.Key.StartsWith("ModelTextures/PLAYERVIEW/", StringComparison.OrdinalIgnoreCase) &&
                    version.Value.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase) &&
                    texturePhysicalCopiesByLogicalPath.TryGetValue(version.Key, out List<ExplorerItem>? physicalCopies) &&
                    HasConsistentTextureCopies(physicalCopies))
                .ToArray();
            if (playerViewVersions.Length != 1)
            {
                continue;
            }

            var playerViewVersion = playerViewVersions[0];
            string outputPath = NormalizePath(playerViewVersion.Value.OutputRelativePath);
            if (outputPath.Length == 0)
            {
                continue;
            }

            string textureBaseName = Path.GetFileNameWithoutExtension(playerViewVersion.Key);
            uniquePlayerViewDtxByBaseName[textureName.Key] = new DtxCandidate(
                outputPath,
                playerViewVersion.Key,
                GetContextTokens(textureBaseName));
        }

        var modelFolderTexturesByName = new Dictionary<string, Dictionary<string, ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, ExplorerItem> logicalVersions in texturesByBaseName.Values)
        {
            foreach (var logicalVersion in logicalVersions)
            {
                string logicalPath = logicalVersion.Key;
                if (!logicalPath.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string normalizedBaseName = NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(logicalPath));
                if (normalizedBaseName.Length == 0)
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(logicalPath)?.Replace('\\', '/') ?? string.Empty;
                string key = directory + "/" + normalizedBaseName;
                if (!modelFolderTexturesByName.TryGetValue(key, out Dictionary<string, ExplorerItem>? logicalPaths))
                {
                    logicalPaths = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
                    modelFolderTexturesByName.Add(key, logicalPaths);
                }

                logicalPaths.TryAdd(logicalPath, logicalVersion.Value);
            }
        }

        var uniqueModelFolderDtxByNormalizedBaseName = new Dictionary<string, DtxCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in modelFolderTexturesByName)
        {
            if (pair.Value.Count != 1)
            {
                continue;
            }

            var logicalVersion = pair.Value.Single();
            ExplorerItem item = logicalVersion.Value;
            if (!item.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string outputPath = NormalizePath(item.OutputRelativePath);
            string logicalPath = logicalVersion.Key;
            if (outputPath.Length == 0)
            {
                continue;
            }

            string textureBaseName = Path.GetFileNameWithoutExtension(logicalPath);
            uniqueModelFolderDtxByNormalizedBaseName[pair.Key] = new DtxCandidate(
                outputPath,
                logicalPath,
                GetContextTokens(textureBaseName));
        }

        var uniqueDtxByBaseName = new Dictionary<string, DtxCandidate>(StringComparer.OrdinalIgnoreCase);
        var uniqueUccModelDtxByBaseName = new Dictionary<string, DtxCandidate>(StringComparer.OrdinalIgnoreCase);
        var uniqueUccObjectDtxByBaseName = new Dictionary<string, DtxCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in texturesByBaseName)
        {
            var uccModelVersions = pair.Value
                .Where(version =>
                    version.Key.StartsWith("UCC/ModelAssets/UCC_Textures/Model/", StringComparison.OrdinalIgnoreCase) &&
                    version.Value.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (uccModelVersions.Length == 1)
            {
                var uccModelVersion = uccModelVersions[0];
                string uccOutputPath = NormalizePath(uccModelVersion.Value.OutputRelativePath);
                if (uccOutputPath.Length > 0)
                {
                    string uccTextureBaseName = Path.GetFileNameWithoutExtension(uccModelVersion.Key);
                    uniqueUccModelDtxByBaseName[uccTextureBaseName] = new DtxCandidate(
                        uccOutputPath,
                        uccModelVersion.Key,
                        GetContextTokens(uccTextureBaseName));
                }
            }

            var uccObjectVersions = pair.Value
                .Where(version =>
                    (version.Key.StartsWith("UCC/ModelAssets/UCC_Textures/Object/", StringComparison.OrdinalIgnoreCase) ||
                     version.Key.Contains("/UCC/ModelAssets/UCC_Textures/Object/", StringComparison.OrdinalIgnoreCase)) &&
                    version.Value.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (uccObjectVersions.Length == 1)
            {
                var uccObjectVersion = uccObjectVersions[0];
                string uccOutputPath = NormalizePath(uccObjectVersion.Value.OutputRelativePath);
                if (uccOutputPath.Length > 0)
                {
                    string uccTextureBaseName = Path.GetFileNameWithoutExtension(uccObjectVersion.Key);
                    uniqueUccObjectDtxByBaseName[uccTextureBaseName] = new DtxCandidate(
                        uccOutputPath,
                        uccObjectVersion.Key,
                        GetContextTokens(uccTextureBaseName));
                }
            }

            if (pair.Value.Count != 1)
            {
                continue;
            }

            var logicalVersion = pair.Value.Single();
            ExplorerItem item = logicalVersion.Value;
            string logicalPath = logicalVersion.Key;
            if (!item.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!logicalPath.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) &&
                !MapTexturePathPattern.IsMatch(logicalPath))
            {
                // Name/context matching is only a fallback. UI icons often
                // share an item's display name, but are not mesh materials.
                continue;
            }

            string outputPath = NormalizePath(item.OutputRelativePath);
            if (outputPath.Length == 0)
            {
                continue;
            }

            string textureBaseName = Path.GetFileNameWithoutExtension(logicalPath);
            string[] textureTokens = GetContextTokens(textureBaseName);
            if (textureTokens.Length == 0)
            {
                continue;
            }

            uniqueDtxByBaseName[pair.Key] = new DtxCandidate(outputPath, logicalPath, textureTokens);
        }

        var numberedCandidatesByFamily = new Dictionary<string, Dictionary<int, List<DtxCandidate>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var textureName in texturesByBaseName)
        {
            foreach (var logicalVersion in textureName.Value)
            {
                string logicalPath = logicalVersion.Key;
                if (!logicalPath.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(logicalPath)?.Replace('\\', '/') ?? string.Empty;
                var localVersions = textureName.Value
                    .Where(version => string.Equals(
                        Path.GetDirectoryName(version.Key)?.Replace('\\', '/') ?? string.Empty,
                        directory,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (localVersions.Length != 1 ||
                    !localVersions[0].Value.FileExtension.Equals("dtx", StringComparison.OrdinalIgnoreCase))
                {
                    // Keep exact mirrored-folder families independent of same-named
                    // map copies elsewhere, but reject conflicting formats or paths
                    // inside the mirrored directory itself.
                    continue;
                }

                string baseName = Path.GetFileNameWithoutExtension(logicalPath);
                Match match = NumberedTexturePattern.Match(baseName);
                if (!match.Success || !int.TryParse(match.Groups["ordinal"].Value, out int ordinal) || ordinal is < 0 or > 99)
                {
                    continue;
                }

                string familyStem = match.Groups["base"].Value.TrimEnd('_', '-');
                if (familyStem.Length == 0)
                {
                    continue;
                }

                string familyKey = directory + "/" + familyStem;
                if (!numberedCandidatesByFamily.TryGetValue(familyKey, out Dictionary<int, List<DtxCandidate>>? family))
                {
                    family = [];
                    numberedCandidatesByFamily.Add(familyKey, family);
                }

                if (!family.TryGetValue(ordinal, out List<DtxCandidate>? ordinalCandidates))
                {
                    ordinalCandidates = [];
                    family.Add(ordinal, ordinalCandidates);
                }

                ExplorerItem item = localVersions[0].Value;
                string outputPath = NormalizePath(item.OutputRelativePath);
                if (outputPath.Length > 0)
                {
                    ordinalCandidates.Add(new DtxCandidate(
                        outputPath,
                        logicalPath,
                        GetContextTokens(baseName)));
                }
            }
        }

        var numberedModelDtxByFamily = new Dictionary<string, Dictionary<int, DtxCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in numberedCandidatesByFamily)
        {
            var uniqueOrdinals = family.Value
                .Where(pair => pair.Value.Count == 1)
                .ToDictionary(pair => pair.Key, pair => pair.Value[0]);
            if (uniqueOrdinals.Count == 0 || !HasContiguousZeroOrOneBasedOrdinals(uniqueOrdinals.Keys))
            {
                continue;
            }

            numberedModelDtxByFamily.Add(family.Key, uniqueOrdinals);
        }

        Dictionary<string, ExplorerItem> uniqueModelItemsByPath = modelItemsByPath
            .Where(pair => GetUniqueResourceHash(pair.Value) is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.OrdinalIgnoreCase);
        var verifiedMotionBaseStemsByModelPath = BuildVerifiedMotionBaseStems(
            numberedModelDtxByFamily,
            uniqueModelItemsByPath);
        Func<IEnumerable<string>, IReadOnlyList<string>>? textureConfigResolver =
            LithTechModelTextureConfigIndex.CreateResolver(root);
        Func<string, int, IReadOnlyList<string>>? textureSlotResolver =
            LithTechModelTextureConfigIndex.CreateTextureSlotResolver(root);
        Func<string, System.Windows.Media.ImageSource?>? globalTextureResolver =
            LithTechModelTextureLoader.CreateGlobalResolver(root);
        Func<string, bool>? textureCandidateExists = globalTextureResolver is null
            ? null
            : candidate => globalTextureResolver(candidate) is not null;

        return new MeshTextureIndex(
            uniqueDtxByBaseName,
            uniquePlayerViewDtxByBaseName,
            uniqueModelFolderDtxByNormalizedBaseName,
            uniqueUccModelDtxByBaseName,
            uniqueUccObjectDtxByBaseName,
            numberedModelDtxByFamily,
            verifiedMotionBaseStemsByModelPath,
            modelItemsByPath,
            uniqueModelItemsByPath,
            textureConfigResolver,
            textureSlotResolver,
            textureCandidateExists,
            new Dictionary<string, LithTechModelDocument?>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new object());
    }

    private static bool HasContiguousZeroOrOneBasedOrdinals(IEnumerable<int> ordinalValues)
    {
        int[] ordinals = ordinalValues.Order().ToArray();
        return ordinals.Length > 0 &&
               (ordinals[0] == 0 && ordinals[^1] == ordinals.Length - 1 ||
                ordinals[0] == 1 && ordinals[^1] == ordinals.Length);
    }

    private static string? GetUniqueResourceHash(IReadOnlyList<ExplorerItem> items)
    {
        string[] resourceKeys = items
            .Select(item =>
            {
                string? archiveHash = item.ArchiveFile?.Md5;
                if (!string.IsNullOrWhiteSpace(archiveHash))
                {
                    return "md5:" + archiveHash;
                }

                return item.Kind == ExplorerItemKind.LocalFile
                    ? "local:" + Path.GetFullPath(item.SourcePath)
                    : "path:" + NormalizePath(item.OutputRelativePath);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return resourceKeys.Length == 1 ? resourceKeys[0] : null;
    }

    private static bool HasConsistentTextureCopies(IReadOnlyList<ExplorerItem> items)
    {
        if (items.Count == 0)
        {
            return false;
        }

        if (items.All(item => item.ArchiveFile is not null))
        {
            string[] hashes = items
                .Select(item => item.ArchiveFile?.Md5)
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Select(hash => hash!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return hashes.Length == 1 && items.All(item => !string.IsNullOrWhiteSpace(item.ArchiveFile?.Md5));
        }

        return items.All(item => item.ArchiveFile is null) &&
               items.Select(item => Path.GetFullPath(item.SourcePath))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Count() == 1;
    }

    private static Dictionary<string, string> BuildVerifiedMotionBaseStems(
        IReadOnlyDictionary<string, Dictionary<int, DtxCandidate>> numberedModelDtxByFamily,
        IReadOnlyDictionary<string, ExplorerItem> modelItemsByPath)
    {
        var verifiedBaseStems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decodedModels = new Dictionary<string, LithTechModelDocument?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string modelPath, ExplorerItem motionItem) in modelItemsByPath)
        {
            if (!TryGetMotionBasePath(modelPath, out string? baseModelPath, out string? baseModelStem) ||
                baseModelPath is null ||
                baseModelStem is null ||
                !modelItemsByPath.TryGetValue(baseModelPath, out ExplorerItem? baseItem))
            {
                continue;
            }

            string relativeMotionPath = modelPath["Models/".Length..];
            string relativeDirectory = Path.GetDirectoryName(relativeMotionPath)?.Replace('\\', '/') ?? string.Empty;
            string textureDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? "ModelTextures"
                : "ModelTextures/" + relativeDirectory;
            string familyKey = textureDirectory + "/" + baseModelStem;
            if (!numberedModelDtxByFamily.ContainsKey(familyKey))
            {
                continue;
            }

            LithTechModelDocument? motionDocument = TryDecodeModel(motionItem, modelPath, decodedModels);
            LithTechModelDocument? baseDocument = TryDecodeModel(baseItem, baseModelPath, decodedModels);
            if (motionDocument is null || baseDocument is null || !HaveIdenticalMeshLayouts(motionDocument, baseDocument))
            {
                continue;
            }

            verifiedBaseStems.TryAdd(modelPath, baseModelStem);
        }

        return verifiedBaseStems;
    }

    private static bool TryGetMotionBasePath(
        string modelPath,
        out string? baseModelPath,
        out string? baseModelStem)
    {
        baseModelPath = null;
        baseModelStem = null;
        if (!modelPath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(modelPath).Equals(".ltb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        string? prefixBaseStem = null;
        (string Prefix, string BasePrefix)[] motionPrefixes =
        {
            ("AI_M-motion_", "AI_"),
            ("AI_M-motion-", "AI_"),
            ("M-motion_", string.Empty),
            ("M-motion-", string.Empty)
        };
        foreach ((string prefix, string basePrefix) in motionPrefixes)
        {
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && stem.Length > prefix.Length)
            {
                prefixBaseStem = basePrefix + stem[prefix.Length..];
                break;
            }
        }

        if (prefixBaseStem is not null)
        {
            baseModelStem = prefixBaseStem;
            string directory = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
            baseModelPath = string.IsNullOrWhiteSpace(directory)
                ? $"{baseModelStem}.LTB"
                : $"{directory}/{baseModelStem}.LTB";
            return true;
        }

        const string infixMotionMarker = "_M-MOTION";
        int infixMotionIndex = stem.IndexOf(infixMotionMarker + "_", StringComparison.OrdinalIgnoreCase);
        if (infixMotionIndex > 0 && infixMotionIndex + infixMotionMarker.Length + 1 < stem.Length)
        {
            baseModelStem = stem.Remove(infixMotionIndex, infixMotionMarker.Length);
            string directory = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
            baseModelPath = string.IsNullOrWhiteSpace(directory)
                ? $"{baseModelStem}.LTB"
                : $"{directory}/{baseModelStem}.LTB";
            return true;
        }

        // `_M-motion` also ends with `-motion`; remove the longer marker first
        // so the inferred base stem does not retain a stray `_M` suffix.
        string[] motionSuffixes = ["_M-MOTION", "_MOTION", "-MOTION"];
        foreach (string suffix in motionSuffixes)
        {
            if (stem.Length <= suffix.Length || !stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            baseModelStem = stem[..^suffix.Length];
            string directory = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
            baseModelPath = string.IsNullOrWhiteSpace(directory)
                ? $"{baseModelStem}.LTB"
                : $"{directory}/{baseModelStem}.LTB";
            return true;
        }

        return false;
    }

    private static LithTechModelDocument? TryDecodeModel(
        ExplorerItem item,
        string modelPath,
        Dictionary<string, LithTechModelDocument?> decodedModels)
    {
        string cacheKey = item.ArchiveFile?.Md5 is { Length: > 0 } archiveHash
            ? "md5:" + archiveHash
            : modelPath;
        if (decodedModels.TryGetValue(cacheKey, out LithTechModelDocument? cached))
        {
            return cached;
        }

        byte[]? modelData = TryReadModelBytes(item);
        LithTechModelDocument? document = null;
        if (modelData is not null)
        {
            LithTechModelDecoder.TryDecode(modelData, modelPath, "ltb", out document, out _);
        }

        decodedModels[cacheKey] = document;
        return document;
    }

    private static byte[]? TryReadModelBytes(ExplorerItem item)
    {
        const int maxModelBytes = 64 * 1024 * 1024;
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                return !info.Exists || info.Length <= 0 || info.Length > maxModelBytes
                    ? null
                    : File.ReadAllBytes(item.SourcePath);
            }

            if (item.Kind != ExplorerItemKind.RezFile ||
                item.Archive is null ||
                item.ArchiveFile is null ||
                item.ArchiveFile.Size <= 0 ||
                item.ArchiveFile.Size > maxModelBytes)
            {
                return null;
            }

            using Stream source = RezArchiveReader.OpenFileRead(item.Archive, item.ArchiveFile);
            using var memory = new MemoryStream((int)item.ArchiveFile.Size);
            source.CopyTo(memory);
            return memory.Length is > 0 and <= maxModelBytes ? memory.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool HaveIdenticalMeshLayouts(LithTechModelDocument left, LithTechModelDocument right)
    {
        if (left.Meshes.Count == 0 || left.Meshes.Count != right.Meshes.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Meshes.Count; index++)
        {
            if (!HaveIdenticalMeshLayout(left.Meshes[index], right.Meshes[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HaveIdenticalMeshGeometryAndTextureSlots(
        LithTechModelDocument left,
        LithTechModelDocument right)
    {
        if (left.Meshes.Count != right.Meshes.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Meshes.Count; index++)
        {
            LithTechMesh leftMesh = left.Meshes[index];
            LithTechMesh rightMesh = right.Meshes[index];
            if (!leftMesh.Vertices.SequenceEqual(rightMesh.Vertices) ||
                !leftMesh.TriangleIndices.SequenceEqual(rightMesh.TriangleIndices) ||
                !NullableSequencesEqual(leftMesh.TextureCoordinates, rightMesh.TextureCoordinates) ||
                !NullableSequencesEqual(leftMesh.TextureSlotIndices, rightMesh.TextureSlotIndices))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HaveIdenticalTextureSlotSets(
        LithTechModelDocument left,
        LithTechModelDocument right)
    {
        static int[] GetTextureSlotSet(LithTechModelDocument document) => document.Meshes
            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
            .Distinct()
            .Order()
            .ToArray();

        int[] leftSlots = GetTextureSlotSet(left);
        int[] rightSlots = GetTextureSlotSet(right);
        return leftSlots.Length > 0 && leftSlots.SequenceEqual(rightSlots);
    }

    private static bool HaveIdenticalMeshLayout(LithTechMesh leftMesh, LithTechMesh rightMesh)
    {
        return string.Equals(leftMesh.Name, rightMesh.Name, StringComparison.Ordinal) &&
               string.Equals(leftMesh.TexturePath, rightMesh.TexturePath, StringComparison.OrdinalIgnoreCase) &&
               leftMesh.Vertices.SequenceEqual(rightMesh.Vertices) &&
               leftMesh.TriangleIndices.SequenceEqual(rightMesh.TriangleIndices) &&
               NullableSequencesEqual(leftMesh.TextureCoordinates, rightMesh.TextureCoordinates) &&
               NullableSequencesEqual(leftMesh.TextureSlotIndices, rightMesh.TextureSlotIndices) &&
               NullableSequencesEqual(leftMesh.MaterialHints, rightMesh.MaterialHints, StringComparer.OrdinalIgnoreCase);
    }

    private static bool NullableSequencesEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        return left is null
            ? right is null
            : right is not null && left.SequenceEqual(right);
    }

    private static bool NullableSequencesEqual<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right,
        IEqualityComparer<T> comparer)
    {
        return left is null
            ? right is null
            : right is not null && left.SequenceEqual(right, comparer);
    }

    private static IReadOnlyList<string> ResolveMeshTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex,
        string candidateBaseName)
    {
        IReadOnlyList<string> pvInkTexture = ResolvePvInkTexture(index, modelPath, slotIndex);
        if (pvInkTexture.Count > 0)
        {
            return pvInkTexture;
        }

        IReadOnlyList<string> uccModelMeshTexture = ResolveUccModelMeshTexture(index, modelPath, meshName);
        if (uccModelMeshTexture.Count > 0)
        {
            return uccModelMeshTexture;
        }

        IReadOnlyList<string> playerViewArmTexture = ResolvePlayerViewArmModelTexture(
            index,
            modelPath,
            meshName);
        if (playerViewArmTexture.Count > 0)
        {
            return playerViewArmTexture;
        }

        IReadOnlyList<string> pvDeAngelicBeastTexture = ResolvePvDeAngelicBeastTexture(index, modelPath, slotIndex);
        if (pvDeAngelicBeastTexture.Count > 0)
        {
            return pvDeAngelicBeastTexture;
        }

        IReadOnlyList<string> pvScarLightAngelicBeastTexture = ResolvePvScarLightAngelicBeastTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (pvScarLightAngelicBeastTexture.Count > 0)
        {
            return pvScarLightAngelicBeastTexture;
        }

        IReadOnlyList<string> pvAnniv18thHkg36cTexture = ResolvePvAnniv18thHkg36cTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (pvAnniv18thHkg36cTexture.Count > 0)
        {
            return pvAnniv18thHkg36cTexture;
        }

        IReadOnlyList<string> battleShipOpeningBigCrawlerTexture = ResolveBattleShipOpeningBigCrawlerTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (battleShipOpeningBigCrawlerTexture.Count > 0)
        {
            return battleShipOpeningBigCrawlerTexture;
        }

        IReadOnlyList<string> zumaMachineDragonOpening2Texture = ResolveZumaMachineDragonOpening2Texture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (zumaMachineDragonOpening2Texture.Count > 0)
        {
            return zumaMachineDragonOpening2Texture;
        }

        IReadOnlyList<string> eliteMechanicalDeviceTexture = ResolveEliteMechanicalDeviceVariantTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (eliteMechanicalDeviceTexture.Count > 0)
        {
            return eliteMechanicalDeviceTexture;
        }

        IReadOnlyList<string> ai3HellTowerElevatorCeilingTexture = ResolveAi3HellTowerElevatorCeilingTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (ai3HellTowerElevatorCeilingTexture.Count > 0)
        {
            return ai3HellTowerElevatorCeilingTexture;
        }

        IReadOnlyList<string> aiGigantPredatorMotionTexture = ResolveAiGigantPredatorMotionTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (aiGigantPredatorMotionTexture.Count > 0)
        {
            return aiGigantPredatorMotionTexture;
        }

        IReadOnlyList<string> pvBoxCubeArmTexture = ResolvePvBoxCubeArmTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (pvBoxCubeArmTexture.Count > 0)
        {
            return pvBoxCubeArmTexture;
        }

        IReadOnlyList<string> pvNanoAssassinKnifeTextures = ResolvePvNanoAssassinKnifeTextures(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (pvNanoAssassinKnifeTextures.Count > 0)
        {
            return pvNanoAssassinKnifeTextures;
        }

        IReadOnlyList<string> modelFolderExact = ResolveModelFolderExactTexture(index, modelPath, candidateBaseName);
        if (modelFolderExact.Count > 0)
        {
            return modelFolderExact;
        }

        IReadOnlyList<string> motionBaseTexture = ResolveVerifiedMotionBaseSlotTexture(
            index,
            modelPath,
            meshName,
            slotIndex);
        if (motionBaseTexture.Count > 0)
        {
            return motionBaseTexture;
        }

        string textureBaseName = candidateBaseName.Length > 0 ? candidateBaseName : meshName;
        if (!index.UniqueDtxByBaseName.TryGetValue(textureBaseName, out DtxCandidate? candidate))
        {
            return ResolveNumberedOrMotionBaseTexture(index, modelPath, meshName, slotIndex);
        }

        if (meshName.Equals(candidateBaseName, StringComparison.OrdinalIgnoreCase) &&
            NumberedMapMeshNamePattern.IsMatch(meshName) &&
            MapTexturePathPattern.IsMatch(candidate.LogicalPath))
        {
            // Map-owned scene meshes sometimes carry only a numbered mesh/material
            // name in LTB, with no LTO or texture-config reference. Require an
            // exact globally unique DTX basename and a numbered mesh name so
            // generic names such as "rock" cannot bind unrelated map textures.
            return [candidate.OutputPath];
        }

        string[] modelTokens = GetContextTokens(modelPath);
        if (!modelTokens.Intersect(candidate.TextureTokens, StringComparer.OrdinalIgnoreCase).Any())
        {
            return ResolveNumberedOrMotionBaseTexture(index, modelPath, meshName, slotIndex);
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePlayerViewArmModelTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName)
    {
        string normalizedModelPath = NormalizePath(modelPath);
        bool isArmModelPath = normalizedModelPath.StartsWith("ArmModel/Arm_", StringComparison.OrdinalIgnoreCase) ||
                              normalizedModelPath.Contains("/ArmModel/Arm_", StringComparison.OrdinalIgnoreCase);
        if (!isArmModelPath)
        {
            return [];
        }

        string modelStem = GetModelResourceStem(normalizedModelPath);
        string variantSuffix = PlayerViewArmModelVariantSuffixes.FirstOrDefault(suffix =>
            modelStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        string meshTextureBaseName = Path.GetFileNameWithoutExtension(meshName);
        string modelFamily = modelStem.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase)
            ? modelStem["Arm_".Length..]
            : modelStem;
        string unvariantedModelFamily = variantSuffix.Length > 0 && modelFamily.EndsWith(variantSuffix, StringComparison.OrdinalIgnoreCase)
            ? modelFamily[..^variantSuffix.Length]
            : modelFamily;
        string teamSuffix = variantSuffix.EndsWith("_BL", StringComparison.OrdinalIgnoreCase) ||
                            variantSuffix.EndsWith("_GR", StringComparison.OrdinalIgnoreCase) ||
                            variantSuffix.EndsWith("_SP", StringComparison.OrdinalIgnoreCase)
            ? variantSuffix[^3..]
            : string.Empty;

        string meshFamily = GetPlayerViewArmMeshFamily(meshName);
        string meshVariantSuffix = PlayerViewArmModelVariantSuffixes.FirstOrDefault(suffix =>
            meshFamily.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        string unvariantedMeshFamily = meshVariantSuffix.Length > 0
            ? meshFamily[..^meshVariantSuffix.Length]
            : meshFamily;
        string[] roles = GetPlayerViewArmMeshRoles(meshName);
        if (roles.Length == 0)
        {
            return [];
        }

        var candidatePriorityGroups = new List<HashSet<string>>();
        if (meshTextureBaseName.Length > 0)
        {
            bool meshAndModelVariantsConflict = meshVariantSuffix.Length > 0 &&
                teamSuffix.Length > 0 &&
                !meshVariantSuffix.EndsWith(teamSuffix, StringComparison.OrdinalIgnoreCase);
            if (!meshAndModelVariantsConflict)
            {
                bool meshAlreadyHasTeamVariant = meshVariantSuffix.Length > 0 &&
                    teamSuffix.Length > 0 &&
                    meshVariantSuffix.EndsWith(teamSuffix, StringComparison.OrdinalIgnoreCase);
                if (meshAlreadyHasTeamVariant || variantSuffix.Length == 0)
                {
                    candidatePriorityGroups.Add(new HashSet<string>([meshTextureBaseName], StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    candidatePriorityGroups.Add(new HashSet<string>([meshTextureBaseName + variantSuffix], StringComparer.OrdinalIgnoreCase));
                    candidatePriorityGroups.Add(new HashSet<string>([meshTextureBaseName], StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        if (meshTextureBaseName.Length > 0 && unvariantedModelFamily.Length > 0)
        {
            foreach (string suffix in new[] { variantSuffix, teamSuffix, string.Empty }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var meshSpecificFamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string role in roles)
                {
                    meshSpecificFamilyNames.Add($"FVIEW_{role}_{unvariantedModelFamily}_{meshTextureBaseName}{suffix}");
                }

                candidatePriorityGroups.Add(meshSpecificFamilyNames);
            }
        }

        string[] familyNames = new[] { unvariantedMeshFamily, unvariantedModelFamily, modelFamily }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length)
            .ToArray();
        string[] variantSuffixes = [variantSuffix, teamSuffix, string.Empty];
        foreach (string suffix in variantSuffixes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string familyName in familyNames)
            {
                var familyCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string role in roles)
                {
                    string candidateFamily = suffix.Length > 0 && familyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? familyName
                        : familyName + suffix;
                    familyCandidates.Add($"FVIEW_{role}_{candidateFamily}");
                }

                candidatePriorityGroups.Add(familyCandidates);
            }
        }

        foreach (HashSet<string> candidateNames in candidatePriorityGroups)
        {
            string[] matches = candidateNames
                .Where(textureBaseName => index.UniquePlayerViewDtxByBaseName.ContainsKey(textureBaseName))
                .Select(textureBaseName => index.UniquePlayerViewDtxByBaseName[textureBaseName].OutputPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length > 0)
            {
                return matches.Length == 1 ? matches : [];
            }
        }

        return [];
    }

    private static string[] GetPlayerViewArmMeshRoles(string meshName)
    {
        string terminalName = Regex.Replace(meshName, @"\d+$", string.Empty, RegexOptions.CultureInvariant);
        if (Regex.IsMatch(meshName, @"(?i)^FVIEW[\s_-]", RegexOptions.CultureInvariant))
        {
            string rolePrefix = Regex.Replace(meshName, @"(?i)^FVIEW[\s_-]*", string.Empty, RegexOptions.CultureInvariant);
            if (Regex.IsMatch(rolePrefix, @"(?i)^HAND(?:$|[\s_-])", RegexOptions.CultureInvariant))
            {
                return ["HAND"];
            }

            if (Regex.IsMatch(rolePrefix, @"(?i)^ARM(?:$|[\s_-])", RegexOptions.CultureInvariant))
            {
                return ["ARM"];
            }
        }

        if (Regex.IsMatch(terminalName, @"(?i)[\s_-]+HAND$", RegexOptions.CultureInvariant))
        {
            return ["HAND"];
        }

        if (Regex.IsMatch(terminalName, @"(?i)[\s_-]+ARM$", RegexOptions.CultureInvariant))
        {
            return ["ARM"];
        }

        bool isHandMesh = meshName.Contains("hand", StringComparison.OrdinalIgnoreCase);
        bool isArmMesh = meshName.Contains("arm", StringComparison.OrdinalIgnoreCase);
        if (isHandMesh && !isArmMesh)
        {
            return ["HAND"];
        }

        if (isArmMesh && !isHandMesh)
        {
            return ["ARM"];
        }

        if (isHandMesh && isArmMesh)
        {
            if (terminalName.EndsWith("hand", StringComparison.OrdinalIgnoreCase))
            {
                return ["HAND"];
            }

            if (terminalName.EndsWith("arm", StringComparison.OrdinalIgnoreCase))
            {
                return ["ARM"];
            }

            return [];
        }

        // Some arm-model meshes use names such as "Left" or "default".
        // Let the exact candidate set disambiguate only when one role exists.
        return ["ARM", "HAND"];
    }

    private static string GetPlayerViewArmMeshFamily(string meshName)
    {
        string family = Path.GetFileNameWithoutExtension(meshName).Trim();
        family = Regex.Replace(family, @"\d+$", string.Empty, RegexOptions.CultureInvariant);
        family = Regex.Replace(family, @"(?i)^FVIEW[\s_-]*", string.Empty, RegexOptions.CultureInvariant);
        family = Regex.Replace(family, @"(?i)^(ARM|HAND)[\s_-]*", string.Empty, RegexOptions.CultureInvariant);
        family = Regex.Replace(family, @"(?i)[\s_-]+(ARM|HAND)$", string.Empty, RegexOptions.CultureInvariant);
        return family.Trim(' ', '_', '-');
    }

    private static IReadOnlyList<string> ResolveUccModelMeshTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName)
    {
        string? texturePath = ResolveUccModelMeshTexturePath(
            modelPath,
            meshName,
            textureBaseName => index.UniqueUccModelDtxByBaseName.TryGetValue(textureBaseName, out DtxCandidate? candidate)
                ? candidate.OutputPath
                : null,
            textureBaseName => index.UniqueUccObjectDtxByBaseName.TryGetValue(textureBaseName, out DtxCandidate? candidate)
                ? candidate.OutputPath
                : null);
        return string.IsNullOrWhiteSpace(texturePath) ? [] : [texturePath];
    }

    internal static string? ResolveUccModelMeshTexturePath(
        string modelPath,
        string meshName,
        Func<string, string?> modelTexturePathByBaseName,
        Func<string, string?> objectTexturePathByBaseName)
    {
        const string UccModelRoot = "UCC/ModelAssets/UCC_Model/";
        const string UccObjectRoot = "UCC/ModelAssets/UCC_Object/LTB/";
        Func<string, string?>? texturePathByBaseName =
            modelPath.Contains(UccModelRoot, StringComparison.OrdinalIgnoreCase)
                ? modelTexturePathByBaseName
                : modelPath.Contains(UccObjectRoot, StringComparison.OrdinalIgnoreCase)
                    ? objectTexturePathByBaseName
                    : null;
        if (texturePathByBaseName is null ||
            !Path.GetExtension(modelPath).Equals(".ltb", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string candidateBaseName = meshName.Trim();
        if (candidateBaseName.Length == 0)
        {
            return null;
        }

        string? exactPath = texturePathByBaseName(candidateBaseName);
        if (!string.IsNullOrWhiteSpace(exactPath))
        {
            return exactPath;
        }

        const string NonShadowSuffix = "_NS";
        if (candidateBaseName.EndsWith(NonShadowSuffix, StringComparison.OrdinalIgnoreCase) &&
            candidateBaseName.Length > NonShadowSuffix.Length)
        {
            return texturePathByBaseName(candidateBaseName[..^NonShadowSuffix.Length]);
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveNumberedOrMotionBaseTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        IReadOnlyList<string> numberedTexture = ResolveNumberedModelTexture(index, modelPath, slotIndex);
        return numberedTexture.Count > 0
            ? numberedTexture
            : ResolveVerifiedMotionBaseSlotTexture(index, modelPath, meshName, slotIndex);
    }

    private static IReadOnlyList<string> ResolveVerifiedMotionBaseSlotTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        if (slotIndex < 0 ||
            !TryGetMotionBasePath(modelPath, out string? baseModelPath, out string? baseModelStem) ||
            baseModelPath is null ||
            baseModelStem is null ||
            !index.ModelItemsByPath.TryGetValue(modelPath, out List<ExplorerItem>? motionItems) ||
            motionItems.Count == 0 ||
            !index.ModelItemsByPath.TryGetValue(baseModelPath, out List<ExplorerItem>? baseItems) ||
            baseItems.Count == 0)
        {
            return [];
        }

        lock (index.MotionBaseVerificationSync)
        {
            string verificationKey = $"{modelPath}|{meshName}|{slotIndex}";
            if (!index.VerifiedMotionBaseMeshes.TryGetValue(verificationKey, out bool identicalLayouts))
            {
                identicalLayouts = true;
                LithTechMesh? targetMeshLayout = null;
                foreach (ExplorerItem motionItem in motionItems)
                {
                    LithTechModelDocument? decoded = TryDecodeModel(motionItem, modelPath, index.DecodedModels);
                    LithTechMesh[] matchingMeshes = decoded?.Meshes
                        .Where(mesh => string.Equals(mesh.Name, meshName, StringComparison.Ordinal) &&
                                       (mesh.TextureSlotIndices ?? []).Any(value => value == slotIndex))
                        .ToArray() ?? [];
                    if (matchingMeshes.Length != 1 ||
                        (targetMeshLayout is not null && !HaveIdenticalMeshLayout(targetMeshLayout, matchingMeshes[0])))
                    {
                        identicalLayouts = false;
                        break;
                    }

                    targetMeshLayout ??= matchingMeshes[0];
                }

                LithTechMesh? baseMeshLayout = null;
                foreach (ExplorerItem baseItem in identicalLayouts ? baseItems : [])
                {
                    LithTechModelDocument? decoded = TryDecodeModel(baseItem, baseModelPath, index.DecodedModels);
                    LithTechMesh[] matchingMeshes = decoded?.Meshes
                        .Where(mesh => string.Equals(mesh.Name, meshName, StringComparison.Ordinal) &&
                                       (mesh.TextureSlotIndices ?? []).Any(value => value == slotIndex))
                        .ToArray() ?? [];
                    if (matchingMeshes.Length != 1 ||
                        (baseMeshLayout is not null && !HaveIdenticalMeshLayout(baseMeshLayout, matchingMeshes[0])) ||
                        (targetMeshLayout is not null && !HaveIdenticalMeshLayout(targetMeshLayout, matchingMeshes[0])))
                    {
                        identicalLayouts = false;
                        break;
                    }

                    baseMeshLayout ??= matchingMeshes[0];
                }

                identicalLayouts = identicalLayouts &&
                                   targetMeshLayout is not null &&
                                   baseMeshLayout is not null;
                index.VerifiedMotionBaseMeshes[verificationKey] = identicalLayouts;
            }

            if (!identicalLayouts)
            {
                return [];
            }

            IReadOnlyList<string> configuredTextures = index.TextureSlotResolver?.Invoke(baseModelPath, slotIndex) ?? [];
            if (configuredTextures.Count > 0)
            {
                return configuredTextures;
            }

            LithTechModelDocument? verifiedBaseDocument = TryDecodeModel(baseItems[0], baseModelPath, index.DecodedModels);
            if (verifiedBaseDocument is not null && index.TextureCandidateExists is not null)
            {
                var donorSource = new LithTechObjExportSource(
                    Path.GetFileName(baseModelPath),
                    baseModelPath,
                    verifiedBaseDocument,
                    null,
                    index.TextureConfigResolver,
                    index.TextureSlotResolver,
                    null);
                foreach (LithTechMesh donorMesh in verifiedBaseDocument.Meshes.Where(mesh =>
                             string.Equals(mesh.Name, meshName, StringComparison.OrdinalIgnoreCase) &&
                             (mesh.TextureSlotIndices ?? []).Select(Convert.ToInt32).Distinct().SequenceEqual([slotIndex])))
                {
                    foreach (string textureCandidate in LithTechObjExporter.EnumerateTextureCandidates(donorMesh, donorSource))
                    {
                        if (index.TextureCandidateExists(textureCandidate))
                        {
                            return [textureCandidate];
                        }
                    }
                }
            }

            string relativeBasePath = baseModelPath["Models/".Length..];
            string relativeDirectory = Path.GetDirectoryName(relativeBasePath)?.Replace('\\', '/') ?? string.Empty;
            string textureDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? "ModelTextures"
                : "ModelTextures/" + relativeDirectory;
            string exactTextureKey = textureDirectory + "/" + NormalizeTextureBaseName(baseModelStem);
            return index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) &&
                   string.Equals(
                       NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                       NormalizeTextureBaseName(baseModelStem),
                       StringComparison.OrdinalIgnoreCase)
                ? [candidate.OutputPath]
                : [];
        }
    }

    private static IReadOnlyList<string> ResolvePvInkTexture(MeshTextureIndex index, string modelPath, int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        if (!PvInkPlayerviewModelStems.Contains(stem))
        {
            return [];
        }

        string textureBaseName = slotIndex switch
        {
            0 => "PV-INK_DRILL",
            1 => "PV-INK_ARM",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePvDeAngelicBeastTexture(
        MeshTextureIndex index,
        string modelPath,
        int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        bool isBaseModel = string.Equals(stem, "PV-D.E_AngelicBeast", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(stem, "PV-D.E_AngelicBeast_GunFu", StringComparison.OrdinalIgnoreCase);
        bool isTeamModel = PvDeAngelicBeastTeamModelStems.Contains(stem);
        if (!isBaseModel && !isTeamModel)
        {
            return [];
        }

        string textureBaseName = slotIndex switch
        {
            // These numbered CFT/LTC-free preview models encode the hand and
            // arm as slots 0 and 1. Only team-specific model stems have a
            // matching, unambiguous BL/GR first-person hand/arm texture pair.
            0 when isTeamModel => "FVIEW_HAND_DesertEagleAngelicBeast_" + (stem.EndsWith("_GR", StringComparison.OrdinalIgnoreCase) ? "GR" : "BL"),
            1 when isTeamModel => "FVIEW_ARM_DesertEagleAngelicBeast_" + (stem.EndsWith("_GR", StringComparison.OrdinalIgnoreCase) ? "GR" : "BL"),
            // BF005.LTC explicitly associates the D.E Angelic Beast weapon
            // and knife textures with this item's playerview weapon assets.
            2 => "PV-D.E_AngelicBeast",
            3 => "PV-D.E_AngelicBeast_Knife",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePvScarLightAngelicBeastTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !PvScarLightAngelicBeastModelStems.Contains(Path.GetFileNameWithoutExtension(modelPath)))
        {
            return [];
        }

        string textureBaseName = slotIndex switch
        {
            0 when meshName.Equals("Hand", StringComparison.OrdinalIgnoreCase) => "FVIEW_HAND_ScarLight_S_AngelicBeast",
            1 when meshName.Equals("Arm", StringComparison.OrdinalIgnoreCase) => "FVIEW_ARM_ScarLight_S_AngelicBeast",
            // The ARM atlas contains the named weapon body, core, and magazine
            // artwork used by slot 2 in this exact first-person model family.
            2 => "FVIEW_ARM_ScarLight_S_AngelicBeast",
            3 when meshName.Contains("Wing", StringComparison.OrdinalIgnoreCase) => "FVIEW_BLADE_ScarLight_S_AngelicBeast",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePvAnniv18thHkg36cTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        if (!PvAnniv18thHkg36cModelStems.Contains(stem))
        {
            return [];
        }

        bool isGreenTeam = stem.EndsWith("_GR", StringComparison.OrdinalIgnoreCase);
        string team = isGreenTeam ? "GR" : "BL";
        string textureBaseName = (slotIndex, meshName.ToUpperInvariant()) switch
        {
            (0, "FVIEW-HAND2") => "FVIEW_Anniversary_18th_HAND_" + team,
            (1, "FVIEW-ARM2") => "FVIEW_Anniversary_18th_ARM_" + team,
            // The root DTX atlas is the matching character/body sheet; LTC
            // lists it before the _01 weapon atlas for this exact model family.
            (2, "BASEBODY_GIRL001") => "PV-Anniv18th_BL_HKG36C",
            // The canonical LTC-mapped model resolves both M82A1 switcher
            // pieces to this _01 atlas; copied variants embed that same model
            // identity even though their native slot value is 0.
            (0, "SWITCHER_M82A1_003" or "SWITCHER_M82A1_004") => "PV-Anniv18th_BL_HKG36C_01",
            // The principal gun mesh and its two small gun pieces share native
            // slot 3 and the same LTC-listed firearm atlas.
            (3, "PV-HKG36C_18THANNIV_01" or "MAC1" or "MAC002") => "PV-Anniv18th_BL_HKG36C_01",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolveBattleShipOpeningBigCrawlerTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/CHARACTER/AI2_BattleShip/MatineeData/";
        if (slotIndex != 0 ||
            !modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Ai2BattleShipOpeningBigCrawlerModelStems.Contains(Path.GetFileNameWithoutExtension(modelPath)) ||
            !meshName.StartsWith("BigClawler", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // AICharacter.CFT maps the BattleShip cocoon-child BigCrawler to this
        // standard atlas. Its canonical BigCrawler_tongue mesh and these
        // BigClawler tongue pieces share the same UV bounds; the Elite atlas is
        // separately assigned to BrokenStationHard rows and is not selected.
        const string textureBaseName = "AI_BigCrawler";
        const string textureDirectory = "ModelTextures/CHARACTER/AI2_DefenceMode/";
        string exactTextureKey = textureDirectory + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolveZumaMachineDragonOpening2Texture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/ZUMA/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !ZumaMachineDragonOpening2MeshNames.Contains(meshName) ||
            slotIndex is not (0 or 1))
        {
            return [];
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        string textureBaseName = stem switch
        {
            // Opening1's actual export audit resolves all slots to this atlas.
            // Opening2 preserves the same 21 auxiliary mesh UV layouts; its
            // differently tessellated body keeps the canonical AI_MachineDragon
            // body name and bounds used by the CFT-mapped AI_DragonHead model.
            "MachineDragonOpening2" => "AI_DragonHead",
            // The two Opening*_M-motion models have identical complete mesh,
            // slot, and UV layouts, and Opening1's actual audit resolves to
            // the ZUMA BasicCube atlas.
            "MachineDragonOpening2_M-motion" => "AI_BasicCube",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        string exactTextureKey = "ModelTextures/ZUMA/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolveEliteMechanicalDeviceVariantTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string elitePveDirectory = "Models/CHARACTER/ElitePVE/Character/";
        const string quaterViewDirectory = "Models/CHARACTER/Quaterview/";
        if (!(modelPath.StartsWith(elitePveDirectory, StringComparison.OrdinalIgnoreCase) ||
              modelPath.StartsWith(quaterViewDirectory, StringComparison.OrdinalIgnoreCase)) ||
            !EliteMechanicalDeviceVariantModelStems.Contains(Path.GetFileNameWithoutExtension(modelPath)) ||
            !EliteMechanicalDeviceMeshNames.Contains(meshName))
        {
            return [];
        }

        string textureBaseName = slotIndex switch
        {
            0 => "AI_MechanicalDevice01",
            1 => "AI_MechanicalDevice02",
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        // AICharacter.CFT maps EliteBoss_MechanicalDevice and its related
        // motion model to these slot-specific atlases. The UFO and
        // UnknownDestructor variants, including their motion pairs, have
        // identical complete mesh layouts to that canonical model.
        string exactTextureKey = "ModelTextures/CHARACTER/AI_Boss_Arena/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePvBoxCubeArmTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string stem = Path.GetFileNameWithoutExtension(modelPath);
        if (!PvBoxCubeTeamModelStems.Contains(stem))
        {
            return [];
        }

        string team = stem.EndsWith("_GR", StringComparison.OrdinalIgnoreCase) ? "GR" : "BL";
        bool isWoman = stem.Contains("_WOMAN_", StringComparison.OrdinalIgnoreCase);
        string genderSuffix = isWoman ? "_WOMAN" : string.Empty;
        string textureBaseName = (slotIndex, meshName.ToUpperInvariant()) switch
        {
            (0, "FVIEW-HAND2") => "FVIEW_HAND" + genderSuffix + "_" + team,
            (1, "FVIEW-ARM2") => "FVIEW_ARM" + genderSuffix + "_" + team,
            _ => string.Empty
        };
        if (textureBaseName.Length == 0)
        {
            return [];
        }

        // The BL/GR CharacterType.CFT ArmModel rows map these exact hand/arm
        // atlases. PV-BoxCube's first two meshes are geometry-identical to the
        // canonical Arm_MAN meshes; WOMAN variants retain the same slot/UV
        // layout and use the corresponding WOMAN atlas family.
        string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !string.Equals(
                NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                NormalizeTextureBaseName(textureBaseName),
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolveAi3HellTowerElevatorCeilingTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/AI3HELLTOWER/";
        const string textureBaseName = "AI3_Tower1F_201";
        const string expectedTexturePath = "MAP30/AI3_Tower_Elevator/AI3_Tower1F_201.DTX";
        if (slotIndex != 0 ||
            !modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Ai3HellTowerElevatorCeilingModelStems.Contains(Path.GetFileNameWithoutExtension(modelPath)) ||
            !meshName.Equals("AI3_Tower_201", StringComparison.OrdinalIgnoreCase) ||
            !index.UniqueDtxByBaseName.TryGetValue(textureBaseName, out DtxCandidate? candidate) ||
            !candidate.LogicalPath.Equals(expectedTexturePath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // The three elevator-ceiling LTBs all expose AI3_Tower_201 in slot 0.
        // Their remaining same-named meshes resolve to the MAP30 atlas family;
        // AI3_Tower1F_201.DTX is the unique matching tile for this slot.
        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolveAiGigantPredatorMotionTexture(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelPathExpected = "Models/CHARACTER/AI4/AI_M-motion_GigantPredator.LTB";
        const string textureDirectory = "ModelTextures/CHARACTER/AI4/GigantPredator/";
        string textureBaseName = (slotIndex, meshName) switch
        {
            (0, "Object176") => "AI_GigantPredator_body1",
            (2, _) when AiGigantPredatorMotionRegMeshNames.Contains(meshName) => "AI_GigantPredator_reg",
            _ => string.Empty
        };
        if (!modelPath.Equals(modelPathExpected, StringComparison.OrdinalIgnoreCase) || textureBaseName.Length == 0)
        {
            return [];
        }

        string expectedTexturePath = textureDirectory + textureBaseName + ".DTX";
        string exactTextureKey = textureDirectory + NormalizeTextureBaseName(textureBaseName);
        if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
            !candidate.LogicalPath.Equals(expectedTexturePath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // AI_GigantPredator and its M-motion model share exact UVs and topology
        // for Object176 (slot 0) and the four slot-2 ssdsd meshes. The base
        // model's exporter-resolved slot textures are body1 and reg respectively.
        // Object175 changes topology/UVs in the motion model, so slot 1 is
        // intentionally left unresolved instead of inheriting body2 by name.
        return [candidate.OutputPath];
    }

    private static IReadOnlyList<string> ResolvePvNanoAssassinKnifeTextures(
        MeshTextureIndex index,
        string modelPath,
        string meshName,
        int slotIndex)
    {
        const string modelDirectory = "Models/PLAYERVIEW/";
        if (!modelPath.StartsWith(modelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !PvNanoAssassinKnifeModelStems.Contains(Path.GetFileNameWithoutExtension(modelPath)))
        {
            return [];
        }

        string normalizedMeshName = meshName.ToUpperInvariant();
        bool expectedMeshSlot = (slotIndex, normalizedMeshName) switch
        {
            (0, "FVIEW-HAND2") => true,
            (1, "FVIEW-ARM2") => true,
            (2, "BOSS_KNIFE01" or "BOSS_KNIFE02" or "BOSS_KNIFE03" or
                "BOSS_KNIFE04" or "BOSS_KNIFE05" or "BOSS_KNIFE06") => true,
            _ => false
        };
        if (!expectedMeshSlot)
        {
            return [];
        }

        // The LTC maps the canonical PV_NANO_KNIFE model to both atlas
        // candidates in this order. The two Assassin variants have byte-for-
        // byte identical mesh layouts (including slots and UVs), so preserve
        // the same model-level texture set instead of guessing which runtime
        // weapon state is active.
        string[] textureBaseNames = ["PV_NANO_KNIFE", "PV_NANO_UP_KNIFE"];
        var candidates = new List<string>(textureBaseNames.Length);
        foreach (string textureBaseName in textureBaseNames)
        {
            string exactTextureKey = "ModelTextures/PLAYERVIEW/" + NormalizeTextureBaseName(textureBaseName);
            if (!index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(exactTextureKey, out DtxCandidate? candidate) ||
                !string.Equals(
                    NormalizeTextureBaseName(Path.GetFileNameWithoutExtension(candidate.LogicalPath)),
                    NormalizeTextureBaseName(textureBaseName),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!candidates.Contains(candidate.OutputPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate.OutputPath);
            }
        }

        return candidates;
    }

    private static IReadOnlyList<string> ResolveModelFolderExactTexture(
        MeshTextureIndex index,
        string modelPath,
        string candidateBaseName)
    {
        if (candidateBaseName.Length == 0 || !modelPath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string relativeModelPath = modelPath["Models/".Length..];
        string relativeDirectory = Path.GetDirectoryName(relativeModelPath)?.Replace('\\', '/') ?? string.Empty;
        string textureDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? "ModelTextures"
            : "ModelTextures/" + relativeDirectory;
        string normalizedCandidate = NormalizeTextureBaseName(candidateBaseName);
        if (normalizedCandidate.Length == 0)
        {
            return [];
        }

        string key = textureDirectory + "/" + normalizedCandidate;
        return index.UniqueModelFolderDtxByNormalizedBaseName.TryGetValue(key, out DtxCandidate? candidate)
            ? [candidate.OutputPath]
            : [];
    }

    private static string NormalizeTextureBaseName(string value) => NonAlphaNumericPattern.Replace(value, string.Empty);

    private static IReadOnlyList<string> ResolveNumberedModelTexture(MeshTextureIndex index, string modelPath, int slotIndex)
    {
        if (slotIndex < 0 || !modelPath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string relativeModelPath = modelPath["Models/".Length..];
        string relativeDirectory = Path.GetDirectoryName(relativeModelPath)?.Replace('\\', '/') ?? string.Empty;
        string textureDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? "ModelTextures"
            : "ModelTextures/" + relativeDirectory;
        string modelStem = GetModelResourceStem(relativeModelPath);
        var modelStemVariants = EnumerateModelStemVariants(modelStem).ToList();
        bool hasVerifiedMotionBaseStem = index.VerifiedMotionBaseStemsByModelPath.TryGetValue(modelPath, out string? verifiedBaseStem);
        if (hasVerifiedMotionBaseStem && verifiedBaseStem is not null &&
            !modelStemVariants.Contains(verifiedBaseStem, StringComparer.OrdinalIgnoreCase))
        {
            modelStemVariants.Insert(0, verifiedBaseStem);
        }

        foreach (string modelStemVariant in modelStemVariants)
        {
            string familyKey = textureDirectory + "/" + modelStemVariant;
            if (!index.NumberedModelDtxByFamily.TryGetValue(familyKey, out Dictionary<int, DtxCandidate>? family))
            {
                continue;
            }

            if (!HasContiguousZeroOrOneBasedOrdinals(family.Keys))
            {
                continue;
            }

            if (family.Count == 1 &&
                !string.Equals(modelStemVariant, modelStem, StringComparison.OrdinalIgnoreCase) &&
                !(hasVerifiedMotionBaseStem && verifiedBaseStem is not null &&
                  string.Equals(modelStemVariant, verifiedBaseStem, StringComparison.OrdinalIgnoreCase)))
            {
                // A lone _00/_01 texture is enough only for the exact same
                // model stem. Variant-suffix stripping is supported only for
                // families whose ordinal sequence is independently evidenced.
                continue;
            }

            int firstOrdinal = family.ContainsKey(0) ? 0 : 1;
            if (family.TryGetValue(firstOrdinal + slotIndex, out DtxCandidate? candidate))
            {
                return [candidate.OutputPath];
            }
        }

        return ResolveInferredMotionNumberedTexture(index, modelPath, textureDirectory, slotIndex);
    }

    private static IReadOnlyList<string> ResolveInferredMotionNumberedTexture(
        MeshTextureIndex index,
        string modelPath,
        string textureDirectory,
        int slotIndex)
    {
        if (!TryGetMotionBasePath(modelPath, out _, out string? motionBaseStem) ||
            motionBaseStem is null ||
            !index.ModelItemsByPath.TryGetValue(modelPath, out List<ExplorerItem>? modelItems) ||
            modelItems.Count == 0 ||
            !index.NumberedModelDtxByFamily.TryGetValue(
                textureDirectory + "/" + motionBaseStem,
                out Dictionary<int, DtxCandidate>? family) ||
            family.Count < 2 ||
            !HasContiguousZeroOrOneBasedOrdinals(family.Keys))
        {
            return [];
        }

        int[]? modelSlots = null;
        foreach (ExplorerItem modelItem in modelItems)
        {
            LithTechModelDocument? document = TryDecodeModel(modelItem, modelPath, index.DecodedModels);
            if (document is null)
            {
                return [];
            }

            int[] currentSlots = document.Meshes
                .SelectMany(mesh => mesh.TextureSlotIndices ?? Array.Empty<int>())
                .Distinct()
                .Order()
                .ToArray();
            if (currentSlots.Length != family.Count ||
                !currentSlots.SequenceEqual(Enumerable.Range(0, family.Count)) ||
                (modelSlots is not null && !modelSlots.SequenceEqual(currentSlots)))
            {
                return [];
            }

            modelSlots ??= currentSlots;
        }

        int firstOrdinal = family.ContainsKey(0) ? 0 : 1;
        if (!family.TryGetValue(firstOrdinal + slotIndex, out DtxCandidate? candidate) ||
            index.TextureCandidateExists is null ||
            !index.TextureCandidateExists(candidate.OutputPath))
        {
            return [];
        }

        return [candidate.OutputPath];
    }

    private static IEnumerable<string> EnumerateModelStemVariants(string modelStem)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = modelStem;
        if (current.Length > 0 && yielded.Add(current))
        {
            yield return current;
        }

        bool stripped;
        do
        {
            stripped = false;
            foreach (string suffix in ModelVariantSuffixes)
            {
                if (current.Length <= suffix.Length || !current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                current = current[..^suffix.Length];
                if (current.Length > 0 && yielded.Add(current))
                {
                    yield return current;
                }

                stripped = true;
                break;
            }
        }
        while (stripped);
    }

    private static string GetModelResourceStem(string value)
    {
        string fileName = Path.GetFileName(value);
        string extension = Path.GetExtension(fileName);
        return extension.Equals(".ltb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lta", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ltc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
    }

    private static string[] GetContextTokens(string value)
    {
        return ContextTokenPattern.Matches(value)
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(token => !IgnoredContextTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeMeshName(string value)
    {
        string name = value.Trim().Trim('"', '\'').Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name;
    }

    private static string GetCandidateBaseName(string candidateReference)
    {
        string fileName = Path.GetFileName(NormalizePath(candidateReference));
        string extension = Path.GetExtension(fileName);
        if (TextureExtensions.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
        {
            return fileName[..^extension.Length];
        }

        // Dotted mesh names are valid (for example AI3.0_Door); only remove
        // extensions the game actually uses for texture resources.
        return fileName;
    }

    private static string NormalizePath(string value)
    {
        return value.Trim().Trim('"', '\'').Replace('\\', '/').TrimStart('/');
    }

    private sealed record DtxCandidate(string OutputPath, string LogicalPath, string[] TextureTokens);

    private sealed record MeshTextureIndex(
        Dictionary<string, DtxCandidate> UniqueDtxByBaseName,
        Dictionary<string, DtxCandidate> UniquePlayerViewDtxByBaseName,
        Dictionary<string, DtxCandidate> UniqueModelFolderDtxByNormalizedBaseName,
        Dictionary<string, DtxCandidate> UniqueUccModelDtxByBaseName,
        Dictionary<string, DtxCandidate> UniqueUccObjectDtxByBaseName,
        Dictionary<string, Dictionary<int, DtxCandidate>> NumberedModelDtxByFamily,
        Dictionary<string, string> VerifiedMotionBaseStemsByModelPath,
        Dictionary<string, List<ExplorerItem>> ModelItemsByPath,
        Dictionary<string, ExplorerItem> UniqueModelItemsByPath,
        Func<IEnumerable<string>, IReadOnlyList<string>>? TextureConfigResolver,
        Func<string, int, IReadOnlyList<string>>? TextureSlotResolver,
        Func<string, bool>? TextureCandidateExists,
        Dictionary<string, LithTechModelDocument?> DecodedModels,
        Dictionary<string, bool> VerifiedMotionBaseMeshes,
        object MotionBaseVerificationSync);
}
