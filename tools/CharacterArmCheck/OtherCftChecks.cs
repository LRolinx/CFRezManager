using CFRezManager;
using System.IO;
using System.Text;

static partial class Program
{
    static byte[] StringCft(string[] columns, string[][] rows, string? integerColumn = null)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write(new byte[8718]); output.Position = 0; w.Write(Encoding.ASCII.GetBytes("su")); output.Position = 6; w.Write(columns.Length); w.Write(rows.Length);
        for (int c = 0; c < columns.Length; c++) { output.Position = 14 + c * 64; w.Write(Encoding.ASCII.GetBytes(columns[c])); output.Position = 14 + 128 * 64 + c * 4; w.Write(columns[c] == integerColumn ? 3 : 1); }
        output.Position = 8718;
        for (int r = 0; r < rows.Length; r++)
        {
            w.Write(1200 + r);
            for (int c = 0; c < columns.Length; c++)
            {
                if (columns[c] == integerColumn) { w.Write(int.Parse(rows[r][c])); continue; }
                byte[] bytes = Encoding.Unicode.GetBytes(rows[r][c]); w.Write((ushort)bytes.Length); w.Write(bytes);
            }
        }
        w.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); w.Write((byte)0); return output.ToArray();
    }
    static Func<string, string, int, IReadOnlyList<string>> CftResolver(string source)
    {
        var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!; Call("LithTechObjExportCommand", "LoadAllArchives", root);
        return (Func<string, string, int, IReadOnlyList<string>>)Call("LithTechModelTextureConfigIndex", "CreateTextureContextSlotResolver", root)!;
    }
    static string[][] CftExport(string source, string model, string context, string obj)
    {
        TextWriter saved = Console.Out; int code;
        using (var log = new StringWriter())
        {
            try { Console.SetOut(log); code = (int)Call("LithTechObjExportCommand", "Run", (object)new string[] { "--export-obj", "--source-root", source, "--model", model, "--texture-context", context, "--output", obj })!; }
            finally { Console.SetOut(saved); }
            File.WriteAllText(Path.ChangeExtension(obj, ".log"), log.ToString());
        }
        Check("declared scalar/set CLI keeps geometry", code == 0);
        return File.ReadAllLines(Path.ChangeExtension(obj, null) + "_texture_slots.tsv").Skip(1).Select(l => l.Split('\t')).ToArray();
    }
    static void RunOtherCftChecks(string run)
    {
        string id = Guid.NewGuid().ToString("N");
        foreach (var schema in new[] { ("Parts.CFT", "_ModelFileName", "_ModelTexFileName"), ("MountedWeapon.CFT", "_TRIPOD_MODEL_PATH", "_TRIPOD_TEXTURE_PATH"), ("CharacterMotionData.CFT", "_3rdModelFile", "_3rdTextureFile") })
        {
            string source = Path.Combine(run, "scalar-" + schema.Item1); string model = "Models/ScalarCheck/UnrelatedModel.ltb";
            Directory.CreateDirectory(Path.Combine(source, "Table")); Directory.CreateDirectory(Path.Combine(source, "Models/ScalarCheck")); File.WriteAllBytes(Path.Combine(source, model), NanoModel([7]));
            string Texture(int row) => $"ModelTextures/ScalarCheck/{id}/{schema.Item1}_{row}.dtx";
            for (int row = 0; row < 2; row++) { string target = Path.Combine(source, Texture(row)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(20 + row * 50))); }
            string[][] values = Enumerable.Range(0, 3).Select(row => new[] { "Scalar" + row, model, Texture(row) }).ToArray();
            byte[] table = StringCft(["_ObjectName", schema.Item2, schema.Item3], values); File.WriteAllBytes(Path.Combine(source, "Table", schema.Item1), table);
            var resolver = CftResolver(source);
            for (int row = 0; row < 2; row++)
                foreach (string context in new[] { $"row:{schema.Item1}#{row}", "Scalar" + row, $"recordid:{schema.Item1}#{1200 + row}" })
                    Check(schema.Item1 + " unnumbered field selects the row's sole native slot", resolver(model, context, 7).Select(Key).SequenceEqual(new[] { Key(Texture(row)) }));
            Check(schema.Item1 + " missing selected scalar texture stays unresolved", resolver(model, $"row:{schema.Item1}#2", 7).Count == 0);
            Check(schema.Item1 + " scalar does not invent slot zero", resolver(model, $"row:{schema.Item1}#0", 0).Count == 0);
            for (int row = 0; row < 3; row++)
            {
                var rows = CftExport(source, model, $"row:{schema.Item1}#{row}", Path.Combine(run, schema.Item1 + "-" + row + ".obj")); Check("scalar CLI retains the sole native stage", rows.Length == 1 && rows[0][4] == "7");
                if (row == 2) { Check("missing scalar does not inherit another row's texture", rows[0][8] != "exported" && rows[0][7].Length == 0); continue; }
                Check("scalar CLI uses the declared field without a filename match", rows[0][5] == "context-slot" && rows[0][8] == "exported" && Key(rows[0][6]) == Key(Texture(row)));
                Check("scalar CLI preserves declared DTX pixels", PixelHash(Path.Combine(run, rows[0][7])) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(source, Texture(row))).AsSpan(164, 4))));
            }
            string copies = Path.Combine(run, "scalar-copies-" + schema.Item1); string duplicate = Path.Combine(copies, model); Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!); File.WriteAllBytes(duplicate, NanoModel([7]));
            void ArchiveCopies() => RezArchiveWriter.WriteFromDirectory(copies, Path.Combine(source, "copies.rez"));
            ArchiveCopies();
            Check("consistent scalar model copies keep the row binding", CftResolver(source)(model, $"row:{schema.Item1}#0", 7).Count == 1);
            File.WriteAllBytes(duplicate, NanoModel([8])); ArchiveCopies(); Check("different scalar model slots block binding", CftResolver(source)(model, $"row:{schema.Item1}#0", 7).Count == 0);
            File.WriteAllBytes(duplicate, [1, 2, 3]); ArchiveCopies(); Check("invalid scalar model copy blocks binding", CftResolver(source)(model, $"row:{schema.Item1}#0", 7).Count == 0);
            File.WriteAllBytes(duplicate, NanoModel([7]));
            string duplicateTexture = Path.Combine(copies, Texture(0)); Directory.CreateDirectory(Path.GetDirectoryName(duplicateTexture)!); File.WriteAllBytes(duplicateTexture, [1, 2, 3]); ArchiveCopies();
            var invalidRows = CftExport(source, model, $"row:{schema.Item1}#0", Path.Combine(run, schema.Item1 + "-invalid-copy.obj"));
            Check("invalid scalar texture copy prevents a written texture", invalidRows.Length == 2 && invalidRows.All(r => r[8] != "exported" && r[7].Length == 0));
            Check("another scalar row stays available after a texture conflict", CftResolver(source)(model, $"row:{schema.Item1}#1", 7).Count == 1);
            File.WriteAllBytes(Path.Combine(source, "Table", schema.Item1), StringCft(["_ObjectName", schema.Item2, schema.Item3, "_ExtraTextureFile"], values.Select(v => v.Append(Texture(1)).ToArray()).ToArray()));
            Check("multiple unnumbered textures do not become a scalar binding", CftResolver(source)(model, $"row:{schema.Item1}#1", 7).Count == 0);
        }
        string setRoot = Path.Combine(run, "set-role-source"), setModel = "Models/PLAYERVIEW/ArmModel/Arm_OtherSet_GR.ltb";
        Directory.CreateDirectory(Path.Combine(setRoot, "Table")); Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(setRoot, setModel))!);
        File.WriteAllBytes(Path.Combine(setRoot, setModel), NanoModel([0, 1], ["FVIEW_Hand_IronBeast2Arm", "FVIEW_Arm_IronBeast2Arm"]));
        string SetTexture(int row, string side, string role) => $"ModelTextures/SetCheck/{id}/{row}_{side}_{role}.dtx";
        var setRows = new List<string[]>();
        for (int row = 0; row < 2; row++)
        {
            foreach (string side in new[] { "GR", "BL" }) foreach (string role in new[] { "Arm", "Hand" }) { string target = Path.Combine(setRoot, SetTexture(row, side, role)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, SocketDtx((byte)(15 + row * 80 + (side == "GR" ? 10 : 30) + (role == "Hand" ? 5 : 0)))); }
            setRows.Add(["Set" + row, setModel, setModel, SetTexture(row, "GR", "Arm"), SetTexture(row, "BL", "Arm"), SetTexture(row, "GR", "Hand"), SetTexture(row, "BL", "Hand")]);
        }
        File.WriteAllBytes(Path.Combine(setRoot, "Table/NobleGoldSetInfo.CFT"), StringCft(["_ResourceName", "_GRPVArmModel", "_BLPVArmModel", "_GRPVArmTextureFile", "_BLPVArmTextureFile", "_GRPVHandTextureFile", "_BLPVHandTextureFile"], setRows.ToArray()));
        var setResolver = CftResolver(setRoot);
        for (int row = 0; row < 2; row++) foreach (string side in new[] { "GR", "BL" })
        {
            for (int slot = 0; slot < 2; slot++) foreach (string context in new[] { $"row:NobleGoldSetInfo.CFT#{row}:{side}", $"Set{row}:{side}", $"recordid:NobleGoldSetInfo.CFT#{1200 + row}:{side}" })
                Check("set asset suffix does not override the hand/arm role word", setResolver(setModel, context, slot).Select(Key).SequenceEqual(new[] { Key(SetTexture(row, side, slot == 0 ? "Hand" : "Arm")) }));
            var rows = CftExport(setRoot, setModel, $"row:NobleGoldSetInfo.CFT#{row}:{side}", Path.Combine(run, $"set-{row}-{side}.obj")); Check("set CLI keeps both native stages", rows.Length == 2);
            foreach (var r in rows) { string expected = SetTexture(row, side, r[4] == "0" ? "Hand" : "Arm"); Check("set CLI selects the declared side and row skin", r[5] == "context-slot" && r[8] == "exported" && Key(r[6]) == Key(expected)); Check("set CLI preserves declared skin pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(setRoot, expected)).AsSpan(164, 4)))); }
        }
        foreach (int slot in new[] { 0, 1 }) Check("shared set model needs an explicit side", setResolver(setModel, "row:NobleGoldSetInfo.CFT#0", slot).Count == 0);
        var vertices = new[] { new LithTechVector3(0, 0, 0), new LithTechVector3(1, 0, 0), new LithTechVector3(0, 1, 0) };
        foreach (string name in new[] { "hand_arm", "HandArm" })
        {
            var doc = new LithTechModelDocument("Ambiguous", [new LithTechMesh(name, vertices, [0, 1, 2], TextureSlotIndices: [0], HasAuthoritativeTextureBindings: true), new LithTechMesh("arm", vertices, [0, 1, 2], TextureSlotIndices: [1], HasAuthoritativeTextureBindings: true)], "fixture", 0, 0);
            object roles = Call("CftPlayerViewRoleSlotResolver", "Resolve", new[] { doc }, true, true)!;
            Check("ambiguous combined hand/arm role stays unbound", !((System.Collections.IEnumerable)roles.GetType().GetProperty("HandSlots")!.GetValue(roles)!).Cast<int>().Any());
        }
        RunChaserBaseChecks(run, id);
    }
    static void RunChaserBaseChecks(string run, string id)
    {
        string source = Path.Combine(run, "chaser-source"), model = "Models/ChaserCheck/Body.ltb", child = "Models/ChaserCheck/Animation.ltb";
        Directory.CreateDirectory(Path.Combine(source, "Table")); Directory.CreateDirectory(Path.Combine(source, "Models/ChaserCheck"));
        foreach (string path in new[] { model, child }) File.WriteAllBytes(Path.Combine(source, path), NanoModel([0, 1]));
        string Texture(int row, int slot) => $"ModelTextures/ChaserCheck/{id}/{row}_{slot}.dtx";
        for (int row = 0; row < 3; row++) for (int slot = 0; slot < 3; slot++) { string path = Path.Combine(source, Texture(row, slot)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, SocketDtx((byte)(10 + row * 50 + slot * 10))); }
        string[] columns = ["_ObjectName", "_ChaserType", "_ModelFileName", "_ChildModelFileName", "_SkinFileName", "_SkinFileName1", "_PVHandArmSkinFiles", "_PVHandArmSkinFiles1"];
        string[][] values = Enumerable.Range(0, 3).Select(row => new[] { "Chaser" + row, "0", model, child, row == 2 ? "" : Texture(row, 0), Texture(row, 1), Texture(row, 2), Texture(row, 2) }).ToArray();
        File.WriteAllBytes(Path.Combine(source, "Table/TigChaserTypes.CFT"), StringCft(columns, values, "_ChaserType"));
        var resolver = CftResolver(source);
        for (int row = 0; row < 3; row++) foreach (string path in new[] { model, child })
        {
            foreach (int slot in new[] { 0, 1 }) Check("Chaser base and numbered skins keep their native slots", resolver(path, $"row:TigChaserTypes.CFT#{row}", slot).Select(Key).SequenceEqual(row == 2 && slot == 0 ? Array.Empty<string>() : new[] { Key(Texture(row, slot)) }));
            var rows = CftExport(source, path, $"row:TigChaserTypes.CFT#{row}", Path.Combine(run, $"chaser-{row}-{Path.GetFileNameWithoutExtension(path)}.obj")); Check("Chaser CLI retains primary and child slots", rows.Length == 2);
            foreach (var r in rows)
            {
                int slot = int.Parse(r[4]);
                if (row == 2 && slot == 0) { Check("missing Chaser base stays unwritten without using PV skins", r[8] != "exported" && r[7].Length == 0); continue; }
                Check("Chaser CLI selects the body skin without mixing PV fields", r[5] == "context-slot" && r[8] == "exported" && Key(r[6]) == Key(Texture(row, slot)));
                Check("Chaser CLI preserves body and numbered skin pixels", PixelHash(Path.Combine(run, r[7])) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(source, Texture(row, slot))).AsSpan(164, 4))));
            }
        }
    }
}
