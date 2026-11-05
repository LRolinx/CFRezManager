using System.IO;

namespace CFRezManager;

internal static class CftExportCommand
{
    public static bool IsInvocation(string[] args) => args.Any(arg => arg.Equals("--export-cft-xlsx", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        try
        {
            string? input = null, output = null;
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (argument.Equals("--export-cft-xlsx", StringComparison.OrdinalIgnoreCase)) continue;
                if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + argument);
                if (argument.Equals("--input", StringComparison.OrdinalIgnoreCase)) input = args[++index];
                else if (argument.Equals("--output", StringComparison.OrdinalIgnoreCase) || argument.Equals("-o", StringComparison.OrdinalIgnoreCase)) output = args[++index];
                else throw new ArgumentException("Unknown option: " + argument);
            }
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Missing --input <file.cft>.");
            string inputPath = Path.GetFullPath(input);
            output ??= Path.ChangeExtension(inputPath, ".xlsx");
            if (string.Equals(inputPath, Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Input and output paths must differ.");
            if (new FileInfo(inputPath).Length > 128L * 1024 * 1024) throw new InvalidDataException("The CFT input exceeds 128 MiB.");
            CftSpreadsheetExportResult result = CftSpreadsheetExporter.Export(File.ReadAllBytes(inputPath), output);
            Console.WriteLine($"XLSX: {result.Path}");
            Console.WriteLine($"Rows: {result.Rows}; columns: {result.Columns} (including RecordId)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
