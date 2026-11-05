using CFRezManager;
using System.IO;
using System.Reflection;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

static class Program
{
    static readonly Type Decoder = typeof(RezArchiveReader).Assembly.GetType("CFRezManager.LithTechModelDecoder")!;
    static readonly List<string> Checks = ["check\tresult"];
    static void Check(string name, bool ok) { if (!ok) throw new Exception("FAILED: " + name); Checks.Add(name + "\tpassed"); }
    static LithTechModelDocument Decode(byte[] data, bool skin = true)
    {
        object?[] args = skin ? [data, "fixture.ltb", null, null] : [data, "fixture.ltb", "ltb", null, null];
        MethodInfo method = skin ? Decoder.GetMethod("TryDecodeWithSkinning")! : Decoder.GetMethods().Single(m => m.Name == "TryDecode" && m.GetParameters().Length == 5);
        if (!(bool)method.Invoke(null, args)!) throw new InvalidDataException((string?)args[^1]);
        return (LithTechModelDocument)args[^2]!;
    }
    static int Main(string[] args)
    {
        string output = "artifacts/native-skin-check", resources = "", poses = ""; bool vertices = false;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else if (args[i] == "--resources" && i + 1 < args.Length) resources = args[++i];
            else if (args[i] == "--vertices") vertices = true;
            else if (args[i] == "--poses" && i + 1 < args.Length) poses = args[++i];
            else throw new ArgumentException("Expected --resources <snapshot>, --output <directory>, --vertices or --poses <channel-report>.");
        if (poses.Length > 0 && resources.Length == 0) throw new ArgumentException("Pose inspection requires a resource snapshot.");
        Directory.CreateDirectory(output);
        RunChecks(); File.WriteAllLines(Path.Combine(output, "checks.tsv"), Checks);
        Console.WriteLine($"{Checks.Count - 1} native skin checks passed.");
        if (resources.Length > 0) NativePoseAudit.Reset(output);
        int result = resources.Length == 0 ? 0 : Audit(resources, output, vertices, poses.Length > 0);
        return result != 0 || poses.Length == 0 ? result : NativePoseAudit.Run(resources, output, poses, data => Decode(data));
    }
    static void RunChecks()
    {
        void Case(string name, Fixture fixture, Action<LithTechModelDocument> inspect)
        {
            var doc = Decode(fixture.Data); Check(name + " preserves geometry and slots", Geometry(doc.Meshes[0]) == Geometry(Decode(fixture.Data, false).Meshes[0]));
            Check(name + " captures every skeleton index", doc.NativeSkeleton!.Count == fixture.NodeCount && doc.NativeSkeleton.Select(n => n.Index).SequenceEqual(Enumerable.Range(0, fixture.NodeCount)));
            inspect(doc);
        }
        Case("direct", Make(), d => {
            var s = d.Meshes[0].NativeSkinning!; Check("direct draw decoded", s.IsDecoded && s.Method == "draw-direct");
            Check("stored weights map to first slots, last is implicit", s.Vertices[0] == new LithTechVertexSkin(0, 1, 2, -1, .2f, .3f, .5f, 0));
        });
        Case("rigid", Make(mode: "rigid"), d => Check("rigid effector applies to every vertex", d.Meshes[0].NativeSkinning!.Vertices.All(v => v.Node0 == 2 && v.Weight0 == 1)));
        Case("palette global", Make(mode: "global", nodes: 8), d => {
            var s = d.Meshes[0].NativeSkinning!; Check("palette indices do not add the minimum", s.IsDecoded && s.Vertices[0].Node0 == 5 && s.Vertices[0].Node1 == 6 && s.Vertices[0].Weight1 == .75f);
        });
        Case("palette remapped", Make(mode: "remap"), d => Check("palette remaps the raw slot directly", d.Meshes[0].NativeSkinning!.IsDecoded && d.Meshes[0].NativeSkinning!.Vertices[0].Node0 == 2 && d.Meshes[0].NativeSkinning!.Vertices[0].Node1 == 1));
        Case("palette 255", Make(mode: "large", nodes: 300), d => Check("palette slot 255 and global node 299 are retained", d.Meshes[0].NativeSkinning!.IsDecoded && d.Meshes[0].NativeSkinning!.Vertices[0].Node0 == 299 && d.Meshes[0].NativeSkinning!.Vertices[0].Node1 == 299));
        Case("overlapping draw ranges", Make(mode: "overlap"), d => {
            var s = d.Meshes[0].NativeSkinning!; Check("shared vertex keeps per-corner bindings", s.IsDecoded && s.RequiresVertexSplit && s.CornerOverrides.Count == 2);
            Check("range overlap does not overwrite unrelated triangle vertices", s.Vertices[1].Node0 == 1 && s.GetCornerSkin(3, 0).Node0 == 2 && s.GetCornerSkin(0, 0).Node0 == 1);
        });
        Case("equivalent overlap", Make(mode: "same-overlap"), d => Check("matching draw bindings need no vertex split", d.Meshes[0].NativeSkinning!.IsDecoded && !d.Meshes[0].NativeSkinning!.RequiresVertexSplit));
        Case("unused vertex", Make(mode: "single"), d => Check("undrawn vertices have no invented draw binding", d.Meshes[0].NativeSkinning!.VerticesWithoutDrawBinding == 1 && d.Meshes[0].NativeSkinning!.Vertices[3].Node0 == -1));
        Case("no normals single bone", Make(mode: "single", normals: false), d => Check("single bone needs no stored weights", d.Meshes[0].NativeSkinning!.IsDecoded));
        Case("duplicate streams", Make(duplicate: true), d => Check("matching skin streams are accepted", d.Meshes[0].NativeSkinning!.IsDecoded));
        Case("finite negative implicit weight", Make(weights: [.8f, .7f]), d => Check("weights are not clamped or normalized", d.Meshes[0].NativeSkinning!.IsDecoded && d.Meshes[0].NativeSkinning!.HasWeightsOutsideUnitInterval && d.Meshes[0].NativeSkinning!.Vertices[0].Weight2 == -.5f));
        void Bad(string name, Fixture f, int offset, byte[] value)
        {
            byte[] changed = (byte[])f.Data.Clone(); value.CopyTo(changed, offset);
            var d = Decode(changed); Check(name + " leaves static geometry available", Geometry(d.Meshes[0]) == Geometry(Decode(changed, false).Meshes[0]));
            Check(name + " is an explicit skin error", d.Meshes[0].NativeSkinning is { IsDecoded: false, Error.Length: > 0 });
        }
        var direct = Make();
        Bad("NaN stored weight", direct, direct.Weights, BitConverter.GetBytes(float.NaN));
        Bad("out-of-range bone", direct, direct.Bones, [9]);
        Bad("empty active bone set", direct, direct.Bones, [255, 255, 255, 255]);
        Bad("non-triangle group boundary", direct, direct.IndexEnd, BitConverter.GetBytes(4u));
        Bad("incomplete triangle coverage", direct, direct.IndexEnd, BitConverter.GetBytes(3u));
        Bad("vertex range excludes a referenced index", direct, direct.FirstVertex, BitConverter.GetBytes((ushort)1));
        Bad("rigid effector outside skeleton", Make(mode: "rigid"), Make(mode: "rigid").Effector, BitConverter.GetBytes(3u));
        var palette = Make(mode: "global", nodes: 8);
        Bad("palette slot outside range", palette, palette.PaletteIndices, [7]);
        var remap = Make(mode: "remap"); Bad("weighted remap outside skeleton", remap, remap.Remap + 5 * 4, BitConverter.GetBytes(50u));
        var duplicate = Make(duplicate: true); Bad("conflicting weight streams", duplicate, duplicate.SecondWeights, BitConverter.GetBytes(.5f));
        var missing = Make(normals: false); Check("missing weight stream is an explicit error", !Decode(missing.Data).Meshes[0].NativeSkinning!.IsDecoded);
        var wrongSkeleton = (byte[])direct.Data.Clone(); BitConverter.GetBytes((ushort)0).CopyTo(wrongSkeleton, direct.SecondNodeIndex);
        void Reject(string name, byte[] data) { object?[] invalid = [data, "bad.ltb", null, null]; Check(name, !(bool)Decoder.GetMethod("TryDecodeWithSkinning")!.Invoke(null, invalid)!); }
        Reject("duplicate skeleton indices reject full capture", wrongSkeleton);
        var badMatrix = (byte[])direct.Data.Clone(); BitConverter.GetBytes(float.PositiveInfinity).CopyTo(badMatrix, direct.SecondNodeIndex + 3);
        Reject("non-finite bind matrix rejects full capture", badMatrix);
        var oldVersion = (byte[])direct.Data.Clone(); BitConverter.GetBytes(24u).CopyTo(oldVersion, 20);
        Reject("skin capture requires validated model version", oldVersion);
        Check("unsupported skin version retains ordinary geometry decoding", Decode(oldVersion, false).Meshes.Count == 1);
        var reordered = (byte[])direct.Data.Clone(); BitConverter.GetBytes((ushort)2).CopyTo(reordered, direct.SecondNodeIndex); BitConverter.GetBytes((ushort)1).CopyTo(reordered, direct.ThirdNodeIndex);
        var reorderedDoc = Decode(reordered);
        Check("skeleton lookup uses serialized indices rather than traversal order", reorderedDoc.NativeSkeleton![1].Name == "Bone2" && reorderedDoc.NativeSkeleton![2].Name == "Bone1");
        var noPieces = direct.Data[..(direct.PieceCount + 4)].Concat(direct.Data[direct.SkeletonStart..]).ToArray();
        foreach (int offset in new[] { 24 + 3 * 4, 24 + 8 * 4, direct.PieceCount }) BitConverter.GetBytes(0u).CopyTo(noPieces, offset);
        var skeletonOnly = Decode(noPieces);
        Check("skeleton-only capture retains nodes without inventing meshes", skeletonOnly.Meshes.Count == 0 && skeletonOnly.NativeSkeleton!.Count == 3);
        Check("default decode does not capture skin arrays", Decode(direct.Data, false) is { NativeSkeleton: null } normal && normal.Meshes.All(m => m.NativeSkinning is null));
        RunPoseChecks();
    }
    static void RunPoseChecks()
    {
        LithTechSkeletonNode Node(int index, int parent, Vector3 p, byte flags = 0)
            => new(index, parent, "Bone" + index, flags, [1, 0, 0, p.X, 0, 1, 0, p.Y, 0, 0, 1, p.Z, 0, 0, 0, 1]);
        LithTechNodeSample Sample(int index, Vector3 p, Quaternion? q = null) => new(index, p, q ?? Quaternion.Identity);
        bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .0001f;
        var nodes = new[] { Node(0, 1, new(10, 2, 0)), Node(1, -1, new(10, 0, 0)) };
        var bind = new[] { Sample(1, new(10, 0, 0)), Sample(0, new(0, 2, 0)) };
        var binding = new LithTechVertexSkin(0, -1, -1, -1, 1, 0, 0, 0);
        Vector3 Skin(LithTechNativePose pose) => LithTechPoseEvaluator.SkinVertex(new(10, 3, 0), binding, pose.SkinTransforms);
        var bindPose = LithTechPoseEvaluator.Evaluate(nodes, bind, bind, 0);
        Check("bind pose preserves source vertex", Near(Skin(bindPose), new(10, 3, 0)));
        var z90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var frame = new[] { Sample(0, new(0, 2, 0)), Sample(1, new(20, 0, 0), z90) };
        var pose = LithTechPoseEvaluator.Evaluate(nodes.Reverse().ToArray(), frame, frame, 0);
        Check("pose composes parent rotation and child translation by index", Near(pose.GlobalTransforms[0].Translation, new(18, 0, 0)));
        Check("skin applies inverse bind before animated global transform", Near(Skin(pose), new(17, 0, 0)));
        var shifted = LithTechPoseEvaluator.Evaluate(nodes, frame, frame, 0, new(5, 0, 0));
        Check("animation translation applies once at the root", Near(Skin(shifted), new(22, 0, 0)));
        var rotationOnly = new[] { nodes[0] with { Flags = 2 }, nodes[1] };
        var movedChild = new[] { Sample(0, new(999, 999, 999)), frame[1] };
        Check("rotation-only child uses its parent-relative bind offset", Near(Skin(LithTechPoseEvaluator.Evaluate(rotationOnly, movedChild, movedChild, 0)), new(17, 0, 0)));
        var rotatedBind = new[] {
            nodes[0] with { GlobalBindMatrix = new float[] { 0, -1, 0, 8, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } },
            nodes[1] with { GlobalBindMatrix = new float[] { 0, -1, 0, 10, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } } };
        var rotatedSamples = new[] { Sample(0, new(0, 2, 0)), Sample(1, new(10, 0, 0), z90) };
        var rotatedPose = LithTechPoseEvaluator.Evaluate(rotatedBind, rotatedSamples, rotatedSamples, 0);
        Check("rotated inverse bind preserves its source-space vertex", Near(LithTechPoseEvaluator.SkinVertex(new(7, 0, 0), binding, rotatedPose.SkinTransforms), new(7, 0, 0)));
        var rotatedOnly = new[] { rotatedBind[0] with { Flags = 2 }, rotatedBind[1] };
        Check("rotation-only offset accounts for rotated parent bind basis", Near(LithTechPoseEvaluator.Evaluate(rotatedOnly, movedChild, movedChild, 0).GlobalTransforms[0].Translation, new(18, 0, 0)));
        var rootOnly = new[] { Node(0, -1, Vector3.Zero, 2) };
        var moveRoot = new[] { Sample(0, new(999, 999, 999)) };
        Check("rotation-only root ignores translation and animation shift", LithTechPoseEvaluator.Evaluate(rootOnly, moveRoot, moveRoot, 0, Vector3.One).GlobalTransforms[0].Translation == Vector3.Zero);
        var single = new[] { Node(0, -1, Vector3.Zero) };
        var left = new[] { Sample(0, Vector3.Zero) }; var right = new[] { Sample(0, new(10, 4, 0)) };
        Check("positions interpolate linearly", Near(LithTechPoseEvaluator.Evaluate(single, left, right, .25f).GlobalTransforms[0].Translation, new(2.5f, 1, 0)));
        Vector3 Turn(Quaternion a, Quaternion b, float f) => Vector3.Transform(Vector3.UnitX, LithTechPoseEvaluator.Evaluate(single, [Sample(0, Vector3.Zero, a)], [Sample(0, Vector3.Zero, b)], f).GlobalTransforms[0]);
        var a170 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 170 / 180); var b170 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI * 170 / 180);
        Check("quaternion interpolation takes the shortest arc", Near(Turn(a170, b170, .5f), -Vector3.UnitX));
        Check("opposite quaternion signs preserve the same rotation", Near(Turn(z90, -z90, .5f), Vector3.UnitY));
        Check("non-unit quaternion magnitude does not scale geometry", Near(Turn(z90 * 3, z90 * 3, 0), Vector3.UnitY));
        Check("nearby quaternion interpolation retains a finite rotation", Near(Turn(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .001f), .5f), new(MathF.Cos(.0005f), MathF.Sin(.0005f), 0)));
        var weighted = new LithTechVertexSkin(0, 1, -1, -1, .25f, .75f, 0, 0);
        Check("CPU skin blends transformed vertices without normalization", Near(LithTechPoseEvaluator.SkinVertex(new(0, 0, 0), weighted, [Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.CreateTranslation(4, 0, 0)]), new(3.5f, 0, 0)));
        Check("CPU skin retains finite negative influences", Near(LithTechPoseEvaluator.SkinVertex(new(0, 0, 0), weighted with { Weight0 = 1.5f, Weight1 = -.5f }, [Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.CreateTranslation(4, 0, 0)]), new(1, 0, 0)));
        var shared = Decode(Make(mode: "overlap").Data).Meshes[0]; var sharedSkin = shared.NativeSkinning!;
        Matrix4x4[] sharedPose = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(2, 0, 0)];
        Check("CPU skin respects different palettes on shared triangle corners", Near(LithTechPoseEvaluator.SkinVertex(shared.Vertices[0], sharedSkin.GetCornerSkin(0, 0), sharedPose), new(1, 0, 0))
            && Near(LithTechPoseEvaluator.SkinVertex(shared.Vertices[0], sharedSkin.GetCornerSkin(3, 0), sharedPose), new(2, 0, 0)));
        void Bad(string name, Action run) { bool rejected = false; try { run(); } catch (InvalidDataException) { rejected = true; } Check(name, rejected); }
        Bad("pose rejects duplicate samples", () => LithTechPoseEvaluator.Evaluate(nodes, [bind[0], bind[0]], bind, 0));
        Bad("pose rejects missing samples", () => LithTechPoseEvaluator.Evaluate(nodes, [bind[0]], bind, 0));
        Bad("pose rejects missing root", () => LithTechPoseEvaluator.Evaluate([nodes[0], nodes[1] with { ParentIndex = 0 }], bind, bind, 0));
        Bad("pose rejects disconnected parent cycle", () => LithTechPoseEvaluator.Evaluate([Node(0, -1, default), Node(1, 2, default), Node(2, 1, default)], [Sample(0, default), Sample(1, default), Sample(2, default)], [Sample(0, default), Sample(1, default), Sample(2, default)], 0));
        Bad("pose rejects multiple roots", () => LithTechPoseEvaluator.Evaluate([nodes[0] with { ParentIndex = -1 }, nodes[1]], bind, bind, 0));
        Bad("pose rejects zero quaternions even at the other endpoint", () => LithTechPoseEvaluator.Evaluate(single, [new(0, default, default)], left, 1));
        Bad("pose rejects non-finite samples", () => LithTechPoseEvaluator.Evaluate(single, [Sample(0, new(float.NaN, 0, 0))], left, 0));
        Bad("pose rejects extrapolation", () => LithTechPoseEvaluator.Evaluate(single, left, left, 1.1f));
        Bad("pose rejects non-finite fractions", () => LithTechPoseEvaluator.Evaluate(single, left, left, float.NaN));
        Bad("CPU skin rejects unbound vertices", () => LithTechPoseEvaluator.SkinVertex(new(0, 0, 0), new(-1, -1, -1, -1, 0, 0, 0, 0), pose.SkinTransforms));
        Bad("CPU skin rejects weighted nodes outside pose", () => LithTechPoseEvaluator.SkinVertex(new(0, 0, 0), binding with { Node0 = 5 }, pose.SkinTransforms));
    }
    sealed record Fixture(byte[] Data, int NodeCount, int Weights, int SecondWeights, int Bones, int IndexEnd, int FirstVertex, int PaletteIndices, int Remap, int Effector, int SecondNodeIndex, int ThirdNodeIndex, int PieceCount, int SkeletonStart);
    static Fixture Make(string mode = "direct", int nodes = 3, bool normals = true, bool duplicate = false, float[]? weights = null)
    {
        bool rigid = mode == "rigid", palette = mode is "global" or "remap" or "large", reindex = mode is "remap" or "large";
        bool one = mode is "overlap" or "same-overlap" or "single";
        int stored = rigid || one ? 0 : palette ? 1 : 2;
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        void Str(string value) { byte[] b = Encoding.ASCII.GetBytes(value); w.Write((ushort)b.Length); w.Write(b); }
        w.Write((ushort)1); w.Write((ushort)9); w.Write(new byte[16]); w.Write(25u);
        uint[] alloc = new uint[15]; alloc[2] = (uint)nodes; alloc[3] = 1; alloc[4] = 1; alloc[8] = 1; foreach (uint value in alloc) w.Write(value);
        Str(""); w.Write(1f); w.Write(0u); int pieceCountOffset = (int)output.Position;
        w.Write(1u); Str("SkinMesh"); w.Write(1u); w.Write(0f); w.Write(0u); w.Write(0u);
        w.Write(1u); w.Write(new byte[16]); w.Write(0u); w.Write((byte)0); w.Write(rigid ? 4u : 5u);
        int sizeOffset = (int)output.Position; w.Write(0u); int body = (int)output.Position;
        w.Write(4u); w.Write(mode == "single" ? 1u : 2u); w.Write((uint)(rigid || one ? 1 : palette ? 2 : 3)); w.Write((uint)(rigid || one ? 1 : palette ? 2 : 3));
        if (!rigid) w.Write(reindex);
        uint flags = normals ? 0x13u : 0x11u;
        w.Write(flags); w.Write(duplicate ? flags : 0u); w.Write(0u); w.Write(0u);
        int effector = -1, remapOffset = -1;
        if (rigid) { effector = (int)output.Position; w.Write(2u); }
        else
        {
            w.Write(palette);
            if (palette)
            {
                w.Write(mode == "large" ? 255u : 5u); w.Write(mode == "large" ? 255u : 6u);
                if (reindex)
                {
                    int count = mode == "large" ? 256 : 7; w.Write((uint)count); remapOffset = (int)output.Position;
                    for (int i = 0; i < count; i++) w.Write(mode == "large" ? (i == 255 ? 299u : uint.MaxValue) : i == 5 ? 2u : i == 6 ? 1u : uint.MaxValue);
                }
            }
        }
        int firstWeight = -1, secondWeight = -1, paletteIndex = -1;
        for (int stream = 0; stream < (duplicate ? 2 : 1); stream++) for (int v = 0; v < 4; v++)
        {
            w.Write((float)(v % 2)); w.Write((float)(v / 2)); w.Write(0f);
            if (normals)
            {
                if (v == 0) { if (stream == 0) firstWeight = (int)output.Position; else secondWeight = (int)output.Position; }
                for (int slot = 0; slot < stored; slot++) w.Write(weights?[slot] ?? (palette ? .25f : slot == 0 ? .2f : .3f));
                if (palette) { if (paletteIndex < 0) paletteIndex = (int)output.Position; w.Write(mode == "large" ? new byte[] { 255, 255, 0, 0 } : new byte[] { 5, 6, 0, 0 }); }
                w.Write(0f); w.Write(0f); w.Write(1f);
            }
            w.Write((float)(v % 2)); w.Write((float)(v / 2));
        }
        foreach (ushort index in mode == "single" ? new ushort[] { 0, 1, 2 } : new ushort[] { 0, 1, 2, 0, 2, 3 }) w.Write(index);
        int boneOffset = -1, endOffset = -1, vertexOffset = -1;
        if (!rigid && !palette)
        {
            bool overlap = mode is "overlap" or "same-overlap"; w.Write(overlap ? 2u : 1u);
            for (int group = 0; group < (overlap ? 2 : 1); group++)
            {
                vertexOffset = (int)output.Position; w.Write((ushort)0); w.Write((ushort)4); boneOffset = (int)output.Position;
                w.Write(one ? new byte[] { (byte)(group == 1 && mode == "overlap" ? 2 : 1), 255, 255, 255 } : new byte[] { 0, 1, 2, 255 });
                endOffset = (int)output.Position; w.Write((uint)(mode == "single" ? 3 : overlap && group == 0 ? 3 : 6));
            }
        }
        int end = (int)output.Position; output.Position = sizeOffset; w.Write((uint)(end - body)); output.Position = end;
        w.Write((byte)1); w.Write((byte)0); int skeletonStart = (int)output.Position, secondNode = -1, thirdNode = -1;
        for (int node = 0; node < nodes; node++)
        {
            Str(node == 0 ? "Root" : "Bone" + node); if (node == 1) secondNode = (int)output.Position;
            if (node == 2) thirdNode = (int)output.Position;
            w.Write((ushort)node); w.Write((byte)0); for (int i = 0; i < 16; i++) w.Write(i % 5 == 0 ? 1f : 0f);
            w.Write(node == 0 ? (uint)(nodes - 1) : 0u);
        }
        w.Write(0u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u);
        return new(output.ToArray(), nodes, firstWeight, secondWeight, boneOffset, endOffset, vertexOffset, paletteIndex, remapOffset, effector, secondNode, thirdNode, pieceCountOffset, skeletonStart);
    }
    static string Geometry(LithTechMesh mesh)
    {
        using var data = new MemoryStream(); using var w = new BinaryWriter(data);
        w.Write(mesh.Name); foreach (var v in mesh.Vertices) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (int i in mesh.TriangleIndices) w.Write(i);
        for (int set = 0; set < 4; set++) if (mesh.GetTextureCoordinateSet(set) is { } uv) foreach (var v in uv) { w.Write(set); w.Write(v.X); w.Write(v.Y); }
        foreach (int slot in mesh.TextureSlotIndices ?? []) w.Write(slot); w.Write(mesh.RenderStyleIndex ?? -1); w.Write(mesh.HasAuthoritativeTextureBindings);
        return Convert.ToHexString(SHA256.HashData(data.ToArray()));
    }
    static int Audit(string root, string output, bool vertices, bool poses)
    {
        var meshes = new List<string> { "source\tmesh\tmethod\tvertices\ttriangles\tnodes\tdraws\tcorner_overrides\twithout_draw_binding\toutside_unit_interval\tgeometry_sha256\tskin_sha256\tweight_stream\tweight_decoded_byte_offset\tweight_vertex_stride\tstored_weights_per_vertex\tpalette_min\tpalette_max\tpalette_remap\terror" };
        var draws = new List<string> { "source\tmesh\tdraw\tindex_start\tindex_count\tvertex_first\tvertex_count\tnodes" };
        var nodeRows = new List<string> { "source\tnode_index\tparent_index\tname\tflags\tglobal_bind_matrix_f32_hex" };
        var usageRows = new List<string> { "source\tmesh\tnode_index\tnode_name\tparent_index\tweighted_vertices\tweighted_corners\tmin_weight_f32_hex\tmax_weight_f32_hex" };
        using var vertexRows = new StreamWriter(Path.Combine(output, "vertices.tsv"), false, new UTF8Encoding(false));
        using var cornerRows = new StreamWriter(Path.Combine(output, "corner-overrides.tsv"), false, new UTF8Encoding(false));
        vertexRows.WriteLine("source\tmesh\tvertex\tnodes_i32_hex\tweights_f32_hex");
        cornerRows.WriteLine("source\tmesh\tcorner\tvertex\tnodes_i32_hex\tweights_f32_hex");
        File.WriteAllText(Path.Combine(output, "inspection.tsv"), "inspection\trequested\nvertex-values\t" + (vertices ? "true" : "false") + "\nnative-poses\t" + (poses ? "true" : "false") + "\n");
        int failed = 0;
        foreach (string line in File.ReadLines(Path.Combine(root, "resource-manifest.tsv")).Skip(1))
        {
            string[] row = line.Split('\t'); if (!row[1].EndsWith(".ltb", StringComparison.OrdinalIgnoreCase)) continue;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, row[5])); if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Resource path is outside the snapshot.");
            byte[] data = File.ReadAllBytes(path); if (Convert.ToHexString(SHA256.HashData(data)) != row[4]) throw new InvalidDataException("Stored resource hash differs from its manifest.");
            string source = row[0].Replace('\\', '/') + ":" + row[1]; var document = Decode(data);
            var normal = document.Meshes.Count > 0 ? Decode(data, false) : null;
            foreach (var n in document.NativeSkeleton!) nodeRows.Add(string.Join('\t', source, n.Index, n.ParentIndex, n.Name, n.Flags, Convert.ToHexString(n.GlobalBindMatrix.SelectMany(BitConverter.GetBytes).ToArray())));
            for (int index = 0; index < document.Meshes.Count; index++)
            {
                var mesh = document.Meshes[index]; var skin = mesh.NativeSkinning!; string geometryHash = Geometry(mesh);
                if (geometryHash != Geometry(normal!.Meshes[index])) throw new InvalidDataException("Capturing skin data changed geometry or texture slots.");
                using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer);
                for (int v = 0; v < skin.Vertices.Count; v++)
                {
                    var s = skin.Vertices[v]; byte[] bones = Enumerable.Range(0, 4).SelectMany(k => BitConverter.GetBytes(s.GetNode(k))).ToArray();
                    byte[] weights = Enumerable.Range(0, 4).SelectMany(k => BitConverter.GetBytes(s.GetWeight(k))).ToArray();
                    w.Write(bones); w.Write(weights); if (vertices) vertexRows.WriteLine(string.Join('\t', source, mesh.Name, v, Convert.ToHexString(bones), Convert.ToHexString(weights)));
                }
                foreach (var pair in skin.CornerOverrides.OrderBy(p => p.Key))
                {
                    w.Write(pair.Key); for (int slot = 0; slot < 4; slot++) { w.Write(pair.Value.GetNode(slot)); w.Write(pair.Value.GetWeight(slot)); }
                    if (vertices) cornerRows.WriteLine(string.Join('\t', source, mesh.Name, pair.Key, mesh.TriangleIndices[pair.Key],
                        Convert.ToHexString(Enumerable.Range(0, 4).SelectMany(k => BitConverter.GetBytes(pair.Value.GetNode(k))).ToArray()),
                        Convert.ToHexString(Enumerable.Range(0, 4).SelectMany(k => BitConverter.GetBytes(pair.Value.GetWeight(k))).ToArray())));
                }
                var usage = new Dictionary<int, (HashSet<int> Vertices, HashSet<int> Corners, float Min, float Max)>();
                void Use(LithTechVertexSkin s, int vertex, int corner)
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        float weight = s.GetWeight(slot); if (weight == 0) continue;
                        int node = s.GetNode(slot);
                        if (!usage.TryGetValue(node, out var u)) u = ([], [], weight, weight);
                        u.Vertices.Add(vertex); if (corner >= 0) u.Corners.Add(corner);
                        u.Min = Math.Min(u.Min, weight); u.Max = Math.Max(u.Max, weight); usage[node] = u;
                    }
                }
                for (int v = 0; v < skin.Vertices.Count; v++) Use(skin.Vertices[v], v, -1);
                if (skin.IsDecoded) for (int corner = 0; corner < mesh.TriangleIndices.Count; corner++)
                    Use(skin.GetCornerSkin(corner, mesh.TriangleIndices[corner]), mesh.TriangleIndices[corner], corner);
                foreach (var pair in usage.OrderBy(p => p.Key))
                {
                    var n = document.NativeSkeleton!.Single(n => n.Index == pair.Key); var u = pair.Value;
                    usageRows.Add(string.Join('\t', source, mesh.Name, pair.Key, n.Name, n.ParentIndex, u.Vertices.Count, u.Corners.Count,
                        Convert.ToHexString(BitConverter.GetBytes(u.Min)), Convert.ToHexString(BitConverter.GetBytes(u.Max))));
                }
                for (int draw = 0; draw < skin.Draws.Count; draw++) { var d = skin.Draws[draw]; draws.Add(string.Join('\t', source, mesh.Name, draw, d.IndexStart, d.IndexCount, d.FirstVertex, d.VertexCount, string.Join(',', d.Nodes))); }
                meshes.Add(string.Join('\t', source, mesh.Name, skin.Method, mesh.Vertices.Count, mesh.TriangleIndices.Count / 3, document.NativeSkeleton!.Count, skin.Draws.Count,
                    skin.CornerOverrides.Count, skin.VerticesWithoutDrawBinding, skin.HasWeightsOutsideUnitInterval, geometryHash, Convert.ToHexString(SHA256.HashData(buffer.ToArray())),
                    skin.WeightStreamIndex, skin.WeightDataOffset, skin.WeightVertexStride, skin.StoredWeightsPerVertex, skin.PaletteMin, skin.PaletteMax,
                    skin.PaletteRemap is null ? "\"\"" : string.Join(',', skin.PaletteRemap), skin.Error is null ? "\"\"" : skin.Error));
                if (!skin.IsDecoded) failed++;
            }
        }
        File.WriteAllLines(Path.Combine(output, "meshes.tsv"), meshes); File.WriteAllLines(Path.Combine(output, "draws.tsv"), draws); File.WriteAllLines(Path.Combine(output, "nodes.tsv"), nodeRows);
        File.WriteAllLines(Path.Combine(output, "bone-usage.tsv"), usageRows);
        Console.WriteLine($"Captured {meshes.Count - 1} meshes; {draws.Count - 1} draw groups; invalid skin bindings={failed}");
        return failed == 0 ? 0 : 1;
    }
}
