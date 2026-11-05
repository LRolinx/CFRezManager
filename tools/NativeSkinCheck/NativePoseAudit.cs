using CFRezManager;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.IO;

static class NativePoseAudit
{
    static readonly Dictionary<string, string> Headers = new() {
        ["pose-sources.tsv"] = "source\tstored_sha256\tdecoded_sha256\tnodes\tanimations\tpose_samples\tstatus",
        ["poses.tsv"] = "source\tanimation_index\tanimation_name\tsample\tfrom_frame\tto_frame\tfraction_f32_hex\ttime_ms_f32_hex\troot_translation_f32_hex\tlocal_matrices_sha256\tglobal_matrices_sha256\tskin_matrices_sha256",
        ["pose-meshes.tsv"] = "source\tanimation_index\tsample\tmesh\tvertices\tcorner_overrides\tpoints\tpayload_offset\tpositions_sha256\tmin_position_f32_hex\tmax_position_f32_hex",
        ["pose-nodes.tsv"] = "source\tanimation_index\tsample\tnode_index\tlocal_matrix_f32_hex\tglobal_matrix_f32_hex\tskin_matrix_f32_hex"
    };
    public static void Reset(string output)
    {
        foreach (var report in Headers) File.WriteAllText(Path.Combine(output, report.Key), report.Value + "\n");
        File.WriteAllBytes(Path.Combine(output, "pose-vertices.bin"), []);
    }
    static List<Dictionary<string, string>> Read(string path)
    {
        string Field(string s) => s.StartsWith('"') && s.EndsWith('"') ? s[1..^1].Replace("\"\"", "\"") : s;
        var lines = File.ReadAllLines(path); string[] header = lines[0].Split('\t').Select(Field).ToArray();
        return lines.Skip(1).Select(line => {
            string[] values = line.Split('\t').Select(Field).ToArray();
            if (values.Length != header.Length) throw new InvalidDataException("Invalid report row.");
            return header.Zip(values).ToDictionary(p => p.First, p => p.Second);
        }).ToList();
    }
    static int Number(Dictionary<string, string> row, string field) => int.Parse(row[field], CultureInfo.InvariantCulture);
    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    static string Hex(params float[] values) => Convert.ToHexString(values.SelectMany(BitConverter.GetBytes).ToArray());
    static byte[] NativeMatrix(Matrix4x4 m) => new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43, m.M14, m.M24, m.M34, m.M44 }.SelectMany(BitConverter.GetBytes).ToArray();
    public static int Run(string root, string output, string report, Func<byte[], LithTechModelDocument> decode)
    {
        var marker = Read(Path.Combine(report, "inspection.tsv"));
        if (!marker.Any(r => r["inspection"] == "animation-channels" && r["requested"] == "true")) throw new InvalidDataException("Pose inspection needs a completed channel inspection.");
        var models = Read(Path.Combine(report, "models.tsv")).ToDictionary(r => r["source"]);
        var channels = Read(Path.Combine(report, "channels.tsv")).GroupBy(r => r["source"]).ToDictionary(g => g.Key, g => g.ToList());
        var nodes = Read(Path.Combine(report, "nodes.tsv")).GroupBy(r => r["source"]).ToDictionary(g => g.Key, g => g.OrderBy(r => Number(r, "node_index")).ToList());
        var animations = Read(Path.Combine(report, "animations.tsv")).GroupBy(r => r["source"]).ToDictionary(g => g.Key, g => g.ToList());
        var keys = Read(Path.Combine(report, "keyframes.tsv")).GroupBy(r => (r["source"], Number(r, "animation_index"))).ToDictionary(g => g.Key, g => g.OrderBy(r => Number(r, "keyframe_index")).ToList());
        var bindings = Read(Path.Combine(report, "animation-bindings.tsv"));
        var summaries = new List<string> { Headers["pose-sources.tsv"] };
        var poses = new List<string> { Headers["poses.tsv"] };
        var meshRows = new List<string> { Headers["pose-meshes.tsv"] };
        using var nodeRows = new StreamWriter(Path.Combine(output, "pose-nodes.tsv"), false, new UTF8Encoding(false));
        nodeRows.WriteLine(Headers["pose-nodes.tsv"]);
        using var binary = new BinaryWriter(File.Create(Path.Combine(output, "pose-vertices.bin")));
        var prepare = typeof(RezArchiveReader).Assembly.GetType("CFRezManager.LzmaAloneDecoder")!.GetMethod("TryPrepareData", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var resource in Read(Path.Combine(root, "resource-manifest.tsv")))
        {
            if (!resource["resource_path"].EndsWith(".ltb", StringComparison.OrdinalIgnoreCase)) continue;
            string source = resource["archive"].Replace('\\', '/') + ":" + resource["resource_path"];
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, resource["extracted_path"]));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Resource is outside its snapshot.");
            byte[] stored = File.ReadAllBytes(path); var model = models[source];
            byte[] data = (byte[]?)prepare.Invoke(null, [stored, 128L * 1024 * 1024]) ?? throw new InvalidDataException("Decompression failed.");
            if (Hash(stored) != resource["sha256"] || model["status"] != "structure-complete" || Hash(stored) != model["stored_sha256"] || Hash(data) != model["decoded_sha256"])
                throw new InvalidDataException("Pose source does not match its complete structure report.");
            var document = decode(stored); var skeleton = document.NativeSkeleton!; var nodeReport = nodes[source];
            if (skeleton.Count != nodeReport.Count || skeleton.Where((n, i) => n.Index != Number(nodeReport[i], "node_index") || n.ParentIndex != Number(nodeReport[i], "parent_index") || n.Name != nodeReport[i]["name"] || n.Flags != Number(nodeReport[i], "flags") || Hex(n.GlobalBindMatrix.ToArray()) != nodeReport[i]["global_bind_matrix_f32_hex"]).Any())
                throw new InvalidDataException("Skin and animation skeleton records differ.");
            var anims = animations.GetValueOrDefault(source, []); int samples = 0;
            if (anims.Count != Number(model, "parsed_animations")) throw new InvalidDataException("Incomplete animation report.");
            foreach (var animation in anims)
            {
                int index = Number(animation, "animation_index"), frames = Number(animation, "keyframes");
                if (frames == 0) throw new InvalidDataException("An animation without keys has no sampled pose.");
                var keyRows = keys[(source, index)];
                if (keyRows.Count != frames || keyRows.Where((r, i) => Number(r, "keyframe_index") != i).Any()) throw new InvalidDataException("Incomplete keyframe records.");
                var channelRows = channels[source].Where(r => Number(r, "animation_index") == index).ToList();
                if (channelRows.Count != skeleton.Count * 2 || channelRows.Any(r => r["kind"] is not ("position" or "rotation"))) throw new InvalidDataException("A sampled skeletal pose needs position/rotation channels for every node.");
                var lookup = channelRows.ToDictionary(r => (Number(r, "node_index"), r["kind"]));
                foreach (var channel in channelRows)
                {
                    int start = Number(channel, "decoded_byte_offset"), bytes = Number(channel, "payload_bytes"), count = Number(channel, "samples");
                    bool rotation = channel["kind"] == "rotation"; string expected = rotation ? "f32-quat" : "f32-vec3";
                    int size = channel["encoding"] == expected ? 4 : channel["encoding"] == (rotation ? "i16-quat-div32767" : "i16-vec3-div16") ? 2 : throw new InvalidDataException("Unsupported channel encoding.");
                    if (count != 0 && count != 1 && count != frames || bytes != checked(count * (rotation ? 4 : 3) * size) || start < 0 || bytes > data.Length - start || Hash(data.AsSpan(start, bytes).ToArray()) != channel["payload_sha256"])
                        throw new InvalidDataException("Invalid channel bounds, frame binding or payload hash.");
                }
                float[] Values(int node, string kind, int frame)
                {
                    var c = lookup[(node, kind)]; int count = Number(c, "samples"), n = kind == "position" ? 3 : 4;
                    if (count == 0) return n == 3 ? [0, 0, 0] : [0, 0, 0, 1];
                    bool quantized = c["encoding"].StartsWith("i16-"); int size = quantized ? 2 : 4;
                    int start = Number(c, "decoded_byte_offset") + (count == 1 ? 0 : frame) * n * size;
                    return Enumerable.Range(0, n).Select(i => quantized ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + i * size, 2)) / (n == 3 ? 16f : 32767f) : BitConverter.ToSingle(data, start + i * size)).ToArray();
                }
                LithTechNodeSample[] Frame(int frame) => skeleton.Select(n => {
                    float[] p = Values(n.Index, "position", frame), q = Values(n.Index, "rotation", frame);
                    return new LithTechNodeSample(n.Index, new(p[0], p[1], p[2]), new(q[0], q[1], q[2], q[3]));
                }).ToArray();
                var binding = bindings.Single(r => r["source"] == source && r["child_index"] == "0" && Number(r, "binding_index") == index);
                if (binding["animation_name"] != animation["name"]) throw new InvalidDataException("Self animation binding index/name differs.");
                byte[] translation = Convert.FromHexString(binding["translation_f32_hex"]);
                var shift = new Vector3(BitConverter.ToSingle(translation, 0), BitConverter.ToSingle(translation, 4), BitConverter.ToSingle(translation, 8));
                var selections = new List<(string Name, int From, int To, float Fraction)> { ("first", 0, 0, 0) };
                if (frames > 1)
                {
                    selections.Add(("last", frames - 1, frames - 1, 0));
                    int gap = Enumerable.Range(0, frames - 1).OrderByDescending(i => Number(keyRows[i + 1], "time_ms") - Number(keyRows[i], "time_ms")).First();
                    if (Number(keyRows[gap + 1], "time_ms") > Number(keyRows[gap], "time_ms")) selections.Add(("interval-midpoint", gap, gap + 1, .5f));
                }
                foreach (var selection in selections)
                {
                    var pose = LithTechPoseEvaluator.Evaluate(skeleton, Frame(selection.From), Frame(selection.To), selection.Fraction, shift);
                    byte[] local = pose.LocalTransforms.SelectMany(NativeMatrix).ToArray(), global = pose.GlobalTransforms.SelectMany(NativeMatrix).ToArray(), skin = pose.SkinTransforms.SelectMany(NativeMatrix).ToArray();
                    float time = Number(keyRows[selection.From], "time_ms") + (Number(keyRows[selection.To], "time_ms") - Number(keyRows[selection.From], "time_ms")) * selection.Fraction;
                    poses.Add(string.Join('\t', source, index, animation["name"], selection.Name, selection.From, selection.To, Hex(selection.Fraction), Hex(time), Hex(shift.X, shift.Y, shift.Z), Hash(local), Hash(global), Hash(skin)));
                    for (int n = 0; n < skeleton.Count; n++) nodeRows.WriteLine(string.Join('\t', source, index, selection.Name, n, Convert.ToHexString(local.AsSpan(n * 64, 64)), Convert.ToHexString(global.AsSpan(n * 64, 64)), Convert.ToHexString(skin.AsSpan(n * 64, 64))));
                    foreach (var mesh in document.Meshes)
                    {
                        var meshSkin = mesh.NativeSkinning!;
                        if (!meshSkin.IsDecoded) throw new InvalidDataException("Cannot pose an invalid skin binding.");
                        using var buffer = new MemoryStream(); using var w = new BinaryWriter(buffer); Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                        void Point(int v, LithTechVertexSkin s)
                        {
                            Vector3 p = LithTechPoseEvaluator.SkinVertex(mesh.Vertices[v], s, pose.SkinTransforms); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                        }
                        for (int v = 0; v < mesh.Vertices.Count; v++) Point(v, meshSkin.Vertices[v]);
                        foreach (var pair in meshSkin.CornerOverrides.OrderBy(p => p.Key)) Point(mesh.TriangleIndices[pair.Key], pair.Value);
                        byte[] positions = buffer.ToArray(); long offset = binary.BaseStream.Position; binary.Write(positions);
                        meshRows.Add(string.Join('\t', source, index, selection.Name, mesh.Name, mesh.Vertices.Count, meshSkin.CornerOverrides.Count, mesh.Vertices.Count + meshSkin.CornerOverrides.Count, offset, Hash(positions), Hex(min.X, min.Y, min.Z), Hex(max.X, max.Y, max.Z)));
                    }
                    samples++;
                }
            }
            string status = anims.Count == 0 ? "no-embedded-animation" : "sampled-single-animation; runtime-render-unverified";
            if (Number(model, "child_models") > 1) status += "; external-child-not-selected";
            summaries.Add(string.Join('\t', source, model["stored_sha256"], model["decoded_sha256"], skeleton.Count, anims.Count, samples, status));
        }
        File.WriteAllLines(Path.Combine(output, "poses.tsv"), poses); File.WriteAllLines(Path.Combine(output, "pose-meshes.tsv"), meshRows); File.WriteAllLines(Path.Combine(output, "pose-sources.tsv"), summaries);
        Console.WriteLine($"Sampled {poses.Count - 1} native poses; {meshRows.Count - 1} posed mesh records.");
        return 0;
    }
}
