using CFRezManager;
using System.IO;
using System.Text;

static partial class Program
{
    const string BodyPath = "Models/Character/Shared_GR.ltb";
    const string RoyalPath = "Models/Character/BattleRoyal/Shared_GR_BR.ltb";
    static string BodyTexture(int row, string side, int slot) => $"ModelTextures/Character/BodyCheck/Row{row}_{slot}_{(side == "GR" ? "BL" : "GR")}.png";
    static void RunBodyChecks(string run)
    {
        string source = Path.Combine(run, "body-source");
        void Save(string path, byte[] data) { string p = Path.Combine(source, path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, data); }
        Save(BodyPath, Model()); Save(RoyalPath, Model()); Save("Table/CharacterType.CFT", BodyTable());
        foreach (int row in new[] { 0, 1 }) foreach (string side in new[] { "GR", "BL" }) foreach (int slot in new[] { 0, 1 })
            Save(BodyTexture(row, side, slot), Png((byte)(20 + row * 4 + (side == "BL" ? 2 : 0) + slot)));
        var mappings = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", BodyTable())!).Cast<object>()
            .Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray();
        Check("body rows retain both primary and BattleRoyal model paths", mappings.Length == 4);
        foreach (object m in mappings)
        {
            var textures = ((System.Collections.IEnumerable)m.GetType().GetProperty("TextureReferences")!.GetValue(m)!).Cast<object>().ToArray();
            Check("body base fields survive alongside numbered fields", textures.Length == 4);
            var columns = (IReadOnlyList<string>)m.GetType().GetProperty("ModelColumns")!.GetValue(m)!;
            Check("shared body retains every model column", columns.Count is 2 or 4);
        }
        ExplorerItem Root()
        {
            var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!;
            Call("LithTechObjExportCommand", "LoadAllArchives", root); return root;
        }
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        foreach (string model in new[] { BodyPath, RoyalPath }) foreach (int row in new[] { 0, 1 }) foreach (string side in new[] { "GR", "BL" })
        {
            foreach (string context in new[] { $"row:CharacterType.CFT#{row}:{side}", $"Body{row}:{side}", $"recordid:CharacterType.CFT#{row + 300}:{side}" })
            foreach (int slot in new[] { 0, 1 })
                Check($"body {model} {context} slot {slot}", resolver(model, context, slot).Select(Key).SequenceEqual(new[] { Key(BodyTexture(row, side, slot)) }));
            string obj = Path.Combine(run, $"body-{(model == BodyPath ? "normal" : "royal")}-{row}-{side}.obj");
            string[] command = ["--export-obj", "--source-root", source, "--model", model, "--texture-context", $"row:CharacterType.CFT#{row}:{side}", "--output", obj];
            int code; TextWriter stdout = Console.Out;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)command)!; }
                finally { Console.SetOut(stdout); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            Check("body CLI export succeeds " + obj, code == 0);
            var report = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(line => line.Split('\t')).ToArray();
            Check("body CLI exports both native slots", report.Length == 2 && report.All(r => r[5] == "context-slot" && r[8] == "exported"));
            foreach (var r in report) Check("body CLI uses declared side pixels", Key(r[6]) == Key(BodyTexture(row, side, int.Parse(r[4]))) && PixelHash(Path.Combine(run, r[7])) == PixelHash(Path.Combine(source, r[6])));
        }
        foreach (string model in new[] { BodyPath, RoyalPath }) foreach (int slot in new[] { 0, 1 })
            Check("shared body requires side " + model + " slot " + slot, resolver(model, "row:CharacterType.CFT#0", slot).Count == 0);
        File.Move(Path.Combine(source, BodyTexture(0, "BL", 0)), Path.Combine(source, BodyTexture(0, "BL", 0)) + ".removed");
        var missing = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        Check("missing BL body declaration does not become a GR default", missing(BodyPath, "row:CharacterType.CFT#0", 0).Count == 0);
        Check("missing BL body remains the selected declared reference", missing(BodyPath, "row:CharacterType.CFT#0:BL", 0).Select(Key).SequenceEqual(new[] { Key(BodyTexture(0, "BL", 0)) }));
        Check("missing BL body keeps its valid second texture", missing(BodyPath, "row:CharacterType.CFT#0:BL", 1).Select(Key).SequenceEqual(new[] { Key(BodyTexture(0, "BL", 1)) }));
        string missingObj = Path.Combine(run, "body-missing.obj"); TextWriter saved = Console.Out; int missingCode;
        using (var log = new StringWriter())
        {
            try { Console.SetOut(log); missingCode = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", BodyPath, "--texture-context", "row:CharacterType.CFT#0:BL", "--output", missingObj })!; }
            finally { Console.SetOut(saved); }
        }
        var missingRows = File.ReadAllLines(Path.ChangeExtension(missingObj, null) + "_texture_slots.tsv").Skip(1).Select(line => line.Split('\t')).ToArray();
        Check("missing body CLI keeps static geometry", missingCode == 0 && missingRows.Length == 2);
        Check("missing body CLI does not replace the declared base skin", missingRows.Single(r => r[4] == "0") is var failed && failed[8] != "exported" && failed[7].Length == 0);
        Check("missing body CLI still exports the declared second skin", missingRows.Single(r => r[4] == "1") is var retained && retained[8] == "exported" && Key(retained[6]) == Key(BodyTexture(0, "BL", 1)));
        string duplicate = Path.Combine(run, "body-invalid-copy", BodyPath); Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!); File.WriteAllBytes(duplicate, [0, 1, 2]);
        RezArchiveWriter.WriteFromDirectory(Path.Combine(run, "body-invalid-copy"), Path.Combine(source, "invalid-body.rez"));
        var invalid = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        foreach (string side in new[] { "GR", "BL" }) Check("invalid physical body copy prevents base slot binding " + side, invalid(BodyPath, "row:CharacterType.CFT#0:" + side, 0).Count == 0);
    }
    static byte[] BodyTable()
    {
        string[] columns = ["_RealCharacterType", "_ItemName", "_ResourceName", "_ItemID", "_ItemCode", "_ButeType", "_GRModelFile", "_BLModelFile", "_GRModelFile2", "_BLModelFile2", "_BattleRoyal_GRModelFile", "_BattleRoyal_BLModelFile", "_GRTextureFile", "_GRTextureFile2", "_BLTextureFile", "_BLTextureFile2"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(2);
        for (int i = 0; i < columns.Length; i++) { output.Position = 14 + i * 64; w.Write(Encoding.ASCII.GetBytes(columns[i])); output.Position = 14 + 128 * 64 + i * 4; w.Write(i is 0 or 5 ? 3 : 1); }
        output.Position = 8718;
        for (int row = 0; row < 2; row++)
        {
            w.Write(row + 300);
            object[] values = [0, "Body" + row, "BodyCheck", (row + 800).ToString(), "B" + row, 0, BodyPath, BodyPath, BodyPath, BodyPath, RoyalPath, RoyalPath, BodyTexture(row, "GR", 0), BodyTexture(row, "GR", 1), BodyTexture(row, "BL", 0), BodyTexture(row, "BL", 1)];
            foreach (object value in values) if (value is int n) w.Write(n); else { byte[] b = Encoding.Unicode.GetBytes((string)value); w.Write((ushort)b.Length); w.Write(b); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
}
