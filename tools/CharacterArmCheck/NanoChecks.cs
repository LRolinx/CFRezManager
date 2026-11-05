using CFRezManager;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static partial class Program
{
    const string NanoBody = "Models/Character/NanoCheck/Body.ltb";
    static string NanoSocket(int index) => $"Models/SocketItem/NanoCheck/Part{index}.ltb";
    static readonly string NanoFixture = Guid.NewGuid().ToString("N");
    static readonly string[] NanoTextureFields = ["base", "one", "two", "socket1", "socket2", "zero"];
    static string NanoTexture(int row, string field) => $"ModelTextures/Character/NanoCheck/{NanoFixture}_{row}_{field}.dtx";
    static void RunNanoChecks(string run)
    {
        string source = Path.Combine(run, "nano-source"); Directory.CreateDirectory(Path.Combine(source, "Table"));
        foreach (var model in new[] { (NanoBody, new[] { 0, 1, 2 }), (NanoSocket(1), new[] { 0 }), (NanoSocket(2), new[] { 0 }) })
        {
            string target = Path.Combine(source, model.Item1); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, NanoModel(model.Item2));
        }
        for (int row = 0; row < 4; row++) foreach (string field in NanoTextureFields)
        {
            if (row == 2 && field == "base") continue;
            string target = Path.Combine(source, NanoTexture(row, field)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(10 + row * 30 + Array.IndexOf(NanoTextureFields, field) * 3)));
        }
        File.WriteAllBytes(Path.Combine(source, "Table/NanoCharacterItem.CFT"), NanoTable());
        var extracted = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", NanoTable())!).Cast<object>().Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray();
        Check("nano preserves all 12 body and accessory rows (found " + extracted.Length + ")", extracted.Length == 12);
        foreach (object mapping in extracted)
        {
            int row = (int)mapping.GetType().GetProperty("RowIndex")!.GetValue(mapping)!;
            string model = (string)mapping.GetType().GetProperty("ModelReference")!.GetValue(mapping)!;
            var refs = ((System.Collections.IEnumerable)mapping.GetType().GetProperty("TextureReferences")!.GetValue(mapping)!).Cast<object>().ToArray();
            string[] columns = refs.Select(r => (string)r.GetType().GetProperty("Column")!.GetValue(r)!).ToArray();
            string[] expected = model == NanoBody ? row == 3 ? ["_SkinFileName", "_SkinFileName0", "_SkinFileName1", "_SkinFileName2"] : ["_SkinFileName", "_SkinFileName1", "_SkinFileName2"] : ["_SocketSkinFileName" + (model == NanoSocket(1) ? 1 : 2)];
            Check("nano retains base and isolates accessory fields", columns.Order().SequenceEqual(expected.Order()));
        }
        var other = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", NanoTable(nanoSchema: false))!).Cast<object>().Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray();
        Check("ordinary schemas keep their existing field interpretation", other.SelectMany(m => ((System.Collections.IEnumerable)m.GetType().GetProperty("TextureReferences")!.GetValue(m)!).Cast<object>()).All(t => t.GetType().GetProperty("NativeSlotIndex")!.GetValue(t) is null));
        var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!; Call("LithTechObjExportCommand", "LoadAllArchives", root);
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", root)!;
        for (int row = 0; row < 4; row++) foreach (string model in new[] { NanoBody, NanoSocket(1), NanoSocket(2) })
        {
            foreach (int slot in model == NanoBody ? new[] { 0, 1, 2 } : new[] { 0 })
            {
                if (model == NanoBody && slot == 0 && row is 2 or 3) continue;
                string field = model == NanoBody ? slot == 0 ? "base" : slot == 1 ? "one" : "two" : model == NanoSocket(1) ? "socket1" : "socket2";
                foreach (string context in new[] { $"row:NanoCharacterItem.CFT#{row}", "Nano" + row, $"recordid:NanoCharacterItem.CFT#{row + 700}" })
                    Check("nano selector " + context + " slot " + slot, resolver(model, context, slot).Select(Key).SequenceEqual(new[] { Key(NanoTexture(row, field)) }));
            }
            string obj = Path.Combine(run, $"nano-{row}-{Path.GetFileNameWithoutExtension(model)}.obj"); TextWriter saved = Console.Out; int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", model, "--texture-context", $"row:NanoCharacterItem.CFT#{row}", "--output", obj })!; }
                finally { Console.SetOut(saved); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            var rows = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(line => line.Split('\t')).ToArray();
            Check("nano CLI preserves geometry", code == 0 && rows.Length == (model == NanoBody ? 3 : 1));
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]);
                if (model == NanoBody && slot == 0 && row is 2 or 3) { Check("nano missing or conflicting base stays unwritten", r[8] != "exported" && r[7].Length == 0); continue; }
                string field = model == NanoBody ? slot == 0 ? "base" : slot == 1 ? "one" : "two" : model == NanoSocket(1) ? "socket1" : "socket2";
                Check("nano CLI uses the selected declared skin", r[8] == "exported" && r[5] == "context-slot" && Key(r[6]) == Key(NanoTexture(row, field)));
                Check("nano CLI preserves native DTX pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, NanoTexture(row, field))).AsSpan(164, 4))));
            }
        }
        foreach (int index in new[] { 1, 2 }) Check("accessory number does not become a second native slot", resolver(NanoSocket(index), "row:NanoCharacterItem.CFT#0", 1).Count == 0);
    }
    static byte[] NanoTable(bool nanoSchema = true)
    {
        string[] columns = [nanoSchema ? "_NanoType" : "_OtherType", "_ResourceName", "_ModelFileName", "_SkinFileName", "_SkinFileName0", "_SkinFileName1", "_SkinFileName2", "_SocketModelFileName1", "_SocketSkinFileName1", "_SocketModelFileName2", "_SocketSkinFileName2"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(4);
        for (int c = 0; c < columns.Length; c++) { output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c])); output.Position = 14 + 128 * 64 + c * 4; w.Write(c == 0 ? 3 : 1); }
        output.Position = 8718;
        for (int row = 0; row < 4; row++)
        {
            w.Write(row + 700);
            object[] values = [row + 1, "Nano" + row, NanoBody, NanoTexture(row, "base"), row == 3 ? NanoTexture(row, "zero") : "", NanoTexture(row, "one"), NanoTexture(row, "two"), NanoSocket(1), NanoTexture(row, "socket1"), NanoSocket(2), NanoTexture(row, "socket2")];
            foreach (object value in values) if (value is int n) w.Write(n); else { byte[] bytes = Encoding.Unicode.GetBytes((string)value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
    static byte[] NanoModel(int[] slots, string[]? names = null)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write((ushort)1); w.Write((ushort)9); w.Write(new byte[16]); w.Write(25u);
        uint[] allocations = new uint[15]; allocations[2] = 1; allocations[3] = (uint)slots.Length; allocations[4] = 1; allocations[8] = (uint)slots.Length;
        foreach (uint value in allocations) w.Write(value);
        Str(w, ""); w.Write(1f); w.Write(0u); w.Write((uint)slots.Length);
        for (int piece = 0; piece < slots.Length; piece++)
        {
            int slot = slots[piece];
            Str(w, names is null ? "part-" + slot : names[piece]); w.Write(1u); w.Write(0f); w.Write(0u); w.Write(0u);
            w.Write(1u); w.Write(slot); w.Write(new byte[12]); w.Write(0); w.Write((byte)0); w.Write(4u); w.Write(102u);
            w.Write(3u); w.Write(1u); w.Write(1u); w.Write(1u); w.Write(0x11u); w.Write(new byte[12]); w.Write(0u);
            foreach (float[] vertex in new[] { new[] { 0f, 0f, 0f, 0f, 0f }, new[] { 1f, 0f, 0f, 1f, 0f }, new[] { 0f, 1f, 0f, 0f, 1f } }) foreach (float v in vertex) w.Write(v);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((byte)1); w.Write((byte)0);
        }
        Str(w, "Root"); w.Write((ushort)0); w.Write((byte)0); for (int i = 0; i < 16; i++) w.Write(i % 5 == 0 ? 1f : 0f); w.Write(0u);
        w.Write(0u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u); return output.ToArray();
    }
}
