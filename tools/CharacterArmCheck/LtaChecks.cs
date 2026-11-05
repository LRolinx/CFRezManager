using CFRezManager;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static partial class Program
{
    const string LtaMeshText = "(mesh \"Repeated\" (vertex ((0 0 0)(1 0 0)(0 1 0))) (tri-fs (0 1 2 0 2 1)) (uvs ((0 0)(1 0)(0 1)(0.5 0.5)(0.25 0.75)(0.75 0.25))) (tex-fs (0 1 2 3 4 5)))";
    static string LtaShape(string name, string mesh, string slots, string style = "0") => $"(shape \"{name}\" (geometry {mesh}) (texture-indices ({slots})) (renderstyle-index {style}))";
    static LithTechModelDocument LtaDocument(string text)
    {
        object?[] args = [text, "LtaCheck", "LTA text", text.Length, text.Length, null, null];
        Check("LTA model retains valid geometry", (bool)Call("LithTechModelDecoder", "TryParseLtaText", args)!);
        return (LithTechModelDocument)args[5]!;
    }
    static void RunLtaChecks(string run)
    {
        string text = "(lt-model-0 " + LtaShape("First", LtaMeshText, "0", "2") + LtaShape("Second", LtaMeshText, "3", "7") + LtaMeshText + ")";
        var document = LtaDocument(text); Check("LTA keeps repeated geometry names under separate shapes", document.Meshes.Count == 3 && document.Meshes.All(m => m.Name == "Repeated"));
        for (int index = 0; index < 2; index++)
        {
            var mesh = document.Meshes[index]; Check("LTA texture slots belong to the owning shape", mesh.HasAuthoritativeTextureBindings && mesh.TextureSlotIndices!.SequenceEqual(new[] { index == 0 ? 0 : 3 }) && mesh.RenderStyleIndex == (index == 0 ? 2 : 7));
            Check("LTA splits position/UV seams", mesh.Vertices.Count == 6 && mesh.TextureCoordinates!.Count == 6 && mesh.TriangleIndices.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }));
            var expectedPositions = new[] { new LithTechVector3(0, 0, 0), new LithTechVector3(1, 0, 0), new LithTechVector3(0, 1, 0), new LithTechVector3(0, 0, 0), new LithTechVector3(0, 1, 0), new LithTechVector3(1, 0, 0) };
            var expectedUvs = new[] { new LithTechVector2(0, 0), new LithTechVector2(1, 0), new LithTechVector2(0, 1), new LithTechVector2(.5, .5), new LithTechVector2(.25, .75), new LithTechVector2(.75, .25) };
            Check("LTA preserves every source corner position", mesh.TriangleIndices.Select(v => mesh.Vertices[v]).SequenceEqual(expectedPositions));
            Check("LTA preserves every explicit corner UV", mesh.TriangleIndices.Select(v => mesh.TextureCoordinates![v]).SequenceEqual(expectedUvs));
        }
        Check("LTA orphan meshes do not inherit another shape binding", !document.Meshes[2].HasAuthoritativeTextureBindings);
        string nested = "(lt-model-0 (shape \"Parent\" (texture-indices (1)) (geometry " + LtaMeshText + ") " + LtaShape("Child", LtaMeshText, "2") + "))";
        var nestedMeshes = LtaDocument(nested).Meshes; Check("LTA uses the nearest nested shape", nestedMeshes.Count == 2 && nestedMeshes[0].TextureSlotIndices!.SequenceEqual(new[] { 1 }) && nestedMeshes[1].TextureSlotIndices!.SequenceEqual(new[] { 2 }));
        foreach (string slots in new[] { "0 bad", "-1", "256", "0 1 2 3 4", "0.5" }) Check("LTA invalid slot list keeps geometry without authoritative bindings " + slots, !LtaDocument(LtaShape("Bad", LtaMeshText, slots)).Meshes[0].HasAuthoritativeTextureBindings);
        foreach (string style in new[] { "-1", "0 1", "invalid" }) Check("LTA invalid render style does not establish bindings " + style, !LtaDocument(LtaShape("Bad", LtaMeshText, "0", style)).Meshes[0].HasAuthoritativeTextureBindings);
        var empty = LtaDocument(LtaShape("Empty", LtaMeshText, "")).Meshes[0]; Check("LTA explicit empty slot list stays empty", empty.HasAuthoritativeTextureBindings && empty.TextureSlotIndices!.Count == 0);
        foreach (string faces in new[] { "0 1 99 3 4 5", "0 1 2", "0 1 bad 2 3 4 5" })
        {
            var invalid = LtaDocument(LtaShape("BadUv", LtaMeshText.Replace("tex-fs (0 1 2 3 4 5)", "tex-fs (" + faces + ")"), "0")).Meshes[0];
            Check("LTA invalid UV indices preserve geometry without inventing UVs", invalid.Vertices.Count == 3 && invalid.TriangleIndices.Count == 6 && !invalid.HasTextureCoordinates);
        }
        var badTriangle = LtaDocument(LtaShape("BadTriangle", LtaMeshText.Replace("tri-fs (0 1 2 0 2 1)", "tri-fs (0 1 99 0 2 1)").Replace("tex-fs (0 1 2 3 4 5)", "tex-fs (0 1 99 3 4 5)"), "0")).Meshes[0];
        Check("LTA invalid triangles do not shift the next face UVs", badTriangle.TriangleIndices.SequenceEqual(new[] { 0, 2, 1 }) && badTriangle.TriangleIndices.Select(v => badTriangle.TextureCoordinates![v]).SequenceEqual(new[] { new LithTechVector2(.5, .5), new LithTechVector2(.25, .75), new LithTechVector2(.75, .25) }));
        string legacyMesh = "(mesh \"Legacy\" (vertex ((0 0 0)(1 0 0)(0 1 0))) (tri-fs (0 1 2)) (uvs ((0 0)(1 0)(0 1))))";
        var legacy = LtaDocument(LtaShape("Legacy", legacyMesh, "0")).Meshes[0]; Check("LTA per-vertex UVs remain available without tex-fs", legacy.Vertices.Count == 3 && legacy.HasTextureCoordinates);
        RunRelativeTextureChecks(run, "(lt-model-0 " + LtaShape("First", LtaMeshText, "0") + LtaShape("Second", LtaMeshText, "3") + ")");
    }
    static void RunRelativeTextureChecks(string run, string lta)
    {
        string source = Path.Combine(run, "relative-source"), fixture = Guid.NewGuid().ToString("N");
        string[] models = ["UCC/ModelAssets/UCC_Model/LTA/Owned.lta", "Models/RelativeCheck/Native.ltb", "Models/RelativeCheck/InvalidReferences.ltb"];
        string Texture(int row, int slot) => $"Map2/RelativeCheck/{fixture}_{row}_{slot}.dtx";
        Directory.CreateDirectory(Path.Combine(source, "Table_UGC"));
        for (int i = 0; i < models.Length; i++) { string target = Path.Combine(source, models[i]); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, i == 0 ? Encoding.ASCII.GetBytes(lta) : NanoModel([0, 1, 2, 3])); }
        for (int row = 0; row < 3; row++) for (int slot = 0; slot < 4; slot++) { string target = Path.Combine(source, Texture(row, slot)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(10 + row * 30 + slot * 5))); }
        string[] columns = ["_ObjectName", "_ModelFileName1", "_TextureFileName1", "_TextureFileName2", "_TextureFileName3", "_TextureFileName4", "_Icon"];
        using var table = new MemoryStream(); using (var writer = new BinaryWriter(table, Encoding.UTF8, true))
        {
            writer.Write(new byte[8718]); table.Position = 0; writer.Write(Encoding.ASCII.GetBytes("su")); table.Position = 6; writer.Write(columns.Length); writer.Write(3);
            for (int c = 0; c < columns.Length; c++) { table.Position = 14 + c * 64; writer.Write(Encoding.ASCII.GetBytes(columns[c])); table.Position = 14 + 128 * 64 + c * 4; writer.Write(1); }
            table.Position = 8718;
            for (int row = 0; row < 3; row++)
            {
                writer.Write(row + 1000);
                string[] values = ["Relative" + row, models[row], row == 2 ? "../" + Texture(row, 0) : Texture(row, 0), row == 2 ? "C:/Outside/relative.dtx" : Texture(row, 1), Texture(row, 2), Texture(row, 3), "Map2/NotATextureField.dtx"];
                foreach (string value in values) { byte[] bytes = Encoding.Unicode.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
            }
            writer.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); writer.Write((byte)0);
        }
        byte[] data = table.ToArray(); File.WriteAllBytes(Path.Combine(source, "Table_UGC/UCCAssetModel.CFT"), data);
        var mappings = ((System.Collections.IEnumerable)Call("CftModelTextureMappingDecoder", "Extract", data)!).Cast<object>().Where(m => (bool)m.GetType().GetProperty("HasTypedRowContext")!.GetValue(m)!).ToArray(); Check("relative archive paths preserve three typed model rows", mappings.Length == 3);
        foreach (object mapping in mappings)
        {
            int row = (int)mapping.GetType().GetProperty("RowIndex")!.GetValue(mapping)!;var refs = ((System.Collections.IEnumerable)mapping.GetType().GetProperty("TextureReferences")!.GetValue(mapping)!).Cast<object>().ToArray();
            Check("relative references reject absolute/traversal and non-texture fields", refs.Length == (row == 2 ? 2 : 4));
        }
        for (int row = 0; row < 3; row++)
        {
            string obj = Path.Combine(run, "relative-" + row + ".obj"); TextWriter saved = Console.Out; int code;
            using (var log = new StringWriter())
            {
                try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", models[row], "--texture-context", $"row:UCCAssetModel.CFT#{row}", "--output", obj })!; }
                finally { Console.SetOut(saved); }
                File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
            }
            Check("relative texture CLI keeps geometry", code == 0); var rows = File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(l => l.Split('\t')).ToArray(); Check("LTA and native slots are reported", rows.Length == (row == 0 ? 2 : 4));
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]); if (row == 2 && slot < 2) { Check("unsafe relative field does not become a texture binding", r[8] != "exported" && r[7].Length == 0); continue; }
                Check("relative CLI selects the declared Map2 texture", r[8] == "exported" && r[5] == "context-slot" && Key(r[6]) == Key(Texture(row, slot)));
                Check("relative CLI preserves DTX pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, Texture(row, slot))).AsSpan(164, 4))));
            }
        }
    }
}
