using CFRezManager;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const string Logical = "ModelTextures/CopyCheck/Shared.png";
    private static readonly string FixtureRoot = Path.GetFullPath("artifacts/texture-copy-check/fixtures-" + Guid.NewGuid().ToString("N"));
    private static readonly List<string> Results = ["case\texpected\tobserved\tresult"];
    private static readonly Type Loader = typeof(ExplorerItem).Assembly.GetType("CFRezManager.LithTechModelTextureLoader")!;
    private static readonly MethodInfo Global = Loader.GetMethod("CreateGlobalResolver")!;
    private static readonly MethodInfo GlobalState = Loader.GetMethod("CreateGlobalResolverState", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Combined = typeof(ExplorerItem).Assembly.GetType("CFRezManager.LithTechObjExportCommand")!.GetMethod("CreateObjTextureResolver", BindingFlags.Static | BindingFlags.NonPublic)!;

    [STAThread]
    private static void Main(string[] args)
    {
        string? resourceRoot = null;
        string output = "artifacts/texture-copy-check/results.tsv";
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--resources" && index + 1 < args.Length) resourceRoot = args[++index];
            else if (args[index] == "--output" && index + 1 < args.Length) output = args[++index];
            else throw new ArgumentException("Expected --resources <snapshot> or --output <report.tsv>.");
        }
        Directory.CreateDirectory(FixtureRoot);
        byte[] color = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255];
        byte[] different = (byte[])color.Clone(); different[0]++;
        byte[] alpha = (byte[])color.Clone(); alpha[3] = 128;
        byte[] tailColor = (byte[])color.Clone(); tailColor[^2]++;
        byte[] tailAlpha = (byte[])color.Clone(); tailAlpha[^1] = 128;
        byte[] original = Png(color, 2, 2, 96);
        byte[] metadata = Png(color, 2, 2, 144);
        byte[] padded = PngWithTextChunk(original, 1024);
        if (PixelHash(original) != PixelHash(padded)) throw new Exception("PNG metadata fixture changed pixels");
        byte[] other = Png(different, 2, 2, 96);
        byte[] transparent = Png(alpha, 2, 2, 96);
        byte[] dimensions = Png(color, 1, 4, 96);
        byte[] lastColor = Png(tailColor, 2, 2, 96);
        byte[] lastAlpha = Png(tailAlpha, 2, 2, 96);
        if (original.SequenceEqual(metadata)) throw new Exception("Metadata fixture must have different stored bytes");

        foreach (bool loose in new[] { false, true })
        {
            string kind = loose ? "loose" : "REZ";
            Check(kind + " identical bytes", [Copy(kind + "-same-a", original, loose), Copy(kind + "-same-b", original, loose)], true);
            Check(kind + " identical pixels, different DPI/bytes", [Copy(kind + "-meta-a", original, loose), Copy(kind + "-meta-b", metadata, loose)], true);
            Check(kind + " identical PNG pixels at raw-TGA size", [Copy(kind + "-png-a", original, loose), Copy(kind + "-png-b", padded, loose)], true);
            string tgaPath = "ModelTextures/CopyCheck/Shared.tga";
            Check(kind + " PNG payload in TGA reference", [Copy(kind + "-png-tga-a", original, loose, tgaPath), Copy(kind + "-png-tga-b", padded, loose, tgaPath)], true, tgaPath);
            var invalidPng = Copy(kind + "-invalid-png", original[..8].Concat(new byte[56]).ToArray(), loose);
            Expect(kind + " recognized PNG decode failure cannot become raw TGA", Resolver(Root([invalidPng]))(invalidPng.OutputRelativePath), null);
            foreach ((string variant, byte[] data) in new[] { ("RGB difference", other), ("alpha difference", transparent), ("last-row RGB difference", lastColor), ("last-row alpha difference", lastAlpha), ("dimension difference", dimensions), ("invalid copy", Array.Empty<byte>()) })
            {
                ExplorerItem a = Copy(kind + "-" + variant + "-a", original, loose);
                ExplorerItem b = Copy(kind + "-" + variant + "-b", data, loose);
                Check(kind + " " + variant + " forward", [a, b], false);
                Check(kind + " " + variant + " reverse", [b, a], false);
            }
            var differentPath = Copy(kind + "-different-path", original, loose, "ModelTextures/Other/Shared.png");
            Check(kind + " same basename, different logical paths", [Copy(kind + "-path-a", original, loose), differentPath], false, "Shared.png");

            ExplorerItem first = Copy(kind + "-qualified-a", original, loose);
            ExplorerItem second = Copy(kind + "-qualified-b", other, loose);
            var root = Root([first, second]);
            var resolve = Resolver(root);
            Expect(kind + " conflict before explicit source", resolve(Logical), null);
            Expect(kind + " explicit first source", resolve(first.OutputRelativePath), PixelHash(original));
            Expect(kind + " explicit second source", resolve(second.OutputRelativePath), PixelHash(other));
            if (loose)
            {
                string model = Path.Combine(Path.GetDirectoryName(first.SourcePath)!, "../../Models/Check/Model.ltb");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(model))!);
                File.WriteAllBytes(model, []);
                var modelItem = new ExplorerItem { Name = "Model.ltb", Kind = ExplorerItemKind.LocalFile, SourcePath = Path.GetFullPath(model), OutputRelativePath = "Models/Check/Model.ltb" };
                var primary = (Func<string, ImageSource?>)Loader.GetMethod("CreateResolver")!.Invoke(null, [modelItem])!;
                if (primary(Logical) is null) throw new Exception("Local fallback fixture must resolve on its own");
                var combined = (Func<string, ImageSource?>)Combined.Invoke(null, [modelItem, GlobalState.Invoke(null, [root])])!;
                Expect(kind + " combined resolver cannot bypass conflict", combined(Logical), null);
                var unrelated = Copy(kind + "-unrelated", original, true, "ModelTextures/Other/Unrelated.png");
                var outside = (Func<string, ImageSource?>)Combined.Invoke(null, [modelItem, GlobalState.Invoke(null, [Root([unrelated])])])!;
                Expect(kind + " local fallback when global path absent", outside(Logical), PixelHash(original));
            }
            else
            {
                var primary = (Func<string, ImageSource?>)Loader.GetMethod("CreateResolver")!.Invoke(null, [first])!;
                if (primary(Logical) is null) throw new Exception("REZ fallback fixture must resolve on its own");
                var combined = (Func<string, ImageSource?>)Combined.Invoke(null, [first, GlobalState.Invoke(null, [root])])!;
                Expect(kind + " combined resolver cannot bypass conflict", combined(Logical), null);
            }
        }

        ExplorerItem staleA = Copy("stale-a", original, false), staleB = Copy("stale-b", metadata, false);
        staleA.ArchiveFile = WithMd5(staleA.ArchiveFile!, new string('0', 32));
        staleB.ArchiveFile = WithMd5(staleB.ArchiveFile!, new string('1', 32));
        Check("stale index MD5, equal actual pixels", [staleA, staleB], true);
        ExplorerItem forgedA = Copy("forged-a", original, false), forgedB = Copy("forged-b", other, false);
        forgedB.ArchiveFile = WithMd5(forgedB.ArchiveFile!, forgedA.ArchiveFile!.Md5);
        Check("equal index MD5, different actual pixels", [forgedA, forgedB], false);

        ExplorerItem conflictA = Copy("alternate-a", original, false), conflictB = Copy("alternate-b", other, false);
        var alternate = Copy("alternate-format", original, false, "ModelTextures/CopyCheck/Shared.bmp");
        Expect("conflicting declared extension cannot fall through", Resolver(Root([conflictA, conflictB, alternate]))(Logical), null);
        var invalid = Copy("invalid-exact", [], true);
        var invalidRoot = Root([invalid, Copy("valid-other", original, true)]);
        Expect("invalid physical source cannot fall through", Resolver(invalidRoot)(invalid.OutputRelativePath), null);
        if (resourceRoot is not null) RealDtxCases(resourceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllLines(output, Results, new UTF8Encoding(false));
        Console.WriteLine($"PASS {Results.Count - 1} texture-copy cases");
    }

    private static RezFileNode WithMd5(RezFileNode file, string md5) => new(file.Name, file.FullPath, file.Extension, file.DataOffset, file.Size, file.Time, file.Id, md5);
    private static ExplorerItem Root(IEnumerable<ExplorerItem> copies)
    {
        var root = new ExplorerItem { Name = "root", Kind = ExplorerItemKind.Directory };
        root.Children.AddRange(copies); return root;
    }
    private static Func<string, ImageSource?> Resolver(ExplorerItem root) => (Func<string, ImageSource?>)Global.Invoke(null, [root])!;
    private static void Check(string name, ExplorerItem[] copies, bool success, string path = Logical)
    {
        ImageSource? result = Resolver(Root(copies))(path);
        string? expected = success ? Hash((BitmapSource)((Func<string, ImageSource?>)Loader.GetMethod("CreateResolver")!.Invoke(null, [copies[0]])!)(path)!) : null;
        Expect(name, result, expected);
    }
    private static void Expect(string name, ImageSource? image, string? expected)
    {
        string? actual = image is BitmapSource bitmap ? Hash(bitmap) : null;
        if (actual != expected) throw new Exception($"{name}: expected {expected ?? "unresolved"}, got {actual ?? "unresolved"}");
        Results.Add(string.Join('\t', name, expected ?? "unresolved", actual ?? "unresolved", "passed"));
    }
    private static ExplorerItem Copy(string name, byte[] data, bool loose, string logical = Logical)
    {
        string folder = Path.Combine(FixtureRoot, name.Replace(' ', '-'));
        string source = Path.Combine(folder, logical.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); File.WriteAllBytes(source, data);
        if (loose) return new ExplorerItem { Name = Path.GetFileName(source), Kind = ExplorerItemKind.LocalFile, SourcePath = source, OutputRelativePath = name + "/" + logical };
        string archivePath = folder + ".rez";
        RezArchiveWriter.WriteFromDirectory(folder, archivePath);
        var archive = new RezArchiveReader().Read(archivePath);
        RezFileNode file = Files(archive.Root).Single();
        return new ExplorerItem { Name = file.Name, Kind = ExplorerItemKind.RezFile, Archive = archive, ArchiveFile = file, OutputRelativePath = name + "/" + logical };
    }
    private static IEnumerable<RezFileNode> Files(RezDirectoryNode folder) => folder.Children.SelectMany(node => node is RezFileNode file ? new[] { file } : Files((RezDirectoryNode)node));
    private static byte[] Png(byte[] pixels, int width, int height, double dpi)
    {
        var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static string PixelHash(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return Hash(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
    }
    private static byte[] PngWithTextChunk(byte[] source, int size)
    {
        if (!source.AsSpan(source.Length - 8, 4).SequenceEqual("IEND"u8)) throw new Exception("PNG lacks final IEND");
        int dataLength = size - source.Length - 12;
        byte[] chunk = new byte[dataLength + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, dataLength);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        "pad\0"u8.CopyTo(chunk.AsSpan(8));
        chunk.AsSpan(12, dataLength - 4).Fill((byte)'x');
        uint crc = uint.MaxValue;
        foreach (byte value in chunk.AsSpan(4, dataLength + 4))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return source[..^12].Concat(chunk).Concat(source[^12..]).ToArray();
    }
    private static string Hash(BitmapSource bitmap)
    {
        var normalized = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[normalized.PixelWidth * normalized.PixelHeight * 4];
        normalized.CopyPixels(pixels, normalized.PixelWidth * 4, 0);
        byte[] dimensions = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(dimensions, normalized.PixelWidth); BinaryPrimitives.WriteInt32LittleEndian(dimensions.AsSpan(4), normalized.PixelHeight);
        return Convert.ToHexString(SHA256.HashData(dimensions.Concat(pixels).ToArray()));
    }
    private static void RealDtxCases(string source)
    {
        string[] paths = ["CFPL_Headset_GR.DTX", "The_Fate_Casual_HAIR_GR.DTX", "The_Fates_Casual_HAT_GR.DTX"];
        string[][] manifest = File.ReadAllLines(Path.Combine(source, "resource-manifest.tsv")).Skip(1).Select(line => line.Split('\t')).ToArray();
        foreach (string path in paths)
        {
            var records = manifest.Where(row => Path.GetFileName(row[1]).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (records.Length != 2) throw new Exception("Expected two original DTX copies: " + path);
            var copies = records.Select(row => new ExplorerItem { Name = path, Kind = ExplorerItemKind.LocalFile, SourcePath = Path.GetFullPath(Path.Combine(source, row[5])), OutputRelativePath = row[5].Replace('\\', '/') }).ToArray();
            Check("original " + path + " forward", copies, false, records[0][1]);
            Check("original " + path + " reverse", copies.Reverse().ToArray(), false, records[0][1]);
            var resolver = Resolver(Root(copies));
            foreach (ExplorerItem item in copies)
            {
                var direct = (Func<string, ImageSource?>)Loader.GetMethod("CreateLocalResolver")!.Invoke(null, [Path.Combine(Path.GetDirectoryName(item.SourcePath)!, "Model.ltb")])!;
                Expect("original " + path + " physical " + item.OutputRelativePath.Split('/')[0], resolver(item.OutputRelativePath), Hash((BitmapSource)direct(item.SourcePath)!));
            }
        }
    }
}
