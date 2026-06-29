using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal sealed record LithTechObjExportSource(
    string Name,
    string ResourcePath,
    LithTechModelDocument Document,
    Func<string, ImageSource?>? TextureResolver,
    Func<IEnumerable<string>, IReadOnlyList<string>>? TextureConfigResolver = null);

internal sealed record LithTechObjExportResult(
    string ObjPath,
    string MtlPath,
    string TextureDirectoryPath,
    int SourceCount,
    int MeshCount,
    int VertexCount,
    int TriangleCount,
    int TextureCount,
    int TextureReferenceCount,
    int MissingTextureCount,
    string TextureReportPath);

internal static class LithTechObjExporter
{
    private const double BlenderFitSize = 4.5;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> TextureNameCandidateCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SgfxAuxiliaryTextureSuffixes = ["_GLOW_01", "_GLOW_02", "_GLOW"];

    private static readonly (double R, double G, double B)[] FallbackColors =
    [
        (0.49, 0.72, 0.94),
        (0.96, 0.65, 0.36),
        (0.53, 0.83, 0.57),
        (0.85, 0.55, 0.85),
        (0.95, 0.82, 0.42),
        (0.47, 0.83, 0.84)
    ];

    public static LithTechObjExportResult Export(string objPath, IReadOnlyList<LithTechObjExportSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No model documents were provided for OBJ export.");
        }

        string fullObjPath = Path.GetFullPath(objPath);
        string? outputDirectory = Path.GetDirectoryName(fullObjPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Environment.CurrentDirectory;
            fullObjPath = Path.Combine(outputDirectory, Path.GetFileName(fullObjPath));
        }

        Directory.CreateDirectory(outputDirectory);

        string baseName = Path.GetFileNameWithoutExtension(fullObjPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        string mtlPath = Path.Combine(outputDirectory, $"{baseName}.mtl");
        string textureDirectoryName = $"{baseName}_textures";
        string textureDirectory = Path.Combine(outputDirectory, textureDirectoryName);

        var materials = new List<ObjMaterial>();
        var materialByKey = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
        var usedMaterialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportedTextureByCandidate = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var exportedTextureByBitmap = new Dictionary<BitmapSource, string>(ReferenceEqualityComparer.Instance);
        var textureReports = new List<ObjTextureReport>();
        ExportTransform transform = CalculateExportTransform(sources);

        using var objWriter = new StreamWriter(fullObjPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        objWriter.WriteLine("# Exported by CF Rez Manager");
        objWriter.WriteLine($"# Coordinates centered and scaled by {transform.Scale.ToString("G17", CultureInfo.InvariantCulture)} for Blender import.");
        objWriter.WriteLine($"mtllib {ToObjPath(Path.GetFileName(mtlPath))}");
        objWriter.WriteLine($"o {MakeObjIdentifier(baseName)}");

        int vertexOffset = 0;
        int textureCoordinateOffset = 0;
        int globalMeshIndex = 0;
        int meshCount = 0;

        foreach (LithTechObjExportSource source in sources)
        {
            foreach (LithTechMesh mesh in source.Document.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count < 3)
                {
                    continue;
                }

                ObjMaterial material = GetOrCreateMaterial(
                    mesh,
                    source,
                    source.TextureResolver,
                    outputDirectory,
                    textureDirectory,
                    textureDirectoryName,
                    materialByKey,
                    usedMaterialNames,
                    usedTextureNames,
                    exportedTextureByCandidate,
                    exportedTextureByBitmap,
                    materials,
                    textureReports,
                    globalMeshIndex);

                string groupName = MakeObjIdentifier($"{source.Name}_{mesh.Name}");
                objWriter.WriteLine();
                objWriter.WriteLine($"g {groupName}");
                objWriter.WriteLine($"usemtl {material.Name}");

                foreach (LithTechVector3 vertex in mesh.Vertices)
                {
                    objWriter.Write("v ");
                    WriteInvariant(objWriter, (vertex.X - transform.CenterX) * transform.Scale);
                    objWriter.Write(' ');
                    WriteInvariant(objWriter, (vertex.Y - transform.CenterY) * transform.Scale);
                    objWriter.Write(' ');
                    WriteInvariant(objWriter, (vertex.Z - transform.CenterZ) * transform.Scale);
                    objWriter.WriteLine();
                }

                bool hasTextureCoordinates = mesh.HasTextureCoordinates && mesh.TextureCoordinates is not null;
                if (hasTextureCoordinates)
                {
                    foreach (LithTechVector2 coordinate in mesh.TextureCoordinates!)
                    {
                        objWriter.Write("vt ");
                        WriteInvariant(objWriter, coordinate.X);
                        objWriter.Write(' ');
                        WriteInvariant(objWriter, 1.0 - coordinate.Y);
                        objWriter.WriteLine();
                    }
                }

                int usableIndexCount = mesh.TriangleIndices.Count - mesh.TriangleIndices.Count % 3;
                for (int index = 0; index < usableIndexCount; index += 3)
                {
                    int a = mesh.TriangleIndices[index];
                    int b = mesh.TriangleIndices[index + 1];
                    int c = mesh.TriangleIndices[index + 2];
                    if (!IsTriangleInRange(a, b, c, mesh.Vertices.Count))
                    {
                        continue;
                    }

                    objWriter.Write("f ");
                    WriteFaceVertex(objWriter, a, vertexOffset, textureCoordinateOffset, hasTextureCoordinates);
                    objWriter.Write(' ');
                    WriteFaceVertex(objWriter, b, vertexOffset, textureCoordinateOffset, hasTextureCoordinates);
                    objWriter.Write(' ');
                    WriteFaceVertex(objWriter, c, vertexOffset, textureCoordinateOffset, hasTextureCoordinates);
                    objWriter.WriteLine();
                }

                vertexOffset += mesh.Vertices.Count;
                if (hasTextureCoordinates)
                {
                    textureCoordinateOffset += mesh.TextureCoordinates!.Count;
                }

                globalMeshIndex++;
                meshCount++;
            }
        }

        WriteMaterialLibrary(mtlPath, materials);
        int missingTextureCount = textureReports.Count(report => string.IsNullOrWhiteSpace(report.ExportedRelativePath));
        string textureReportPath = string.Empty;
        if (missingTextureCount > 0 || textureReports.Count == 0)
        {
            textureReportPath = Path.Combine(outputDirectory, $"{baseName}_texture_report.txt");
            WriteTextureReport(textureReportPath, sources, textureReports);
        }

        return new LithTechObjExportResult(
            fullObjPath,
            mtlPath,
            textureDirectory,
            sources.Count,
            meshCount,
            sources.Sum(source => source.Document.VertexCount),
            sources.Sum(source => source.Document.TriangleCount),
            materials.Count(material => !string.IsNullOrWhiteSpace(material.TextureRelativePath)),
            textureReports.Count,
            missingTextureCount,
            textureReportPath);
    }

    private static ExportTransform CalculateExportTransform(IReadOnlyList<LithTechObjExportSource> sources)
    {
        bool hasVertex = false;
        double minX = 0;
        double minY = 0;
        double minZ = 0;
        double maxX = 0;
        double maxY = 0;
        double maxZ = 0;

        foreach (LithTechVector3 vertex in sources
                     .SelectMany(source => source.Document.Meshes)
                     .SelectMany(mesh => mesh.Vertices))
        {
            if (!hasVertex)
            {
                minX = maxX = vertex.X;
                minY = maxY = vertex.Y;
                minZ = maxZ = vertex.Z;
                hasVertex = true;
                continue;
            }

            minX = Math.Min(minX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            minZ = Math.Min(minZ, vertex.Z);
            maxX = Math.Max(maxX, vertex.X);
            maxY = Math.Max(maxY, vertex.Y);
            maxZ = Math.Max(maxZ, vertex.Z);
        }

        if (!hasVertex)
        {
            return new ExportTransform(0, 0, 0, 1);
        }

        double maxDimension = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        double scale = maxDimension <= 0 ? 1 : BlenderFitSize / maxDimension;
        return new ExportTransform(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2,
            scale);
    }

    private static ObjMaterial GetOrCreateMaterial(
        LithTechMesh mesh,
        LithTechObjExportSource source,
        Func<string, ImageSource?>? textureResolver,
        string outputDirectory,
        string textureDirectory,
        string textureDirectoryName,
        Dictionary<string, ObjMaterial> materialByKey,
        HashSet<string> usedMaterialNames,
        HashSet<string> usedTextureNames,
        Dictionary<string, string?> exportedTextureByCandidate,
        Dictionary<BitmapSource, string> exportedTextureByBitmap,
        List<ObjMaterial> materials,
        List<ObjTextureReport> textureReports,
        int meshIndex)
    {
        string textureKey = NormalizeTextureKey(mesh.TexturePath);
        IReadOnlyList<string> materialHints = mesh.MaterialHints ?? [];
        string materialHintKey = string.Join("|", materialHints.Take(8));
        List<string> inferredTextureCandidates = EnumerateTextureCandidates(mesh, source).ToList();
        string inferredTextureKey = inferredTextureCandidates.FirstOrDefault() ?? string.Empty;
        string materialKey = !string.IsNullOrEmpty(textureKey)
            ? $"texture:{textureKey}"
            : !string.IsNullOrWhiteSpace(materialHintKey)
                ? $"hint:{materialHintKey}"
                : !string.IsNullOrWhiteSpace(inferredTextureKey)
                    ? $"inferred:{NormalizeTextureKey(inferredTextureKey)}"
                    : $"solid:{meshIndex}";
        if (materialByKey.TryGetValue(materialKey, out ObjMaterial? existing))
        {
            return existing;
        }

        string materialBaseName = !string.IsNullOrEmpty(textureKey)
            ? Path.GetFileNameWithoutExtension(textureKey)
            : materialHints.FirstOrDefault() ?? mesh.Name;
        string materialName = MakeUniqueName(MakeObjIdentifier(materialBaseName), usedMaterialNames);
        (double r, double g, double b) = FallbackColors[meshIndex % FallbackColors.Length];
        string? resolvedTextureReference = null;
        string? textureRelativePath = TryExportBestTexture(
            inferredTextureCandidates,
            textureResolver,
            outputDirectory,
            textureDirectory,
            textureDirectoryName,
            usedTextureNames,
            exportedTextureByCandidate,
            exportedTextureByBitmap,
            out resolvedTextureReference);
        if (!string.IsNullOrWhiteSpace(mesh.TexturePath) ||
            materialHints.Count > 0 ||
            inferredTextureCandidates.Count > 0 ||
            !string.IsNullOrWhiteSpace(textureRelativePath))
        {
            textureReports.Add(new ObjTextureReport(
                source.ResourcePath,
                mesh.Name,
                mesh.TexturePath,
                materialHints,
                inferredTextureCandidates,
                resolvedTextureReference,
                textureRelativePath));
        }

        var material = new ObjMaterial(materialName, textureRelativePath, r, g, b);
        materialByKey[materialKey] = material;
        materials.Add(material);
        return material;
    }

    private static IEnumerable<string> EnumerateTextureCandidates(LithTechMesh mesh, LithTechObjExportSource source)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(mesh.TexturePath))
        {
            foreach (string candidate in ExpandTextureNameCandidates(mesh.TexturePath))
            {
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        if (mesh.MaterialHints is not null)
        {
            foreach (string hint in mesh.MaterialHints)
            {
                foreach (string candidate in ExpandTextureNameCandidates(hint))
                {
                    if (yielded.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        List<string> sourceTextureCandidates = EnumerateSourceTextureCandidates(source).ToList();
        foreach (string sourceCandidate in sourceTextureCandidates)
        {
            if (yielded.Add(sourceCandidate))
            {
                yield return sourceCandidate;
            }
        }

        if (source.TextureConfigResolver is not null)
        {
            foreach (string configTexture in source.TextureConfigResolver(sourceTextureCandidates))
            {
                if (yielded.Add(configTexture))
                {
                    yield return configTexture;
                }

                foreach (string candidate in ExpandTextureNameCandidates(configTexture))
                {
                    if (yielded.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceTextureCandidates(LithTechObjExportSource source)
    {
        foreach (string candidate in ExpandSourceResourceTexturePathCandidates(source.ResourcePath))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(Path.GetFileNameWithoutExtension(source.ResourcePath)))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(Path.GetFileNameWithoutExtension(source.Name)))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(Path.GetFileNameWithoutExtension(source.Document.Name)))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> ExpandSourceResourceTexturePathCandidates(string resourcePath)
    {
        string normalized = NormalizeTextureKey(resourcePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        string withoutExtension = RemovePathExtension(normalized);
        if (string.IsNullOrWhiteSpace(withoutExtension))
        {
            yield break;
        }

        yield return withoutExtension;

        const string modelsSegment = "/Models/";
        int modelsIndex = withoutExtension.IndexOf(modelsSegment, StringComparison.OrdinalIgnoreCase);
        if (modelsIndex >= 0)
        {
            string afterModels = withoutExtension[(modelsIndex + modelsSegment.Length)..];
            if (!string.IsNullOrWhiteSpace(afterModels))
            {
                yield return "ModelTextures/" + afterModels;
                yield return withoutExtension[..modelsIndex] + "/ModelTextures/" + afterModels;

                string sourceRoot = withoutExtension[..modelsIndex].Trim('/');
                if (string.Equals(GetLastPathSegment(sourceRoot), "RF016", StringComparison.OrdinalIgnoreCase))
                {
                    yield return "rf017/ModelTextures/" + afterModels;
                    int slashIndex = sourceRoot.LastIndexOf('/');
                    if (slashIndex >= 0)
                    {
                        yield return sourceRoot[..slashIndex] + "/rf017/ModelTextures/" + afterModels;
                    }
                }
            }
        }

        const string leadingModels = "Models/";
        if (withoutExtension.StartsWith(leadingModels, StringComparison.OrdinalIgnoreCase))
        {
            string afterModels = withoutExtension[leadingModels.Length..];
            if (!string.IsNullOrWhiteSpace(afterModels))
            {
                yield return "ModelTextures/" + afterModels;
            }
        }
    }

    private static string GetLastPathSegment(string path)
    {
        int slashIndex = path.LastIndexOf('/');
        return slashIndex >= 0 ? path[(slashIndex + 1)..] : path;
    }

    private static IEnumerable<string> ExpandTextureNameCandidates(string? value)
    {
        string normalized = NormalizeTextureKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        if (TextureNameCandidateCache.TryGetValue(normalized, out IReadOnlyList<string>? cached))
        {
            foreach (string candidate in cached)
            {
                yield return candidate;
            }

            yield break;
        }

        List<string> candidates = BuildTextureNameCandidates(normalized);
        TextureNameCandidateCache[normalized] = candidates;
        foreach (string candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static List<string> BuildTextureNameCandidates(string normalized)
    {
        var candidates = new List<string>();
        string fileName = Path.GetFileName(normalized);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = normalized;
        }

        candidates.Add(stem);

        string numberedBase = LithTechModelPartGrouper.GetNumberedPartBase(stem);
        if (!string.Equals(numberedBase, stem, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(numberedBase);
        }

        AddModelFamilyTextureCandidates(candidates, stem);

        foreach (string stripped in StripModelVariantSuffixes(stem))
        {
            candidates.Add(stripped);
            AddModelFamilyTextureCandidates(candidates, stripped);
        }

        foreach (string stripped in StripViewModelPrefixes(stem))
        {
            candidates.Add(stripped);

            string strippedNumberedBase = LithTechModelPartGrouper.GetNumberedPartBase(stripped);
            if (!string.Equals(strippedNumberedBase, stripped, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(strippedNumberedBase);
            }

            AddModelFamilyTextureCandidates(candidates, stripped);

            foreach (string variantStripped in StripModelVariantSuffixes(stripped))
            {
                candidates.Add(variantStripped);
                AddModelFamilyTextureCandidates(candidates, variantStripped);
            }
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddModelFamilyTextureCandidates(List<string> candidates, string stem)
    {
        foreach (string familyBase in LithTechModelPartGrouper.EnumerateModelFamilyBaseCandidates(stem))
        {
            candidates.Add(familyBase);
            foreach (string relatedTexture in EnumerateRelatedSgfxTextureCandidates(familyBase))
            {
                candidates.Add(relatedTexture);
            }
        }
    }

    private static IEnumerable<string> EnumerateRelatedSgfxTextureCandidates(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem) ||
            !stem.StartsWith("SGFX_", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string[] tokens = stem.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 4)
        {
            yield break;
        }

        foreach (string suffix in SgfxAuxiliaryTextureSuffixes)
        {
            yield return stem + suffix;
        }
    }

    private static IEnumerable<string> StripModelVariantSuffixes(string stem)
    {
        string[] suffixes =
        [
            "_WOMAN_BL",
            "_WOMAN_GR",
            "_WOMAN_SP",
            "_FEMALE_BL",
            "_FEMALE_GR",
            "_FEMALE_SP",
            "_BL",
            "_GR",
            "_SP",
            "_F",
            "_M",
            "_W"
        ];

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = stem;
        bool strippedAny;
        do
        {
            strippedAny = false;
            foreach (string suffix in suffixes)
            {
                if (current.Length <= suffix.Length ||
                    !current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                current = current[..^suffix.Length];
                if (!string.IsNullOrWhiteSpace(current) && yielded.Add(current))
                {
                    yield return current;
                }

                strippedAny = true;
                break;
            }
        }
        while (strippedAny);
    }

    private static IEnumerable<string> StripViewModelPrefixes(string stem)
    {
        string[] prefixes =
        [
            "PV-",
            "PV_",
            "QV-",
            "QV_",
            "TV-",
            "TV_",
            "WV-",
            "WV_"
        ];

        foreach (string prefix in prefixes)
        {
            if (stem.Length > prefix.Length &&
                stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return stem[prefix.Length..];
            }
        }
    }

    private static string? TryExportBestTexture(
        IEnumerable<string> textureCandidates,
        Func<string, ImageSource?>? textureResolver,
        string outputDirectory,
        string textureDirectory,
        string textureDirectoryName,
        HashSet<string> usedTextureNames,
        Dictionary<string, string?> exportedTextureByCandidate,
        Dictionary<BitmapSource, string> exportedTextureByBitmap,
        out string? resolvedReference)
    {
        resolvedReference = null;
        List<string> distinctCandidates = textureCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int index = 0; index < distinctCandidates.Count; index++)
        {
            string textureCandidate = distinctCandidates[index];
            string? textureRelativePath = TryExportTexture(
                textureCandidate,
                textureResolver,
                outputDirectory,
                textureDirectory,
                textureDirectoryName,
                usedTextureNames,
                exportedTextureByCandidate,
                exportedTextureByBitmap);
            if (!string.IsNullOrWhiteSpace(textureRelativePath))
            {
                resolvedReference = textureCandidate;
                ExportRelatedSgfxTextures(
                    distinctCandidates.Skip(index + 1),
                    textureCandidate,
                    textureResolver,
                    outputDirectory,
                    textureDirectory,
                    textureDirectoryName,
                    usedTextureNames,
                    exportedTextureByCandidate,
                    exportedTextureByBitmap);
                return textureRelativePath;
            }
        }

        return null;
    }

    private static void ExportRelatedSgfxTextures(
        IEnumerable<string> textureCandidates,
        string primaryTextureCandidate,
        Func<string, ImageSource?>? textureResolver,
        string outputDirectory,
        string textureDirectory,
        string textureDirectoryName,
        HashSet<string> usedTextureNames,
        Dictionary<string, string?> exportedTextureByCandidate,
        Dictionary<BitmapSource, string> exportedTextureByBitmap)
    {
        foreach (string textureCandidate in textureCandidates)
        {
            if (!IsRelatedSgfxTextureCandidate(primaryTextureCandidate, textureCandidate))
            {
                continue;
            }

            TryExportTexture(
                textureCandidate,
                textureResolver,
                outputDirectory,
                textureDirectory,
                textureDirectoryName,
                usedTextureNames,
                exportedTextureByCandidate,
                exportedTextureByBitmap);
        }
    }

    private static bool IsRelatedSgfxTextureCandidate(string primaryTextureCandidate, string textureCandidate)
    {
        string primaryBase = GetSgfxRelatedTextureBase(primaryTextureCandidate);
        if (string.IsNullOrWhiteSpace(primaryBase))
        {
            return false;
        }

        string candidateBase = GetSgfxRelatedTextureBase(textureCandidate);
        return !string.IsNullOrWhiteSpace(candidateBase) &&
               string.Equals(primaryBase, candidateBase, StringComparison.OrdinalIgnoreCase) &&
               (IsSgfxAuxiliaryTextureName(primaryTextureCandidate) || IsSgfxAuxiliaryTextureName(textureCandidate));
    }

    private static string GetSgfxRelatedTextureBase(string textureCandidate)
    {
        string stem = Path.GetFileNameWithoutExtension(NormalizeTextureKey(textureCandidate));
        if (string.IsNullOrWhiteSpace(stem) ||
            !stem.StartsWith("SGFX_", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        foreach (string suffix in SgfxAuxiliaryTextureSuffixes)
        {
            if (stem.Length > suffix.Length &&
                stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    private static bool IsSgfxAuxiliaryTextureName(string textureCandidate)
    {
        string stem = Path.GetFileNameWithoutExtension(NormalizeTextureKey(textureCandidate));
        return SgfxAuxiliaryTextureSuffixes.Any(suffix =>
            stem.Length > suffix.Length &&
            stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryExportTexture(
        string? texturePath,
        Func<string, ImageSource?>? textureResolver,
        string outputDirectory,
        string textureDirectory,
        string textureDirectoryName,
        HashSet<string> usedTextureNames,
        Dictionary<string, string?> exportedTextureByCandidate,
        Dictionary<BitmapSource, string> exportedTextureByBitmap)
    {
        if (string.IsNullOrWhiteSpace(texturePath) || textureResolver is null)
        {
            return null;
        }

        try
        {
            string normalizedTexturePath = NormalizeTextureKey(texturePath);
            if (exportedTextureByCandidate.TryGetValue(normalizedTexturePath, out string? cachedRelativePath))
            {
                return cachedRelativePath;
            }

            ImageSource? image = textureResolver(texturePath);
            if (image is not BitmapSource bitmap)
            {
                exportedTextureByCandidate[normalizedTexturePath] = null;
                return null;
            }

            if (exportedTextureByBitmap.TryGetValue(bitmap, out string? existingRelativePath))
            {
                exportedTextureByCandidate[normalizedTexturePath] = existingRelativePath;
                return existingRelativePath;
            }

            Directory.CreateDirectory(textureDirectory);
            string textureBaseName = Path.GetFileNameWithoutExtension(normalizedTexturePath);
            if (string.IsNullOrWhiteSpace(textureBaseName))
            {
                textureBaseName = "texture";
            }

            string textureFileName = MakeUniqueName(SanitizeFileName(textureBaseName), usedTextureNames) + ".png";
            string textureOutputPath = Path.Combine(textureDirectory, textureFileName);
            using (FileStream stream = File.Create(textureOutputPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            string relativePath = Path.GetRelativePath(outputDirectory, textureOutputPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = Path.Combine(textureDirectoryName, textureFileName);
            }

            string objRelativePath = ToObjPath(relativePath);
            exportedTextureByCandidate[normalizedTexturePath] = objRelativePath;
            exportedTextureByBitmap[bitmap] = objRelativePath;
            return objRelativePath;
        }
        catch
        {
            exportedTextureByCandidate[NormalizeTextureKey(texturePath)] = null;
            return null;
        }
    }

    private static void WriteTextureReport(
        string reportPath,
        IReadOnlyList<LithTechObjExportSource> sources,
        IReadOnlyList<ObjTextureReport> textureReports)
    {
        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager OBJ texture report");
        writer.WriteLine();
        writer.WriteLine($"Sources: {sources.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Meshes: {sources.Sum(source => source.Document.Meshes.Count).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Texture references: {textureReports.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Missing textures: {textureReports.Count(report => string.IsNullOrWhiteSpace(report.ExportedRelativePath)).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine();

        if (textureReports.Count == 0)
        {
            writer.WriteLine("No texture references were found in the decoded model meshes.");
            return;
        }

        foreach (ObjTextureReport report in textureReports)
        {
            writer.WriteLine($"Source: {report.SourcePath}");
            writer.WriteLine($"Mesh: {report.MeshName}");
            writer.WriteLine(string.IsNullOrWhiteSpace(report.TexturePath)
                ? "  Reference: <none>"
                : $"  Reference: {report.TexturePath}");
            if (report.MaterialHints.Count > 0)
            {
                writer.WriteLine($"  Material hints: {string.Join(", ", report.MaterialHints.Take(16))}");
            }

            if (report.InferredCandidates.Count > 0)
            {
                writer.WriteLine($"  Inferred candidates: {string.Join(", ", report.InferredCandidates.Take(16))}");
            }

            if (!string.IsNullOrWhiteSpace(report.ResolvedReference))
            {
                writer.WriteLine($"  Resolved from: {report.ResolvedReference}");
            }

            writer.WriteLine(string.IsNullOrWhiteSpace(report.ExportedRelativePath)
                ? "  Exported: <missing>"
                : $"  Exported: {report.ExportedRelativePath}");
            writer.WriteLine();
        }
    }

    private static void WriteMaterialLibrary(string mtlPath, IReadOnlyList<ObjMaterial> materials)
    {
        using var writer = new StreamWriter(mtlPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("# Exported by CF Rez Manager");
        foreach (ObjMaterial material in materials)
        {
            writer.WriteLine();
            writer.WriteLine($"newmtl {material.Name}");
            writer.WriteLine("Ka 0 0 0");
            writer.Write("Kd ");
            WriteInvariant(writer, material.R);
            writer.Write(' ');
            WriteInvariant(writer, material.G);
            writer.Write(' ');
            WriteInvariant(writer, material.B);
            writer.WriteLine();
            writer.WriteLine("Ks 0.05 0.05 0.05");
            writer.WriteLine("Ns 16");
            writer.WriteLine("d 1");
            writer.WriteLine("illum 2");
            if (!string.IsNullOrWhiteSpace(material.TextureRelativePath))
            {
                writer.WriteLine($"map_Kd {material.TextureRelativePath}");
            }
        }
    }

    private static bool IsTriangleInRange(int a, int b, int c, int vertexCount)
    {
        return a >= 0 && a < vertexCount &&
               b >= 0 && b < vertexCount &&
               c >= 0 && c < vertexCount;
    }

    private static void WriteFaceVertex(TextWriter writer, int index, int vertexOffset, int textureCoordinateOffset, bool hasTextureCoordinates)
    {
        int vertexIndex = vertexOffset + index + 1;
        writer.Write(vertexIndex.ToString(CultureInfo.InvariantCulture));
        if (!hasTextureCoordinates)
        {
            return;
        }

        int textureCoordinateIndex = textureCoordinateOffset + index + 1;
        writer.Write('/');
        writer.Write(textureCoordinateIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteInvariant(TextWriter writer, double value)
    {
        writer.Write(value.ToString("G17", CultureInfo.InvariantCulture));
    }

    private static string NormalizeTextureKey(string? texturePath)
    {
        return string.IsNullOrWhiteSpace(texturePath)
            ? string.Empty
            : texturePath.Replace('\\', '/').Trim().Trim('"');
    }

    private static string RemovePathExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? path : path[..^extension.Length];
    }

    private static string ToObjPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string MakeObjIdentifier(string value)
    {
        string sanitized = SanitizeFileName(value).Replace(' ', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "object" : sanitized;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
    {
        string safeBaseName = string.IsNullOrWhiteSpace(baseName) ? "_" : baseName;
        if (usedNames.Add(safeBaseName))
        {
            return safeBaseName;
        }

        for (int index = 2; ; index++)
        {
            string candidate = $"{safeBaseName}_{index}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed record ObjMaterial(string Name, string? TextureRelativePath, double R, double G, double B);

    private sealed record ObjTextureReport(
        string SourcePath,
        string MeshName,
        string? TexturePath,
        IReadOnlyList<string> MaterialHints,
        IReadOnlyList<string> InferredCandidates,
        string? ResolvedReference,
        string? ExportedRelativePath);

    private readonly record struct ExportTransform(double CenterX, double CenterY, double CenterZ, double Scale);
}
