using System.Buffers.Binary;
using System.IO;

namespace CFRezManager;

public sealed record LithTechSkeletonNode(int Index, int ParentIndex, string Name, byte Flags, IReadOnlyList<float> GlobalBindMatrix);

public readonly record struct LithTechVertexSkin(int Node0, int Node1, int Node2, int Node3, float Weight0, float Weight1, float Weight2, float Weight3)
{
    public int GetNode(int slot) => slot switch { 0 => Node0, 1 => Node1, 2 => Node2, 3 => Node3, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
    public float GetWeight(int slot) => slot switch { 0 => Weight0, 1 => Weight1, 2 => Weight2, 3 => Weight3, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
}

public sealed record LithTechSkinDraw(int IndexStart, int IndexCount, int FirstVertex, int VertexCount, IReadOnlyList<int> Nodes);

public sealed record LithTechSkinBinding(string Method, IReadOnlyList<LithTechVertexSkin> Vertices)
{
    public string? Error { get; init; }
    public bool IsDecoded => Error is null;
    public int WeightStreamIndex { get; init; } = -1;
    public int WeightDataOffset { get; init; } = -1;
    public int WeightVertexStride { get; init; }
    public int StoredWeightsPerVertex { get; init; }
    public uint PaletteMin { get; init; }
    public uint PaletteMax { get; init; }
    public IReadOnlyList<uint>? PaletteRemap { get; init; }
    public IReadOnlyList<LithTechSkinDraw> Draws { get; init; } = [];
    public IReadOnlyDictionary<int, LithTechVertexSkin> CornerOverrides { get; init; } = new Dictionary<int, LithTechVertexSkin>();
    public int VerticesWithoutDrawBinding { get; init; }
    public bool HasWeightsOutsideUnitInterval { get; init; }
    public bool RequiresVertexSplit => CornerOverrides.Count > 0;
    public LithTechVertexSkin GetCornerSkin(int corner, int vertex) => CornerOverrides.TryGetValue(corner, out var skin) ? skin : Vertices[vertex];
}

internal static class LithTechNativeSkinReader
{
    public static LithTechSkinBinding Read(ReadOnlySpan<byte> data, int start, int end, uint objectType, int nodeCount, LithTechMesh mesh)
    {
        string method = objectType == 4 ? "rigid" : "draw-direct";
        try
        {
            var c = new Cursor(data, start, end);
            int vertices = checked((int)c.U32()), triangles = checked((int)c.U32());
            uint maxTriangle = c.U32(), maxVertex = c.U32();
            if (vertices != mesh.Vertices.Count || checked(triangles * 3) != mesh.TriangleIndices.Count) throw new InvalidDataException("Geometry counts differ from the decoded mesh.");
            bool reindexed = objectType == 5 && c.Byte() != 0;
            uint[] flags = [c.U32(), c.U32(), c.U32(), c.U32()];
            int rigidNode = -1; bool palette = false; uint min = 0, max = 0; uint[]? remap = null;
            if (objectType == 4) rigidNode = checked((int)c.U32());
            else
            {
                byte value = c.Byte(); if (value > 1) throw new InvalidDataException("Invalid matrix palette flag.");
                palette = value == 1;
                if (palette)
                {
                    method = reindexed ? "palette-remapped" : "palette-global";
                    min = c.U32(); max = c.U32();
                    if (min > max || max > byte.MaxValue || maxVertex is < 2 or > 4) throw new InvalidDataException("Invalid matrix palette range or influence count.");
                    if (reindexed)
                    {
                        uint count = c.U32(); if (count > 10000) throw new InvalidDataException("Bone remap count exceeds the supported bound.");
                        remap = new uint[count]; for (int i = 0; i < remap.Length; i++) remap[i] = c.U32();
                    }
                }
                else if (maxTriangle is < 1 or > 4) throw new InvalidDataException("Invalid direct influence count.");
            }
            int weightCount = objectType == 4 ? 0 : checked((int)(palette ? maxVertex : maxTriangle) - 1);
            int prefix = weightCount * 4 + (palette ? 4 : 0);
            int weightStream = -1, weightOffset = -1, weightStride = 0;
            float[] weights = new float[checked(vertices * weightCount)];
            byte[] paletteIndices = palette ? new byte[checked(vertices * 4)] : [];
            for (int stream = 0; stream < 4; stream++)
            {
                if (flags[stream] == 0) continue;
                if (!LithTechNativeLtbDecoder.TryGetStreamLayout(flags[stream], prefix, out var layout)) throw new InvalidDataException("Invalid skin vertex layout.");
                int streamStart = c.Position; c.Skip(checked(vertices * layout.Stride));
                if ((flags[stream] & 3) != 3 || objectType == 4) continue;
                for (int vertex = 0; vertex < vertices; vertex++)
                {
                    var v = new Cursor(data, streamStart + vertex * layout.Stride, end);
                    float x = v.Float(), y = v.Float(), z = v.Float();
                    if (x != mesh.Vertices[vertex].X || y != mesh.Vertices[vertex].Y || z != mesh.Vertices[vertex].Z) throw new InvalidDataException("Skin and geometry position streams disagree.");
                    for (int slot = 0; slot < weightCount; slot++)
                    {
                        float value = v.Float(); int index = vertex * weightCount + slot;
                        if (weightStream >= 0 && weights[index] != value) throw new InvalidDataException("Conflicting skin weight streams.");
                        weights[index] = value;
                    }
                    if (palette) for (int slot = 0; slot < 4; slot++)
                    {
                        byte value = v.Byte(); int index = vertex * 4 + slot;
                        if (weightStream >= 0 && paletteIndices[index] != value) throw new InvalidDataException("Conflicting palette index streams.");
                        paletteIndices[index] = value;
                    }
                }
                if (weightStream < 0) { weightStream = stream; weightOffset = streamStart + 12; weightStride = layout.Stride; }
            }
            if (objectType == 5 && (palette || weightCount > 0) && weightStream < 0) throw new InvalidDataException("No position/normal stream contains the stored skin data.");
            c.Skip(checked(triangles * 6));
            var skins = new LithTechVertexSkin[vertices];
            Array.Fill(skins, new(-1, -1, -1, -1, 0, 0, 0, 0));
            var assigned = new bool[vertices]; var overrides = new Dictionary<int, LithTechVertexSkin>(); var draws = new List<LithTechSkinDraw>();
            bool outsideUnit = false;
            if (objectType == 4)
            {
                if (rigidNode < 0 || rigidNode >= nodeCount) throw new InvalidDataException("Rigid bone effector is outside the skeleton.");
                Array.Fill(skins, new(rigidNode, -1, -1, -1, 1, 0, 0, 0)); Array.Fill(assigned, true);
                draws.Add(new(0, mesh.TriangleIndices.Count, 0, vertices, [rigidNode]));
            }
            else if (palette)
            {
                Span<int> boneSlots = stackalloc int[4];
                for (int vertex = 0; vertex < vertices; vertex++)
                {
                    boneSlots.Fill(-1);
                    for (int slot = 0; slot < (int)maxVertex; slot++)
                    {
                        int index = paletteIndices[vertex * 4 + slot];
                        // The bytes address palette slots directly; min is a range bound, not an offset.
                        boneSlots[slot] = index < min || index > max || remap is not null && index >= remap.Length ? -1
                            : remap is null ? index : remap[index] < nodeCount ? (int)remap[index] : -1;
                    }
                    skins[vertex] = MakeSkin(weights.AsSpan(vertex * weightCount, weightCount), boneSlots[..(int)maxVertex], nodeCount, ref outsideUnit);
                    assigned[vertex] = true;
                }
                draws.Add(new(0, mesh.TriangleIndices.Count, 0, vertices, []));
            }
            else
            {
                uint setCount = c.U32(); if (setCount > 1000000) throw new InvalidDataException("Bone-set count exceeds the supported bound.");
                int indexStart = 0;
                Span<int> bones = stackalloc int[4];
                for (uint set = 0; set < setCount; set++)
                {
                    int first = c.U16(), count = c.U16();
                    bones.Fill(-1); int active = 0; bool terminated = false;
                    for (int slot = 0; slot < 4; slot++)
                    {
                        int value = c.Byte(); if (value == 255) terminated = true;
                        if (!terminated) bones[active++] = value;
                    }
                    int indexEnd = checked((int)c.U32());
                    if (indexEnd < indexStart || indexEnd > mesh.TriangleIndices.Count || (indexEnd - indexStart) % 3 != 0 || first + count > vertices)
                        throw new InvalidDataException("Invalid bone-set vertex or triangle range.");
                    if (indexEnd > indexStart && (active == 0 || active > maxTriangle)) throw new InvalidDataException("Invalid active bone count for a draw.");
                    draws.Add(new(indexStart, indexEnd - indexStart, first, count, bones[..active].ToArray()));
                    for (int corner = indexStart; corner < indexEnd; corner++)
                    {
                        int vertex = mesh.TriangleIndices[corner];
                        if (vertex < first || vertex >= first + count) throw new InvalidDataException("Draw index is outside its declared vertex range.");
                        var skin = MakeSkin(weights.AsSpan(vertex * weightCount, weightCount), bones[..active], nodeCount, ref outsideUnit);
                        if (!assigned[vertex]) { skins[vertex] = skin; assigned[vertex] = true; }
                        else if (!Equivalent(skins[vertex], skin)) overrides.Add(corner, skin);
                    }
                    indexStart = indexEnd;
                }
                if (indexStart != mesh.TriangleIndices.Count) throw new InvalidDataException("Bone draws do not cover every triangle.");
            }
            if (end - c.Position > 3 || data[c.Position..end].IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Unexpected skin object tail.");
            return new(method, skins) { WeightStreamIndex = weightStream, WeightDataOffset = weightOffset, WeightVertexStride = weightStride,
                StoredWeightsPerVertex = weightCount, PaletteMin = min, PaletteMax = max, PaletteRemap = remap, Draws = draws,
                CornerOverrides = overrides, VerticesWithoutDrawBinding = assigned.Count(v => !v), HasWeightsOutsideUnitInterval = outsideUnit };
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            return new(method, []) { Error = ex.Message };
        }
    }

    private static LithTechVertexSkin MakeSkin(ReadOnlySpan<float> stored, ReadOnlySpan<int> bones, int nodeCount, ref bool outsideUnit)
    {
        Span<float> values = stackalloc float[4]; values.Clear();
        Span<int> nodes = stackalloc int[4]; nodes.Fill(-1);
        float sum = 0;
        for (int slot = 0; slot < bones.Length; slot++)
        {
            float weight = slot == bones.Length - 1 ? 1f - sum : stored[slot];
            if (!float.IsFinite(weight)) throw new InvalidDataException("Non-finite implicit skin weight.");
            outsideUnit |= weight < 0 || weight > 1;
            int node = bones[slot];
            if (weight != 0 && (node < 0 || node >= nodeCount)) throw new InvalidDataException("Weighted bone index is outside the skeleton or palette.");
            nodes[slot] = node >= 0 && node < nodeCount ? node : -1; values[slot] = weight; sum += weight;
        }
        return new(nodes[0], nodes[1], nodes[2], nodes[3], values[0], values[1], values[2], values[3]);
    }
    private static bool Equivalent(LithTechVertexSkin a, LithTechVertexSkin b)
    {
        int left = 0, right = 0;
        while (true)
        {
            while (left < 4 && a.GetWeight(left) == 0) left++;
            while (right < 4 && b.GetWeight(right) == 0) right++;
            if (left == 4 || right == 4) return left == 4 && right == 4;
            if (a.GetNode(left) != b.GetNode(right) || a.GetWeight(left) != b.GetWeight(right)) return false;
            left++; right++;
        }
    }
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly int end;
        public int Position { get; private set; }
        public Cursor(ReadOnlySpan<byte> data, int start, int end) { this.data = data; this.end = end; Position = start; }
        public void Skip(int bytes) { if (bytes < 0 || (long)Position + bytes > end) throw new InvalidDataException("Truncated skin data."); Position += bytes; }
        public byte Byte() { int p = Position; Skip(1); return data[p]; }
        public ushort U16() { int p = Position; Skip(2); return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(p, 2)); }
        public uint U32() { int p = Position; Skip(4); return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4)); }
        public float Float() { float v = BitConverter.UInt32BitsToSingle(U32()); if (!float.IsFinite(v)) throw new InvalidDataException("Non-finite stored skin value."); return v; }
    }
}
