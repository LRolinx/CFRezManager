using CFRezManager;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static partial class Program
{
    const string AiParent = "Models/Character/AiCheck/Parent.ltb";
    const string AiChild = "Models/Character/AiCheck/AnimationAsset.ltb";
    const string AiChildOnlySlot = "Models/Character/AiCheck/ExtraAsset.ltb";
    static readonly string AiFixture = Guid.NewGuid().ToString("N");
    static string AiTexture(int row, int slot) => $"{(slot == 2 ? "ModelTextures" : "Models")}/Character/AiCheck/{AiFixture}_{row}_{slot}.dtx";
    static void RunAiChecks(string run)
    {
        string source = Path.Combine(run, "ai-source"); Directory.CreateDirectory(Path.Combine(source, "Table"));
        foreach (var model in new[] { (AiParent, new[] { 0, 1 }), (AiChild, new[] { 0, 1 }), (AiChildOnlySlot, new[] { 2 }) })
        {
            string target = Path.Combine(source, model.Item1); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, NanoModel(model.Item2));
        }
        for (int row = 0; row < 4; row++) for (int slot = 0; slot < 3; slot++)
        {
            string target = Path.Combine(source, AiTexture(row, slot)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(row * 30 + slot * 7 + 10)));
        }
        File.WriteAllBytes(Path.Combine(source, "Table/AICharacter.CFT"), AiTable());
        var mappings = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", AiTable())!).Cast<object>().Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray();
        Check("AI texture fields belong to four primary rows", mappings.Length == 4);
        foreach (object mapping in mappings)
        {
            int row = (int)mapping.GetType().GetProperty("RowIndex")!.GetValue(mapping)!;
            var refs = ((System.Collections.IEnumerable)mapping.GetType().GetProperty("TextureReferences")!.GetValue(mapping)!).Cast<object>().ToArray();
            Check("AI preserves numbered fields under both resource roots", refs.Length == (row == 2 ? 3 : 2));
            var children = (IReadOnlyList<string>)mapping.GetType().GetProperty("ChildModelReferences")!.GetValue(mapping)!;
            Check("AI records child fields without requiring a filename keyword", row == 3 ? children.Count == 0 : children.SequenceEqual(new[] { row == 2 ? AiChildOnlySlot : AiChild }));
        }
        ExplorerItem Root() { var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!; Call("LithTechObjExportCommand", "LoadAllArchives", root); return root; }
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        for (int row = 0; row < 4; row++) foreach (string model in row == 0 ? new[] { AiParent, AiChild } : row == 1 ? new[] { AiChild } : row == 2 ? new[] { AiParent, AiChildOnlySlot } : new[] { AiParent })
        {
            int[] slots = model == AiChildOnlySlot ? [2] : [0, 1];
            foreach (int slot in slots) foreach (string context in new[] { $"row:AICharacter.CFT#{row}", "Ai" + row, $"recordid:AICharacter.CFT#{row + 800}" })
                Check("AI declared child selector " + context + " slot " + slot, row == 1 ? resolver(model, context, slot).Count == 0 : resolver(model, context, slot).Select(Key).SequenceEqual(new[] { Key(AiTexture(row, slot)) }));
            string obj = Path.Combine(run, $"ai-{row}-{Path.GetFileNameWithoutExtension(model)}.obj"); TextWriter saved = Console.Out; int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", model, "--texture-context", $"row:AICharacter.CFT#{row}", "--output", obj })!; }
                finally { Console.SetOut(saved); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            Check("AI CLI exports geometry", code == 0);
            var rows = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(line => line.Split('\t')).ToArray();
            Check("AI CLI retains every native stage", rows.Length == slots.Length);
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]);
                if (row == 1) { Check("AI missing parent does not authorize a child skin", r[8] != "exported" && r[7].Length == 0); continue; }
                Check("AI CLI uses exact typed fields for parents and declared children", r[8] == "exported" && r[5] == "context-slot" && Key(r[6]) == Key(AiTexture(row, slot)));
                Check("AI CLI preserves native DTX pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, AiTexture(row, slot))).AsSpan(164, 4))));
            }
        }
        string bad = Path.Combine(run, "ai-invalid-copy", AiChild); Directory.CreateDirectory(Path.GetDirectoryName(bad)!); File.WriteAllBytes(bad, [1, 2, 3]);
        RezArchiveWriter.WriteFromDirectory(Path.Combine(run, "ai-invalid-copy"), Path.Combine(source, "bad.rez"));
        var invalid = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", Root())!;
        foreach (int slot in new[] { 0, 1 }) Check("AI invalid child copy prevents inherited slot " + slot, invalid(AiChild, "row:AICharacter.CFT#0", slot).Count == 0);
    }
    static byte[] AiTable()
    {
        string[] columns = ["_CharacterName", "_ModelFile", "_ChildModelFile", "_TextureFile1", "_TextureFile2", "_TextureFile3"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(4);
        for (int c = 0; c < columns.Length; c++) { output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c])); output.Position = 14 + 128 * 64 + c * 4; w.Write(1); }
        output.Position = 8718;
        for (int row = 0; row < 4; row++)
        {
            w.Write(row + 800);
            string[] values = ["Ai" + row, row == 1 ? $"Models/Character/AiCheck/Missing_{AiFixture}.ltb" : AiParent, row == 3 ? AiParent : row == 2 ? AiChildOnlySlot : AiChild, AiTexture(row, 0), AiTexture(row, 1), row == 2 ? AiTexture(row, 2) : ""];
            foreach (string value in values) { byte[] bytes = Encoding.Unicode.GetBytes(value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
}
