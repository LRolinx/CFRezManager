using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal static class LithTechTextureMapAuditCommand
{
    private sealed record Options(
        string SourceRoot,
        string OutputPath,
        string? AuditInputPath,
        bool ProbeMappedPaths,
        bool InspectLtcMotionLinks,
        bool InspectCftMotionLinks,
        bool InspectCftModelTextureLinks,
        bool InspectLtcModelTextureLinks);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--audit-texture-map", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--probe-texture-map-paths", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--inspect-ltc-motion-links", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--inspect-cft-motion-links", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--inspect-cft-model-texture-links", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--inspect-ltc-model-texture-links", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            ExplorerItem root = LithTechObjExportCommand.BuildDirectoryTree(options.SourceRoot);
            LithTechObjExportCommand.LoadAllArchives(root);

            if (options.InspectLtcMotionLinks)
            {
                var motionAudit = LithTechModelTextureConfigIndex.WriteLtcMotionReferenceAuditReport(root, options.OutputPath);
                Console.WriteLine($"LTC motion reference report: {Path.GetFullPath(options.OutputPath)}");
                Console.WriteLine($"LTC physical resource copies: {motionAudit.ResourceCopies.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Unique LTC contents: {motionAudit.UniqueDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"LTC contents decoded: {motionAudit.DecodedDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Motion-model references: {motionAudit.MotionReferences.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            if (options.InspectCftMotionLinks)
            {
                var motionAudit = LithTechModelTextureConfigIndex.WriteCftMotionReferenceAuditReport(root, options.OutputPath);
                Console.WriteLine($"CFT motion reference report: {Path.GetFullPath(options.OutputPath)}");
                Console.WriteLine($"CFT physical resource copies: {motionAudit.ResourceCopies.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Unique CFT contents: {motionAudit.UniqueDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"CFT contents decoded: {motionAudit.DecodedDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Typed motion-model links: {motionAudit.MotionLinks.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            if (options.InspectCftModelTextureLinks)
            {
                var cftAudit = LithTechModelTextureConfigIndex.WriteCftModelTextureReferenceAuditReport(root, options.OutputPath);
                Console.WriteLine($"CFT model/texture reference report: {Path.GetFullPath(options.OutputPath)}");
                Console.WriteLine($"CFT physical resource copies: {cftAudit.ResourceCopies.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Unique CFT contents: {cftAudit.UniqueDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"CFT contents decoded: {cftAudit.DecodedDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Typed model rows: {cftAudit.ModelRows.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Related model references: {cftAudit.RelatedModelReferences.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            if (options.InspectLtcModelTextureLinks)
            {
                var ltcAudit = LithTechModelTextureConfigIndex.WriteLtcModelTextureReferenceAuditReport(root, options.OutputPath);
                Console.WriteLine($"LTC model/texture reference report: {Path.GetFullPath(options.OutputPath)}");
                Console.WriteLine($"LTC physical resource copies: {ltcAudit.ResourceCopies.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Unique LTC contents: {ltcAudit.UniqueDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"LTC contents decoded: {ltcAudit.DecodedDocuments.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Typed model rows: {ltcAudit.ModelRows.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            if (options.ProbeMappedPaths)
            {
                var probe = WriteMappedTextureDecodeReport(root, options.AuditInputPath!, options.OutputPath);
                Console.WriteLine($"Texture path decode report: {Path.GetFullPath(options.OutputPath)}");
                Console.WriteLine($"Unique mapped texture paths: {probe.Paths.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Physical texture copies tested: {probe.Copies.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Paths with a decodable copy: {probe.Decoded.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Paths with mixed copy decode results: {probe.Partial.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Paths whose copies all failed decoding: {probe.Failed.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine($"Paths not found as exact resources: {probe.NotFound.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            }

            var result = LithTechModelTextureConfigIndex.WriteSlotMappingAuditReport(root, options.OutputPath);

            Console.WriteLine($"Texture mapping audit: {Path.GetFullPath(options.OutputPath)}");
            Console.WriteLine($"Rows: {result.RowCount.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Resolved slot mappings: {result.SlotMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Ambiguous mappings: {result.AmbiguousMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Inferred mappings: {result.InferredMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Mesh-resolver mappings: {result.MeshResolverMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Exporter candidate textures decoded: {result.ExporterCandidateMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Semantic candidate textures decoded: {result.SemanticCandidateMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Existing candidates with unresolved texture results: {result.UnresolvedCandidateResourceRows.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Unresolved candidates outside model-texture path scope: {result.OutOfScopeUnresolvedCandidateRows.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Exact texture resource paths probed: {result.ExactTextureResourcePathsProbed.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Exact physical texture copies tested: {result.ExactTextureDecodeAttempts.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Texture paths with a decodable copy: {result.ExactTextureResourcePathsDecoded.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Texture paths whose copies all failed image decoding: {result.ExactTextureResourcePathsFailed.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Texture paths not found as exact resources: {result.ExactTextureResourcePathsNotFound.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Model groups with decode failures: {result.ModelGroupsWithDecodeFailures.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Non-geometry LTB resource groups: {result.NonGeometryLtbResourceGroups.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Models with mapping evidence: {result.ModelsWithEvidence.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Models without mapping evidence: {result.ModelsWithoutEvidence.ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? sourceRoot = null;
        string? outputPath = null;
        string? auditInputPath = null;
        bool probeMappedPaths = false;
        bool inspectLtcMotionLinks = false;
        bool inspectCftMotionLinks = false;
        bool inspectCftModelTextureLinks = false;
        bool inspectLtcModelTextureLinks = false;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--audit-texture-map", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(arg, "--probe-texture-map-paths", StringComparison.OrdinalIgnoreCase))
            {
                probeMappedPaths = true;
                continue;
            }

            if (string.Equals(arg, "--inspect-ltc-motion-links", StringComparison.OrdinalIgnoreCase))
            {
                inspectLtcMotionLinks = true;
                continue;
            }

            if (string.Equals(arg, "--inspect-cft-motion-links", StringComparison.OrdinalIgnoreCase))
            {
                inspectCftMotionLinks = true;
                continue;
            }

            if (string.Equals(arg, "--inspect-cft-model-texture-links", StringComparison.OrdinalIgnoreCase))
            {
                inspectCftModelTextureLinks = true;
                continue;
            }

            if (string.Equals(arg, "--inspect-ltc-model-texture-links", StringComparison.OrdinalIgnoreCase))
            {
                inspectLtcModelTextureLinks = true;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--audit", out string? auditInputValue))
            {
                auditInputPath = auditInputValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--source-root", out string? sourceRootValue) ||
                TryReadOptionValue(args, ref index, "--root", out sourceRootValue))
            {
                sourceRoot = sourceRootValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--output", out string? outputValue) ||
                TryReadOptionValue(args, ref index, "-o", out outputValue))
            {
                outputPath = outputValue;
                continue;
            }

            throw new InvalidOperationException($"Unknown option: {arg}");
        }

        UserSettings settings = UserSettings.Load();
        sourceRoot ??= Directory.Exists(settings.LastRezDirectory)
            ? settings.LastRezDirectory
            : settings.LastDirectory;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException("Missing --source-root, and no saved resource root directory is available.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            string outputDirectory = Directory.Exists(settings.LastOutputDirectory)
                ? settings.LastOutputDirectory
                : Environment.CurrentDirectory;
            outputPath = Path.Combine(
                outputDirectory,
                inspectLtcMotionLinks
                    ? "crossfire_ltc_motion_reference_audit.tsv"
                    : inspectCftMotionLinks
                        ? "crossfire_cft_motion_reference_audit.tsv"
                        : inspectCftModelTextureLinks
                            ? "crossfire_cft_model_texture_reference_audit.tsv"
                            : inspectLtcModelTextureLinks
                                ? "crossfire_ltc_model_texture_reference_audit.tsv"
                                : probeMappedPaths
                                    ? "crossfire_texture_path_decode_probe.tsv"
                                    : "crossfire_texture_mapping_audit.tsv");
        }

        if (probeMappedPaths && string.IsNullOrWhiteSpace(auditInputPath))
        {
            throw new InvalidOperationException("Missing --audit path for --probe-texture-map-paths.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? fullAuditInputPath = string.IsNullOrWhiteSpace(auditInputPath) ? null : Path.GetFullPath(auditInputPath);
        if (fullAuditInputPath is not null &&
            string.Equals(fullAuditInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The decode report must not overwrite its input audit.");
        }

        return new Options(
            Path.GetFullPath(sourceRoot),
            fullOutputPath,
            fullAuditInputPath,
            probeMappedPaths,
            inspectLtcMotionLinks,
            inspectCftMotionLinks,
            inspectCftModelTextureLinks,
            inspectLtcModelTextureLinks);
    }

    private static (int Paths, int Copies, int Decoded, int Partial, int Failed, int NotFound) WriteMappedTextureDecodeReport(
        ExplorerItem root,
        string auditInputPath,
        string outputPath)
    {
        string[] textureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"];
        var referenceRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(auditInputPath))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < 8 ||
                !(string.Equals(fields[2], "model", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(fields[2], "context", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(fields[2], "mesh-slot", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string texturePath = LithTechResourceHeuristics.NormalizeResourcePath(fields[5]);
            if (texturePath.Length == 0 ||
                !textureExtensions.Contains(Path.GetExtension(texturePath), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            referenceRows.TryGetValue(texturePath, out int count);
            referenceRows[texturePath] = count + 1;
        }

        var physicalItems = new Dictionary<string, List<ExplorerItem>>(StringComparer.OrdinalIgnoreCase);
        var availableTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ExplorerItem>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ExplorerItem item = pending.Pop();
            if (item.IsFile && textureExtensions.Contains("." + item.FileExtension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            {
                availableTexturePaths.Add(LithTechResourceHeuristics.NormalizeResourcePath(
                    item.ArchiveFile?.FullPath ?? item.OutputRelativePath));
                AddPhysicalItem(item.OutputRelativePath, item);
                AddPhysicalItem(item.ArchiveFile?.FullPath ?? item.OutputRelativePath, item);
            }

            foreach (ExplorerItem child in item.Children)
            {
                pending.Push(child);
            }
        }

        var texturePathsByInitial = new Dictionary<char, HashSet<string>>();
        var texturePathsBySecond = new Dictionary<char, HashSet<string>>();
        foreach (string texturePath in availableTexturePaths)
        {
            string stem = NormalizeTextureStem(Path.GetFileNameWithoutExtension(texturePath));
            if (stem.Length == 0)
            {
                continue;
            }

            AddToIndex(texturePathsByInitial, stem[0], texturePath);
            if (stem.Length > 1)
            {
                AddToIndex(texturePathsBySecond, stem[1], texturePath);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("texture_path\treference_rows\tphysical_copies\tdecode_attempts\tdecodable_copies\tstatus\tcopy_sources\tnear_name_candidates");
        int copiesTested = 0;
        int decodedPaths = 0;
        int partiallyDecodedPaths = 0;
        int failedPaths = 0;
        int notFoundPaths = 0;
        int pathIndex = 0;
        foreach (string texturePath in referenceRows.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            pathIndex++;
            if (!physicalItems.TryGetValue(texturePath, out List<ExplorerItem>? items) || items.Count == 0)
            {
                notFoundPaths++;
                string nearNameCandidates = FindNearNameCandidates(texturePath);
                writer.WriteLine($"{texturePath}\t{referenceRows[texturePath].ToString(CultureInfo.InvariantCulture)}\t0\t0\t0\ttexture-path-not-found\t\t{nearNameCandidates}");
                continue;
            }

            int decodableCopies = 0;
            var copyDetails = new List<string>(items.Count);
            for (int copyIndex = 0; copyIndex < items.Count; copyIndex++)
            {
                ExplorerItem item = items[copyIndex];
                bool decodable = LithTechModelTextureLoader.TryDecodeTextureItem(item);
                copiesTested++;
                if (decodable)
                {
                    decodableCopies++;
                }

                string copyPath = LithTechResourceHeuristics.NormalizeResourcePath(
                    item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
                string archivePath = item.Archive?.FilePath ?? string.Empty;
                string md5 = item.ArchiveFile?.Md5 ?? "local";
                copyDetails.Add($"copy{copyIndex + 1}={archivePath}:{copyPath}#{md5}:{(decodable ? "decoded" : "failed")}");
            }

            string status = decodableCopies == 0
                ? "texture-path-decode-failed-all-copies"
                : decodableCopies == items.Count
                    ? "texture-path-decodes-all-copies"
                    : "texture-path-decodes-some-copies";
            if (decodableCopies == 0)
            {
                failedPaths++;
            }
            else
            {
                decodedPaths++;
                if (decodableCopies < items.Count)
                {
                    partiallyDecodedPaths++;
                }
            }

            writer.WriteLine(
                $"{texturePath}\t{referenceRows[texturePath].ToString(CultureInfo.InvariantCulture)}\t{items.Count.ToString(CultureInfo.InvariantCulture)}\t{items.Count.ToString(CultureInfo.InvariantCulture)}\t{decodableCopies.ToString(CultureInfo.InvariantCulture)}\t{status}\t{string.Join(" | ", copyDetails)}\t");

            if (pathIndex % 500 == 0)
            {
                Console.WriteLine($"Decoded exact texture paths: {pathIndex.ToString(CultureInfo.InvariantCulture)}/{referenceRows.Count.ToString(CultureInfo.InvariantCulture)}");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
        }

        return (referenceRows.Count, copiesTested, decodedPaths, partiallyDecodedPaths, failedPaths, notFoundPaths);

        void AddPhysicalItem(string path, ExplorerItem item)
        {
            string normalizedPath = LithTechResourceHeuristics.NormalizeResourcePath(path);
            if (normalizedPath.Length == 0 || !referenceRows.ContainsKey(normalizedPath))
            {
                return;
            }

            if (!physicalItems.TryGetValue(normalizedPath, out List<ExplorerItem>? items))
            {
                items = [];
                physicalItems.Add(normalizedPath, items);
            }

            if (!items.Contains(item))
            {
                items.Add(item);
            }
        }

        string FindNearNameCandidates(string missingPath)
        {
            string missingStem = NormalizeTextureStem(Path.GetFileNameWithoutExtension(missingPath));
            if (missingStem.Length == 0)
            {
                return string.Empty;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (texturePathsByInitial.TryGetValue(missingStem[0], out HashSet<string>? initialMatches))
            {
                candidates.UnionWith(initialMatches);
            }

            if (missingStem.Length > 1 && texturePathsBySecond.TryGetValue(missingStem[1], out HashSet<string>? secondMatches))
            {
                candidates.UnionWith(secondMatches);
            }

            int maxDistance = Math.Max(2, Math.Min(6, (missingStem.Length + 4) / 5));
            string extension = Path.GetExtension(missingPath);
            return string.Join(" || ", candidates
                .Where(candidate => string.Equals(Path.GetExtension(candidate), extension, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => (Path: candidate, Stem: NormalizeTextureStem(Path.GetFileNameWithoutExtension(candidate))))
                .Select(candidate => (candidate.Path, candidate.Stem, Distance: GetEditDistance(missingStem, candidate.Stem)))
                .Where(candidate => candidate.Distance <= maxDistance &&
                    (candidate.Distance == 0 || GetLongestCommonSubstringLength(missingStem, candidate.Stem) >= 5))
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(candidate => $"d{candidate.Distance}:{candidate.Path}"));
        }

        static void AddToIndex(Dictionary<char, HashSet<string>> index, char key, string path)
        {
            if (!index.TryGetValue(key, out HashSet<string>? paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                index.Add(key, paths);
            }

            paths.Add(path);
        }
    }

    private static string NormalizeTextureStem(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static int GetEditDistance(string left, string right)
    {
        if (left.Length > right.Length)
        {
            (left, right) = (right, left);
        }

        var previous = new int[left.Length + 1];
        var current = new int[left.Length + 1];
        for (int column = 0; column <= left.Length; column++)
        {
            previous[column] = column;
        }

        for (int row = 1; row <= right.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= left.Length; column++)
            {
                int substitutionCost = left[column - 1] == right[row - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[left.Length];
    }

    private static int GetLongestCommonSubstringLength(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        int longest = 0;
        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = 0;
            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                current[rightIndex] = left[leftIndex - 1] == right[rightIndex - 1]
                    ? previous[rightIndex - 1] + 1
                    : 0;
                longest = Math.Max(longest, current[rightIndex]);
            }

            (previous, current) = (current, previous);
        }

        return longest;
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string? value)
    {
        value = null;
        if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        value = args[++index];
        return true;
    }
}
