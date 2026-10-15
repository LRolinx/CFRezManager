using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal static class LithTechTextureMapAuditCommand
{
    private sealed record Options(string SourceRoot, string OutputPath, string? AuditInputPath, bool ProbeMappedPaths);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--audit-texture-map", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--probe-texture-map-paths", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            ExplorerItem root = LithTechObjExportCommand.BuildDirectoryTree(options.SourceRoot);
            LithTechObjExportCommand.LoadAllArchives(root);

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
                probeMappedPaths ? "crossfire_texture_path_decode_probe.tsv" : "crossfire_texture_mapping_audit.tsv");
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

        return new Options(Path.GetFullPath(sourceRoot), fullOutputPath, fullAuditInputPath, probeMappedPaths);
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
        var pending = new Stack<ExplorerItem>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ExplorerItem item = pending.Pop();
            if (item.IsFile && textureExtensions.Contains("." + item.FileExtension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            {
                AddPhysicalItem(item.OutputRelativePath, item);
                AddPhysicalItem(item.ArchiveFile?.FullPath ?? item.OutputRelativePath, item);
            }

            foreach (ExplorerItem child in item.Children)
            {
                pending.Push(child);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        writer.WriteLine("texture_path\treference_rows\tphysical_copies\tdecode_attempts\tdecodable_copies\tstatus\tcopy_sources");
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
                writer.WriteLine($"{texturePath}\t{referenceRows[texturePath].ToString(CultureInfo.InvariantCulture)}\t0\t0\t0\ttexture-path-not-found\t");
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
                $"{texturePath}\t{referenceRows[texturePath].ToString(CultureInfo.InvariantCulture)}\t{items.Count.ToString(CultureInfo.InvariantCulture)}\t{items.Count.ToString(CultureInfo.InvariantCulture)}\t{decodableCopies.ToString(CultureInfo.InvariantCulture)}\t{status}\t{string.Join(" | ", copyDetails)}");

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
