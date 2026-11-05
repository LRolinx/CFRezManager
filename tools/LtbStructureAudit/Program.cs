using CFRezManager;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Serialized layout: ModelPiece::Load, ModelNode::Load, ModelAnim::Load,
// LoadSockets and LoadAnimBindings in the LithTech D3D LTB v9/model v25 loader.
// Optional channel inspection decodes samples without evaluating animation poses.

if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--channels")
{
    Console.Error.WriteLine("Usage: LtbStructureAudit <extracted-resource-root> <output-directory> [--channels]");
    return 2;
}

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
bool inspectChannels = args.Length == 3;
string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (!Directory.Exists(root))
{
    Console.Error.WriteLine("Extracted resource root does not exist: " + root);
    return 2;
}
Directory.CreateDirectory(output);
var prepare = typeof(RezArchiveReader).Assembly.GetType("CFRezManager.LzmaAloneDecoder")!.GetMethod("TryPrepareData", BindingFlags.Public | BindingFlags.Static)!;
var summaries = new List<string> { "source\tversion\tallocated_nodes\tparsed_nodes\tallocated_animations\tparsed_animations\tsockets\thierarchy_sha256\tbind_pose_sha256\tstatus\tposition\tdecoded_bytes\tstored_sha256\tdecoded_sha256\tcommand\terror\tweight_sets\tchild_models\tanimation_bindings" };
var nodeRows = new List<string> { "source\tnode_index\tparent_index\tname\tflags\tglobal_bind_matrix_f32_hex" };
var animRows = new List<string> { "source\tanimation_index\tname\tcompression\tinterpolation_ms\tkeyframes\tlast_key_time_ms" };
var socketRows = new List<string> { "source\tsocket_index\tname\tnode_index\trotation_position_scale_f32_hex" };
var weightRows = new List<string> { "source\tweight_set_index\tname\tnode_count\tnode_weights_f32_hex" };
var childRows = new List<string> { "source\tchild_index\tis_self\tdeclared_path\tanimation_bindings" };
var bindingRows = new List<string> { "source\tchild_index\tbinding_index\tanimation_name\tdimensions_f32_hex\ttranslation_f32_hex\tself_name_match_count\tself_same_index_name_matches" };
var channelRows = new List<string> { "source\tanimation_index\tnode_index\tnode_name\tkind\tencoding\tsamples\tframe_binding\tdecoded_byte_offset\tpayload_bytes\tpayload_sha256\tfirst_value_f32_hex\tlast_value_f32_hex" };
var keyRows = new List<string> { "source\tanimation_index\tkeyframe_index\ttime_ms\tevent_text\tevent_cp949_hex" };
string manifestPath = Path.Combine(root, "resource-manifest.tsv");
IEnumerable<string[]> resources = File.Exists(manifestPath)
    ? File.ReadAllLines(manifestPath).Skip(1).Select(line => line.Split('\t'))
    : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path).Equals(".ltb", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select(path => new[] { "", Path.GetRelativePath(root, path), "", "", "", Path.GetRelativePath(root, path) });
int failures = 0;
foreach (string[] fields in resources)
{
    if (fields.Length != 6) throw new InvalidDataException("Expected the six-column extraction resource manifest.");
    if (!fields[1].EndsWith(".ltb", StringComparison.OrdinalIgnoreCase)) continue;
    string source = fields[0].Length == 0 ? fields[1].Replace('\\', '/') : fields[0].Replace('\\', '/') + ":" + fields[1];
    byte[] data = [];
    var c = new Cursor(data);
    string storedHash = "", decodedHash = "";
    var nodes = new List<Node>();
    var anims = new List<Anim>();
    var sockets = new List<Socket>();
    var weightSets = new List<WeightSet>();
    var childModels = new List<ChildModel>();
    var animationBindings = new List<AnimationBinding>();
    var channels = new List<Channel>();
    var keys = new List<Key>();
    uint[] alloc = new uint[15]; uint version = 0; string command = "", error = "", status = "header-invalid";
    try
    {
        string path = Path.GetFullPath(Path.Combine(root, fields[5]));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest file is outside the extracted resource root.");
        if (new FileInfo(path).Length > 128L * 1024 * 1024)
            throw new InvalidDataException("Stored LTB exceeds the 128 MiB limit.");
        byte[] stored = File.ReadAllBytes(path);
        storedHash = Convert.ToHexString(SHA256.HashData(stored));
        if (fields[4].Length > 0 && !fields[4].Equals(storedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored bytes differ from the extraction manifest SHA-256.");
        data = (byte[]?)prepare.Invoke(null, [stored, 128L * 1024 * 1024]) ?? throw new InvalidDataException("LTB decompression failed.");
        decodedHash = Convert.ToHexString(SHA256.HashData(data));
        c = new Cursor(data);
        if (c.U16() != 1 || c.U16() != 9) throw new InvalidDataException("Unsupported native D3D LTB header");
        c.Skip(16); version = c.U32();
        if (version != 25) throw new InvalidDataException("Only model version 25 has a validated structure layout.");
        for (int i = 0; i < 15; i++) alloc[i] = c.U32();
        if (alloc[2] is 0 or > 65536 || alloc[3] > 4096 || alloc[1] > 65536) throw new InvalidDataException("Allocation limits");
        command = c.Str(); c.Float(); uint obbs = c.U32();
        if (obbs != 0 && version != 25) throw new InvalidDataException("Unknown OBB version");
        c.Skip(checked((int)obbs * 68));
        uint pieces = c.U32(); if (pieces != alloc[3]) throw new InvalidDataException("Piece count mismatch");
        uint lodTotal = 0;
        for (uint i = 0; i < pieces; i++)
        {
            c.Str(); uint lods = c.U32(); if (lods is 0 or > 64) throw new InvalidDataException("LOD limit");
            lodTotal += lods; c.Skip(checked((int)lods * 4 + 8));
            for (uint lod = 0; lod < lods; lod++)
            {
                uint textures = c.U32(); if (textures > 4) throw new InvalidDataException("Texture count");
                c.Skip(16 + 4 + 1); c.U32(); uint size = c.U32(); c.Skip(checked((int)size));
                int used = c.Byte(); for (int n = 0; n < used; n++) if (c.Byte() >= alloc[2]) throw new InvalidDataException("Used node out of range");
            }
        }
        if (lodTotal != alloc[8]) throw new InvalidDataException("LOD count mismatch");
        status = "hierarchy-invalid";
        var indices = new HashSet<ushort>();
        ReadNode(c, -1, 0, alloc[2], nodes, indices);
        if (nodes.Count != alloc[2]) throw new InvalidDataException("Node count mismatch");
        status = "hierarchy-valid";
        uint weights = c.U32(); if (weights != alloc[10]) throw new InvalidDataException("Weight-set count mismatch");
        for (uint i = 0; i < weights; i++)
        {
            string name = c.Str(); uint count = c.U32(); if (count != alloc[2]) throw new InvalidDataException("Weight count mismatch");
            int start = c.Position; for (uint n = 0; n < count; n++) c.Float();
            weightSets.Add(new(i, name, count, Convert.ToHexString(data.AsSpan(start, checked((int)count * 4)))));
        }
        uint children = c.U32(); if (children != alloc[4] || children is 0 or > 4096) throw new InvalidDataException("Child-model count mismatch");
        // Index zero is the model itself; it has no serialized path string.
        childModels.Add(new(0, ""));
        for (uint i = 1; i < children; i++) childModels.Add(new(i, c.Str()));
        uint animations = c.U32(); if (animations != alloc[1]) throw new InvalidDataException($"Animation count mismatch: {animations} != {alloc[1]}");
        for (uint i = 0; i < animations; i++)
        {
            for (int n = 0; n < 3; n++) c.Float();
            string name = c.Str(); uint compression = c.U32(), interp = c.U32(), frames = c.U32();
            if (compression > 3 || frames > 1000000) throw new InvalidDataException("Animation limits");
            uint lastTime = 0;
            for (uint k = 0; k < frames; k++)
            {
                uint time = c.U32(); if (time < lastTime) throw new InvalidDataException("Unordered keys"); lastTime = time;
                int start = c.Position; string text = c.Str();
                if (inspectChannels) keys.Add(new(i, k, time, text, Convert.ToHexString(data.AsSpan(start + 2, c.Position - start - 2))));
            }
            foreach (Node node in nodes)
            {
                if (compression == 0)
                {
                    byte vertex = c.Byte(); if (vertex > 1) throw new InvalidDataException("Invalid vertex animation flag");
                    if (vertex == 0)
                    {
                        ReadChannel(c, i, node, "position", frames, frames, false, channels, inspectChannels);
                        ReadChannel(c, i, node, "rotation", frames, frames, false, channels, inspectChannels);
                    }
                    else
                    {
                        int start = c.Position;
                        for (uint k = 0; k < frames; k++)
                        {
                            uint count = c.U32(); if (count > 1000000) throw new InvalidDataException("Vertex count limit");
                            if (inspectChannels) for (uint v = 0; v < count; v++) { c.Float(); c.Float(); c.Float(); }
                            else c.Skip(checked((int)count * 12));
                        }
                        if (inspectChannels) channels.Add(new(i, node.Index, node.Name, "vertex", "f32-vec3-lists", frames, "per-frame-vertex-lists",
                            start, c.Position - start, Convert.ToHexString(SHA256.HashData(data.AsSpan(start, c.Position - start))), "", ""));
                    }
                }
                else
                {
                    uint count = c.U32(); if (count > Math.Max(1, frames)) throw new InvalidDataException("Position channel count");
                    ReadChannel(c, i, node, "position", count, frames, compression == 2, channels, inspectChannels);
                    count = c.U32(); if (count > Math.Max(1, frames)) throw new InvalidDataException("Rotation channel count");
                    ReadChannel(c, i, node, "rotation", count, frames, compression != 1, channels, inspectChannels);
                }
            }
            anims.Add(new(i, name, compression, interp, frames, lastTime));
        }
        uint socketCount = c.U32(); if (socketCount != alloc[9]) throw new InvalidDataException($"Socket count mismatch: {socketCount} != {alloc[9]}");
        for (uint i = 0; i < socketCount; i++)
        {
            uint node = c.U32(); if (node >= alloc[2]) throw new InvalidDataException("Socket node out of range");
            string name = c.Str(); int start = c.Position; for (int n = 0; n < 10; n++) c.Float();
            sockets.Add(new(i, name, node, Convert.ToHexString(data.AsSpan(start, 40))));
        }
        for (uint child = 0; child < children; child++)
        {
            uint bindings = c.U32(); if (bindings > 65536) throw new InvalidDataException("Animation binding count");
            for (uint i = 0; i < bindings; i++)
            {
                string name = c.Str(); int start = c.Position; for (int n = 0; n < 6; n++) c.Float();
                animationBindings.Add(new(child, i, name,
                    Convert.ToHexString(data.AsSpan(start, 12)), Convert.ToHexString(data.AsSpan(start + 12, 12))));
            }
        }
        if (data.Length - c.Position > 3 || data.AsSpan(c.Position).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Unexpected trailing bytes");
        status = "structure-complete";
    }
    catch (Exception ex) { error = ex.InnerException?.Message ?? ex.Message; }
    string hierarchy = status == "structure-complete" ? Hash(string.Join('\n', nodes.OrderBy(n => n.Index).Select(n => $"{n.Index}\t{n.Parent}\t{n.Name}\t{n.Flags}"))) : "";
    string pose = status == "structure-complete" ? Hash(string.Join('\n', nodes.OrderBy(n => n.Index).Select(n => n.Matrix))) : "";
    summaries.Add(string.Join('\t', source, version, alloc[2], nodes.Count, alloc[1], anims.Count, sockets.Count, hierarchy, pose, status, c.Position, data.Length, storedHash, decodedHash, Clean(command), Clean(error), weightSets.Count, childModels.Count, animationBindings.Count));
    if (status == "structure-complete")
    {
        foreach (var n in nodes) nodeRows.Add(string.Join('\t', source, n.Index, n.Parent, Clean(n.Name), n.Flags, n.Matrix));
        foreach (var a in anims) animRows.Add(string.Join('\t', source, a.Index, Clean(a.Name), a.Compression, a.Interpolation, a.Frames, a.Duration));
        foreach (var s in sockets) socketRows.Add(string.Join('\t', source, s.Index, Clean(s.Name), s.Node, s.Transform));
        foreach (var w in weightSets) weightRows.Add(string.Join('\t', source, w.Index, Clean(w.Name), w.Nodes, w.Weights));
        foreach (var child in childModels) childRows.Add(string.Join('\t', source, child.Index,
            child.Index == 0 ? "true" : "false", Clean(child.Path), animationBindings.Count(b => b.Child == child.Index)));
        var animationNameCounts = anims.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var b in animationBindings)
        {
            string matches = b.Child == 0 ? animationNameCounts.GetValueOrDefault(b.Name).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            string sameIndex = b.Child == 0
                ? (b.Index < anims.Count && string.Equals(anims[(int)b.Index].Name, b.Name, StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant()
                : "";
            bindingRows.Add(string.Join('\t', source, b.Child, b.Index, Clean(b.Name), b.Dimensions, b.Translation, matches, sameIndex));
        }
        foreach (var ch in channels) channelRows.Add(string.Join('\t', source, ch.Animation, ch.Node, Clean(ch.Name), ch.Kind, ch.Encoding, ch.Samples,
            ch.FrameBinding, ch.Offset, ch.Bytes, ch.Hash, Clean(ch.First), Clean(ch.Last)));
        foreach (var k in keys) keyRows.Add(string.Join('\t', source, k.Animation, k.Index, k.Time, Clean(k.Text), Clean(k.Raw)));
    }
    else failures++;
    Console.WriteLine($"{source}: {status}; nodes={nodes.Count}/{alloc[2]}, animations={anims.Count}/{alloc[1]}; {error}");
}
File.WriteAllLines(Path.Combine(output, "models.tsv"), summaries);
File.WriteAllLines(Path.Combine(output, "nodes.tsv"), nodeRows);
File.WriteAllLines(Path.Combine(output, "animations.tsv"), animRows);
File.WriteAllLines(Path.Combine(output, "sockets.tsv"), socketRows);
File.WriteAllLines(Path.Combine(output, "weight-sets.tsv"), weightRows);
File.WriteAllLines(Path.Combine(output, "child-models.tsv"), childRows);
File.WriteAllLines(Path.Combine(output, "animation-bindings.tsv"), bindingRows);
File.WriteAllLines(Path.Combine(output, "channels.tsv"), channelRows);
File.WriteAllLines(Path.Combine(output, "keyframes.tsv"), keyRows);
File.WriteAllLines(Path.Combine(output, "inspection.tsv"), ["inspection\trequested", "animation-channels\t" + inspectChannels.ToString().ToLowerInvariant()]);
return failures == 0 ? 0 : 1;

static void ReadNode(Cursor c, int parent, int depth, uint allocated, List<Node> nodes, HashSet<ushort> indices)
{
    if (depth >= 256 || nodes.Count >= allocated) throw new InvalidDataException("Node bounds");
    string name = c.Str(); ushort index = c.U16(); byte flags = c.Byte();
    if (index >= allocated || !indices.Add(index)) throw new InvalidDataException("Invalid node index");
    int start = c.Position; for (int i = 0; i < 16; i++) c.Float();
    string matrix = Convert.ToHexString(c.Data.AsSpan(start, 64)); uint count = c.U32();
    if (count > allocated - nodes.Count - 1) throw new InvalidDataException("Child count bounds");
    nodes.Add(new(index, parent, name, flags, matrix));
    for (uint i = 0; i < count; i++) ReadNode(c, index, depth + 1, allocated, nodes, indices);
}
static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static void ReadChannel(Cursor c, uint animation, Node node, string kind, uint count, uint frames, bool quantized, List<Channel> channels, bool inspect)
{
    int components = kind == "position" ? 3 : 4;
    int start = c.Position, bytes = checked((int)count * components * (quantized ? 2 : 4));
    if (!inspect) { c.Skip(bytes); return; }
    if (count > 1 && count != frames) throw new InvalidDataException("Partial animation channel has no supported frame binding.");
    byte[] first = new byte[components * 4], last = new byte[components * 4];
    if (count == 0 && kind == "rotation") BitConverter.GetBytes(1f).CopyTo(first, 12);
    first.CopyTo(last, 0);
    for (uint sample = 0; sample < count; sample++)
    for (int component = 0; component < components; component++)
    {
        float value = quantized ? unchecked((short)c.U16()) / (kind == "position" ? 16f : 32767f) : c.Float();
        if (sample == 0) BitConverter.GetBytes(value).CopyTo(first, component * 4);
        if (sample == count - 1) BitConverter.GetBytes(value).CopyTo(last, component * 4);
    }
    string encoding = quantized ? kind == "position" ? "i16-vec3-div16" : "i16-quat-div32767" : kind == "position" ? "f32-vec3" : "f32-quat";
    channels.Add(new(animation, node.Index, node.Name, kind, encoding, count, count == 0 ? "default" : count == 1 ? "constant" : "per-keyframe",
        start, bytes, Convert.ToHexString(SHA256.HashData(c.Data.AsSpan(start, bytes))), Convert.ToHexString(first), Convert.ToHexString(last)));
}
static string Clean(string text)
{
    string value = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    return value.Length == 0 || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
record Node(ushort Index, int Parent, string Name, byte Flags, string Matrix);
record Anim(uint Index, string Name, uint Compression, uint Interpolation, uint Frames, uint Duration);
record Socket(uint Index, string Name, uint Node, string Transform);
record WeightSet(uint Index, string Name, uint Nodes, string Weights);
record ChildModel(uint Index, string Path);
record AnimationBinding(uint Child, uint Index, string Name, string Dimensions, string Translation);
record Channel(uint Animation, uint Node, string Name, string Kind, string Encoding, uint Samples, string FrameBinding, int Offset, int Bytes, string Hash, string First, string Last);
record Key(uint Animation, uint Index, uint Time, string Text, string Raw);
class Cursor(byte[] data)
{
    public byte[] Data => data; public int Position { get; private set; }
    public void Skip(int count) { if (count < 0 || (long)Position + count > data.Length) throw new EndOfStreamException($"Read beyond {Position}"); Position += count; }
    public byte Byte() { int p = Position; Skip(1); return data[p]; }
    public ushort U16() { int p = Position; Skip(2); return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2)); }
    public uint U32() { int p = Position; Skip(4); return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4)); }
    public float Float() { float f = BitConverter.UInt32BitsToSingle(U32()); if (!float.IsFinite(f)) throw new InvalidDataException("Non-finite float"); return f; }
    public string Str() { int n = U16(); if (n > 4096) throw new InvalidDataException("String bound"); int p = Position; Skip(n); return Encoding.GetEncoding(949).GetString(data, p, n).TrimEnd('\0'); }
}
