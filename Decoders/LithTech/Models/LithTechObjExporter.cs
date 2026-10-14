using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal sealed record LithTechObjExportSource(
    string Name,
    string ResourcePath,
    LithTechModelDocument Document,
    Func<string, ImageSource?>? TextureResolver,
    Func<IEnumerable<string>, IReadOnlyList<string>>? TextureConfigResolver = null,
    Func<string, int, IReadOnlyList<string>>? TextureSlotResolver = null,
    Func<string, string, int, string, IReadOnlyList<string>>? MeshTextureResolver = null,
    string? TextureContext = null,
    Func<string, string, int, IReadOnlyList<string>>? TextureContextSlotResolver = null,
    Func<string, bool>? ModelTextureSlotMappingPresenceResolver = null);

internal sealed record LithTechObjExportResult(
    string ObjPath,
    string MtlPath,
    string TextureDirectoryPath,
    int SourceCount,
    int MeshCount,
    int VertexCount,
    int TriangleCount,
    int InvalidGeometryMeshCount,
    int TextureCount,
    int TextureReferenceCount,
    int MissingTextureCount,
    string TextureReportPath);

internal static class LithTechObjExporter
{
    private const double BlenderFitSize = 4.5;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> TextureNameCandidateCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] KnownTextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"];
    private static readonly string[] KnownModelExtensions = [".ltb", ".lta", ".ltc", ".dat"];
    private static readonly string[] SgfxAuxiliaryTextureSuffixes = ["_GLOW_01", "_GLOW_02", "_GLOW"];
    private static readonly IReadOnlyDictionary<string, string> LegacyModelTextureAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The base client calls this model 3Burst-DualMag, while its RF017
            // shader/texture family uses the historical DualMag_Ghetto name.
            ["PV-AK12_3Burst-DualMag"] = "QV-AK12_DualMag_Ghetto",
            ["QV-AK12_3Burst-DualMag"] = "QV-AK12_DualMag_Ghetto"
        };

    private static readonly IReadOnlyDictionary<string, string> ExplicitSocketItemTextureAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // These are stable engine-era renames verified against the RF017
            // SOCKETITEM texture inventory. They are intentionally keyed by
            // the complete relative model path so a generic name heuristic
            // cannot apply them to an unrelated item.
            ["NANO/GHOST_TAIL_BASE"] = "NANO/NANO_TAIL",
            ["BASIC/HERO/HERO_SHOULDER"] = "BASIC/Hero_Veteran/Hero_Veteran_shoulder",
            ["BASIC/GSG9/GSG9_HELMET_BL"] = "BASIC/GSG9/GSG9_CH_Helmet_BL",
            ["BASIC/GSG9/GSG9_HELMET_GR"] = "BASIC/GSG9/GSG9_CH_Helmet_GR",
            ["BASIC/RUNAWAYS/RUNAWAYS_BLUE_HAIR_SP"] = "BASIC/Runaways_Blue_SP/Runaways_Blue_Hair_SP",
            ["BASIC/SUBJECTALPHA/SUBJECTALPHA_BACKPACK_BL"] = "BASIC/SubjectAlpha/BL/SubjectAlpha_Bag_BL",
            ["BASIC/FEMALEOMOH/FEMALEOMOH_THIGH_GR"] = "WAIST/W_FemaleOMOH_POUCH_GR",
            ["BASIC/WUREI/WUREI_WC"] = "CHARACTER/MotionItem/WuRei_WC",
            ["BACK/B_SANDDRAGON_BACKPACK_F"] = "BACK/B_Ani_SandDragon_Backpack",
            ["BACK/B_SANDDRAGON_BACKPACK_M"] = "BACK/B_Ani_SandDragon_Backpack",
            ["BACK/B_ANI_ALPHA_BACKPACK_F"] = "BACK/B_Alpha_Backpack",
            ["BACK/B_ANI_ALPHA_BACKPACK_M"] = "BACK/B_Alpha_Backpack",
            ["BACK/B_ANI_BOOMER_BACKPACK_F"] = "BACK/BoomerBackPack",
            ["BACK/B_ANI_BOOMER_BACKPACK_M"] = "BACK/BoomerBackPack",
            ["BACK/B_ANI_BUTTERFLY_WING_F"] = "BACK/B_Butterfly_Wing",
            ["BACK/B_ANI_BUTTERFLY_WING_M"] = "BACK/B_Butterfly_Wing",
            ["BACK/B_ANI_DIMENSIONSPEAR_BACKPACK_F"] = "BACK/B_DimensionSpear_Backpack",
            ["BACK/B_ANI_DIMENSIONSPEAR_BACKPACK_M"] = "BACK/B_DimensionSpear_Backpack",
            ["BACK/B_ANI_FESTIVALMASK_BACKPACK_F"] = "BACK/B_FestivalMask_BackPack",
            ["BACK/B_ANI_FESTIVALMASK_BACKPACK_M"] = "BACK/B_FestivalMask_BackPack",
            ["BACK/B_ANI_IDOLQINGYA_CHAMELEON_BACKPACK_F"] = "BACK/B_IdolQingya_Chameleon_Backpack",
            ["BACK/B_ANI_IDOLQINGYA_CHAMELEON_BACKPACK_M"] = "BACK/B_IdolQingya_Chameleon_Backpack",
            ["BACK/B_ANI_MEDUSA_HALO_F"] = "BACK/B_Medusa_Halo",
            ["BACK/B_ANI_MEDUSA_HALO_M"] = "BACK/B_Medusa_Halo",
            ["BACK/B_ANI_PREMIUMBOX_BACKPACK_F"] = "BACK/B_Premiumbox_Backpack",
            ["BACK/B_ANI_PREMIUMBOX_BACKPACK_M"] = "BACK/B_Premiumbox_Backpack",
            ["BACK/B_ANI_SIXEYED_SISTER_BACKPACK_F"] = "BACK/B_SixEyed_Sister_Backpack",
            ["BACK/B_ANI_SIXEYED_SISTER_BACKPACK_M"] = "BACK/B_SixEyed_Sister_Backpack",
            ["BACK/B_ANI_WHITEFOX_BACKPACK_F"] = "BACK/B_WhiteFox_Backpack",
            ["BACK/B_ANI_WHITEFOX_BACKPACK_M"] = "BACK/B_WhiteFox_Backpack",
            ["BACK/B_ANI_ZARATAN_BACKPACK_F"] = "BACK/ZaratanBackPack",
            ["BACK/B_ANI_ZARATAN_BACKPACK_M"] = "BACK/ZaratanBackPack",
            ["BACK/B_BABYSHARK_BACKPACK_F"] = "BACK/B_BabyShark_Backpack_D",
            ["BACK/B_BABYSHARK_BACKPACK_M"] = "BACK/B_BabyShark_Backpack_D",
            ["BACK/B_KFH_LOTUSFLOWER_BACKPACK_F"] = "BACK/B_Ani_KFH_LotusFlower_Backpack",
            ["BACK/B_KFH_LOTUSFLOWER_BACKPACK_M"] = "BACK/B_Ani_KFH_LotusFlower_Backpack",
            ["BACK/QUEENT_FESTIVAL_BACKPACK_F"] = "BACK/QueenT_Festival_Back Pack",
            ["BACK/QUEENT_FESTIVAL_BACKPACK_M"] = "BACK/QueenT_Festival_Back Pack",
            ["BASIC/BLAZEHERO_PHOENIX/BLAZEHERO_PHOENIX_BACKPACK"] = "BACK/BlazeHero_Phoenix_Wing",
            ["WAIST/W_CFSTEAM_ACCESSORY_F"] = "WAIST/W_CFSTeam_Accessory_S9",
            ["WAIST/W_CFSTEAM_ACCESSORY_M"] = "WAIST/W_CFSTeam_Accessory_S9",
            ["BASIC/ANNIVERSARY_17TH/ANNIVERSARY_17TH_GLASSES_GR"] = "BASIC/Anniversary_17th/Anniversary_17th_FACE2_GR",
            ["BASIC/LANXIER_NG/LANXIER_NG_SHOULER_GR"] = "BASIC/LanXier_NG/LanXier_NG_Radio_GR",
            ["BASIC/LANXIER/LANXIER_SHOULER_GR"] = "BASIC/LanXier/LanXier_radio_GR"
        };

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
        var invalidGeometryMeshes = new List<ObjInvalidMeshReport>();
        ExportTransform transform = CalculateExportTransform(sources);

        using var objWriter = new StreamWriter(fullObjPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        objWriter.WriteLine("# Exported by CF Rez Manager");
        objWriter.WriteLine($"# Coordinates centered and scaled by {transform.Scale.ToString("G17", CultureInfo.InvariantCulture)} for Blender import.");
        objWriter.WriteLine($"mtllib {ToObjPath(Path.GetFileName(mtlPath))}");

        int vertexOffset = 0;
        int textureCoordinateOffset = 0;
        int globalMeshIndex = 0;
        int meshCount = 0;
        int vertexCount = 0;
        int triangleCount = 0;

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

                int nonFinitePositionCount = mesh.Vertices.Count(vertex => !IsFinite(vertex));
                int nonFiniteTextureCoordinateCount = mesh.HasTextureCoordinates
                    ? mesh.TextureCoordinates!.Count(coordinate => !IsFinite(coordinate))
                    : 0;
                if (nonFinitePositionCount > 0 || nonFiniteTextureCoordinateCount > 0)
                {
                    invalidGeometryMeshes.Add(new ObjInvalidMeshReport(
                        source.ResourcePath,
                        mesh.Name,
                        mesh.Vertices.Count,
                        nonFinitePositionCount,
                        nonFiniteTextureCoordinateCount));
                    globalMeshIndex++;
                    continue;
                }

                string groupName = MakeObjIdentifier($"{source.Name}_{mesh.Name}");
                objWriter.WriteLine();
                // Keep each source mesh as its own Blender object. A shared OBJ
                // object makes unrelated body/head UV islands appear together
                // in Blender's UV editor even when their face materials differ.
                objWriter.WriteLine($"o {groupName}");
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
                    triangleCount++;
                }

                vertexOffset += mesh.Vertices.Count;
                vertexCount += mesh.Vertices.Count;
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
        string textureReportPath = Path.Combine(outputDirectory, $"{baseName}_texture_report.txt");
        WriteTextureReport(textureReportPath, sources, textureReports, invalidGeometryMeshes);

        return new LithTechObjExportResult(
            fullObjPath,
            mtlPath,
            textureDirectory,
            sources.Count,
            meshCount,
            vertexCount,
            triangleCount,
            invalidGeometryMeshes.Count,
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
            if (!IsFinite(vertex))
            {
                continue;
            }

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
        bool hasModelSlotSpecificBindings = source.ModelTextureSlotMappingPresenceResolver is not null &&
                                            source.ModelTextureSlotMappingPresenceResolver(source.ResourcePath);
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
            string? cachedResolvedReference = !string.IsNullOrWhiteSpace(existing.TextureRelativePath)
                ? inferredTextureCandidates.FirstOrDefault(candidate =>
                    exportedTextureByCandidate.TryGetValue(NormalizeTextureKey(candidate), out string? cachedPath) &&
                    string.Equals(cachedPath, existing.TextureRelativePath, StringComparison.OrdinalIgnoreCase))
                : null;
            AddTextureReportForMesh(
                textureReports,
                source.ResourcePath,
                mesh,
                materialHints,
                hasModelSlotSpecificBindings,
                inferredTextureCandidates,
                cachedResolvedReference,
                existing.TextureRelativePath);
            return existing;
        }

        string materialBaseName = !string.IsNullOrEmpty(textureKey)
            ? GetKnownResourceStem(textureKey)
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
        if (!string.IsNullOrWhiteSpace(textureRelativePath))
        {
            // The fallback palette is only useful for untextured meshes. In an
            // MTL, Kd multiplies map_Kd in common importers, so tinted fallback
            // colors corrupt otherwise-correct game diffuse textures.
            r = 1;
            g = 1;
            b = 1;
        }
        AddTextureReportForMesh(
            textureReports,
            source.ResourcePath,
            mesh,
            materialHints,
            hasModelSlotSpecificBindings,
            inferredTextureCandidates,
            resolvedTextureReference,
            textureRelativePath);

        var material = new ObjMaterial(materialName, textureRelativePath, r, g, b);
        materialByKey[materialKey] = material;
        materials.Add(material);
        return material;
    }

    private static void AddTextureReportForMesh(
        List<ObjTextureReport> textureReports,
        string sourcePath,
        LithTechMesh mesh,
        IReadOnlyList<string> materialHints,
        bool hasModelSlotSpecificBindings,
        IReadOnlyList<string> inferredTextureCandidates,
        string? resolvedTextureReference,
        string? exportedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(mesh.TexturePath) &&
            materialHints.Count == 0 &&
            inferredTextureCandidates.Count == 0 &&
            string.IsNullOrWhiteSpace(exportedRelativePath) &&
            !mesh.HasAuthoritativeTextureBindings &&
            !hasModelSlotSpecificBindings)
        {
            return;
        }

        textureReports.Add(new ObjTextureReport(
            sourcePath,
            mesh.Name,
            mesh.TexturePath,
            mesh.TextureSlotIndices ?? [],
            mesh.RenderStyleIndex,
            materialHints,
            inferredTextureCandidates,
            resolvedTextureReference,
            exportedRelativePath));
    }

    internal static IEnumerable<string> EnumerateTextureCandidates(LithTechMesh mesh, LithTechObjExportSource source)
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

        string slotModelReference = string.IsNullOrWhiteSpace(source.ResourcePath)
            ? source.Document.Name
            : source.ResourcePath;
        if (source.TextureContextSlotResolver is not null && !string.IsNullOrWhiteSpace(source.TextureContext))
        {
            foreach (int slotIndex in (mesh.TextureSlotIndices ?? []).Distinct())
            {
                foreach (string slotTexture in source.TextureContextSlotResolver(slotModelReference, source.TextureContext, slotIndex))
                {
                    if (yielded.Add(slotTexture))
                    {
                        yield return slotTexture;
                    }

                    foreach (string candidate in ExpandTextureNameCandidates(slotTexture))
                    {
                        if (yielded.Add(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }
        else if (source.TextureSlotResolver is not null)
        {
            foreach (int slotIndex in (mesh.TextureSlotIndices ?? []).Distinct())
            {
                foreach (string slotTexture in source.TextureSlotResolver(slotModelReference, slotIndex))
                {
                    if (yielded.Add(slotTexture))
                    {
                        yield return slotTexture;
                    }

                    foreach (string candidate in ExpandTextureNameCandidates(slotTexture))
                    {
                        if (yielded.Add(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }

        bool hasOnlyUnmappedTranslucentSlot31 = mesh.HasAuthoritativeTextureBindings &&
                                                  mesh.RenderStyleIndex == 1 &&
                                                  (mesh.TextureSlotIndices ?? []).Contains(31) &&
                                                  !(mesh.TextureSlotIndices ?? []).Any(slotIndex => slotIndex != 31);
        if (hasOnlyUnmappedTranslucentSlot31)
        {
            yield break;
        }

        // Try model/mesh-scoped resolution before generic mesh-name aliases.
        // A short name such as "hand" can resolve globally to an unrelated
        // asset even when the model has a unique item-specific FVIEW texture.
        // Explicit model paths, material hints, and configured slot references
        // above retain priority; the candidate-dependent pass below remains as
        // a fallback for resolvers that need a generated alias.
        if (source.MeshTextureResolver is not null)
        {
            string modelReference = string.IsNullOrWhiteSpace(source.ResourcePath)
                ? source.Document.Name
                : source.ResourcePath;
            int[] slotIndices = GetTextureResolverSlotIndices(mesh);
            if (slotIndices.Length == 0)
            {
                if (mesh.HasAuthoritativeTextureBindings)
                {
                    yield break;
                }

                slotIndices = [-1];
            }

            foreach (int slotIndex in slotIndices)
            {
                foreach (string texturePath in source.MeshTextureResolver(modelReference, mesh.Name, slotIndex, string.Empty))
                {
                    if (yielded.Add(texturePath))
                    {
                        yield return texturePath;
                    }
                }
            }
        }

        if (source.ModelTextureSlotMappingPresenceResolver is not null &&
            source.ModelTextureSlotMappingPresenceResolver(slotModelReference))
        {
            // Once a model has slot-specific evidence, do not paint unmapped
            // slots with a model-wide or mesh-name guess. That would silently
            // assign a weapon skin to unrelated hand/arm or accessory meshes.
            yield break;
        }

        // A number of LTBs contain no texture path at all, but their mesh
        // names carry the differentiating suffix used by the diffuse asset
        // (for example ArmoredEngineer02 -> AI_ArmoredEngineer02). Try the
        // mesh-scoped resource aliases before falling back to the model-wide
        // name so those multi-material models keep their per-mesh mapping.
        List<string> meshTextureCandidates = EnumerateMeshTextureCandidates(mesh, source).ToList();
        foreach (string meshCandidate in meshTextureCandidates)
        {
            if (yielded.Add(meshCandidate))
            {
                yield return meshCandidate;
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
            IEnumerable<string> configLookupNames = meshTextureCandidates.Concat(sourceTextureCandidates);
            if (!string.IsNullOrWhiteSpace(source.ResourcePath))
            {
                configLookupNames = configLookupNames.Append(source.ResourcePath);
            }

            foreach (string configTexture in source.TextureConfigResolver(configLookupNames))
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

        if (source.MeshTextureResolver is not null)
        {
            string modelReference = string.IsNullOrWhiteSpace(source.ResourcePath)
                ? source.Document.Name
                : source.ResourcePath;
            string[] candidateReferences = yielded.ToArray();
            int[] slotIndices = GetTextureResolverSlotIndices(mesh);
            if (slotIndices.Length == 0)
            {
                if (mesh.HasAuthoritativeTextureBindings)
                {
                    yield break;
                }

                slotIndices = [-1];
            }

            foreach (int slotIndex in slotIndices)
            {
                foreach (string candidateReference in candidateReferences)
                {
                    foreach (string texturePath in source.MeshTextureResolver(modelReference, mesh.Name, slotIndex, candidateReference))
                    {
                        if (yielded.Add(texturePath))
                        {
                            yield return texturePath;
                        }
                    }
                }
            }
        }
    }

    private static int[] GetTextureResolverSlotIndices(LithTechMesh mesh)
    {
        IEnumerable<int> slotIndices = (mesh.TextureSlotIndices ?? []).Distinct();
        if (mesh.HasAuthoritativeTextureBindings)
        {
            slotIndices = slotIndices.Where(slotIndex => slotIndex != 31);
        }

        return slotIndices.ToArray();
    }

    private static IEnumerable<string> EnumerateMeshTextureCandidates(
        LithTechMesh mesh,
        LithTechObjExportSource source)
    {
        string meshStem = GetKnownTextureStem(mesh.Name);
        if (string.IsNullOrWhiteSpace(meshStem))
        {
            yield break;
        }

        string normalizedResourcePath = NormalizeTextureKey(source.ResourcePath);
        string modelPath = RemovePathExtension(normalizedResourcePath);
        int separator = modelPath.LastIndexOf('/');
        if (separator < 0)
        {
            foreach (string candidate in ExpandTextureNameCandidates(meshStem))
            {
                yield return candidate;
            }

            yield break;
        }

        string modelDirectory = modelPath[..(separator + 1)];
        var meshStems = new List<string> { meshStem };
        string modelFamily = GetModelFamilyFromResourcePath(modelPath);
        if (string.Equals(modelFamily, "CHARACTER", StringComparison.OrdinalIgnoreCase) &&
            !meshStem.StartsWith("AI_", StringComparison.OrdinalIgnoreCase))
        {
            meshStems.Add("AI_" + meshStem);

            // Several boss models name their first numbered mesh with a
            // trailing zero while the corresponding diffuse is conventionally
            // suffixed 01 (ArmoredEngineer0 -> AI_ArmoredEngineer01).
            if (meshStem.EndsWith("0", StringComparison.Ordinal) &&
                meshStem.Length > 1)
            {
                meshStems.Add("AI_" + meshStem + "1");
            }
        }

        if (string.Equals(modelFamily, "WEAPONS", StringComparison.OrdinalIgnoreCase) &&
            !meshStem.StartsWith("L_", StringComparison.OrdinalIgnoreCase) &&
            !meshStem.StartsWith("PV", StringComparison.OrdinalIgnoreCase) &&
            !meshStem.StartsWith("QV", StringComparison.OrdinalIgnoreCase))
        {
            meshStems.Add("L_" + meshStem);
        }

        foreach (string candidateStem in meshStems.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string candidate in ExpandTextureNameCandidates(candidateStem))
            {
                yield return candidate;
            }

            string syntheticModelPath = modelDirectory + candidateStem + ".LTB";
            foreach (string candidate in ExpandSourceResourceTexturePathCandidates(syntheticModelPath))
            {
                yield return candidate;
            }
        }
    }

    private static string GetModelFamilyFromResourcePath(string modelPath)
    {
        int modelsIndex = modelPath.IndexOf("/Models/", StringComparison.OrdinalIgnoreCase);
        if (modelsIndex < 0)
        {
            return string.Empty;
        }

        string afterModels = modelPath[(modelsIndex + "/Models/".Length)..];
        int separator = afterModels.IndexOf('/');
        return separator > 0 ? afterModels[..separator] : afterModels;
    }

    private static IEnumerable<string> EnumerateSourceTextureCandidates(LithTechObjExportSource source)
    {
        foreach (string candidate in ExpandSourceResourceTexturePathCandidates(source.ResourcePath))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(GetKnownResourceStem(source.ResourcePath)))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(GetKnownResourceStem(source.Name)))
        {
            yield return candidate;
        }

        foreach (string candidate in ExpandTextureNameCandidates(GetKnownResourceStem(source.Document.Name)))
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
                foreach (string texturePath in EnumerateModelTexturePathAliases(afterModels))
                {
                    yield return "ModelTextures/" + texturePath;
                    yield return withoutExtension[..modelsIndex] + "/ModelTextures/" + texturePath;
                }

                string sourceRoot = withoutExtension[..modelsIndex].Trim('/');
                if (string.Equals(GetLastPathSegment(sourceRoot), "RF016", StringComparison.OrdinalIgnoreCase))
                {
                    int slashIndex = sourceRoot.LastIndexOf('/');
                    foreach (string texturePath in EnumerateModelTexturePathAliases(afterModels))
                    {
                        yield return "rf017/ModelTextures/" + texturePath;
                        if (slashIndex >= 0)
                        {
                            yield return sourceRoot[..slashIndex] + "/rf017/ModelTextures/" + texturePath;
                        }

                        foreach (string laterTextureRoot in EnumerateLaterPackageTextureRoots(sourceRoot, slashIndex))
                        {
                            yield return laterTextureRoot + "/rf017/ModelTextures/" + texturePath;
                        }
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
                foreach (string texturePath in EnumerateModelTexturePathAliases(afterModels))
                {
                    yield return "ModelTextures/" + texturePath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLaterPackageTextureRoots(string sourceRoot, int sourceRootSlashIndex)
    {
        if (sourceRootSlashIndex < 0)
        {
            yield break;
        }

        string packageRoot = sourceRoot[..sourceRootSlashIndex];
        string packageName = GetLastPathSegment(packageRoot);
        int packageNumber;
        if (string.Equals(packageName, "rez", StringComparison.OrdinalIgnoreCase))
        {
            // The first package is named "rez"; later packages continue as
            // rez2, rez3, ... rather than starting at rez1.
            packageNumber = 1;
        }
        else if (!packageName.StartsWith("rez", StringComparison.OrdinalIgnoreCase) ||
                 !int.TryParse(packageName[3..], out packageNumber))
        {
            yield break;
        }

        string parentRoot = packageRoot[..Math.Max(0, packageRoot.Length - packageName.Length)];
        for (int candidatePackageNumber = 1; candidatePackageNumber <= 6; candidatePackageNumber++)
        {
            string candidatePackage = candidatePackageNumber == 1
                ? "rez"
                : "rez" + candidatePackageNumber.ToString(CultureInfo.InvariantCulture);
            if (candidatePackageNumber != packageNumber)
            {
                yield return parentRoot + candidatePackage;
            }
        }
    }

    private static IEnumerable<string> EnumerateModelTexturePathAliases(string modelPath)
    {
        string normalized = NormalizeTextureKey(modelPath).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (yielded.Add(normalized))
        {
            yield return normalized;
        }

        int slashIndex = normalized.IndexOf('/');
        if (slashIndex <= 0 || slashIndex + 1 >= normalized.Length)
        {
            yield break;
        }

        string family = normalized[..slashIndex];
        string relativePath = normalized[(slashIndex + 1)..];
        int directorySlashIndex = relativePath.LastIndexOf('/');
        string directory = directorySlashIndex >= 0 ? relativePath[..directorySlashIndex] : string.Empty;
        string stem = directorySlashIndex >= 0 ? relativePath[(directorySlashIndex + 1)..] : relativePath;
        foreach (string aliasPath in EnumerateNonViewTexturePathAliases(family, directory, stem))
        {
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        foreach (string aliasPath in EnumerateViewTexturePathAliases(family, directory, stem))
        {
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        foreach (string aliasPath in EnumerateCharacterTexturePathAliases(family, directory, stem))
        {
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        if (string.Equals(family, "SOCKETITEM", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(directory))
        {
            // A number of character accessory packages keep the texture in
            // the same BASIC/<item> directory but rename the semantic part
            // (for example IXIONS_MASK -> IXIONS_FACE) or add H_/T_/W_.
            List<string> socketSlots = EnumerateSocketItemTextureSlots(stem, sourceSlot: null).ToList();
            if (socketSlots.Count == 0)
            {
                // Some legacy accessory names (Grenade, Gun, Tail, etc.)
                // encode no socket slot, but still use the same package
                // directory and color subdirectory.
                socketSlots.Add(string.Empty);
            }

            foreach (string slot in socketSlots)
            {
                foreach (string textureStem in EnumerateSocketItemTextureStemAliases(stem, slot))
                {
                    string sameDirectoryPath = family + "/" + directory + "/" + textureStem;
                    if (yielded.Add(sameDirectoryPath))
                    {
                        yield return sameDirectoryPath;
                    }

                    foreach (string colorDirectory in EnumerateSocketItemColorDirectories(directory, stem))
                    {
                        string colorDirectoryPath = family + "/" + colorDirectory + "/" + textureStem;
                        if (yielded.Add(colorDirectoryPath))
                        {
                            yield return colorDirectoryPath;
                        }
                    }

                    foreach (string reorderedStem in EnumerateSocketItemReorderedColorStems(directory, textureStem))
                    {
                        string reorderedPath = family + "/" + directory + "/" + reorderedStem;
                        if (yielded.Add(reorderedPath))
                        {
                            yield return reorderedPath;
                        }
                    }

                    foreach (string qualifiedStem in EnumerateSocketItemDirectoryQualifiedStems(directory, textureStem))
                    {
                        string qualifiedPath = family + "/" + directory + "/" + qualifiedStem;
                        if (yielded.Add(qualifiedPath))
                        {
                            yield return qualifiedPath;
                        }
                    }

                    foreach (string directoryAlias in EnumerateSocketItemDirectoryAliases(directory))
                    {
                        string aliasedDirectoryPath = family + "/" + directoryAlias + "/" + textureStem;
                        if (yielded.Add(aliasedDirectoryPath))
                        {
                            yield return aliasedDirectoryPath;
                        }

                        foreach (string directoryStemAlias in
                                 EnumerateSocketItemDirectoryStemAliases(directory, directoryAlias, textureStem))
                        {
                            string aliasedStemPath = family + "/" + directoryAlias + "/" + directoryStemAlias;
                            if (yielded.Add(aliasedStemPath))
                            {
                                yield return aliasedStemPath;
                            }
                        }
                    }
                }
            }
        }

        foreach (string aliasPath in EnumerateSocketItemSemanticTexturePathAliases(family, directory, stem))
        {
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        foreach ((string family, string directory, string stem) alias in
                 EnumerateModelTextureFamilyAliases(family, directory, stem))
        {
            string aliasRelativePath = string.IsNullOrWhiteSpace(alias.directory)
                ? alias.stem
                : alias.directory + "/" + alias.stem;
            string aliasPath = alias.family + "/" + aliasRelativePath;
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        if (LegacyModelTextureAliases.TryGetValue(stem, out string? legacyTextureStem))
        {
            string aliasPath = "WEAPONS/" + legacyTextureStem;
            if (yielded.Add(aliasPath))
            {
                yield return aliasPath;
            }
        }

        foreach (string variantStem in StripModelVariantSuffixes(stem))
        {
            string variantRelativePath = string.IsNullOrWhiteSpace(directory)
                ? variantStem
                : directory + "/" + variantStem;
            string sameFamilyPath = family + "/" + variantRelativePath;
            if (yielded.Add(sameFamilyPath))
            {
                yield return sameFamilyPath;
            }

            foreach (string aliasPath in EnumerateNonViewTexturePathAliases(family, directory, variantStem))
            {
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }

            foreach (string aliasPath in EnumerateViewTexturePathAliases(family, directory, variantStem))
            {
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }

            foreach (string aliasPath in EnumerateCharacterTexturePathAliases(family, directory, variantStem))
            {
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }

            foreach (string aliasPath in EnumerateSocketItemSemanticTexturePathAliases(family, directory, variantStem))
            {
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }

            foreach ((string family, string directory, string stem) alias in
                     EnumerateModelTextureFamilyAliases(family, directory, variantStem))
            {
                string aliasRelativePath = string.IsNullOrWhiteSpace(alias.directory)
                    ? alias.stem
                    : alias.directory + "/" + alias.stem;
                string aliasPath = alias.family + "/" + aliasRelativePath;
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }

            if (LegacyModelTextureAliases.TryGetValue(variantStem, out string? variantLegacyTextureStem))
            {
                string aliasPath = "WEAPONS/" + variantLegacyTextureStem;
                if (yielded.Add(aliasPath))
                {
                    yield return aliasPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateNonViewTexturePathAliases(
        string family,
        string directory,
        string stem)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            yield break;
        }

        int slashIndex = directory.LastIndexOf('/');
        string flattenedDirectory = slashIndex >= 0 ? directory[..slashIndex] : string.Empty;
        if (!string.IsNullOrWhiteSpace(flattenedDirectory))
        {
            yield return family + "/" + flattenedDirectory + "/" + stem;
        }

        if (!string.Equals(family, "SOCKETITEM", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string slot = directory.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        string? slotPrefix = slot.ToUpperInvariant() switch
        {
            "WAIST" => "W_",
            "THIGH" => "T_",
            "HEAD" => "H_",
            "FACE" => "F_",
            "BACK" => "B_",
            "SHOULDER" => "S_",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(slotPrefix) ||
            stem.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return family + "/" + directory + "/" + slotPrefix + stem;
        if (!string.IsNullOrWhiteSpace(flattenedDirectory))
        {
            yield return family + "/" + flattenedDirectory + "/" + slotPrefix + stem;
        }
    }

    private static IEnumerable<string> EnumerateViewTexturePathAliases(
        string family,
        string directory,
        string stem)
    {
        if (!string.Equals(family, "PLAYERVIEW", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(family, "WEAPONS", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        // View-model packages are not consistent about preserving the model's
        // subdirectory. The base weapon texture is often flattened to the
        // family root, while older AI assets keep only the first directory.
        yield return family + "/" + stem;

        string[] directories = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] directoryAliases = EnumerateViewTextureDirectoryAliases(directories).ToArray();
        if (directories.Length > 1)
        {
            yield return family + "/" + directories[0] + "/" + stem;
        }

        foreach (string directoryAlias in directoryAliases)
        {
            yield return family + "/" + directoryAlias + "/" + stem;
        }

        foreach (string stemAlias in EnumerateViewTextureStemAliases(stem))
        {
            yield return family + "/" + stemAlias;
            if (directories.Length > 1)
            {
                yield return family + "/" + directories[0] + "/" + stemAlias;
            }

            foreach (string directoryAlias in directoryAliases)
            {
                yield return family + "/" + directoryAlias + "/" + stemAlias;
            }

            if (directories.Length > 0)
            {
                yield return family + "/" + directories[^1] + "/" + stemAlias;
            }
        }

        string alternateFamily = string.Equals(family, "PLAYERVIEW", StringComparison.OrdinalIgnoreCase)
            ? "WEAPONS"
            : "PLAYERVIEW";

        // Weapon-view assets are frequently flattened to the alternate
        // family root. For example Models/PLAYERVIEW/.../PV-FakeBalloon.LTB
        // uses ModelTextures/WEAPONS/QV-Fakeballoon.DTX, while some older
        // assets omit the QV/PV prefix entirely (AK47-GOLD).
        yield return alternateFamily + "/" + stem;
        foreach (string stemAlias in EnumerateViewTextureStemAliases(stem))
        {
            yield return alternateFamily + "/" + stemAlias;
        }

        if (string.Equals(family, "PLAYERVIEW", StringComparison.OrdinalIgnoreCase) &&
            TryReplaceViewPrefix(stem, "PV", "QV", out string worldStem))
        {
            yield return alternateFamily + "/" + worldStem;
        }
        else if (string.Equals(family, "WEAPONS", StringComparison.OrdinalIgnoreCase) &&
                 TryReplaceViewPrefix(stem, "QV", "PV", out string viewStem))
        {
            yield return alternateFamily + "/" + viewStem;
        }

        if (string.Equals(family, "WEAPONS", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("L_", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("PV", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("QV", StringComparison.OrdinalIgnoreCase))
        {
            yield return family + "/L_" + stem;
        }

        foreach (string strippedStem in StripViewModelPrefixes(stem))
        {
            yield return alternateFamily + "/" + strippedStem;
        }

        foreach (string directoryAlias in directoryAliases)
        {
            yield return alternateFamily + "/" + directoryAlias + "/" + stem;
            foreach (string stemAlias in EnumerateViewTextureStemAliases(stem))
            {
                yield return alternateFamily + "/" + directoryAlias + "/" + stemAlias;
            }
        }

        // Some first-person character models are stored below PLAYERVIEW,
        // while their diffuse textures remain in the matching CHARACTER
        // package (for example PV-DevilHunter -> DevilHunter_body/face).
        foreach (string characterAlias in EnumerateCharacterTexturePathAliases("CHARACTER", directory, stem))
        {
            yield return characterAlias;
        }
    }

    private static IEnumerable<string> EnumerateViewTextureDirectoryAliases(string[] directories)
    {
        if (directories.Length == 0)
        {
            yield break;
        }

        string last = directories[^1];
        foreach ((string from, string to) in new[]
        {
            ("WAVE_C4", "DemolitionC4"),
            ("WAVE_Claymore", "AI2"),
            ("Claymore", "AI2"),
            ("Claymore_Remote", "AI2"),
            ("PlasmaShocker", "AI2"),
            ("Sentrygun", "AI2"),
            ("PredetorMissile", "AI2")
        })
        {
            if (last.Equals(from, StringComparison.OrdinalIgnoreCase))
            {
                yield return to;
            }
        }

        if (directories.Length > 1)
        {
            yield return directories[0];
        }
    }

    private static IEnumerable<string> EnumerateViewTextureStemAliases(string stem)
    {
        foreach (string stripped in StripViewModelPrefixes(stem))
        {
            yield return stripped;
            yield return stripped + "_arm";
            yield return stripped + "_hand";
            yield return stripped + "_weapon";
        }

        foreach ((string from, string to) in new[]
        {
            ("PV-WAVE-", "PV-"),
            ("PV-WAVE_", "PV_"),
            ("QV-WAVE-", "QV-"),
            ("QV-WAVE_", "QV_")
        })
        {
            if (stem.StartsWith(from, StringComparison.OrdinalIgnoreCase) &&
                stem.Length > from.Length)
            {
                yield return to + stem[from.Length..];
            }
        }

        string[] sentryReplacements = ["_1ST", "_2ND", "_3RD"];
        for (int index = 0; index < sentryReplacements.Length; index++)
        {
            string marker = sentryReplacements[index];
            int markerIndex = stem.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                yield return stem[..markerIndex] + "_v" + (index + 1).ToString(CultureInfo.InvariantCulture) + stem[(markerIndex + marker.Length)..];
            }
        }

        if (stem.Equals("PV-Board", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("QV-Board", StringComparison.OrdinalIgnoreCase))
        {
            yield return stem + "LEG";
        }

        if (stem.Contains("AK-47", StringComparison.OrdinalIgnoreCase))
        {
            yield return stem.Replace("AK-47", "AK47", StringComparison.OrdinalIgnoreCase);
        }

        if (stem.Contains("AK_47", StringComparison.OrdinalIgnoreCase))
        {
            yield return stem.Replace("AK_47", "AK47", StringComparison.OrdinalIgnoreCase);
        }

        if (stem.Contains("AK47_NEW", StringComparison.OrdinalIgnoreCase))
        {
            yield return stem.Replace("AK47_NEW", "AK47_NEWTRO", StringComparison.OrdinalIgnoreCase);
        }

        if (!stem.StartsWith("PV-", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("PV_", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("QV-", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("QV_", StringComparison.OrdinalIgnoreCase))
        {
            yield return "PV-" + stem;
            yield return "QV-" + stem;
        }
    }

    private static IEnumerable<string> EnumerateCharacterTexturePathAliases(
        string family,
        string directory,
        string stem)
    {
        if (!string.Equals(family, "CHARACTER", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (string stemAlias in EnumerateCharacterTextureStemAliases(stem))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return family + "/" + directory + "/" + stemAlias;
            }

            string[] directories = directory
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (directories.Length > 0)
            {
                yield return family + "/" + directories[0] + "/" + stemAlias;
            }

            yield return family + "/" + stemAlias;
        }
    }

    private static IEnumerable<string> EnumerateCharacterTextureStemAliases(string stem)
    {
        var bases = new List<string> { stem };
        string current = stem;
        foreach (string prefix in new[] { "PV-", "PV_", "QV-", "QV_" })
        {
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                current.Length > prefix.Length)
            {
                current = current[prefix.Length..];
                bases.Add(current);
                break;
            }
        }

        foreach (string prefix in new[] { "AI_M-motion_", "M-motion-", "M-motion_", "AI_" })
        {
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                current.Length > prefix.Length)
            {
                current = current[prefix.Length..];
                bases.Add(current);
                break;
            }
        }

        foreach (string stripped in StripCharacterMotionSuffixes(current))
        {
            bases.Add(stripped);
        }

        foreach (string baseStem in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return baseStem;

            if (baseStem.StartsWith("NANO_", StringComparison.OrdinalIgnoreCase) &&
                baseStem.Length > "NANO_".Length)
            {
                yield return baseStem["NANO_".Length..];
            }
            else if (!baseStem.StartsWith("NANO", StringComparison.OrdinalIgnoreCase))
            {
                yield return "NANO_" + baseStem;
            }

            foreach (string suffix in new[]
            {
                "_Normal",
                "_Master",
                "_Host",
                "_body",
                "_face",
                "_hair",
                "_Head"
            })
            {
                yield return baseStem + suffix;
            }
        }

    }

    private static IEnumerable<string> StripCharacterMotionSuffixes(string stem)
    {
        string current = stem;
        foreach (string suffix in new[] { "_M-motion", "-motion", "_motion", "_MOTION" })
        {
            if (current.Length > suffix.Length &&
                current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                current = current[..^suffix.Length];
                yield return current;
            }
        }
    }

    private static IEnumerable<string> EnumerateSocketItemSemanticTexturePathAliases(
        string family,
        string directory,
        string stem)
    {
        if (!string.Equals(family, "SOCKETITEM", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(stem))
        {
            yield break;
        }

        string relativeModelPath = string.IsNullOrWhiteSpace(directory)
            ? stem
            : directory + "/" + stem;
        if (ExplicitSocketItemTextureAliases.TryGetValue(relativeModelPath, out string? explicitTexturePath))
        {
            yield return family + "/" + explicitTexturePath;
        }

        string[] sourceDirectories = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? sourceSlot = sourceDirectories.FirstOrDefault(segment =>
            IsSocketItemSlot(segment));
        string? familyDirectory = sourceDirectories.Length > 0
            ? sourceDirectories[^1]
            : null;

        foreach (string slot in EnumerateSocketItemTextureSlots(stem, sourceSlot))
        {
            var targetDirectories = new List<string> { slot };
            if (!string.IsNullOrWhiteSpace(familyDirectory) &&
                !IsSocketItemSlot(familyDirectory) &&
                !string.Equals(familyDirectory, "BASIC", StringComparison.OrdinalIgnoreCase))
            {
                targetDirectories.Add(slot + "/" + familyDirectory);
                targetDirectories.Add(slot + "/" + familyDirectory + "_NEW");
                targetDirectories.Add("BASIC/" + familyDirectory);
                targetDirectories.Add("BASIC/" + familyDirectory + "_NEW");
            }

            if (!string.IsNullOrWhiteSpace(sourceSlot))
            {
                // Legacy accessories placed their diffuse in BASIC even when
                // the model was moved to HEAD/FACE/BACK for attachment.
                targetDirectories.Add("BASIC");
            }

            foreach (string textureStem in EnumerateSocketItemTextureStemAliases(stem, slot))
            {
                foreach (string targetDirectory in targetDirectories)
                {
                    yield return family + "/" + targetDirectory + "/" + textureStem;
                }
            }
        }

        // Character accessories occasionally share the character diffuse
        // rather than shipping a duplicate SOCKETITEM texture (Foxhowl
        // Renewal is one such package). Use the last model directory because
        // BASIC is a storage bucket, not part of the character texture key.
        if (!string.IsNullOrWhiteSpace(familyDirectory))
        {
            foreach (string characterAlias in EnumerateCharacterTexturePathAliases(
                         "CHARACTER",
                         familyDirectory,
                         stem))
            {
                yield return characterAlias;
            }
        }

        if (stem.EndsWith("_WC", StringComparison.OrdinalIgnoreCase))
        {
            yield return "CHARACTER/MotionItem/" + stem;
        }

        if (stem.StartsWith("PV-", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("PV_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("QV-", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("QV_", StringComparison.OrdinalIgnoreCase))
        {
            yield return "WEAPONS/" + stem;
            yield return "PLAYERVIEW/" + stem;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemTextureSlots(
        string stem,
        string? sourceSlot)
    {
        if (!string.IsNullOrWhiteSpace(sourceSlot))
        {
            yield return sourceSlot.ToUpperInvariant();
            yield break;
        }

        if (TryGetSocketItemSlotPrefix(stem, out string? prefixSlot))
        {
            yield return prefixSlot!;
            yield break;
        }

        string upper = stem.ToUpperInvariant();
        if (ContainsAnyToken(upper, "HAIR", "HAIRPIN", "HAT", "HELMET", "CAP"))
        {
            yield return "HEAD";
        }

        if (ContainsAnyToken(upper, "MASK", "FACE", "EYE", "GLASS", "SUNGLASS", "GOGGLE", "GOGGLES", "EYEWEAR", "EYEPATCH"))
        {
            // MASK is used for both face-mounted and head-mounted items in the
            // client. Keep both engine layouts as ordered candidates.
            if (upper.Contains("MASK", StringComparison.Ordinal))
            {
                yield return "FACE";
                yield return "HEAD";
            }
            else
            {
                yield return "FACE";
            }
        }

        if (ContainsAnyToken(upper, "THIGH", "POUCH", "GUNMAG", "TRIPLEMAG", "DOUBLEMAG", "MULTIPOUCH", "GUNHOLSTER", "GUNPATCH", "BATON", "LEGITEM"))
        {
            yield return "THIGH";
        }

        if (ContainsAnyToken(upper, "POUCH", "WAIST", "CANTEEN", "BOTTLE"))
        {
            yield return "WAIST";
        }

        if (ContainsAnyToken(upper, "BACK", "BACKPACK", "MEDAL"))
        {
            yield return "BACK";
        }

        if (ContainsAnyToken(upper, "SHOULDER"))
        {
            yield return "SHOULDER";
        }
    }

    private static IEnumerable<string> EnumerateSocketItemTextureStemAliases(
        string stem,
        string slot)
    {
        var bases = new List<string> { stem };
        bases.AddRange(StripModelVariantSuffixes(stem));
        if (TryGetSocketItemSlotPrefix(stem, out _))
        {
            bases.Add(stem[2..]);
        }

        foreach (string baseStem in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string candidate in EnumerateSocketItemTextureStemVariants(baseStem, slot))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateSocketItemTextureStemVariants(
        string stem,
        string slot)
    {
        var baseCandidates = new List<string> { stem, stem.Trim() };
        if (stem.EndsWith("_M", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_F", StringComparison.OrdinalIgnoreCase))
        {
            baseCandidates.Add(stem[..^2]);
        }

        IEnumerable<string> normalizedBaseCandidates = baseCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(EnumerateSocketItemStemNormalizationAliases)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string baseStem in normalizedBaseCandidates)
        {
            yield return baseStem;

            string? slotPrefix = GetSocketItemSlotPrefix(slot);
            if (!string.IsNullOrWhiteSpace(slotPrefix) &&
                !baseStem.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return slotPrefix + baseStem;
            }

            foreach (string replaced in EnumerateSocketItemSemanticStemReplacements(baseStem))
            {
                yield return replaced;
                if (!string.IsNullOrWhiteSpace(slotPrefix) &&
                    !replaced.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return slotPrefix + replaced;
                }
            }

            foreach (string colored in EnumerateSocketItemColorVariants(baseStem))
            {
                yield return colored;
                if (!string.IsNullOrWhiteSpace(slotPrefix) &&
                    !colored.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return slotPrefix + colored;
                }
            }

            if (baseStem.Length > 2 &&
                baseStem[1] == '_' &&
                "HTWBFS".IndexOf(char.ToUpperInvariant(baseStem[0])) >= 0)
            {
                foreach (string numericVariant in new[] { "1", "2", "_SS1", "_SS2", "_SL1", "_SL2" })
                {
                    foreach (string colored in EnumerateSocketItemColorVariants(baseStem + numericVariant))
                    {
                        yield return colored;
                    }
                }
            }

            if (ContainsAnyToken(baseStem.ToUpperInvariant(), "GUNMAG", "TRIPLEMAG", "DOUBLEMAG"))
            {
                foreach (string magazineVariant in new[] { "SS", "SL" })
                {
                    string variantBase = baseStem + "_" + magazineVariant;
                    foreach (string colored in EnumerateSocketItemColorVariants(variantBase))
                    {
                        yield return colored;
                        if (!string.IsNullOrWhiteSpace(slotPrefix) &&
                            !colored.StartsWith(slotPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return slotPrefix + colored;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSocketItemStemNormalizationAliases(string stem)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yielded.Add(value);
            }
        }

        string trimmed = stem.Trim();
        Add(trimmed);

        if (trimmed.EndsWith("_??", StringComparison.OrdinalIgnoreCase))
        {
            Add(trimmed[..^3]);
        }

        string separatorNormalized = trimmed
            .Replace("_ ", "_", StringComparison.Ordinal)
            .Replace(" _", "_", StringComparison.Ordinal);
        Add(separatorNormalized);
        Add(separatorNormalized.Replace(' ', '_'));

        string collapsedSeparators = separatorNormalized;
        while (collapsedSeparators.Contains("__", StringComparison.Ordinal))
        {
            collapsedSeparators = collapsedSeparators.Replace("__", "_", StringComparison.Ordinal);
        }

        Add(collapsedSeparators.Trim('_'));
        Add(separatorNormalized.Replace(" ", string.Empty, StringComparison.Ordinal));

        foreach (string value in yielded)
        {
            yield return value;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemSemanticStemReplacements(string stem)
    {
        foreach ((string from, string to) in new[]
        {
            ("_MASK", "_FACE"),
            ("_POUCH", "_LEGITEM"),
            ("_POUCH", "_WAIST"),
            ("_POUCH2", "_MEDAL2"),
            ("POUCH2", "MEDAL2"),
            ("_BACKPACK", "_BACK"),
            ("_BACKPACK", "_BAG"),
            ("_WAIST", "_POUCH"),
            ("_THIGH", "_POUCH"),
            ("_GLASS", "_GLRASS"),
            ("_GLASSES", "_GLASS"),
            ("_GLASS", "_GLASSES"),
            ("_EYEWEAR", "_GLASSES"),
            ("_MASK", "_GOGGLE"),
            ("_SUNGLASSES", "_SUN"),
            ("_SUNGLASS", "_SUN"),
            ("_WEIST", "_WAIST"),
            ("SHOULER", "SHOULDER"),
            ("POUCHL", "POUCH"),
            ("GOGMEDAL", "GLORYMEDAL"),
            ("GOGPOUCH", "GLORYPOUCH"),
            ("REDMASK", "MASK")
        })
        {
            int index = stem.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                yield return stem[..index] + to + stem[(index + from.Length)..];
            }
        }

        if (stem.EndsWith("_MASK", StringComparison.OrdinalIgnoreCase))
        {
            yield return stem[..^5] + "_HELMET";
        }

        if (stem.Contains('-', StringComparison.Ordinal))
        {
            yield return stem.Replace('-', '_');
        }

        if (stem.StartsWith("GHOST_", StringComparison.OrdinalIgnoreCase))
        {
            yield return "NANO" + stem;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemColorVariants(string stem)
    {
        string[] colors = ["GR", "BL", "SP"];
        string? existingColor = colors.FirstOrDefault(color =>
            stem.EndsWith("_" + color, StringComparison.OrdinalIgnoreCase));
        if (existingColor is not null)
        {
            string baseStem = stem[..^(existingColor.Length + 1)];
            yield return stem;
            foreach (string color in colors)
            {
                if (!string.Equals(color, existingColor, StringComparison.OrdinalIgnoreCase))
                {
                    yield return baseStem + "_" + color;
                }
            }

            yield break;
        }

        foreach (string color in colors)
        {
            yield return stem + "_" + color;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemColorDirectories(
        string directory,
        string stem)
    {
        string? color = new[] { "GR", "BL", "SP" }.FirstOrDefault(candidate =>
            stem.EndsWith("_" + candidate, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(color))
        {
            yield return directory + "/" + color;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemReorderedColorStems(
        string directory,
        string stem)
    {
        string familyDirectory = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(familyDirectory) ||
            !stem.StartsWith(familyDirectory + "_", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string? color = new[] { "GR", "BL", "SP" }.FirstOrDefault(candidate =>
            stem.EndsWith("_" + candidate, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(color))
        {
            yield break;
        }

        string withoutColor = stem[..^(color.Length + 1)];
        if (withoutColor.Length <= familyDirectory.Length + 1)
        {
            yield break;
        }

        string remainder = withoutColor[(familyDirectory.Length + 1)..];
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            yield return familyDirectory + "_" + color + "_" + remainder;
        }
    }

    private static IEnumerable<string> EnumerateSocketItemDirectoryQualifiedStems(
        string directory,
        string stem)
    {
        string familyDirectory = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(familyDirectory))
        {
            yield break;
        }

        int separator = familyDirectory.IndexOf('_');
        if (separator <= 0 ||
            !stem.StartsWith(familyDirectory[..separator] + "_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith(familyDirectory + "_", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return familyDirectory + stem[(separator)..];
    }

    private static IEnumerable<string> EnumerateSocketItemDirectoryAliases(string directory)
    {
        string[] segments = directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 ||
            !segments[0].Equals("BASIC", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string last = segments[^1];
        if (!last.EndsWith("_NEW", StringComparison.OrdinalIgnoreCase))
        {
            string[] newSegments = segments.ToArray();
            newSegments[^1] = last + "_NEW";
            yield return string.Join('/', newSegments);
        }

        if (last.EndsWith("_NGOLD", StringComparison.OrdinalIgnoreCase))
        {
            string[] ngSegments = segments.ToArray();
            ngSegments[^1] = last[..^"_NGOLD".Length] + "_NG";
            yield return string.Join('/', ngSegments);
        }
    }

    private static IEnumerable<string> EnumerateSocketItemDirectoryStemAliases(
        string sourceDirectory,
        string aliasDirectory,
        string stem)
    {
        string sourceFamily = sourceDirectory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        string aliasFamily = aliasDirectory
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceFamily) ||
            string.IsNullOrWhiteSpace(aliasFamily) ||
            string.Equals(sourceFamily, aliasFamily, StringComparison.OrdinalIgnoreCase) ||
            !stem.StartsWith(sourceFamily + "_", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return aliasFamily + stem[sourceFamily.Length..];
    }

    private static bool TryGetSocketItemSlotPrefix(string stem, out string? slot)
    {
        slot = null;
        if (stem.Length < 2 || stem[1] != '_')
        {
            return false;
        }

        slot = char.ToUpperInvariant(stem[0]) switch
        {
            'W' => "WAIST",
            'T' => "THIGH",
            'H' => "HEAD",
            'F' => "FACE",
            'B' => "BACK",
            'S' => "SHOULDER",
            _ => null
        };
        return slot is not null;
    }

    private static string? GetSocketItemSlotPrefix(string slot)
    {
        return slot.ToUpperInvariant() switch
        {
            "WAIST" => "W_",
            "THIGH" => "T_",
            "HEAD" => "H_",
            "FACE" => "F_",
            "BACK" => "B_",
            "SHOULDER" => "S_",
            _ => null
        };
    }

    private static bool IsSocketItemSlot(string value)
    {
        return value.Equals("WAIST", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("THIGH", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("FACE", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("BACK", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SHOULDER", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAnyToken(string value, params string[] tokens)
    {
        return tokens.Any(token => value.Contains(token, StringComparison.Ordinal));
    }

    private static IEnumerable<(string family, string directory, string stem)> EnumerateModelTextureFamilyAliases(
        string family,
        string directory,
        string stem)
    {
        bool isPlayerview = string.Equals(family, "PLAYERVIEW", StringComparison.OrdinalIgnoreCase);
        bool isWeapons = string.Equals(family, "WEAPONS", StringComparison.OrdinalIgnoreCase);
        if (!isPlayerview && !isWeapons)
        {
            yield break;
        }

        if (isPlayerview)
        {
            if (TryReplaceViewPrefix(stem, "PV", "QV", out string worldStem))
            {
                yield return ("WEAPONS", directory, worldStem);
            }
            else
            {
                yield return ("WEAPONS", directory, stem);
            }

            yield break;
        }

        if (TryReplaceViewPrefix(stem, "QV", "PV", out string viewStem))
        {
            yield return ("PLAYERVIEW", directory, viewStem);
        }
        else
        {
            yield return ("PLAYERVIEW", directory, stem);
        }
    }

    private static bool TryReplaceViewPrefix(
        string stem,
        string fromPrefix,
        string toPrefix,
        out string replaced)
    {
        replaced = string.Empty;
        if (stem.Length <= fromPrefix.Length ||
            !stem.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase) ||
            stem[fromPrefix.Length] is not ('-' or '_'))
        {
            return false;
        }

        replaced = toPrefix + stem[fromPrefix.Length..];
        return true;
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
        string stem = GetKnownTextureStem(fileName);
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
            "_CHANGEMODE",
            "_CHANGE_MODE",
            "_REMOT",
            "_PREVIEW",
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
        string stem = GetKnownTextureStem(textureCandidate);
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
        string stem = GetKnownTextureStem(textureCandidate);
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
            string textureBaseName = GetKnownResourceStem(normalizedTexturePath);
            if (string.IsNullOrWhiteSpace(textureBaseName))
            {
                textureBaseName = "texture";
            }

            string textureFileName = MakeUniqueName(SanitizeFileName(textureBaseName), usedTextureNames) + ".png";
            string textureOutputPath = Path.Combine(textureDirectory, textureFileName);
            BitmapSource diffuseBitmap = RemoveUnusedZeroAlphaChannel(bitmap);
            using (FileStream stream = File.Create(textureOutputPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(diffuseBitmap));
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

    private static BitmapSource RemoveUnusedZeroAlphaChannel(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32)
        {
            return bitmap;
        }

        BitmapSource bgraBitmap = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int rowStride = checked(bgraBitmap.PixelWidth * 4);
        byte[] row = new byte[rowStride];
        for (int y = 0; y < bgraBitmap.PixelHeight; y++)
        {
            bgraBitmap.CopyPixels(new Int32Rect(0, y, bgraBitmap.PixelWidth, 1), row, rowStride, 0);
            for (int alphaOffset = 3; alphaOffset < row.Length; alphaOffset += 4)
            {
                if (row[alphaOffset] != 0)
                {
                    return bitmap;
                }
            }
        }

        // Some game diffuse DTX files store valid RGB with an unused, all-zero
        // alpha plane. Blender treats that plane as transparency on map_Kd,
        // making an otherwise correctly mapped mesh appear black/invisible.
        var opaqueBitmap = new FormatConvertedBitmap(bgraBitmap, PixelFormats.Bgr24, null, 0);
        opaqueBitmap.Freeze();
        return opaqueBitmap;
    }

    private static void WriteTextureReport(
        string reportPath,
        IReadOnlyList<LithTechObjExportSource> sources,
        IReadOnlyList<ObjTextureReport> textureReports,
        IReadOnlyList<ObjInvalidMeshReport> invalidGeometryMeshes)
    {
        using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager OBJ texture report");
        writer.WriteLine();
        writer.WriteLine($"Sources: {sources.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Meshes: {sources.Sum(source => source.Document.Meshes.Count).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Texture references: {textureReports.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Missing textures: {textureReports.Count(report => string.IsNullOrWhiteSpace(report.ExportedRelativePath)).ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Meshes skipped (non-finite geometry): {invalidGeometryMeshes.Count.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine();
        writer.WriteLine("Model decode paths:");
        foreach (LithTechObjExportSource source in sources)
        {
            writer.WriteLine($"  {source.ResourcePath}: {source.Document.StorageDescription}; {source.Document.SourceByteCount.ToString(CultureInfo.InvariantCulture)} source bytes -> {source.Document.DecodedByteCount.ToString(CultureInfo.InvariantCulture)} decoded bytes");
        }
        writer.WriteLine();

        if (textureReports.Count == 0)
        {
            writer.WriteLine("No texture references were found in the decoded model meshes.");
        }
        else
        {
            foreach (ObjTextureReport report in textureReports)
            {
                writer.WriteLine($"Source: {report.SourcePath}");
                writer.WriteLine($"Mesh: {report.MeshName}");
                writer.WriteLine(string.IsNullOrWhiteSpace(report.TexturePath)
                    ? "  Reference: <none>"
                    : $"  Reference: {report.TexturePath}");
                if (report.TextureSlotIndices.Count > 0)
                {
                    writer.WriteLine($"  Native texture slots: {string.Join(", ", report.TextureSlotIndices)}");
                }

                if (report.RenderStyleIndex.HasValue)
                {
                    writer.WriteLine($"  Native renderstyle index: {report.RenderStyleIndex.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (report.RenderStyleIndex == 1 &&
                    report.TextureSlotIndices.Contains(31) &&
                    !report.TextureSlotIndices.Any(slotIndex => slotIndex != 31) &&
                    string.IsNullOrWhiteSpace(report.ExportedRelativePath))
                {
                    writer.WriteLine("  Binding status: native slot 31 has no resolved physical texture; kept unbound");
                }

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

        if (invalidGeometryMeshes.Count > 0)
        {
            writer.WriteLine("Skipped meshes with non-finite geometry:");
            foreach (ObjInvalidMeshReport invalidMesh in invalidGeometryMeshes)
            {
                writer.WriteLine($"  {invalidMesh.SourcePath} | {invalidMesh.MeshName}: {invalidMesh.NonFinitePositionCount.ToString(CultureInfo.InvariantCulture)}/{invalidMesh.VertexCount.ToString(CultureInfo.InvariantCulture)} non-finite positions, {invalidMesh.NonFiniteTextureCoordinateCount.ToString(CultureInfo.InvariantCulture)} non-finite UVs");
            }
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

    private static bool IsFinite(LithTechVector3 vertex)
    {
        return double.IsFinite(vertex.X) && double.IsFinite(vertex.Y) && double.IsFinite(vertex.Z);
    }

    private static bool IsFinite(LithTechVector2 coordinate)
    {
        return double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y);
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

    private static string GetKnownTextureStem(string? value)
    {
        string fileName = Path.GetFileName(NormalizeTextureKey(value));
        string extension = Path.GetExtension(fileName);
        return KnownTextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
    }

    private static string GetKnownResourceStem(string? value)
    {
        string fileName = Path.GetFileName(NormalizeTextureKey(value));
        string extension = Path.GetExtension(fileName);
        return KnownTextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               KnownModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
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
        IReadOnlyList<int> TextureSlotIndices,
        int? RenderStyleIndex,
        IReadOnlyList<string> MaterialHints,
        IReadOnlyList<string> InferredCandidates,
        string? ResolvedReference,
        string? ExportedRelativePath);

    private sealed record ObjInvalidMeshReport(
        string SourcePath,
        string MeshName,
        int VertexCount,
        int NonFinitePositionCount,
        int NonFiniteTextureCoordinateCount);

    private readonly record struct ExportTransform(double CenterX, double CenterY, double CenterZ, double Scale);
}
