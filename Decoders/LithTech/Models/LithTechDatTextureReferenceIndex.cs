using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace CFRezManager;

internal static class LithTechDatTextureReferenceIndex
{
    private const long MaxDatScanSourceBytes = 256L * 1024 * 1024;
    private const long MaxDatScanDecodedBytes = 512L * 1024 * 1024;
    private const int MaxResolvedTextureReferences = 512;
    private static readonly ConditionalWeakTable<ExplorerItem, DatTextureReferenceIndex> IndexCache = new();

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

    private static readonly byte[][] TextureExtensionBytes = TextureExtensions
        .Select(extension => Encoding.ASCII.GetBytes(extension))
        .ToArray();

    public static Func<IEnumerable<string>, IReadOnlyList<string>>? CreateResolver(ExplorerItem root)
    {
        DatTextureReferenceIndex index = IndexCache.GetValue(root, BuildIndex);
        if (index.IsEmpty)
        {
            return null;
        }

        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return names =>
        {
            string[] lookupNames = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            string cacheKey = string.Join("|", lookupNames);
            if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached))
            {
                return cached;
            }

            IReadOnlyList<string> resolved = Resolve(index, lookupNames);
            cache[cacheKey] = resolved;
            return resolved;
        };
    }

    private static DatTextureReferenceIndex BuildIndex(ExplorerItem root)
    {
        var byLookupKey = new Dictionary<string, List<DatTextureReferenceItem>>(StringComparer.OrdinalIgnoreCase);
        var allItems = new List<DatTextureReferenceItem>();

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (!string.Equals(item.FileExtension, "dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<string> textureReferences = TryExtractTextureReferences(item);
            if (textureReferences.Count == 0)
            {
                continue;
            }

            string path = string.IsNullOrWhiteSpace(item.OutputRelativePath)
                ? item.Name
                : item.OutputRelativePath;
            var indexItem = new DatTextureReferenceItem(path, item.Name, textureReferences);
            allItems.Add(indexItem);

            foreach (string key in EnumerateLookupKeys(path).Concat(EnumerateLookupKeys(item.Name)))
            {
                AddLookupItem(byLookupKey, key, indexItem);
            }
        }

        return new DatTextureReferenceIndex(byLookupKey, allItems);
    }

    private static IReadOnlyList<string> Resolve(
        DatTextureReferenceIndex index,
        IEnumerable<string> names)
    {
        var textures = new List<string>();
        var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            foreach (string key in EnumerateLookupKeys(name))
            {
                if (!index.ByLookupKey.TryGetValue(key, out List<DatTextureReferenceItem>? items))
                {
                    continue;
                }

                foreach (DatTextureReferenceItem item in items)
                {
                    if (!seenItems.Add(item.Path))
                    {
                        continue;
                    }

                    foreach (string texture in item.TextureReferences)
                    {
                        if (seenTextures.Add(texture))
                        {
                            textures.Add(texture);
                            if (textures.Count >= MaxResolvedTextureReferences)
                            {
                                return textures;
                            }
                        }
                    }
                }
            }
        }

        return textures;
    }

    private static IReadOnlyList<string> TryExtractTextureReferences(ExplorerItem item)
    {
        try
        {
            byte[]? sourceData = TryReadItemBytes(item);
            if (sourceData is null || sourceData.Length == 0)
            {
                return [];
            }

            byte[] scanData = LzmaAloneDecoder.TryPrepareData(sourceData, MaxDatScanDecodedBytes) ?? sourceData;
            return ExtractTextureReferences(scanData);
        }
        catch
        {
            return [];
        }
    }

    private static byte[]? TryReadItemBytes(ExplorerItem item)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(item.SourcePath);
            if (!info.Exists ||
                info.Length <= 0 ||
                info.Length > MaxDatScanSourceBytes ||
                info.Length > int.MaxValue)
            {
                return null;
            }

            return File.ReadAllBytes(item.SourcePath);
        }

        if (item.Kind == ExplorerItemKind.RezFile &&
            item.Archive is not null &&
            item.ArchiveFile is not null &&
            item.ArchiveFile.Size > 0 &&
            item.ArchiveFile.Size <= MaxDatScanSourceBytes)
        {
            byte[] data = new byte[item.ArchiveFile.Size];
            using FileStream source = File.OpenRead(item.Archive.FilePath);
            source.Position = item.ArchiveFile.DataOffset;
            source.ReadExactly(data);
            return data;
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractTextureReferences(byte[] data)
    {
        var references = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int offset = 0; offset < data.Length; offset++)
        {
            foreach (byte[] extension in TextureExtensionBytes)
            {
                if (!HasAsciiAt(data, offset, extension))
                {
                    continue;
                }

                string? reference = TryReadReferenceAt(data, offset, extension.Length);
                if (!string.IsNullOrWhiteSpace(reference) && seen.Add(reference))
                {
                    references.Add(reference);
                }
            }
        }

        return references;
    }

    private static string? TryReadReferenceAt(byte[] data, int extensionOffset, int extensionLength)
    {
        int start = extensionOffset;
        while (start > 0 && IsResourcePathByte(data[start - 1]))
        {
            start--;
        }

        int end = extensionOffset + extensionLength;
        if (end <= start || end > data.Length)
        {
            return null;
        }

        string raw = Encoding.ASCII.GetString(data, start, end - start);
        return NormalizeTextureReference(raw);
    }

    private static string? NormalizeTextureReference(string raw)
    {
        string reference = raw
            .Trim()
            .Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', ':')
            .Replace('\\', '/')
            .TrimStart('/');

        if (string.IsNullOrWhiteSpace(reference) ||
            reference.Length <= Path.GetExtension(reference).Length ||
            reference.Contains("://", StringComparison.Ordinal) ||
            reference.Any(char.IsControl))
        {
            return null;
        }

        string extension = Path.GetExtension(reference);
        return TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? reference
            : null;
    }

    private static bool HasAsciiAt(byte[] data, int offset, byte[] value)
    {
        if (offset < 0 || offset + value.Length > data.Length)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (ToAsciiLower(data[offset + index]) != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsResourcePathByte(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ||
               value is >= (byte)'a' and <= (byte)'z' ||
               value is >= (byte)'0' and <= (byte)'9' ||
               value is (byte)'_' or (byte)'-' or (byte)'.' or (byte)'/' or (byte)'\\';
    }

    private static byte ToAsciiLower(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }

    private static void AddLookupItem(
        Dictionary<string, List<DatTextureReferenceItem>> byLookupKey,
        string key,
        DatTextureReferenceItem item)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!byLookupKey.TryGetValue(key, out List<DatTextureReferenceItem>? items))
        {
            items = [];
            byLookupKey[key] = items;
        }

        if (!items.Any(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(item);
        }
    }

    private static IEnumerable<string> EnumerateLookupKeys(string value)
    {
        string normalized = NormalizeKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        string withoutExtension = RemoveExtension(normalized);
        if (!string.Equals(withoutExtension, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return withoutExtension;
        }

        string fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            !string.Equals(fileName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return fileName;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = Path.GetFileNameWithoutExtension(normalized);
        }

        if (!string.IsNullOrWhiteSpace(stem))
        {
            yield return stem;

            string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(stem);
            if (!string.Equals(numberedBase, stem, StringComparison.OrdinalIgnoreCase))
            {
                yield return numberedBase;
            }

            foreach (string familyBase in LithTechModelPartGrouper.EnumerateModelFamilyBaseCandidates(stem))
            {
                if (!string.Equals(familyBase, stem, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(familyBase, numberedBase, StringComparison.OrdinalIgnoreCase))
                {
                    yield return familyBase;
                }
            }
        }
    }

    private static string NormalizeKey(string value)
    {
        return LithTechResourceHeuristics.NormalizeResourcePath(value);
    }

    private static string RemoveExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? path : path[..^extension.Length];
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

    private sealed record DatTextureReferenceIndex(
        Dictionary<string, List<DatTextureReferenceItem>> ByLookupKey,
        IReadOnlyList<DatTextureReferenceItem> AllItems)
    {
        public bool IsEmpty => AllItems.Count == 0;
    }

    private sealed record DatTextureReferenceItem(
        string Path,
        string Name,
        IReadOnlyList<string> TextureReferences);
}
