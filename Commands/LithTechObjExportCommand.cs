using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace CFRezManager;

internal static class LithTechObjExportCommand
{
    private const int MaxObjModelBytes = 128 * 1024 * 1024;
    private const int MaxObjWorldDatBytes = 256 * 1024 * 1024;

    private readonly record struct ModelObjExportJob(ExplorerItem Item, string RelativePath);

    private sealed record Options(string SourceRoot, string ModelQuery, string OutputPath);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--export-obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "export-obj", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            LithTechObjExportResult result = Export(options, out string mappingReportPath, out int skippedCount);

            string summaryPath = Path.Combine(
                Path.GetDirectoryName(result.ObjPath) ?? Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(result.ObjPath)}_cli_export_result.txt");
            WriteSummary(summaryPath, options, result, mappingReportPath, skippedCount);

            Console.WriteLine($"OBJ: {result.ObjPath}");
            Console.WriteLine($"MTL: {result.MtlPath}");
            Console.WriteLine($"Texture folder: {result.TextureDirectoryPath}");
            if (!string.IsNullOrWhiteSpace(result.TextureReportPath))
            {
                Console.WriteLine($"Texture report: {result.TextureReportPath}");
            }

            if (!string.IsNullOrWhiteSpace(mappingReportPath))
            {
                Console.WriteLine($"Mapping report: {mappingReportPath}");
            }

            Console.WriteLine($"Summary: {summaryPath}");
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
        string? modelQuery = null;
        string? outputPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--export-obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "export-obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--source-root", out string? sourceRootValue) ||
                TryReadOptionValue(args, ref index, "--root", out sourceRootValue))
            {
                sourceRoot = sourceRootValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--model", out string? modelValue) ||
                TryReadOptionValue(args, ref index, "--query", out modelValue))
            {
                modelQuery = modelValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--output", out string? outputValue) ||
                TryReadOptionValue(args, ref index, "-o", out outputValue))
            {
                outputPath = outputValue;
                continue;
            }

            if (modelQuery is null)
            {
                modelQuery = arg;
            }
            else if (outputPath is null)
            {
                outputPath = arg;
            }
        }

        UserSettings settings = UserSettings.Load();
        sourceRoot ??= Directory.Exists(settings.LastRezDirectory)
            ? settings.LastRezDirectory
            : settings.LastDirectory;

        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException("Missing --source-root, and no saved resource root directory is available.");
        }

        if (string.IsNullOrWhiteSpace(modelQuery))
        {
            throw new InvalidOperationException("Missing --model <model name or resource path>.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            string outputDirectory = Directory.Exists(settings.LastOutputDirectory)
                ? settings.LastOutputDirectory
                : Environment.CurrentDirectory;
            outputPath = Path.Combine(outputDirectory, $"{SanitizePathSegment(Path.GetFileNameWithoutExtension(modelQuery))}.obj");
        }

        return new Options(Path.GetFullPath(sourceRoot), modelQuery, Path.GetFullPath(outputPath));
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string? value)
    {
        value = null;
        string arg = args[index];
        if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        value = args[index];
        return true;
    }

    private static LithTechObjExportResult Export(
        Options options,
        out string mappingReportPath,
        out int skippedCount)
    {
        ExplorerItem root = BuildDirectoryTree(options.SourceRoot);
        LoadAllArchives(root);

        List<ExplorerItem> matches = FindModelItems(root, options.ModelQuery);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No model matched '{options.ModelQuery}'.");
        }

        if (matches.Count > 50)
        {
            throw new InvalidOperationException($"Model query matched {matches.Count.ToString(CultureInfo.InvariantCulture)} files; use a more specific path.");
        }

        Func<string, ImageSource?>? globalTextureResolver = LithTechModelTextureLoader.CreateGlobalResolver(root);
        Func<IEnumerable<string>, IReadOnlyList<string>>? textureConfigResolver = LithTechModelTextureConfigIndex.CreateResolver(root);
        Func<IEnumerable<string>, IReadOnlyList<string>>? datTextureReferenceResolver = LithTechDatTextureReferenceIndex.CreateResolver(root);
        Func<IEnumerable<string>, IReadOnlyList<string>>? textureReferenceResolver =
            TextureReferenceResolver.Combine(textureConfigResolver, datTextureReferenceResolver);
        var sources = new List<LithTechObjExportSource>();
        skippedCount = 0;

        foreach (ModelObjExportJob job in BuildModelObjExportJobs(matches))
        {
            if (TryLoadModelDocument(job.Item, out LithTechModelDocument? document, out _) &&
                document is not null)
            {
                sources.Add(new LithTechObjExportSource(
                    CreateObjSourceName(job),
                    GetObjSourceResourcePath(job.Item),
                    document,
                    CreateObjTextureResolver(job.Item, globalTextureResolver),
                    textureReferenceResolver));
            }
            else
            {
                skippedCount++;
            }
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("Matched files were found, but no model could be decoded for OBJ export.");
        }

        LithTechObjExportResult result = LithTechObjExporter.Export(options.OutputPath, sources);
        mappingReportPath = result.MissingTextureCount == 0
            ? string.Empty
            : LithTechTextureMappingScanner.WriteReport(result.ObjPath, root, sources);
        return result;
    }

    private static void WriteSummary(
        string summaryPath,
        Options options,
        LithTechObjExportResult result,
        string mappingReportPath,
        int skippedCount)
    {
        using var writer = new StreamWriter(summaryPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("CF Rez Manager command-line OBJ export");
        writer.WriteLine();
        writer.WriteLine($"Source root: {options.SourceRoot}");
        writer.WriteLine($"Model query: {options.ModelQuery}");
        writer.WriteLine($"OBJ: {result.ObjPath}");
        writer.WriteLine($"MTL: {result.MtlPath}");
        writer.WriteLine($"Texture folder: {result.TextureDirectoryPath}");
        if (!string.IsNullOrWhiteSpace(result.TextureReportPath))
        {
            writer.WriteLine($"Texture report: {result.TextureReportPath}");
        }

        if (!string.IsNullOrWhiteSpace(mappingReportPath))
        {
            writer.WriteLine($"Mapping report: {mappingReportPath}");
        }

        writer.WriteLine();
        writer.WriteLine($"Sources: {result.SourceCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Meshes: {result.MeshCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Vertices: {result.VertexCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Triangles: {result.TriangleCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Textures exported: {result.TextureCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Missing textures: {result.MissingTextureCount.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"Skipped matched files: {skippedCount.ToString(CultureInfo.InvariantCulture)}");
    }

    private static List<ExplorerItem> FindModelItems(ExplorerItem root, string query)
    {
        string normalizedQuery = NormalizePath(query);
        bool queryLooksLikePath = normalizedQuery.Contains('/', StringComparison.Ordinal);
        string queryName = Path.GetFileName(normalizedQuery);
        string queryStem = Path.GetFileNameWithoutExtension(queryName);
        List<ExplorerItem> modelItems = EnumerateFiles(root)
            .Where(item => LithTechModelDecoder.IsCandidate(item.FileExtension) || LithTechWorldDatDecoder.IsCandidate(item.FileExtension))
            .ToList();

        List<ExplorerItem> exact = modelItems
            .Where(item => MatchesModelQuery(item, normalizedQuery, queryName, queryStem, queryLooksLikePath, exact: true))
            .ToList();
        if (exact.Count > 0)
        {
            return exact
                .OrderBy(item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return modelItems
            .Where(item => MatchesModelQuery(item, normalizedQuery, queryName, queryStem, queryLooksLikePath, exact: false))
            .OrderBy(item => item.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesModelQuery(
        ExplorerItem item,
        string normalizedQuery,
        string queryName,
        string queryStem,
        bool queryLooksLikePath,
        bool exact)
    {
        string path = NormalizePath(string.IsNullOrWhiteSpace(item.OutputRelativePath) ? item.Name : item.OutputRelativePath);
        string name = NormalizePath(item.Name);
        string stem = Path.GetFileNameWithoutExtension(name);
        string pathStem = NormalizePath(Path.ChangeExtension(path, null) ?? path);

        if (exact)
        {
            bool exactPathMatch =
                string.Equals(path, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/" + normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pathStem, normalizedQuery, StringComparison.OrdinalIgnoreCase);
            if (queryLooksLikePath)
            {
                return exactPathMatch;
            }

            return exactPathMatch ||
                   string.Equals(name, queryName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stem, queryStem, StringComparison.OrdinalIgnoreCase);
        }

        if (queryLooksLikePath)
        {
            return path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        }

        return path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
               name.Contains(queryName, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(queryStem) && stem.Contains(queryStem, StringComparison.OrdinalIgnoreCase));
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

    private static ExplorerItem BuildDirectoryTree(string folder)
    {
        string rootName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = folder;
        }

        var root = new ExplorerItem
        {
            Name = rootName,
            Kind = ExplorerItemKind.Directory,
            SourcePath = folder,
            OutputRelativePath = string.Empty
        };

        PopulateDirectory(root, folder, folder);
        root.SortChildren();
        return root;
    }

    private static void PopulateDirectory(ExplorerItem parent, string folder, string rootFolder)
    {
        foreach (string directory in SafeEnumerateDirectories(folder))
        {
            string relativePath = Path.GetRelativePath(rootFolder, directory);
            var directoryItem = new ExplorerItem
            {
                Name = Path.GetFileName(directory),
                Kind = ExplorerItemKind.Directory,
                SourcePath = directory,
                OutputRelativePath = SanitizeRelativePath(relativePath)
            };

            PopulateDirectory(directoryItem, directory, rootFolder);
            if (directoryItem.Children.Count > 0)
            {
                parent.AddChild(directoryItem);
            }
        }

        foreach (string rezPath in SafeEnumerateRezFiles(folder))
        {
            parent.AddChild(CreateArchivePlaceholderItem(rezPath, rootFolder));
        }

        foreach (string filePath in SafeEnumerateResourceFiles(folder))
        {
            parent.AddChild(CreateLocalFileItem(filePath, rootFolder));
        }
    }

    private static ExplorerItem CreateArchivePlaceholderItem(string rezPath, string rootFolder)
    {
        string relativePath = Path.ChangeExtension(Path.GetRelativePath(rootFolder, rezPath), null) ?? Path.GetFileNameWithoutExtension(rezPath);
        return new ExplorerItem
        {
            Name = Path.GetFileNameWithoutExtension(rezPath),
            Kind = ExplorerItemKind.RezArchive,
            SourcePath = rezPath,
            OutputRelativePath = SanitizeRelativePath(relativePath),
            IsLoaded = false
        };
    }

    private static ExplorerItem CreateLocalFileItem(string filePath, string rootFolder)
    {
        string relativePath = Path.GetRelativePath(rootFolder, filePath);
        return new ExplorerItem
        {
            Name = Path.GetFileName(filePath),
            Kind = ExplorerItemKind.LocalFile,
            SourcePath = filePath,
            OutputRelativePath = SanitizeRelativePath(relativePath)
        };
    }

    private static ExplorerItem CreateArchiveChildItem(RezNode node, RezArchive archive, string parentOutputPath)
    {
        string outputRelativePath = CombineRelativePath(parentOutputPath, SanitizePathSegment(node.Name));

        if (node is RezDirectoryNode directory)
        {
            return new ExplorerItem
            {
                Name = directory.Name,
                Kind = ExplorerItemKind.RezDirectory,
                SourcePath = archive.FilePath,
                OutputRelativePath = outputRelativePath,
                Archive = archive,
                ArchiveDirectory = directory,
                IsLoaded = false
            };
        }

        var file = (RezFileNode)node;
        return new ExplorerItem
        {
            Name = file.Name,
            Kind = ExplorerItemKind.RezFile,
            SourcePath = archive.FilePath,
            OutputRelativePath = outputRelativePath,
            Archive = archive,
            ArchiveFile = file
        };
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateRezFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file => string.Equals(Path.GetExtension(file), ".rez", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateResourceFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file => !string.Equals(Path.GetExtension(file), ".rez", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void LoadAllArchives(ExplorerItem rootItem)
    {
        var archives = new List<ExplorerItem>();
        CollectArchiveItems(rootItem, archives);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        };

        Parallel.ForEach(archives, options, archive =>
        {
            LoadContainerChildren(archive);
            LoadAllRezDirectories(archive);
        });
    }

    private static void CollectArchiveItems(ExplorerItem item, List<ExplorerItem> archives)
    {
        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            archives.Add(item);
            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectArchiveItems(child, archives);
        }
    }

    private static void LoadAllRezDirectories(ExplorerItem item)
    {
        foreach (ExplorerItem child in item.Children.ToArray())
        {
            if (child.Kind == ExplorerItemKind.RezDirectory)
            {
                LoadContainerChildren(child);
                LoadAllRezDirectories(child);
            }
        }
    }

    private static void LoadContainerChildren(ExplorerItem item)
    {
        if (item.IsLoaded)
        {
            return;
        }

        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            var reader = new RezArchiveReader();
            RezArchive archive = reader.Read(item.SourcePath);
            item.Archive = archive;
            item.ArchiveDirectory = archive.Root;
        }

        if (item.Archive is null || item.ArchiveDirectory is null)
        {
            item.IsLoaded = true;
            return;
        }

        item.Children.Clear();
        foreach (RezNode child in item.ArchiveDirectory.Children)
        {
            item.AddChild(CreateArchiveChildItem(child, item.Archive, item.OutputRelativePath));
        }

        item.SortChildren();
        item.IsLoaded = true;
    }

    private static List<ModelObjExportJob> BuildModelObjExportJobs(IEnumerable<ExplorerItem> items)
    {
        var jobs = new List<ModelObjExportJob>();
        var seenItems = new HashSet<ExplorerItem>();
        var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in LithTechModelPartGrouper.ExpandNumberedSiblingParts(items))
        {
            string selectedRootPath = item.IsFile
                ? SanitizePathSegment(Path.GetFileNameWithoutExtension(item.Name))
                : SanitizePathSegment(item.Name);
            CollectModelObjExportJobs(item, selectedRootPath, jobs, seenItems, usedRelativePaths);
        }

        return jobs;
    }

    private static void CollectModelObjExportJobs(
        ExplorerItem item,
        string relativePath,
        List<ModelObjExportJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths)
    {
        if (item.Kind == ExplorerItemKind.LocalFile ||
            item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
        {
            if (LithTechModelDecoder.IsCandidate(item.FileExtension) || LithTechWorldDatDecoder.IsCandidate(item.FileExtension))
            {
                AddModelObjExportJob(item, relativePath, jobs, seenItems, usedRelativePaths);
            }

            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            string childRelativePath = CombineRelativePath(relativePath, SanitizePathSegment(child.Name));
            CollectModelObjExportJobs(child, childRelativePath, jobs, seenItems, usedRelativePaths);
        }
    }

    private static void AddModelObjExportJob(
        ExplorerItem item,
        string relativePath,
        List<ModelObjExportJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths)
    {
        if (!seenItems.Add(item))
        {
            return;
        }

        string safeRelativePath = string.IsNullOrWhiteSpace(relativePath)
            ? SanitizePathSegment(Path.GetFileNameWithoutExtension(item.Name))
            : relativePath;
        string withoutExtension = Path.ChangeExtension(safeRelativePath, null) ?? safeRelativePath;
        jobs.Add(new ModelObjExportJob(item, MakeUniqueRelativePath(withoutExtension, usedRelativePaths)));
    }

    private static string MakeUniqueRelativePath(string relativePath, HashSet<string> usedRelativePaths)
    {
        if (usedRelativePaths.Add(relativePath))
        {
            return relativePath;
        }

        string? directory = Path.GetDirectoryName(relativePath);
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);
        for (int index = 2; ; index++)
        {
            string candidateName = $"{fileName} ({index}){extension}";
            string candidate = string.IsNullOrEmpty(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
            if (usedRelativePaths.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string CreateObjSourceName(ModelObjExportJob job)
    {
        string name = Path.ChangeExtension(job.RelativePath, null) ?? job.RelativePath;
        return name
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_')
            .Replace('/', '_')
            .Replace('\\', '_');
    }

    private static string GetObjSourceResourcePath(ExplorerItem item)
    {
        return string.IsNullOrWhiteSpace(item.OutputRelativePath)
            ? item.Name
            : item.OutputRelativePath;
    }

    private static Func<string, ImageSource?>? CreateObjTextureResolver(
        ExplorerItem item,
        Func<string, ImageSource?>? globalTextureResolver)
    {
        Func<string, ImageSource?>? primaryResolver = LithTechModelTextureLoader.CreateResolver(item);
        if (primaryResolver is null)
        {
            return globalTextureResolver;
        }

        if (globalTextureResolver is null)
        {
            return primaryResolver;
        }

        return item.Kind == ExplorerItemKind.LocalFile
            ? texturePath => globalTextureResolver(texturePath) ?? primaryResolver(texturePath)
            : texturePath => primaryResolver(texturePath) ?? globalTextureResolver(texturePath);
    }

    private static bool TryLoadModelDocument(ExplorerItem item, out LithTechModelDocument? document, out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        try
        {
            string extension = item.FileExtension;
            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxObjWorldDatBytes
                : MaxObjModelBytes;
            byte[] data = ReadExplorerFileBytes(item, maxBytes);
            if (LithTechWorldDatDecoder.IsCandidate(extension))
            {
                return LithTechWorldDatDecoder.TryDecode(data, item.Name, out document, out errorMessage);
            }

            return LithTechModelDecoder.TryDecode(data, item.Name, extension, out document, out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static byte[] ReadExplorerFileBytes(ExplorerItem item, int maxBytes)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(item.SourcePath);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes || info.Length > int.MaxValue)
            {
                throw new InvalidOperationException($"File is too large for export: {item.Name}");
            }

            return File.ReadAllBytes(item.SourcePath);
        }

        if (item.Archive is null ||
            item.ArchiveFile is null ||
            item.ArchiveFile.Size < 0 ||
            item.ArchiveFile.Size > maxBytes)
        {
            throw new InvalidOperationException($"File is too large for export: {item.Name}");
        }

        byte[] data = new byte[item.ArchiveFile.Size];
        using FileStream source = File.OpenRead(item.Archive.FilePath);
        source.Position = item.ArchiveFile.DataOffset;
        source.ReadExactly(data);
        return data;
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(parts.Select(SanitizePathSegment).ToArray());
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string CombineRelativePath(string parent, string child)
    {
        return string.IsNullOrEmpty(parent) ? child : Path.Combine(parent, child);
    }

    private static string NormalizePath(string value)
    {
        return value
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }
}
