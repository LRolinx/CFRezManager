using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace CFRezManager;

internal static class LithTechModelTextureConfigIndex
{
    private const int MaxConfigBytes = 1024 * 1024;
    private const int MaxCftBytes = 4 * 1024 * 1024;
    private const int MaxLtcBytes = 16 * 1024 * 1024;
    private const int MaxUccMapBytes = 4 * 1024 * 1024;
    private const int MaxClientShellBytes = 192 * 1024 * 1024;
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

    private static TextureConfigIndex BuildIndex(ExplorerItem root)
    {
        var byName = new Dictionary<string, List<TextureConfigItem>>(StringComparer.OrdinalIgnoreCase);
        var byLooseName = new Dictionary<string, List<TextureConfigItem>?>(StringComparer.OrdinalIgnoreCase);
        var allItems = new List<TextureConfigItem>();
        var modelResourceItems = new List<ExplorerItem>();
        var uccMapItems = new List<ExplorerItem>();
        var objectLtoItems = new List<ExplorerItem>();
        var clientFxFxfItems = new List<ExplorerItem>();
        var clientShellItems = new List<ExplorerItem>();
        var ltcItems = new List<ExplorerItem>();
        var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelPathsByBaseName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var texturePathsByBaseName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
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
            modelResourceItems,
            uccMapItems,
            objectLtoItems,
            clientFxFxfItems,
            clientShellItems,
            ltcItems,
            resourcePaths,
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
        IReadOnlyList<ExplorerItem> modelResourceItems,
        IReadOnlySet<string> resourcePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelPathsByBaseName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> texturePathsByBaseName)
    {
        var byModelPath = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var byModelContext = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
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
        var modelItemsByPlayerViewRolePath = modelResourceItems
            .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
            .Select(item => (Item: item, RolePath: NormalizeModelResourcePath(GetModelResourcePath(item))))
            .Where(pair => pair.RolePath.Length > 0)
            .GroupBy(pair => pair.RolePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Item).ToArray(),
                StringComparer.OrdinalIgnoreCase);
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

                var rowSlotTextures = new Dictionary<int, string?>();
                var explicitTextureSlots = new HashSet<int>();
                foreach (CftTextureReference texture in mapping.TextureReferences)
                {
                    if (CftModelTextureMappingDecoder.TryGetTextureSlotIndex(texture.Column, out int slotIndex))
                    {
                        explicitTextureSlots.Add(slotIndex);
                        string? existingTexturePath = ResolveExistingTextureReference(
                            texture.Path,
                            resourcePaths,
                            texturePathsByBaseName);
                        string texturePath = existingTexturePath ?? texture.Path;
                        AddModelTextureSlotMapping(byModelPath, modelPath, slotIndex, texturePath);

                        if (existingTexturePath is not null)
                        {
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

                        if (existingTexturePath is not null)
                        {
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

                CftTextureReference[] playerViewRoleTextures = mapping.TextureReferences
                    .Where(texture => CftPlayerViewRoleSlotResolver.TryGetTextureRole(texture.Column, out _))
                    .ToArray();
                if (playerViewRoleTextures.Length > 0 &&
                    modelItemsByPlayerViewRolePath.TryGetValue(
                        NormalizeModelResourcePath(modelResourcePath),
                        out ExplorerItem[]? playerViewModelItems) &&
                    playerViewModelItems.Length > 0)
                {
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

                        string? existingTexturePath = ResolveExistingTextureReference(
                            texture.Path,
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

                if (rowSlotTextures.Count == 0 ||
                    mapping.RelatedModelReferences is not { Count: > 0 } relatedModels ||
                    !modelItemsByPath.TryGetValue(GetLogicalModelResourcePath(modelResourcePath), out ExplorerItem[]? baseModelItems) ||
                    baseModelItems.Length == 0)
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
                    if (!Path.HasExtension(relatedResourcePath) ||
                        !modelItemsByPath.TryGetValue(GetLogicalModelResourcePath(relatedResourcePath), out ExplorerItem[]? relatedModelItems))
                    {
                        continue;
                    }

                    bool allRelatedCopiesValidated = relatedModelItems.Length > 0;
                    HashSet<int> validatedSlotSet = rowSlotTextures.Keys.ToHashSet();
                    var parentUsedSlots = new HashSet<int>();
                    var childUsedSlots = new HashSet<int>();
                    foreach ((ExplorerItem mappedModelItem, string mappedModelPath, bool isChild) in
                             baseModelItems.Select(item => (item, modelResourcePath, false))
                                 .Concat(relatedModelItems.Select(item => (item, relatedResourcePath, true))))
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

                    validatedSlotSet.IntersectWith(parentUsedSlots);
                    validatedSlotSet.IntersectWith(childUsedSlots);
                    if (!allRelatedCopiesValidated || validatedSlotSet.Count == 0)
                    {
                        continue;
                    }

                    string relatedModelPath = NormalizeExtensionlessModelReference(relatedResourcePath);
                    foreach (int slotIndex in validatedSlotSet)
                    {
                        string? texturePath = rowSlotTextures[slotIndex];
                        if (!string.IsNullOrWhiteSpace(texturePath))
                        {
                            AddModelTextureSlotMapping(byModelPath, relatedModelPath, slotIndex, texturePath);
                            foreach (string contextKey in contextKeys)
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
        }

        return new ModelTextureSlotDictionary(byModelPath, byModelContext);
    }

    private static string[] GetCftContextKeys(string tablePath, CftModelTextureMapping mapping)
    {
        string recordIdentity = mapping.RecordId?.ToString(CultureInfo.InvariantCulture) ??
                                mapping.RowIndex.ToString(CultureInfo.InvariantCulture);
        return mapping.IdentityAliases
            .Append($"{NormalizeKey(tablePath)}#{recordIdentity}")
            .Append($"record:{Path.GetFileName(tablePath)}:{recordIdentity}")
            .Where(context => NormalizeLooseKey(context).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static string GetLogicalModelResourcePath(string resourcePath)
    {
        string normalized = NormalizeKey(resourcePath);
        int modelPathIndex = normalized.IndexOf("Models/", StringComparison.OrdinalIgnoreCase);
        return modelPathIndex >= 0 ? normalized[modelPathIndex..] : string.Empty;
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
        IReadOnlyList<ExplorerItem> ModelResourceItems,
        IReadOnlyList<ExplorerItem> UccMapItems,
        IReadOnlyList<ExplorerItem> ObjectLtoItems,
        IReadOnlyList<ExplorerItem> ClientFxFxfItems,
        IReadOnlyList<ExplorerItem> ClientShellItems,
        IReadOnlyList<ExplorerItem> LtcItems,
        IReadOnlySet<string> ResourcePaths,
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
                               ModelResourceItems.Count == 0 &&
                               UccMapItems.Count == 0 &&
                               ObjectLtoItems.Count == 0 &&
                               ClientFxFxfItems.Count == 0 &&
                               ClientShellItems.Count == 0 &&
                               LtcItems.Count == 0 &&
                               ResourcePaths.Count == 0 &&
                               ModelPathsByBaseName.Count == 0 &&
                               TexturePathsByBaseName.Count == 0;
    }

    private sealed record TextureConfigItem(string Path, string Name, ExplorerItem Item);

    private sealed record ModelMaterialComponentTextureMapping(
        string ModelReference,
        int TextureSlotIndex,
        string TextureReference);

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
        IReadOnlyDictionary<string, Dictionary<int, string?>> ByModelContext)
    {
        public static ModelTextureSlotDictionary Empty { get; } = new(
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record ModelTextureMapping(string ModelReference, IReadOnlyList<string> TextureReferences);

    private readonly record struct ScoredTextureReference(string Texture, int Score);
}
