using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CFRezManager;

internal static class TableDecodeAuditCommand
{
    private const int MaximumSourceBytes = 128 * 1024 * 1024;

    public static bool IsInvocation(string[] args) =>
        args.Any(arg => string.Equals(arg, "--audit-table-decode", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        try
        {
            string? root = null;
            string? output = null;
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (arg.Equals("--audit-table-decode", StringComparison.OrdinalIgnoreCase)) continue;
                if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {arg}.");
                if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase)) root = args[++index];
                else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase)) output = args[++index];
                else throw new ArgumentException($"Unknown option: {arg}");
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new ArgumentException("Missing --root <resource directory>.");
            if (string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("Missing --output <report.tsv>.");
            WriteReport(Path.GetFullPath(root), Path.GetFullPath(output));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void WriteReport(string root, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var writer = new StreamWriter(output, false, new UTF8Encoding(true));
        writer.WriteLine("record_type\tsource\tresource_path\textension\tsource_bytes\tsha256\tstatus\tdecoded_bytes\tconsumed_bytes\ttrailing_bytes\tcolumns\trows\tdetail");
        var counts = new Dictionary<(string Extension, string Status), (int Copies, HashSet<string> Hashes)>();
        int archiveCount = 0;
        int scopeErrors = 0;
        int tableCount = 0;
        var directories = new Stack<string>();
        directories.Push(root);
        var reader = new RezArchiveReader();
        while (directories.TryPop(out string? directory))
        {
            string[] paths;
            try
            {
                paths = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception)
            {
                ScopeFailure(directory, "directory-read-failed", exception.Message);
                continue;
            }

            foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            ScopeFailure(path, "directory-reparse-point-skipped", "Directory was not traversed.");
                        else directories.Push(path);
                        continue;
                    }

                    string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if (extension == "rez")
                    {
                        AuditArchive(path);
                    }
                    else if (extension is "cft" or "ltc")
                    {
                        AuditTable(path, Path.GetRelativePath(root, path), extension,
                            new FileInfo(path).Length, () => File.OpenRead(path));
                    }
                }
                catch (Exception exception)
                {
                    ScopeFailure(path, "entry-read-failed", exception.Message);
                }
            }
        }

        string summaryPath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_summary.txt");
        using var summary = new StreamWriter(summaryPath, false, new UTF8Encoding(true));
        summary.WriteLine($"Root: {root}");
        summary.WriteLine($"Report: {output}");
        summary.WriteLine($"Archives loaded: {archiveCount}");
        summary.WriteLine($"Scope errors or skipped directories: {scopeErrors}");
        summary.WriteLine($"Physical table copies tested: {tableCount}");
        foreach (var pair in counts.OrderBy(pair => pair.Key.Extension).ThenBy(pair => pair.Key.Status))
            summary.WriteLine($"{pair.Key.Extension}\t{pair.Key.Status}\tcopies={pair.Value.Copies}\tunique_contents={pair.Value.Hashes.Count}");
        Console.WriteLine($"Table decode report: {output}");
        Console.WriteLine($"Table decode summary: {summaryPath}");

        void ScopeFailure(string source, string status, string detail)
        {
            scopeErrors++;
            WriteRow("scope", source, "", "", "", "", status, "", "", "", "", "", detail);
        }

        void AuditArchive(string path)
        {
            RezArchive archive;
            try
            {
                archive = reader.Read(path);
            }
            catch (Exception exception)
            {
                ScopeFailure(path, "archive-read-failed", exception.Message);
                return;
            }

            archiveCount++;
            WriteRow("scope", path, "", "rez", "", "", "archive-loaded", "", "", "", "", "", $"indexed_files={archive.FileCount}");
            var pending = new Stack<RezDirectoryNode>();
            pending.Push(archive.Root);
            while (pending.TryPop(out RezDirectoryNode? node))
            {
                foreach (RezNode child in node.Children)
                {
                    if (child is RezDirectoryNode nested) pending.Push(nested);
                    else if (child is RezFileNode file)
                    {
                        string extension = file.Extension.TrimStart('.').ToLowerInvariant();
                        if (extension is "cft" or "ltc")
                            AuditTable(path, file.FullPath, extension, file.Size,
                                () => RezArchiveReader.OpenFileRead(archive, file));
                    }
                }
            }
        }

        void AuditTable(string source, string path, string extension, long size, Func<Stream> open)
        {
            tableCount++;
            string hash = "";
            string status = "read-failed";
            string detail = "";
            int decodedBytes = 0;
            int? consumedBytes = null, trailingBytes = null, columns = null, rows = null;
            try
            {
                if (size < 0 || size > MaximumSourceBytes)
                {
                    status = "source-size-limit";
                    detail = $"Maximum source size: {MaximumSourceBytes} bytes.";
                }
                else
                {
                    byte[] data = new byte[(int)size];
                    using (Stream stream = open()) stream.ReadExactly(data);
                    hash = Convert.ToHexString(SHA256.HashData(data));
                    status = "decode-failed";
                    if (extension == "cft")
                    {
                        bool decoded = CrossFireCftTableDecoder.TryDecodeWithDiagnostics(data,
                            out CrossFireCftTable? table, out decodedBytes, out int consumed, out string? error);
                        consumedBytes = consumed;
                        if (decoded && table is not null)
                        {
                            columns = table.Columns.Count;
                            rows = table.Rows.Count;
                            trailingBytes = decodedBytes - consumed;
                            status = trailingBytes == 0 ? "typed-decoded" : "typed-decoded-with-trailing-bytes";
                            detail = trailingBytes == 0 ? "" : "Bytes after the declared rows have not been interpreted.";
                        }
                        else detail = error ?? "Typed CFT decoding failed.";
                    }
                    else if (CrossFireLtcDecoder.TryDecodeNativeText(data, out CrossFireLtcTextDocument? document, out string? error) && document is not null)
                    {
                        decodedBytes = document.DecodedByteCount;
                        status = "native-text-decoded";
                        detail = $"{document.StorageDescription}; encoding={document.EncodingName}";
                    }
                    else detail = error ?? "In-process LTC decoding failed.";
                }
            }
            catch (Exception exception)
            {
                detail = $"{exception.GetType().Name}: {exception.Message}";
            }

            var key = (extension, status);
            counts.TryGetValue(key, out var count);
            count.Hashes ??= new HashSet<string>(StringComparer.Ordinal);
            if (hash.Length > 0) count.Hashes.Add(hash);
            counts[key] = (count.Copies + 1, count.Hashes);
            WriteRow("table", source, path, extension, size, hash, status, decodedBytes,
                consumedBytes, trailingBytes, columns, rows, detail);
            if (tableCount % 250 == 0) writer.Flush();
        }

        void WriteRow(params object?[] values) => writer.WriteLine(string.Join('\t', values.Select(value =>
            Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "")));
    }
}
