using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace CFRezManager;

internal static class LithTechModelTextureConfigIndex
{
    private const int MaxConfigBytes = 1024 * 1024;
    private static readonly ConditionalWeakTable<ExplorerItem, TextureConfigIndex> TextureConfigIndexCache = new();

    private static readonly string[] ConfigExtensions =
    [
        ".cfg",
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

            IReadOnlyList<string> result = Resolve(index, names, textureCache);
            cache[cacheKey] = result;
            return result;
        };
    }

    private static TextureConfigIndex BuildIndex(ExplorerItem root)
    {
        var byName = new Dictionary<string, List<TextureConfigItem>>(StringComparer.OrdinalIgnoreCase);
        var byLooseName = new Dictionary<string, List<TextureConfigItem>?>(StringComparer.OrdinalIgnoreCase);
        var allItems = new List<TextureConfigItem>();

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
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

        return new TextureConfigIndex(byName, byLooseName, allItems);
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
        foreach (string name in names)
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

            index.ModelTextureDictionary = BuildModelTextureDictionary(index.AllItems, textureCache);
            index.ModelTextureDictionaryBuilt = true;
            return index.ModelTextureDictionary;
        }
    }

    private static ModelTextureDictionary BuildModelTextureDictionary(
        IReadOnlyList<TextureConfigItem> configItems,
        Dictionary<string, IReadOnlyList<string>> textureCache)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (TextureConfigItem configItem in configItems.Where(ShouldScanForModelTextureDictionary))
        {
            string? text = TryReadConfigText(configItem.Item);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (ModelTextureMapping mapping in ExtractModelTextureMappings(text))
            {
                AddModelTextureMapping(byName, mapping.ModelReference, mapping.TextureReferences);
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

        return new ModelTextureDictionary(byName.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase));
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

    private static IEnumerable<string> EnumerateModelLookupKeys(string value)
    {
        string normalized = NormalizeKey(Path.GetFileNameWithoutExtension(value));
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
        string normalized = NormalizeKey(Path.GetFileNameWithoutExtension(name));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        foreach (TextureConfigItem candidate in LookupExact(index, normalized))
        {
            yield return candidate;
        }

        string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(normalized);
        if (!string.Equals(numberedBase, normalized, StringComparison.OrdinalIgnoreCase))
        {
            foreach (TextureConfigItem candidate in LookupExact(index, numberedBase))
            {
                yield return candidate;
            }
        }

        string looseKey = NormalizeLooseKey(normalized);
        if (!string.IsNullOrWhiteSpace(looseKey) &&
            index.ByLooseName.TryGetValue(looseKey, out List<TextureConfigItem>? looseCandidates) &&
            looseCandidates is not null)
        {
            foreach (TextureConfigItem candidate in looseCandidates)
            {
                yield return candidate;
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

        if (LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(item.OutputRelativePath, item.FileExtension))
        {
            return true;
        }

        if (LithTechResourceHeuristics.IsLowValueMappingPath(item.OutputRelativePath))
        {
            return false;
        }

        return (string.Equals(extension, ".cfg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ini", StringComparison.OrdinalIgnoreCase)) &&
               !IsGenericConfigName(Path.GetFileNameWithoutExtension(item.Name));
    }

    private static bool ShouldScanForModelTextureDictionary(TextureConfigItem configItem)
    {
        string extension = Path.GetExtension(configItem.Path).TrimStart('.');
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
        string stem = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
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

    private static byte[]? TryReadItemBytes(ExplorerItem item)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxConfigBytes || info.Length > int.MaxValue)
                {
                    return null;
                }

                return File.ReadAllBytes(item.SourcePath);
            }

            if (item.Kind == ExplorerItemKind.RezFile &&
                item.Archive is not null &&
                item.ArchiveFile is not null &&
                item.ArchiveFile.Size > 0 &&
                item.ArchiveFile.Size <= MaxConfigBytes)
            {
                byte[] data = new byte[item.ArchiveFile.Size];
                using FileStream source = File.OpenRead(item.Archive.FilePath);
                source.Position = item.ArchiveFile.DataOffset;
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
        IReadOnlyList<TextureConfigItem> AllItems)
    {
        public object ModelTextureDictionarySync { get; } = new();
        public bool ModelTextureDictionaryBuilt { get; set; }
        public ModelTextureDictionary? ModelTextureDictionary { get; set; }
        public bool IsEmpty => ByName.Count == 0 && ByLooseName.Count == 0 && AllItems.Count == 0;
    }

    private sealed record TextureConfigItem(string Path, string Name, ExplorerItem Item);

    private sealed record ModelTextureDictionary(IReadOnlyDictionary<string, IReadOnlyList<string>> ByName)
    {
        public static ModelTextureDictionary Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        public bool IsEmpty => ByName.Count == 0;
    }

    private sealed record ModelTextureMapping(string ModelReference, IReadOnlyList<string> TextureReferences);

    private readonly record struct ScoredTextureReference(string Texture, int Score);
}
