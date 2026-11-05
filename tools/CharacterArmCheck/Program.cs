using CFRezManager;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static partial class Program
{
    static readonly Assembly App = typeof(RezArchiveReader).Assembly;
    static readonly List<string> Results = ["check\tstatus"];
    const string ModelPath = "Models/PlayerView/ArmModel/Shared_GR.ltb";
    static object? Call(string type, string method, params object?[] args) => App.GetType("CFRezManager." + type, true)!
        .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    static string Key(string value) => value.Replace('\\', '/').ToLowerInvariant();
    static void Check(string label, bool passed)
    {
        if (!passed) throw new Exception("FAILED: " + label);
        Results.Add(label + "\tpassed");
    }

    [STAThread]
    static int Main(string[] args)
    {
        string output = args.Length == 2 && args[0] == "--output" ? args[1] : args.Length == 0
            ? "artifacts/character-arm-check/results.tsv" : throw new ArgumentException("Expected --output <report.tsv>.");
        string run = Path.GetFullPath(Path.Combine("artifacts/character-arm-check", Guid.NewGuid().ToString("N")));
        string source = Path.Combine(run, "source");
        void Save(string relative, byte[] data)
        {
            string file = Path.Combine(source, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, data);
        }
        Save(ModelPath, Model()); Save("Table/CharacterType.CFT", Table());
        int color = 0;
        foreach (int row in new[] { 0, 1 }) foreach (string side in new[] { "GR", "BL" }) foreach (string role in new[] { "Hand", "Arm" })
            Save(Texture(row, side, role), Png((byte)++color));
        object?[] decode = [Model(), "Shared_GR.ltb", "ltb", null, null];
        bool decoded = (bool)App.GetType("CFRezManager.LithTechModelDecoder", true)!.GetMethods()
            .Single(m => m.Name == "TryDecode" && m.GetParameters().Length == 5).Invoke(null, decode)!;
        Check("native triangle fixture has two authoritative slots", decoded && decode[3] is LithTechModelDocument d &&
            d.Meshes.Count == 2 && d.Meshes.All(m => m.HasAuthoritativeTextureBindings) &&
            d.Meshes.SelectMany(m => m.TextureSlotIndices!).SequenceEqual(new[] { 0, 1 }));
        var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!;
        Call("LithTechObjExportCommand", "LoadAllArchives", root);
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", root)!;
        foreach (int row in new[] { 0, 1 }) foreach (string side in new[] { "GR", "BL" })
        {
            foreach (string context in new[] { $"row:CharacterType.CFT#{row}:{side}", $"A{row + 100}:{side}", $"recordid:CharacterType.CFT#{(row == 0 ? 101 : 202)}:{side}" })
            foreach ((int slot, string role) in new[] { (0, "Hand"), (1, "Arm") })
                Check(context + " slot " + slot, resolver(ModelPath, context, slot).Select(Key).SequenceEqual(new[] { Key(Texture(row, side, role)) }));
            string obj = Path.Combine(run, $"row-{row}-{side}.obj");
            string[] command = ["--export-obj", "--source-root", source, "--model", ModelPath, "--texture-context", $"row:CharacterType.CFT#{row}:{side}", "--output", obj];
            TextWriter stdout = Console.Out;
            int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)command)!; }
                finally { Console.SetOut(stdout); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            Check($"CLI row {row} {side} succeeds", code == 0);
            string[] lines = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv");
            Check($"CLI row {row} {side} has two slot rows", lines.Length == 3);
            foreach (string line in lines.Skip(1))
            {
                string[] cells = line.Split('\t'); int slot = int.Parse(cells[4]); string expected = Texture(row, side, slot == 0 ? "Hand" : "Arm");
                Check($"CLI row {row} {side} slot {slot} uses declared context", cells[5] == "context-slot" && Key(cells[6]) == Key(expected) && cells[8] == "exported");
                Check($"CLI row {row} {side} slot {slot} retains pixels", PixelHash(Path.Combine(run, cells[7])) == PixelHash(Path.Combine(source, expected)));
            }
        }
        foreach (string context in new[] { "row:CharacterType.CFT#0", "row:CharacterType.CFT#1", "row:CharacterType.CFT#101:GR", "recordid:CharacterType.CFT#1:GR", "row:CharacterType.CFT#99:BL" })
        foreach (int slot in new[] { 0, 1 }) Check("ambiguous or absent " + context + " slot " + slot, resolver(ModelPath, context, slot).Count == 0);
        string reversed = Path.Combine(run, "reversed");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(reversed, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        File.WriteAllBytes(Path.Combine(reversed, ModelPath), Model(true));
        var reversedRoot = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", reversed)!;
        Call("LithTechObjExportCommand", "LoadAllArchives", reversedRoot);
        var reversedResolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", reversedRoot)!;
        foreach (string side in new[] { "GR", "BL" }) foreach ((int slot, string role) in new[] { (0, "Arm"), (1, "Hand") })
            Check($"reversed slots with generic hand mesh {side} slot {slot}", reversedResolver(ModelPath, $"row:CharacterType.CFT#0:{side}", slot).Select(Key).SequenceEqual(new[] { Key(Texture(0, side, role)) }));
        object indexCache = App.GetType("CFRezManager.LithTechModelMeshTextureResolver", true)!
            .GetField("IndexCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        bool HasFallbackIndex()
        {
            object?[] values = [root, null]; return (bool)indexCache.GetType().GetMethod("TryGetValue")!.Invoke(indexCache, values)!;
        }
        var fallback = (Func<string, string, int, string, IReadOnlyList<string>>)Call("LithTechModelMeshTextureResolver", "CreateResolver", root)!;
        Check("creating mesh fallback does not build its index", !HasFallbackIndex());
        fallback(ModelPath, "UnknownMesh", 0, "");
        Check("requested mesh fallback builds its index", HasFallbackIndex());
        IEnumerable<string> PrimaryOnly()
        {
            yield return "Known.png";
            throw new Exception("Successful primary texture enumerated an unnecessary fallback.");
        }
        var primaryImage = BitmapFrame.Create(new MemoryStream(Png(20)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var glowImage = BitmapFrame.Create(new MemoryStream(Png(30)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        (string? Path, string? Reference, int Written) ExportCandidates(IEnumerable<string> candidates, string folder, Func<string, ImageSource?> resolve)
        {
            string directory = Path.Combine(run, folder), textures = Path.Combine(directory, "textures"); Directory.CreateDirectory(textures);
            object?[] values = [candidates, resolve, directory, textures, "textures", new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), new Dictionary<BitmapSource, string>(), null];
            object? result = App.GetType("CFRezManager.LithTechObjExporter", true)!
                .GetMethod("TryExportBestTexture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values);
            return ((string?)result, (string?)values[8], Directory.GetFiles(textures, "*.png").Length);
        }
        var primary = ExportCandidates(PrimaryOnly(), "primary-only", _ => primaryImage);
        Check("successful primary does not enumerate name fallbacks", primary.Path is not null && primary.Reference == "Known.png" && primary.Written == 1);
        var sgfx = ExportCandidates(["SGFX_Test.png", "SGFX_Test_GLOW_01.png"], "sgfx", name => name.Contains("GLOW") ? glowImage : primaryImage);
        Check("SGFX still exports its auxiliary texture", sgfx.Path is not null && sgfx.Reference == "SGFX_Test.png" && sgfx.Written == 2);
        RunBodyChecks(run);
        RunSocketChecks(run);
        RunNanoChecks(run);
        RunAiChecks(run);
        RunCharacterPvChecks(run);
        RunLtaChecks(run);
        RunOtherCftChecks(run);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllLines(output, Results);
        Console.WriteLine($"{Results.Count - 1} character arm checks passed. Results: {Path.GetFullPath(output)}");
        return 0;
    }

    static string Texture(int row, string side, string role) => $"ModelTextures/PlayerView/ArmCheck/Row{row}_{side}_{role}.png";
    static byte[] Png(byte color)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { color, (byte)(color + 10), 200, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    static string PixelHash(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit();
        var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[normalized.PixelWidth * normalized.PixelHeight * 4]; normalized.CopyPixels(pixels, normalized.PixelWidth * 4, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
    static void Str(BinaryWriter w, string value) { byte[] bytes = Encoding.ASCII.GetBytes(value); w.Write((ushort)bytes.Length); w.Write(bytes); }
    static byte[] Model(bool reverse = false)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write((ushort)1); w.Write((ushort)9); w.Write(new byte[16]); w.Write(25u);
        uint[] allocations = new uint[15]; allocations[2] = 2; allocations[3] = 2; allocations[4] = 1; allocations[8] = 2;
        foreach (uint value in allocations) w.Write(value);
        Str(w, ""); w.Write(1f); w.Write(0u); w.Write(2u);
        for (int slot = 0; slot < 2; slot++)
        {
            Str(w, slot == 0 ? (reverse ? "Object001" : "Fview-hand2") : "Fview-arm2"); w.Write(1u); w.Write(0f); w.Write(0u); w.Write(0u);
            w.Write(1u); w.Write(reverse ? 1 - slot : slot); w.Write(new byte[12]); w.Write(0); w.Write((byte)0); w.Write(4u); w.Write(102u);
            w.Write(3u); w.Write(1u); w.Write(1u); w.Write(1u); w.Write(0x11u); w.Write(new byte[12]); w.Write(0u);
            foreach (float[] vertex in new[] { new[] { 0f, 0f, 0f, 0f, 0f }, new[] { 1f, 0f, 0f, 1f, 0f }, new[] { 0f, 1f, 0f, 0f, 1f } })
                foreach (float value in vertex) w.Write(value);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((byte)1); w.Write((byte)0);
        }
        for (ushort node = 0; node < 2; node++)
        {
            Str(w, node == 0 ? "Root" : "Bone"); w.Write(node); w.Write((byte)0);
            for (int cell = 0; cell < 16; cell++) w.Write(cell % 5 == 0 ? 1f : 0f);
            w.Write(node == 0 ? 1u : 0u);
        }
        w.Write(0u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u);
        return output.ToArray();
    }
    static byte[] Table()
    {
        string[] columns = ["_RealCharacterType", "_ItemName", "_ResourceName", "_ItemID", "_ItemCode", "_ButeType", "_GRPVArmModel", "_BLPVArmModel", "_GRPVHandTextureFile", "_GRPVArmTextureFile", "_BLPVHandTextureFile", "_BLPVArmTextureFile"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(2);
        for (int c = 0; c < columns.Length; c++)
        {
            output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c]));
            output.Position = 14 + 128 * 64 + c * 4; w.Write(c is 0 or 5 ? 3 : 1);
        }
        output.Position = 8718;
        for (int row = 0; row < 2; row++)
        {
            w.Write(row == 0 ? 101 : 202);
            object[] values = [0, "Row" + row, "ArmCheck", (row + 500).ToString(), "A" + (row + 100), 0, "ArmModel/Shared_GR.ltb", "ArmModel/Shared_GR.ltb", Texture(row, "GR", "Hand"), Texture(row, "GR", "Arm"), Texture(row, "BL", "Hand"), Texture(row, "BL", "Arm")];
            foreach (object value in values)
                if (value is int integer) w.Write(integer);
                else { byte[] bytes = Encoding.Unicode.GetBytes((string)value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0);
        return output.ToArray();
    }
}
