using CFRezManager;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static partial class Program
{
    const string SocketGr = "Models/SocketItem/ItemCheck/Shared_GR.ltb";
    const string SocketBl = "Models/SocketItem/ItemCheck/Shared_BL.ltb";
    static string SocketTexture(int row, string side, int slot) => $"ModelTextures/SocketItem/ItemCheck/Row{row}_{slot}_{(side == "GR" ? "BL" : "GR")}.dtx";
    static byte[] SocketDtx(byte color)
    {
        byte[] data = new byte[168]; BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), -5);
        foreach (int offset in new[] { 8, 10, 12 }) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), 1);
        data[26] = 3; data[164] = color; data[165] = 50; data[166] = 200; data[167] = 255; return data;
    }
    static void RunSocketChecks(string run)
    {
        string source = Path.Combine(run, "socket-source");
        void Save(string path, byte[] data) { string file = Path.Combine(source, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, data); }
        Save(SocketGr, Model()); Save(SocketBl, Model()); Save("Table/CharacterItem.CFT", SocketTable());
        for (int row = 0; row < 3; row++) foreach (string side in new[] { "GR", "BL" }) foreach (int slot in new[] { 0, 1 }) Save(SocketTexture(row, side, slot), SocketDtx((byte)(row * 20 + (side == "BL" ? 10 : 0) + slot + 1)));
        foreach (string wrong in new[] { "BadFemale_GR", "BadFemale_BL", "BadSecondary_GR", "OtherSecondary_GR" }) Save("ModelTextures/SocketItem/ItemCheck/" + wrong + ".dtx", SocketDtx(240));
        var mappings = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", SocketTable())!).Cast<object>().Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray();
        Check("socket fixtures retain separate and shared model rows", mappings.Length == 4);
        foreach (object mapping in mappings)
        {
            int row = (int)mapping.GetType().GetProperty("RowIndex")!.GetValue(mapping)!;
            string model = (string)mapping.GetType().GetProperty("ModelReference")!.GetValue(mapping)!;
            var refs = ((System.Collections.IEnumerable)mapping.GetType().GetProperty("TextureReferences")!.GetValue(mapping)!).Cast<object>().ToArray();
            string[] columns = refs.Select(r => (string)r.GetType().GetProperty("Column")!.GetValue(r)!).ToArray();
            string[] expected = row == 0 ? model == SocketGr ? ["_GRTexFile", "_GRTexFile2"] : ["_BLTexFile", "_BLTexFile2"] : row == 1 ? ["_GRTexFile", "_GRTexFile2", "_BLTexFile", "_BLTexFile2"] : ["_GRTexFile2", "_BLTexFile2"];
            Check("socket columns determine texture roles and sides", columns.Order().SequenceEqual(expected.Order()));
        }
        var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!; Call("LithTechObjExportCommand", "LoadAllArchives", root);
        var resolver = (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", root)!;
        for (int row = 0; row < 3; row++) foreach (string side in new[] { "GR", "BL" })
        {
            string model = row == 0 && side == "BL" ? SocketBl : SocketGr;
            foreach (string context in new[] { $"row:CharacterItem.CFT#{row}:{side}", $"Socket{row}:{side}", $"recordid:CharacterItem.CFT#{row + 600}:{side}" })
            foreach (int slot in row == 2 ? new[] { 1 } : new[] { 0, 1 })
                Check("socket side selector " + context + " slot " + slot, resolver(model, context, slot).Select(Key).SequenceEqual(new[] { Key(SocketTexture(row, side, slot)) }));
            string obj = Path.Combine(run, $"socket-{row}-{side}.obj"); TextWriter saved = Console.Out; int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", model, "--texture-context", $"row:CharacterItem.CFT#{row}:{side}", "--output", obj })!; }
                finally { Console.SetOut(saved); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            var rows = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(line => line.Split('\t')).ToArray();
            Check("socket CLI keeps both meshes", code == 0 && rows.Length == 2);
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]);
                if (row == 2 && slot == 0) { Check("socket CLI does not invent an absent base texture", r[8] != "exported" && r[7].Length == 0); continue; }
                string expected = SocketTexture(row, side, slot);
                Check("socket CLI uses the declared field despite filename suffix", r[5] == "context-slot" && r[8] == "exported" && Key(r[6]) == Key(expected));
                Check("socket CLI preserves original DTX pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, expected)).AsSpan(164, 4))));
            }
        }
        foreach (int slot in new[] { 0, 1 }) Check("shared socket model requires a side", resolver(SocketGr, "row:CharacterItem.CFT#1", slot).Count == 0);
        foreach (string side in new[] { "GR", "BL" }) foreach (int slot in new[] { 0, 1 })
            Check("unique socket side also keeps the unqualified row selector", resolver(side == "GR" ? SocketGr : SocketBl, "row:CharacterItem.CFT#0", slot).Select(Key).SequenceEqual(new[] { Key(SocketTexture(0, side, slot)) }));
    }
    static byte[] SocketTable()
    {
        string[] columns = ["_ItemName", "_CharacterType", "_GRSocketItemModelFile", "_BLSocketItemModelFile", "_GRFSocketItemModelFile", "_BLFSocketItemModelFile", "_GRTexFile", "_GRTexFile2", "_BLTexFile", "_BLTexFile2", "_SocketSecondModelFile", "_SecondTexFile"];
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(3);
        for (int c = 0; c < columns.Length; c++) { output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c])); output.Position = 14 + 128 * 64 + c * 4; w.Write(c == 1 ? 3 : 1); }
        output.Position = 8718;
        for (int row = 0; row < 3; row++)
        {
            w.Write(row + 600);
            object[] values = ["Socket" + row, 0, SocketGr, row == 0 ? SocketBl : SocketGr,
                row == 0 ? "ModelTextures/SocketItem/ItemCheck/BadFemale_GR.dtx" : SocketGr,
                row == 0 ? "ModelTextures/SocketItem/ItemCheck/BadFemale_BL.dtx" : SocketGr,
                row == 2 ? "" : SocketTexture(row, "GR", 0), SocketTexture(row, "GR", 1), row == 2 ? "" : SocketTexture(row, "BL", 0), SocketTexture(row, "BL", 1),
                row == 0 ? "ModelTextures/SocketItem/ItemCheck/BadSecondary_GR.dtx" : "", row == 0 ? "ModelTextures/SocketItem/ItemCheck/OtherSecondary_GR.dtx" : ""];
            foreach (var value in values) if (value is int n) w.Write(n); else { byte[] bytes = Encoding.Unicode.GetBytes((string)value); w.Write((ushort)bytes.Length); w.Write(bytes); }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
}
