using CFRezManager;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static partial class Program
{
    const string CharacterPvPrefix = "Models/PVItem/Check/Ring";
    static readonly string CharacterPvFixture = Guid.NewGuid().ToString("N");
    static string CharacterPvTexture(int row, int slot) => $"ModelTextures/PVItem/Check/{CharacterPvFixture}_{row}_{slot}.dtx";
    static void RunCharacterPvChecks(string run)
    {
        string source = Path.Combine(run, "character-pv-source"); Directory.CreateDirectory(Path.Combine(source, "Table"));
        foreach (string model in new[] { CharacterPvPrefix + "_M.ltb", CharacterPvPrefix + "_F.ltb", "Models/PVItem/Check/Explicit.ltb", "Models/PVItem/Check/Explicit_M.ltb", "Models/Character/Check/Ring_M.ltb", "Models/Character/Check/Ring_F.ltb" })
        {
            string target = Path.Combine(source, model); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, NanoModel([0, 1]));
        }
        for (int row = 0; row < 5; row++) for (int slot = 0; slot < 2; slot++)
        {
            if (row == 2 && slot == 0) continue;
            string target = Path.Combine(source, CharacterPvTexture(row, slot)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(10 + row * 30 + slot * 7)));
        }
        File.WriteAllBytes(Path.Combine(source, "Table/CharacterPvItem.CFT"), CharacterPvTable());
        File.WriteAllBytes(Path.Combine(source, "Table/OtherItem.CFT"), CharacterPvTable());
        ExplorerItem Root() { var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!; Call("LithTechObjExportCommand", "LoadAllArchives", root); return root; }
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        for (int row = 0; row < 3; row++) foreach (string gender in new[] { "M", "F" })
        {
            string model = CharacterPvPrefix + "_" + gender + ".ltb";
            foreach (int slot in row == 2 ? new[] { 1 } : new[] { 0, 1 })
                foreach (string context in new[] { $"row:CharacterPvItem.CFT#{row}", "PvRing" + row, $"recordid:CharacterPvItem.CFT#{row + 900}" })
                    Check("character PV gender model selector " + context + " " + gender + " slot " + slot, resolver(model, context, slot).Select(Key).SequenceEqual(new[] { Key(CharacterPvTexture(row, slot)) }));
            Export(row, model, gender);
        }
        Export(3, "Models/PVItem/Check/Explicit.ltb", "explicit");
        foreach (int slot in new[] { 0, 1 })
        {
            Check("character PV complete filenames do not gain a gender suffix", resolver("Models/PVItem/Check/Explicit_M.ltb", "row:CharacterPvItem.CFT#3", slot).Count == 0);
            foreach (string gender in new[] { "M", "F" })
            {
                Check("character PV expansion requires the source table", resolver(CharacterPvPrefix + "_" + gender + ".ltb", "row:OtherItem.CFT#0", slot).Count == 0);
                Check("character PV expansion requires the PVItem directory", resolver("Models/Character/Check/Ring_" + gender + ".ltb", "row:CharacterPvItem.CFT#4", slot).Count == 0);
            }
        }
        string bad = Path.Combine(run, "character-pv-invalid", CharacterPvPrefix + "_M.ltb"); Directory.CreateDirectory(Path.GetDirectoryName(bad)!); File.WriteAllBytes(bad, [1, 2, 3]);
        RezArchiveWriter.WriteFromDirectory(Path.Combine(run, "character-pv-invalid"), Path.Combine(source, "bad.rez"));
        var invalid = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        foreach (int slot in new[] { 0, 1 })
        {
            Check("character PV invalid M copy prevents prefix inheritance", invalid(CharacterPvPrefix + "_M.ltb", "row:CharacterPvItem.CFT#0", slot).Count == 0);
            Check("character PV invalid M copy preserves the valid F binding", invalid(CharacterPvPrefix + "_F.ltb", "row:CharacterPvItem.CFT#0", slot).Select(Key).SequenceEqual(new[] { Key(CharacterPvTexture(0, slot)) }));
        }
        File.WriteAllBytes(bad, NanoModel([0]));
        RezArchiveWriter.WriteFromDirectory(Path.Combine(run, "character-pv-invalid"), Path.Combine(source, "bad.rez"));
        var inconsistent = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        foreach (int slot in new[] { 0, 1 }) Check("character PV different physical slot sets prevent prefix inheritance", inconsistent(CharacterPvPrefix + "_M.ltb", "row:CharacterPvItem.CFT#0", slot).Count == 0);
        void Export(int row, string model, string label)
        {
            string obj = Path.Combine(run, $"character-pv-{row}-{label}.obj"); TextWriter saved = Console.Out; int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", model, "--texture-context", $"row:CharacterPvItem.CFT#{row}", "--output", obj })!; }
                finally { Console.SetOut(saved); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            Check("character PV CLI exports both gender meshes", code == 0);
            var rows = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(l => l.Split('\t')).ToArray(); Check("character PV CLI retains both native slots", rows.Length == 2);
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]);
                if (row == 2 && slot == 0) { Check("character PV absent base skin remains unwritten", r[8] != "exported" && r[7].Length == 0); continue; }
                Check("character PV CLI uses the declared row field", r[8] == "exported" && r[5] == "context-slot" && Key(r[6]) == Key(CharacterPvTexture(row, slot)));
                Check("character PV CLI preserves native DTX pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, CharacterPvTexture(row, slot))).AsSpan(164, 4))));
            }
        }
    }
    static byte[] CharacterPvTable()
    {
        string[] columns = ["_ItemName", "_ItemIndex", "_AttachSocketName", "_SocketItemModelFile", "_TexFile1", "_TexFile2"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(5);
        for (int c = 0; c < columns.Length; c++) { output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c])); output.Position = 14 + 128 * 64 + c * 4; w.Write(c == 1 ? 3 : 1); }
        output.Position = 8718;
        for (int row = 0; row < 5; row++)
        {
            w.Write(row + 900);
            object[] values = ["PvRing" + row, 2025 + row, "Ring02", row == 3 ? "Models/PVItem/Check/Explicit.ltb" : row == 4 ? "Models/Character/Check/Ring" : CharacterPvPrefix, CharacterPvTexture(row, 0), CharacterPvTexture(row, 1)];
            foreach (object value in values) if (value is int n) w.Write(n); else { byte[] bytes = Encoding.Unicode.GetBytes((string)value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
}
