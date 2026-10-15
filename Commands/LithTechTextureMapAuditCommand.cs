using System.Globalization;
using System.IO;

namespace CFRezManager;

internal static class LithTechTextureMapAuditCommand
{
    private sealed record Options(string SourceRoot, string OutputPath);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--audit-texture-map", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            ExplorerItem root = LithTechObjExportCommand.BuildDirectoryTree(options.SourceRoot);
            LithTechObjExportCommand.LoadAllArchives(root);
            var result = LithTechModelTextureConfigIndex.WriteSlotMappingAuditReport(root, options.OutputPath);

            Console.WriteLine($"Texture mapping audit: {Path.GetFullPath(options.OutputPath)}");
            Console.WriteLine($"Rows: {result.RowCount.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Resolved slot mappings: {result.SlotMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Ambiguous mappings: {result.AmbiguousMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Inferred mappings: {result.InferredMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Mesh-resolver mappings: {result.MeshResolverMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Exporter candidate textures decoded: {result.ExporterCandidateMappings.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Semantic candidate textures decoded: {result.SemanticCandidateMappings.ToString(CultureInfo.InvariantCulture)}");
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
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--audit-texture-map", StringComparison.OrdinalIgnoreCase))
            {
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
            outputPath = Path.Combine(outputDirectory, "crossfire_texture_mapping_audit.tsv");
        }

        return new Options(Path.GetFullPath(sourceRoot), Path.GetFullPath(outputPath));
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
