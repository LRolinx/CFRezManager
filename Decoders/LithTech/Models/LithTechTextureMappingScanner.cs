using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal static class LithTechTextureMappingScanner
{
    private const int MaxScanBytes = 512 * 1024;
    private const int MaxScanFiles = 5_000;
    private const int MaxRawNeedleScanBytes = 8 * 1024 * 1024;
    private const int MaxRawNeedleScanFiles = 40_000;
    private const int MaxMappingHits = 50;
    private const int MaxRawReferenceHits = 50;
    private const int MaxGlobalTableCandidates = 40;
    private const int MaxShaderMaterialCandidates = 40;
    private const int MaxTextureGuesses = 50;
    private const int MaxCompanionCandidates = 50;
    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "dtx",
        "dds",
        "tga",
        "png",
        "jpg",
        "jpeg",
        "bmp",
        "bin"
    };

    private static readonly HashSet<string> PreferredMappingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "apf",
        "cft",
        "cfg",
        "csv",
        "dat",
        "enc",
        "fcf",
        "ini",
        "json",
        "lua",
        "ref",
        "txt",
        "xml"
    };

    private static readonly HashSet<string> ModelReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lta",
        ".ltb",
        ".ltc",
        ".dat"
    };

    private static readonly HashSet<string> TextureReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly string[] BindingKeywords =
    [
        "material",
        "model",
        "skin",
        "tex",
        "texture"
    ];

    private static readonly string[] UiPathMarkers =
    [
        "/ui/",
        "/ui/scripts/",
        "\\ui\\",
        "\\ui\\scripts\\"
    ];

    private static readonly HashSet<string> IgnoredSearchTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "body",
        "default",
        "break",
        "lod",
        "map",
        "map2",
        "mesh",
        "model",
        "node",
        "object",
        "polyhedron",
        "rez",
        "render",
        "rf008",
        "skin",
        "texture",
        "world"
    };

    public static string WriteReport(
        string objPath,
        ExplorerItem? root,
        IReadOnlyList<LithTechObjExportSource> sources)
    {
        string reportPath = GetReportPath(objPath);
        List<string> modelNeedles = BuildModelNeedles(sources);
        List<string> textureReferences = sources
            .SelectMany(source => source.Document.Meshes)
            .Select(mesh => mesh.TexturePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> materialHints = sources
            .SelectMany(source => source.Document.Meshes)
            .SelectMany(mesh => mesh.MaterialHints ?? [])
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hint => hint, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<TextureItem> textures = root is null ? [] : CollectTextureItems(root);
        List<TextureGuess> textureGuesses = GuessTexturesByName(textures, modelNeedles, materialHints);
        List<TextureItem> siblingTextureCandidates = GuessSiblingTextures(textures, sources);
        List<GlobalMappingTableCandidate> allTableCandidates = root is null ? [] : FindGlobalMappingTableCandidates(root);
        List<GlobalMappingTableCandidate> globalTableCandidates = allTableCandidates
            .Where(candidate => candidate.ModelReferenceCount > 0)
            .ToList();
        List<GlobalMappingTableCandidate> shaderMaterialCandidates = allTableCandidates
            .Where(IsShaderMaterialCandidate)
            .ToList();
        List<MappingHit> mappingHits = root is null
            ? []
            : FindMappingHits(root, modelNeedles, textureReferences, textureGuesses);
        List<RawReferenceHit> rawReferenceHits = root is null
            ? []
            : FindRawReferenceHits(root, sources, modelNeedles);
        List<CompanionMappingCandidate> companionCandidates = root is null
            ? []
            : FindCompanionMappingCandidates(root, sources);
        List<LikelyMappingCandidate> likelyMappingFiles = SelectLikelyMappingFiles(
            rawReferenceHits,
            mappingHits,
            companionCandidates,
            globalTableCandidates);

        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager texture mapping candidate report");
        writer.WriteLine();
        writer.WriteLine($"Sources: {sources.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Meshes: {sources.Sum(source => source.Document.Meshes.Count).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Decoded texture references: {textureReferences.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Indexed textures: {textures.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Global mapping table candidates: {globalTableCandidates.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Mapping file hits: {mappingHits.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Raw exact model reference hits: {rawReferenceHits.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine();

        writer.WriteLine("Source Resources");
        foreach (LithTechObjExportSource source in sources)
        {
            writer.WriteLine($"- {source.ResourcePath}");
        }

        writer.WriteLine();

        writer.WriteLine("Most Likely Mapping Files");
        if (likelyMappingFiles.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (LikelyMappingCandidate candidate in likelyMappingFiles)
            {
                writer.WriteLine($"- score {candidate.Score.ToString(CultureInfo.InvariantCulture)}: {candidate.Path}");
                writer.WriteLine($"  Reason: {candidate.Reason}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Model Search Terms");
        if (modelNeedles.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (string needle in modelNeedles.Take(128))
            {
                writer.WriteLine($"- {needle}");
            }

            if (modelNeedles.Count > 128)
            {
                writer.WriteLine($"- ... {modelNeedles.Count - 128} more");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Direct Texture References");
        if (textureReferences.Count == 0)
        {
            writer.WriteLine("- <none found in decoded model meshes>");
        }
        else
        {
            foreach (string reference in textureReferences)
            {
                writer.WriteLine($"- {reference}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Material Hints From Model");
        if (materialHints.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (string hint in materialHints.Take(100))
            {
                writer.WriteLine($"- {hint}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Texture Name Guesses");
        if (textureGuesses.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (TextureGuess guess in textureGuesses.Take(MaxTextureGuesses))
            {
                writer.WriteLine($"- score {guess.Score.ToString(CultureInfo.InvariantCulture)}: {guess.Texture.Path}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Sibling Directory Texture Candidates");
        if (siblingTextureCandidates.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (TextureItem texture in siblingTextureCandidates.Take(100))
            {
                writer.WriteLine($"- {texture.Path}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Likely Global Mapping Tables");
        if (globalTableCandidates.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (GlobalMappingTableCandidate candidate in globalTableCandidates)
            {
                writer.WriteLine($"- score {candidate.Score.ToString(CultureInfo.InvariantCulture)}: {candidate.Path}");
                writer.WriteLine($"  Models: {candidate.ModelReferenceCount.ToString(CultureInfo.InvariantCulture)}, Textures: {candidate.TextureReferenceCount.ToString(CultureInfo.InvariantCulture)}, Keywords: {candidate.KeywordCount.ToString(CultureInfo.InvariantCulture)}");
                if (candidate.ModelSamples.Count > 0)
                {
                    writer.WriteLine($"  Model refs: {string.Join(", ", candidate.ModelSamples.Take(5))}");
                }

                if (candidate.TextureSamples.Count > 0)
                {
                    writer.WriteLine($"  Texture refs: {string.Join(", ", candidate.TextureSamples.Take(5))}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine("Likely Shader Or Material Configs");
        if (shaderMaterialCandidates.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (GlobalMappingTableCandidate candidate in shaderMaterialCandidates)
            {
                writer.WriteLine($"- score {candidate.Score.ToString(CultureInfo.InvariantCulture)}: {candidate.Path}");
                writer.WriteLine($"  Textures: {candidate.TextureReferenceCount.ToString(CultureInfo.InvariantCulture)}, Keywords: {candidate.KeywordCount.ToString(CultureInfo.InvariantCulture)}");
                if (candidate.TextureSamples.Count > 0)
                {
                    writer.WriteLine($"  Texture refs: {string.Join(", ", candidate.TextureSamples.Take(8))}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine("Raw Exact Model Reference Hits");
        if (rawReferenceHits.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (RawReferenceHit hit in rawReferenceHits)
            {
                writer.WriteLine($"- score {hit.Score.ToString(CultureInfo.InvariantCulture)}: {hit.Path}");
                writer.WriteLine($"  Matched model terms: {string.Join(", ", hit.MatchedModelTerms.Take(16))}");
                if (hit.ModelReferences.Count > 0)
                {
                    writer.WriteLine($"  Model refs: {string.Join(", ", hit.ModelReferences.Take(8))}");
                }

                if (hit.TextureReferences.Count > 0)
                {
                    writer.WriteLine($"  Texture refs: {string.Join(", ", hit.TextureReferences.Take(12))}");
                }

                if (!string.IsNullOrWhiteSpace(hit.Snippet))
                {
                    writer.WriteLine($"  Snippet: {hit.Snippet}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine("Companion Mapping Files Near Source");
        if (companionCandidates.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (CompanionMappingCandidate candidate in companionCandidates)
            {
                writer.WriteLine($"- score {candidate.Score.ToString(CultureInfo.InvariantCulture)}: {candidate.Path}");
                if (candidate.ModelReferences.Count > 0)
                {
                    writer.WriteLine($"  Model refs: {string.Join(", ", candidate.ModelReferences.Take(8))}");
                }

                if (candidate.TextureReferences.Count > 0)
                {
                    writer.WriteLine($"  Texture refs: {string.Join(", ", candidate.TextureReferences.Take(12))}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine("Mapping Candidate Files");
        if (mappingHits.Count == 0)
        {
            writer.WriteLine("- <none>");
        }
        else
        {
            foreach (MappingHit hit in mappingHits)
            {
                writer.WriteLine($"- {hit.Path}");
                writer.WriteLine($"  Matched model terms: {string.Join(", ", hit.MatchedModelTerms.Take(16))}");
                if (hit.MatchedTextureTerms.Count > 0)
                {
                    writer.WriteLine($"  Matched texture terms: {string.Join(", ", hit.MatchedTextureTerms.Take(16))}");
                }

                if (!string.IsNullOrWhiteSpace(hit.Snippet))
                {
                    writer.WriteLine($"  Snippet: {hit.Snippet}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine("How to read this report:");
        writer.WriteLine("- If direct texture references are empty, the model format parser did not find texture paths inside the model.");
        writer.WriteLine("- Likely global mapping tables are ranked by files that mention both model-like resources and texture-like resources.");
        writer.WriteLine("- Shader/material configs may only mention texture resources; these are separated from full model-to-texture tables.");
        writer.WriteLine("- Raw exact model reference hits search file bytes for exact model terms before text decoding; these are strong mapping-table suspects.");
        writer.WriteLine("- Companion files are small config/table-like files near the selected model path and may hold local material bindings.");
        writer.WriteLine("- If mapping candidate files mention both model terms and texture terms, those files are likely the external binding tables.");
        writer.WriteLine("- If texture guesses look correct but no mapping file hits appear, the binding may be packed in an unsupported binary table.");

        return reportPath;
    }

    public static string WriteSkippedReport(string objPath, string reason)
    {
        string reportPath = GetReportPath(objPath);
        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager texture mapping candidate report");
        writer.WriteLine();
        writer.WriteLine(reason);
        return reportPath;
    }

    private static string GetReportPath(string objPath)
    {
        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? Environment.CurrentDirectory;
        string baseName = Path.GetFileNameWithoutExtension(objPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        return Path.Combine(outputDirectory, $"{baseName}_mapping_candidates.txt");
    }

    private static List<LikelyMappingCandidate> SelectLikelyMappingFiles(
        IReadOnlyList<RawReferenceHit> rawReferenceHits,
        IReadOnlyList<MappingHit> mappingHits,
        IReadOnlyList<CompanionMappingCandidate> companionCandidates,
        IReadOnlyList<GlobalMappingTableCandidate> globalTableCandidates)
    {
        var candidates = new List<LikelyMappingCandidate>();
        foreach (RawReferenceHit hit in rawReferenceHits)
        {
            int score = 180 + hit.Score;
            string reason = hit.TextureReferences.Count > 0
                ? "exact model-name byte hit with texture references"
                : "exact model-name byte hit";
            if (hit.TextureReferences.Count > 0)
            {
                score += 120;
            }

            candidates.Add(new LikelyMappingCandidate(hit.Path, score, reason));
        }

        foreach (MappingHit hit in mappingHits)
        {
            int score = 140 + hit.MatchedModelTerms.Count * 10 + hit.MatchedTextureTerms.Count * 30;
            string reason = hit.MatchedTextureTerms.Count > 0
                ? "decoded text mentions model terms and likely texture names"
                : "decoded text mentions model terms";
            candidates.Add(new LikelyMappingCandidate(hit.Path, score, reason));
        }

        foreach (CompanionMappingCandidate candidate in companionCandidates)
        {
            int score = 110 + candidate.Score;
            string reason = candidate.TextureReferences.Count > 0
                ? "near the source model and contains texture references"
                : "near the source model and looks like a config/table file";
            candidates.Add(new LikelyMappingCandidate(candidate.Path, score, reason));
        }

        foreach (GlobalMappingTableCandidate candidate in globalTableCandidates)
        {
            candidates.Add(new LikelyMappingCandidate(
                candidate.Path,
                90 + candidate.Score,
                "global table-like file with model and texture resource references"));
        }

        return candidates
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static List<string> BuildModelNeedles(IReadOnlyList<LithTechObjExportSource> sources)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LithTechObjExportSource source in sources)
        {
            AddNameTerms(source.Name, terms);
            AddPathTerms(source.ResourcePath, terms);
            AddNameTerms(source.Document.Name, terms);
            foreach (LithTechMesh mesh in source.Document.Meshes)
            {
                AddNameTerms(mesh.Name, terms);
            }
        }

        return terms
            .Where(IsUsefulSearchTerm)
            .OrderByDescending(term => term.Length)
            .ThenBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddNameTerms(string? name, HashSet<string> terms)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string withoutExtension = NormalizeSearchTerm(Path.GetFileNameWithoutExtension(name));
        if (!string.IsNullOrWhiteSpace(withoutExtension) && IsUsefulSearchTerm(withoutExtension))
        {
            terms.Add(withoutExtension);
        }

        foreach (string token in SplitNameTokens(name))
        {
            if (IsUsefulSearchTerm(token))
            {
                terms.Add(token);
            }
        }
    }

    private static void AddPathTerms(string? path, HashSet<string> terms)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = path.Replace('\\', '/');
        AddNameTerms(Path.GetFileNameWithoutExtension(normalized), terms);
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            AddNameTerms(part, terms);
        }
    }

    private static List<TextureItem> CollectTextureItems(ExplorerItem root)
    {
        var textures = new List<TextureItem>();
        CollectTextureItems(root, textures);
        return textures
            .OrderBy(texture => texture.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectTextureItems(ExplorerItem item, List<TextureItem> textures)
    {
        if (item.IsFile && TextureExtensions.Contains(item.FileExtension))
        {
            string path = string.IsNullOrWhiteSpace(item.OutputRelativePath)
                ? item.Name
                : item.OutputRelativePath;
            textures.Add(new TextureItem(path.Replace('\\', '/'), Path.GetFileNameWithoutExtension(item.Name)));
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectTextureItems(child, textures);
        }
    }

    private static List<TextureGuess> GuessTexturesByName(
        IReadOnlyList<TextureItem> textures,
        IReadOnlyList<string> modelNeedles,
        IReadOnlyList<string> materialHints)
    {
        var modelTokens = new HashSet<string>(
            modelNeedles
                .SelectMany(SplitNameTokens)
                .Concat(modelNeedles)
                .Concat(materialHints)
                .Concat(materialHints.SelectMany(SplitNameTokens))
                .Where(IsUsefulSearchTerm),
            StringComparer.OrdinalIgnoreCase);

        return textures
            .Select(texture =>
            {
                int score = SplitNameTokens(texture.Path)
                    .Count(token => IsUsefulSearchTerm(token) && modelTokens.Contains(token));
                return new TextureGuess(texture, score);
            })
            .Where(guess => guess.Score > 0)
            .OrderByDescending(guess => guess.Score)
            .ThenBy(guess => guess.Texture.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTextureGuesses)
            .ToList();
    }

    private static List<TextureItem> GuessSiblingTextures(
        IReadOnlyList<TextureItem> textures,
        IReadOnlyList<LithTechObjExportSource> sources)
    {
        var sourceDirectories = sources
            .Select(source => NormalizeDirectory(source.ResourcePath))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceDirectories.Count == 0)
        {
            return [];
        }

        return textures
            .Where(texture => sourceDirectories.Any(directory =>
                NormalizeDirectory(texture.Path).Equals(directory, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(texture => texture.Path, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    private static string NormalizeDirectory(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static List<MappingHit> FindMappingHits(
        ExplorerItem root,
        IReadOnlyList<string> modelNeedles,
        IReadOnlyList<string> textureReferences,
        IReadOnlyList<TextureGuess> textureGuesses)
    {
        var hits = new List<MappingHit>();
        int scannedFiles = 0;
        foreach (ExplorerItem item in GetMappingCandidates(root, modelNeedles))
        {
            if (hits.Count >= MaxMappingHits || scannedFiles >= MaxScanFiles)
            {
                break;
            }

            scannedFiles++;
            string? searchableText = TryReadSearchableText(item);
            if (string.IsNullOrWhiteSpace(searchableText))
            {
                continue;
            }

            List<string> matchedModelTerms = FindMatches(searchableText, modelNeedles, maxMatches: 24);
            if (matchedModelTerms.Count == 0)
            {
                continue;
            }

            List<string> textureTerms = textureReferences
                .Concat(textureGuesses.Select(guess => guess.Texture.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToList();
            List<string> matchedTextureTerms = FindMatches(searchableText, textureTerms, maxMatches: 24);
            hits.Add(new MappingHit(
                string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath,
                matchedModelTerms,
                matchedTextureTerms,
                CreateSnippet(searchableText, matchedModelTerms[0])));
        }

        return hits
            .OrderByDescending(hit => hit.MatchedTextureTerms.Count)
            .ThenByDescending(hit => hit.MatchedModelTerms.Count)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<RawReferenceHit> FindRawReferenceHits(
        ExplorerItem root,
        IReadOnlyList<LithTechObjExportSource> sources,
        IReadOnlyList<string> modelNeedles)
    {
        List<string> exactNeedles = BuildRawExactNeedles(sources, modelNeedles);
        if (exactNeedles.Count == 0)
        {
            return [];
        }

        var sourcePaths = new HashSet<string>(
            sources.Select(source => NormalizeReference(source.ResourcePath)),
            StringComparer.OrdinalIgnoreCase);
        var hits = new List<RawReferenceHit>();
        int scannedFiles = 0;

        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (scannedFiles >= MaxRawNeedleScanFiles)
            {
                break;
            }

            string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
            if (sourcePaths.Contains(NormalizeReference(path)) || !ShouldScanRawReferenceCandidate(item))
            {
                continue;
            }

            scannedFiles++;
            byte[]? data = TryReadItemBytes(item, MaxRawNeedleScanBytes);
            if (data is null || data.Length == 0)
            {
                continue;
            }

            List<string> matchedTerms = FindRawNeedleMatches(data, exactNeedles, maxMatches: 24);
            if (matchedTerms.Count == 0)
            {
                continue;
            }

            string? searchableText = TryBuildSearchText(item, data);
            List<string> textureReferences = searchableText is null
                ? []
                : ExtractResourceReferences(searchableText, TextureReferenceExtensions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .ToList();
            List<string> modelReferences = searchableText is null
                ? []
                : ExtractResourceReferences(searchableText, ModelReferenceExtensions)
                    .Where(reference => !LooksLikeNavigationOrWorldOnlyReference(reference))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .ToList();
            int keywordCount = searchableText is null
                ? 0
                : BindingKeywords.Count(keyword => searchableText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            int score = CalculateRawReferenceScore(path, item.FileExtension, matchedTerms, modelReferences.Count, textureReferences.Count, keywordCount);
            string snippet = searchableText is null ? string.Empty : CreateBestSnippet(searchableText, matchedTerms);

            hits.Add(new RawReferenceHit(
                path,
                score,
                matchedTerms,
                modelReferences,
                textureReferences,
                snippet));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.TextureReferences.Count)
            .ThenByDescending(hit => hit.ModelReferences.Count)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRawReferenceHits)
            .ToList();
    }

    private static List<string> BuildRawExactNeedles(
        IReadOnlyList<LithTechObjExportSource> sources,
        IReadOnlyList<string> modelNeedles)
    {
        var needles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string needle in modelNeedles)
        {
            if (needle.Contains('_', StringComparison.Ordinal) ||
                needle.Contains('-', StringComparison.Ordinal) ||
                needle.Contains('.', StringComparison.Ordinal))
            {
                AddRawNeedle(needle, needles);
            }
        }

        foreach (LithTechObjExportSource source in sources)
        {
            AddRawNeedle(source.Name, needles);
            AddRawNeedle(source.Document.Name, needles);
            AddRawNeedle(Path.GetFileNameWithoutExtension(source.ResourcePath), needles);
            AddRawNeedle(source.ResourcePath, needles);
            AddRawNeedle(source.ResourcePath.Replace('\\', '/'), needles);
            AddRawNeedle(source.ResourcePath.Replace('/', '\\'), needles);

            string withoutExtension = Path.ChangeExtension(source.ResourcePath, null) ?? string.Empty;
            AddRawNeedle(withoutExtension, needles);
            AddRawNeedle(withoutExtension.Replace('\\', '/'), needles);
            AddRawNeedle(withoutExtension.Replace('/', '\\'), needles);
        }

        return needles
            .Where(needle => needle.Length >= 5)
            .OrderByDescending(needle => needle.Length)
            .ThenBy(needle => needle, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToList();
    }

    private static void AddRawNeedle(string? value, HashSet<string> needles)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalized = NormalizeSearchTerm(value.Trim().Trim('"', '\''));
        if (normalized.Length < 5)
        {
            return;
        }

        if (IsUsefulSearchTerm(normalized) || normalized.Contains('/') || normalized.Contains('\\') || normalized.Contains('.'))
        {
            needles.Add(normalized);
        }
    }

    private static bool ShouldScanRawReferenceCandidate(ExplorerItem item)
    {
        if (!item.IsFile)
        {
            return false;
        }

        string extension = item.FileExtension;
        if (TextureExtensions.Contains(extension) ||
            string.Equals(extension, "bank", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "gif", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "lta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
        if (LooksLikeInstallerOrManifestPath(path) ||
            LithTechResourceHeuristics.IsLowValueMappingPath(path))
        {
            return false;
        }

        long? byteCount = GetItemByteCount(item);
        if (byteCount is not (> 0 and <= MaxRawNeedleScanBytes))
        {
            return false;
        }

        return LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension) ||
               LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension) ||
               (string.IsNullOrWhiteSpace(extension) && LooksLikeMappingPath(path));
    }

    private static List<string> FindRawNeedleMatches(byte[] data, IReadOnlyList<string> needles, int maxMatches)
    {
        var matches = new List<string>();
        foreach (string needle in needles)
        {
            if (ContainsAsciiIgnoreCase(data, needle))
            {
                matches.Add(needle);
                if (matches.Count >= maxMatches)
                {
                    break;
                }
            }
        }

        return matches;
    }

    private static bool ContainsAsciiIgnoreCase(byte[] data, string needle)
    {
        if (needle.Length == 0 || data.Length < needle.Length)
        {
            return false;
        }

        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        for (int index = 0; index <= data.Length - needleBytes.Length; index++)
        {
            if (AsciiEqualsIgnoreCase(data, index, needleBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AsciiEqualsIgnoreCase(byte[] data, int dataOffset, byte[] needle)
    {
        for (int index = 0; index < needle.Length; index++)
        {
            byte left = ToAsciiLower(data[dataOffset + index]);
            byte right = ToAsciiLower(needle[index]);
            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiLower(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }

    private static int CalculateRawReferenceScore(
        string path,
        string extension,
        IReadOnlyList<string> matchedTerms,
        int modelReferenceCount,
        int textureReferenceCount,
        int keywordCount)
    {
        int score = matchedTerms.Sum(term => Math.Min(term.Length, 80)) +
                    Math.Min(modelReferenceCount, 20) * 8 +
                    Math.Min(textureReferenceCount, 30) * 12 +
                    keywordCount * 10;

        if (PreferredMappingExtensions.Contains(extension))
        {
            score += LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension) ||
                     LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension)
                ? 40
                : 5;
        }

        if (LooksLikeMappingPath(path))
        {
            score += 30;
        }

        if (LooksLikeUiOnlyPath(path))
        {
            score -= 80;
        }

        return score;
    }

    private static List<CompanionMappingCandidate> FindCompanionMappingCandidates(
        ExplorerItem root,
        IReadOnlyList<LithTechObjExportSource> sources)
    {
        var sourceDirectoryScopes = new HashSet<string>(
            sources.SelectMany(source => EnumerateSourceDirectoryScopes(source.ResourcePath)),
            StringComparer.OrdinalIgnoreCase);
        if (sourceDirectoryScopes.Count == 0)
        {
            return [];
        }

        var candidates = new List<CompanionMappingCandidate>();
        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (!ShouldScanCompanionCandidate(item))
            {
                continue;
            }

            string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
            string directory = NormalizeDirectory(path);
            if (!sourceDirectoryScopes.Contains(directory))
            {
                continue;
            }

            string? searchableText = TryReadRawSearchText(item);
            if (string.IsNullOrWhiteSpace(searchableText))
            {
                continue;
            }

            List<string> modelReferences = ExtractResourceReferences(searchableText, ModelReferenceExtensions)
                .Where(reference => !LooksLikeNavigationOrWorldOnlyReference(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
            List<string> textureReferences = ExtractResourceReferences(searchableText, TextureReferenceExtensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
            int keywordCount = BindingKeywords.Count(keyword => searchableText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            int score = CalculateCompanionScore(path, item.FileExtension, modelReferences.Count, textureReferences.Count, keywordCount);
            if (score <= 0)
            {
                continue;
            }

            candidates.Add(new CompanionMappingCandidate(path, score, modelReferences, textureReferences));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.TextureReferences.Count)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompanionCandidates)
            .ToList();
    }

    private static IEnumerable<string> EnumerateSourceDirectoryScopes(string resourcePath)
    {
        string directory = NormalizeDirectory(resourcePath);
        for (int depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            yield return directory;
            int slash = directory.LastIndexOf('/');
            if (slash <= 0)
            {
                break;
            }

            directory = directory[..slash];
        }
    }

    private static bool ShouldScanCompanionCandidate(ExplorerItem item)
    {
        if (!item.IsFile)
        {
            return false;
        }

        if (!ShouldScanRawReferenceCandidate(item))
        {
            return false;
        }

        long? byteCount = GetItemByteCount(item);
        if (byteCount is null or <= 0 or > MaxScanBytes)
        {
            return false;
        }

        string extension = item.FileExtension;
        return PreferredMappingExtensions.Contains(extension) ||
               string.IsNullOrWhiteSpace(extension);
    }

    private static int CalculateCompanionScore(
        string path,
        string extension,
        int modelReferenceCount,
        int textureReferenceCount,
        int keywordCount)
    {
        int score = Math.Min(modelReferenceCount, 20) * 8 +
                    Math.Min(textureReferenceCount, 30) * 10 +
                    keywordCount * 10;

        if (PreferredMappingExtensions.Contains(extension))
        {
            score += LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension) ||
                     LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension)
                ? 25
                : 5;
        }

        if (LooksLikeMappingPath(path))
        {
            score += 20;
        }

        return score;
    }

    private static bool LooksLikeMappingPath(string path)
    {
        return path.Contains("bute", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("material", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("model", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("skin", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("table", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("texture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInstallerOrManifestPath(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/'));
        return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("filelist", StringComparison.OrdinalIgnoreCase);
    }

    private static List<GlobalMappingTableCandidate> FindGlobalMappingTableCandidates(ExplorerItem root)
    {
        var candidates = new List<GlobalMappingTableCandidate>();
        int scannedFiles = 0;
        foreach (ExplorerItem item in EnumerateFiles(root))
        {
            if (scannedFiles >= MaxScanFiles)
            {
                break;
            }

            if (!ShouldScanGlobalMappingCandidate(item))
            {
                continue;
            }

            scannedFiles++;
            string? searchableText = TryReadRawSearchText(item);
            if (string.IsNullOrWhiteSpace(searchableText))
            {
                continue;
            }

            List<string> modelReferences = ExtractResourceReferences(searchableText, ModelReferenceExtensions)
                .Where(reference => !LooksLikeNavigationOrWorldOnlyReference(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
            List<string> textureReferences = ExtractResourceReferences(searchableText, TextureReferenceExtensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
            int keywordCount = BindingKeywords.Count(keyword => searchableText.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (textureReferences.Count == 0)
            {
                continue;
            }

            bool hasModelReferences = modelReferences.Count > 0;
            bool looksLikeMaterialTable = keywordCount >= 2 && textureReferences.Count >= 3;
            if (!hasModelReferences && !looksLikeMaterialTable)
            {
                continue;
            }

            string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
            if (LooksLikeUiOnlyPath(path))
            {
                continue;
            }

            int score = CalculateGlobalMappingScore(path, item.FileExtension, modelReferences.Count, textureReferences.Count, keywordCount);
            if (score <= 0)
            {
                continue;
            }

            candidates.Add(new GlobalMappingTableCandidate(
                path,
                score,
                modelReferences.Count,
                textureReferences.Count,
                keywordCount,
                modelReferences.Take(8).ToList(),
                textureReferences.Take(8).ToList()));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => Math.Min(candidate.ModelReferenceCount, candidate.TextureReferenceCount))
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxGlobalTableCandidates)
            .ToList();
    }

    private static int CalculateGlobalMappingScore(
        string path,
        string extension,
        int modelReferenceCount,
        int textureReferenceCount,
        int keywordCount)
    {
        int score = Math.Min(modelReferenceCount, 25) * 8 +
                    Math.Min(textureReferenceCount, 50) * 3 +
                    keywordCount * 10;

        if (PreferredMappingExtensions.Contains(extension))
        {
            score += 30;
        }

        if (path.Contains("table", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("bute", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("material", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("texture", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (path.EndsWith(".lst", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("filelist", StringComparison.OrdinalIgnoreCase))
        {
            score -= 100;
        }

        if (modelReferenceCount == 0 && keywordCount < 3)
        {
            score -= 50;
        }

        return score;
    }

    private static bool ShouldScanGlobalMappingCandidate(ExplorerItem item)
    {
        if (!item.IsFile)
        {
            return false;
        }

        string extension = item.FileExtension;
        if (TextureExtensions.Contains(extension) ||
            string.Equals(extension, "bank", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "lta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
        if (LooksLikeInstallerOrManifestPath(path) ||
            LithTechResourceHeuristics.IsLowValueMappingPath(path))
        {
            return false;
        }

        long? byteCount = GetItemByteCount(item);
        if (byteCount is null or <= 0 or > MaxScanBytes)
        {
            return false;
        }

        return LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension) ||
               LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension);
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

    private static bool ShouldScanMappingCandidate(ExplorerItem item)
    {
        if (!item.IsFile)
        {
            return false;
        }

        string extension = item.FileExtension;
        if (TextureExtensions.Contains(extension) ||
            string.Equals(extension, "bank", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "lta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
        if (LooksLikeInstallerOrManifestPath(path) ||
            LithTechResourceHeuristics.IsLowValueMappingPath(path))
        {
            return false;
        }

        return LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension) ||
               LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension);
    }

    private static bool IsShaderMaterialCandidate(GlobalMappingTableCandidate candidate)
    {
        if (candidate.TextureReferenceCount == 0)
        {
            return false;
        }

        if (LooksLikeUiOnlyPath(candidate.Path))
        {
            return false;
        }

        bool pathLooksLikeShaderConfig =
            candidate.Path.Contains("ModelTextures", StringComparison.OrdinalIgnoreCase) ||
            candidate.Path.Contains("Shader", StringComparison.OrdinalIgnoreCase) ||
            candidate.Path.Contains("Material", StringComparison.OrdinalIgnoreCase) ||
            candidate.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);

        return pathLooksLikeShaderConfig && candidate.KeywordCount >= 1;
    }

    private static bool LooksLikeUiOnlyPath(string path)
    {
        return LithTechResourceHeuristics.IsUiPath(path) ||
               UiPathMarkers.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ExplorerItem> GetMappingCandidates(ExplorerItem root, IReadOnlyList<string> modelNeedles)
    {
        return EnumerateFiles(root)
            .Where(ShouldScanMappingCandidate)
            .Select(item => new
            {
                Item = item,
                Score = ScoreMappingCandidate(item, modelNeedles)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => GetItemByteCount(candidate.Item) ?? long.MaxValue)
            .ThenBy(candidate => candidate.Item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxScanFiles)
            .Select(candidate => candidate.Item)
            .ToList();
    }

    private static int ScoreMappingCandidate(ExplorerItem item, IReadOnlyList<string> modelNeedles)
    {
        long? byteCount = GetItemByteCount(item);
        if (byteCount is null or <= 0 or > MaxScanBytes)
        {
            return 0;
        }

        string path = string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath;
        string extension = item.FileExtension;
        if (LooksLikeInstallerOrManifestPath(path) ||
            LithTechResourceHeuristics.IsLowValueMappingPath(path))
        {
            return 0;
        }

        bool pathMatchesModel = modelNeedles.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(extension, "dat", StringComparison.OrdinalIgnoreCase) && !pathMatchesModel)
        {
            return 0;
        }

        bool likelyTextureConfig = LithTechResourceHeuristics.IsLikelyModelTextureConfigPath(path, extension);
        bool likelyMappingTable = LithTechResourceHeuristics.IsLikelyModelMappingTablePath(path, extension);
        int score = PreferredMappingExtensions.Contains(extension) ? 10 : 0;
        if (likelyTextureConfig)
        {
            score += 80;
        }

        if (likelyMappingTable)
        {
            score += 40;
        }

        if (pathMatchesModel)
        {
            score += 100;
        }

        if (path.Contains("table", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("bute", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("material", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("texture", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }

    private static string? TryReadSearchableText(ExplorerItem item)
    {
        byte[]? data = TryReadItemBytes(item, MaxScanBytes);
        if (data is null || data.Length == 0)
        {
            return null;
        }

        string extension = item.FileExtension;
        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase) &&
            CfgTextDecoder.TryDecode(data, item.Name, MaxScanBytes, out CfgTextDocument? cfgDocument) &&
            cfgDocument is not null)
        {
            return cfgDocument.Text;
        }

        string? encText = TryDecodeEncSearchText(item, data);
        if (encText is not null)
        {
            return encText;
        }

        if (ResourceTextDecoder.IsCandidate(item.Name, extension) &&
            ResourceTextDecoder.TryDecode(data, item.Name, extension, out ResourceTextDocument? resourceDocument, out _) &&
            resourceDocument is not null)
        {
            return resourceDocument.Text;
        }

        if (CrossFireDatDecoder.IsCandidate(extension) &&
            CrossFireDatDecoder.TryDecode(data, item.Name, out CrossFireDatDocument? datDocument, out _) &&
            datDocument is not null)
        {
            return datDocument.Text;
        }

        if (CrossFireLtcDecoder.IsCandidate(extension) &&
            CrossFireLtcDecoder.TryDecodeText(data, item.Name, out CrossFireLtcTextDocument? ltcDocument, out _) &&
            ltcDocument is not null)
        {
            return ltcDocument.Text;
        }

        byte[] prepared = LzmaAloneDecoder.TryPrepareData(data, MaxScanBytes) ?? data;
        if (TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string text, out _))
        {
            return text;
        }

        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Join('\n', ExtractAsciiStrings(prepared, minLength: 4, maxCount: 512));
    }

    private static string? TryReadRawSearchText(ExplorerItem item)
    {
        byte[]? data = TryReadItemBytes(item, MaxScanBytes);
        if (data is null || data.Length == 0)
        {
            return null;
        }

        string extension = item.FileExtension;
        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase) &&
            CfgTextDecoder.TryDecode(data, item.Name, MaxScanBytes, out CfgTextDocument? cfgDocument) &&
            cfgDocument is not null)
        {
            return cfgDocument.Text;
        }

        string? encText = TryDecodeEncSearchText(item, data);
        if (encText is not null)
        {
            return encText;
        }

        byte[] prepared = LzmaAloneDecoder.TryPrepareData(data, MaxScanBytes) ?? data;
        var parts = new List<string>();
        if (TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string text, out _))
        {
            parts.Add(text);
        }

        if (!string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            parts.AddRange(ExtractAsciiStrings(prepared, minLength: 4, maxCount: 2048));
        }

        return parts.Count == 0
            ? null
            : string.Join('\n', parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? TryBuildSearchText(ExplorerItem item, byte[] data)
    {
        string extension = item.FileExtension;
        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase) &&
            CfgTextDecoder.TryDecode(data, item.Name, MaxRawNeedleScanBytes, out CfgTextDocument? cfgDocument) &&
            cfgDocument is not null)
        {
            return cfgDocument.Text;
        }

        string? encText = TryDecodeEncSearchText(item, data);
        if (encText is not null)
        {
            return encText;
        }

        byte[] prepared = LzmaAloneDecoder.TryPrepareData(data, MaxRawNeedleScanBytes) ?? data;
        var parts = new List<string>();
        if (TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string text, out _))
        {
            parts.Add(text);
        }

        if (!string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            parts.AddRange(ExtractAsciiStrings(prepared, minLength: 4, maxCount: 4096));
        }

        return parts.Count == 0
            ? null
            : string.Join('\n', parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? TryDecodeEncSearchText(ExplorerItem item, byte[] data)
    {
        if (!EncTextDecoder.IsCandidate(item.Name, item.FileExtension) ||
            !EncTextDecoder.TryDecode(data, out byte[] decodedBytes) ||
            !TextPreviewDecoder.TryDecode(decodedBytes, preferKorean: false, out string decodedText, out _))
        {
            return null;
        }

        return decodedText;
    }

    private static byte[]? TryReadItemBytes(ExplorerItem item, int maxBytes)
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

    private static long? GetItemByteCount(ExplorerItem item)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            try
            {
                var info = new FileInfo(item.SourcePath);
                return info.Exists ? info.Length : null;
            }
            catch
            {
                return null;
            }
        }

        return item.ArchiveFile?.Size;
    }

    private static List<string> FindMatches(string text, IReadOnlyList<string> needles, int maxMatches)
    {
        var matches = new List<string>();
        foreach (string needle in needles)
        {
            if (needle.Length < 3)
            {
                continue;
            }

            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(needle);
                if (matches.Count >= maxMatches)
                {
                    break;
                }
            }
        }

        return matches;
    }

    private static IEnumerable<string> ExtractResourceReferences(string text, IReadOnlyCollection<string> extensions)
    {
        foreach (string extension in extensions)
        {
            int searchStart = 0;
            while (searchStart < text.Length)
            {
                int extensionIndex = text.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    break;
                }

                int start = extensionIndex;
                while (start > 0 && IsResourcePathCharacter(text[start - 1]))
                {
                    start--;
                }

                int end = extensionIndex + extension.Length;
                string reference = NormalizeReference(text[start..end]);
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

    private static string NormalizeReference(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', ':')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static bool LooksLikeNavigationOrWorldOnlyReference(string reference)
    {
        return reference.EndsWith(".obj.nav", StringComparison.OrdinalIgnoreCase) ||
               reference.Contains(".obj.nav", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateSnippet(string text, string needle)
    {
        int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, index - 80);
        int length = Math.Min(text.Length - start, needle.Length + 160);
        string snippet = text.Substring(start, length)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

        while (snippet.Contains("  ", StringComparison.Ordinal))
        {
            snippet = snippet.Replace("  ", " ", StringComparison.Ordinal);
        }

        return snippet.Trim();
    }

    private static string CreateBestSnippet(string text, IReadOnlyList<string> needles)
    {
        foreach (string needle in needles)
        {
            string snippet = CreateSnippet(text, needle);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                return snippet;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> SplitNameTokens(string value)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length > 0)
            {
                string token = builder.ToString();
                builder.Clear();
                if (!IgnoredSearchTerms.Contains(token))
                {
                    tokens.Add(token);
                }
            }
        }
    }

    private static bool IsUsefulSearchTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        string normalized = NormalizeSearchTerm(term);
        if (normalized.Length < 4 || IgnoredSearchTerms.Contains(normalized))
        {
            return false;
        }

        if (normalized.Length >= 3 &&
            normalized.StartsWith("rf", StringComparison.OrdinalIgnoreCase) &&
            normalized.Skip(2).All(char.IsDigit))
        {
            return false;
        }

        if (normalized.StartsWith("lt-model", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.StartsWith("object", StringComparison.OrdinalIgnoreCase) &&
            normalized.Skip("object".Length).All(char.IsDigit))
        {
            return false;
        }

        return normalized.Any(char.IsLetter);
    }

    private static string NormalizeSearchTerm(string term)
    {
        string normalized = term.Trim();
        int suffixStart = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffixStart > 0 &&
            normalized.EndsWith(')') &&
            normalized[(suffixStart + 2)..^1].All(char.IsDigit))
        {
            normalized = normalized[..suffixStart];
        }

        return normalized;
    }

    private static List<string> ExtractAsciiStrings(byte[] data, int minLength, int maxCount)
    {
        var strings = new List<string>();
        var current = new StringBuilder();
        foreach (byte value in data)
        {
            if (value is >= 32 and <= 126)
            {
                current.Append((char)value);
                continue;
            }

            FlushCurrent();
            if (strings.Count >= maxCount)
            {
                break;
            }
        }

        FlushCurrent();
        return strings
            .Where(value => value.Length >= minLength)
            .Select(value => value.Trim())
            .Where(value => value.Length >= minLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

        void FlushCurrent()
        {
            if (current.Length >= minLength)
            {
                strings.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private sealed record TextureItem(string Path, string Name);

    private sealed record TextureGuess(TextureItem Texture, int Score);

    private sealed record LikelyMappingCandidate(string Path, int Score, string Reason);

    private sealed record MappingHit(
        string Path,
        IReadOnlyList<string> MatchedModelTerms,
        IReadOnlyList<string> MatchedTextureTerms,
        string Snippet);

    private sealed record RawReferenceHit(
        string Path,
        int Score,
        IReadOnlyList<string> MatchedModelTerms,
        IReadOnlyList<string> ModelReferences,
        IReadOnlyList<string> TextureReferences,
        string Snippet);

    private sealed record CompanionMappingCandidate(
        string Path,
        int Score,
        IReadOnlyList<string> ModelReferences,
        IReadOnlyList<string> TextureReferences);

    private sealed record GlobalMappingTableCandidate(
        string Path,
        int Score,
        int ModelReferenceCount,
        int TextureReferenceCount,
        int KeywordCount,
        IReadOnlyList<string> ModelSamples,
        IReadOnlyList<string> TextureSamples);
}
