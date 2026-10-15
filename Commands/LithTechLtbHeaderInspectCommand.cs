using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace CFRezManager;

internal static class LithTechLtbHeaderInspectCommand
{
    private const int MaxInspectBytes = 128 * 1024 * 1024;

    private sealed record Options(string SourceRoot, string ModelReference, bool AllRenderStyles);

    public static bool IsInvocation(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--inspect-ltb-header", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = ParseOptions(args);
            ExplorerItem root = LithTechObjExportCommand.BuildDirectoryTree(options.SourceRoot);
            LithTechObjExportCommand.LoadAllArchives(root);

            string requestedPath = options.AllRenderStyles ? string.Empty : NormalizeModelPath(options.ModelReference);
            ExplorerItem[] matches = EnumerateFiles(root)
                .Where(item => string.Equals(item.FileExtension, "ltb", StringComparison.OrdinalIgnoreCase))
                .Where(item => options.AllRenderStyles
                    ? LithTechRenderStyleDecoder.IsCandidate(item.OutputRelativePath, item.FileExtension)
                    : MatchesModelReference(item, requestedPath))
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException(options.AllRenderStyles
                    ? "No RenderStyle LTB resources matched the known RenderStyle paths/names."
                    : $"No LTB resource matched: {options.ModelReference}");
            }

            Console.WriteLine(options.AllRenderStyles
                ? $"Matched RenderStyle LTB copies: {matches.Length.ToString(CultureInfo.InvariantCulture)}"
                : $"Matched physical LTB copies: {matches.Length.ToString(CultureInfo.InvariantCulture)}");
            foreach (ExplorerItem item in matches)
            {
                if (options.AllRenderStyles)
                {
                    PrintRenderStyleSummary(item);
                }
                else
                {
                    PrintHeader(item);
                }
            }

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
        string? modelReference = null;
        bool allRenderStyles = false;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--inspect-ltb-header", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(arg, "--all-renderstyles", StringComparison.OrdinalIgnoreCase))
            {
                allRenderStyles = true;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--root", out string? rootValue) ||
                TryReadOptionValue(args, ref index, "--source-root", out rootValue))
            {
                sourceRoot = rootValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--model", out string? modelValue))
            {
                modelReference = modelValue;
                continue;
            }

            throw new InvalidOperationException($"Unknown option: {arg}");
        }

        if (string.IsNullOrWhiteSpace(modelReference) && !allRenderStyles)
        {
            throw new InvalidOperationException("Missing --model LTB resource path or --all-renderstyles.");
        }

        if (!string.IsNullOrWhiteSpace(modelReference) && allRenderStyles)
        {
            throw new InvalidOperationException("Use either --model or --all-renderstyles, not both.");
        }

        UserSettings settings = UserSettings.Load();
        sourceRoot ??= Directory.Exists(settings.LastRezDirectory)
            ? settings.LastRezDirectory
            : settings.LastDirectory;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException("Missing --root, and no saved resource root directory is available.");
        }

        return new Options(Path.GetFullPath(sourceRoot), modelReference ?? string.Empty, allRenderStyles);
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

    private static void PrintHeader(ExplorerItem item)
    {
        byte[]? storedBytes = TryReadItemBytes(item);
        string physicalPath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
        Console.WriteLine();
        Console.WriteLine($"Physical path: {physicalPath}");
        Console.WriteLine($"Output path: {item.OutputRelativePath}");
        Console.WriteLine($"Stored bytes: {storedBytes?.Length.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}");
        if (storedBytes is null)
        {
            return;
        }

        byte[]? decodedBytes = LzmaAloneDecoder.TryPrepareData(storedBytes);
        byte[] headerBytes = decodedBytes ?? storedBytes;
        Console.WriteLine($"Stored prefix: {FormatPrefix(storedBytes)}");
        if (!ReferenceEquals(decodedBytes, storedBytes) && decodedBytes is not null)
        {
            Console.WriteLine($"Decoded bytes: {decodedBytes.Length.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"Decoded prefix: {FormatPrefix(decodedBytes)}");
        }

        byte? fileType = null;
        if (headerBytes.Length >= 4)
        {
            fileType = headerBytes[0];
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(2, sizeof(ushort)));
            Console.WriteLine($"LTB file type: {fileType.Value.ToString(CultureInfo.InvariantCulture)} ({DescribeFileType(fileType.Value)})");
            Console.WriteLine($"LTB header version: {version.ToString(CultureInfo.InvariantCulture)}");
        }

        if (fileType == 5)
        {
            string storageDescription = ReferenceEquals(decodedBytes, storedBytes)
                ? "Packed LithTech RenderStyle"
                : "LZMA-compressed packed LithTech RenderStyle";
            bool renderStyleDecoded = LithTechRenderStyleDecoder.TryDecode(
                headerBytes,
                physicalPath,
                storageDescription,
                storedBytes.Length,
                out LithTechRenderStyleDocument? renderStyle,
                out string? renderStyleError);
            if (renderStyleDecoded && renderStyle is not null)
            {
                int passCount = renderStyle.Styles.Sum(style => style.Passes.Count);
                Console.WriteLine($"RenderStyle decode: success; styles={renderStyle.Styles.Count.ToString(CultureInfo.InvariantCulture)}; passes={passCount.ToString(CultureInfo.InvariantCulture)}");
            }
            else if (LithTechRenderStyleDecoder.TryDecodeShaderAssemblyExtension(
                         headerBytes,
                         physicalPath,
                         storageDescription,
                         storedBytes.Length,
                         out LithTechRenderStyleShaderExtensionDocument? shaderExtension,
                         out string? extensionError) &&
                     shaderExtension is not null)
            {
                Console.WriteLine($"RenderStyle shader-extension decode: success; blocks={shaderExtension.ShaderAssemblyBlockCount.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                Console.WriteLine($"RenderStyle decode: failed; {extensionError ?? renderStyleError ?? "no decoder result"}");
            }

            return;
        }

        bool decoded = LithTechModelDecoder.TryDecode(
            storedBytes,
            item.Name,
            "ltb",
            out LithTechModelDocument? document,
            out string? error);
        Console.WriteLine(decoded && document is not null
            ? $"Model decode: success; meshes={document.Meshes.Count.ToString(CultureInfo.InvariantCulture)}"
            : $"Model decode: failed; {error ?? "no decoder result"}");
    }

    private static void PrintRenderStyleSummary(ExplorerItem item)
    {
        string physicalPath = item.ArchiveFile?.FullPath ?? item.OutputRelativePath;
        byte[]? storedBytes = TryReadItemBytes(item);
        if (storedBytes is null)
        {
            Console.WriteLine($"{physicalPath}: stored bytes unavailable; decode failed");
            return;
        }

        byte[]? decodedBytes = LzmaAloneDecoder.TryPrepareData(storedBytes);
        byte[] headerBytes = decodedBytes ?? storedBytes;
        if (headerBytes.Length < 4)
        {
            Console.WriteLine($"{physicalPath}: invalid LTB header; stored_bytes={storedBytes.Length.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        byte fileType = headerBytes[0];
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(2, sizeof(ushort)));
        if (fileType != 5)
        {
            Console.WriteLine($"{physicalPath}: file_type={fileType.ToString(CultureInfo.InvariantCulture)}; version={version.ToString(CultureInfo.InvariantCulture)}; not a D3D RenderStyle");
            return;
        }

        string storageDescription = ReferenceEquals(decodedBytes, storedBytes)
            ? "Packed LithTech RenderStyle"
            : "LZMA-compressed packed LithTech RenderStyle";
        if (LithTechRenderStyleDecoder.TryDecode(
                headerBytes,
                physicalPath,
                storageDescription,
                storedBytes.Length,
                out LithTechRenderStyleDocument? renderStyle,
                out string? error) &&
            renderStyle is not null)
        {
            int passCount = renderStyle.Styles.Sum(style => style.Passes.Count);
            Console.WriteLine($"{physicalPath}: file_type=5; version={version.ToString(CultureInfo.InvariantCulture)}; decode=success; styles={renderStyle.Styles.Count.ToString(CultureInfo.InvariantCulture)}; passes={passCount.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        if (LithTechRenderStyleDecoder.TryDecodeShaderAssemblyExtension(
                headerBytes,
                physicalPath,
                storageDescription,
                storedBytes.Length,
                out LithTechRenderStyleShaderExtensionDocument? shaderExtension,
                out string? extensionError) &&
            shaderExtension is not null)
        {
            Console.WriteLine($"{physicalPath}: file_type=5; version={version.ToString(CultureInfo.InvariantCulture)}; shader_extension=decoded; blocks={shaderExtension.ShaderAssemblyBlockCount.ToString(CultureInfo.InvariantCulture)}; base_error={error ?? "none"}");
            return;
        }

        Console.WriteLine($"{physicalPath}: file_type=5; version={version.ToString(CultureInfo.InvariantCulture)}; decode=failed; error={extensionError ?? error ?? "no decoder result"}");
    }

    private static string DescribeFileType(byte fileType)
    {
        return fileType switch
        {
            1 => "D3D model",
            2 => "PS2 model",
            3 => "Xbox model",
            4 => "ABC model",
            5 => "D3D render style",
            6 => "PS2 render style",
            7 => "D3D render object",
            _ => "unknown/other"
        };
    }

    private static string FormatPrefix(byte[] data)
    {
        return Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 24)));
    }

    private static bool MatchesModelReference(ExplorerItem item, string requestedPath)
    {
        string archivePath = NormalizeModelPath(item.ArchiveFile?.FullPath ?? item.OutputRelativePath);
        string outputPath = NormalizeModelPath(item.OutputRelativePath);
        if (string.Equals(archivePath, requestedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string requestedFileName = Path.GetFileName(requestedPath);
        return requestedFileName.Length > 0 &&
               string.Equals(Path.GetFileName(archivePath), requestedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').TrimStart('/');
        return Path.HasExtension(normalized) ? normalized : normalized + ".ltb";
    }

    private static byte[]? TryReadItemBytes(ExplorerItem item)
    {
        try
        {
            if (item.Kind == ExplorerItemKind.LocalFile)
            {
                var info = new FileInfo(item.SourcePath);
                return !info.Exists || info.Length <= 0 || info.Length > MaxInspectBytes
                    ? null
                    : File.ReadAllBytes(item.SourcePath);
            }

            if (item.Kind == ExplorerItemKind.RezFile &&
                item.Archive is not null &&
                item.ArchiveFile is not null &&
                item.ArchiveFile.Size > 0 &&
                item.ArchiveFile.Size <= MaxInspectBytes)
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

    private static IEnumerable<ExplorerItem> EnumerateFiles(ExplorerItem root)
    {
        var pending = new Stack<ExplorerItem>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ExplorerItem current = pending.Pop();
            if (current.IsFile)
            {
                yield return current;
            }

            foreach (ExplorerItem child in current.Children)
            {
                pending.Push(child);
            }
        }
    }
}
