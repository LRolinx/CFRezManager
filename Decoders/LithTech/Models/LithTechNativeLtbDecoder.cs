using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CFRezManager;

/// <summary>
/// Reads the native D3D LTB piece/LOD stream. The legacy preview parser predates
/// the size-delimited render objects used by the game files and can lose sync on
/// skinned meshes, so this is used as a strict fallback for those files.
/// </summary>
internal static class LithTechNativeLtbDecoder
{
    private const uint D3dModelFileType = 1;
    private const uint D3dLtbVersion = 9;
    private const int LtbHeaderSize = 20;
    private const int AllocationWordCount = 15;
    private const int MaximumPieces = 4096;
    private const int MaximumLodsPerPiece = 64;
    private const int MaximumVertices = 1_000_000;
    private const int MaximumTriangles = 2_000_000;
    private const int MaximumGeometrylessNodeDepth = 256;
    private const uint VertexDataMask = 0x1ff;
    private const uint VertexPosition = 0x001;
    private const uint VertexNormal = 0x002;
    private const uint VertexDiffuse = 0x004;
    private const uint VertexUv1 = 0x010;
    private const uint VertexUv2 = 0x020;
    private const uint VertexUv3 = 0x040;
    private const uint VertexUv4 = 0x080;
    private const uint VertexUvMask = VertexUv1 | VertexUv2 | VertexUv3 | VertexUv4;
    private const uint VertexBasisVectors = 0x100;
    private const uint RenderObjectRigidMesh = 4;
    private const uint RenderObjectSkelMesh = 5;

    static LithTechNativeLtbDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!TryReadUInt16(data, 0, out ushort fileType) ||
            !TryReadUInt16(data, sizeof(ushort), out ushort ltbVersion) ||
            fileType != D3dModelFileType ||
            ltbVersion != D3dLtbVersion ||
            data.Length < LtbHeaderSize + sizeof(uint) * (1 + AllocationWordCount))
        {
            errorMessage = "Native LTB header is not a supported D3D model.";
            return false;
        }

        int position = LtbHeaderSize;
        if (!TryReadUInt32(data, position, out uint modelFileVersion) || modelFileVersion < 1 || modelFileVersion > 128)
        {
            errorMessage = "Native LTB model file version is invalid.";
            return false;
        }

        position += sizeof(uint);
        Span<uint> allocations = stackalloc uint[AllocationWordCount];
        for (int i = 0; i < allocations.Length; i++)
        {
            if (!TryReadUInt32(data, position, out allocations[i]))
            {
                errorMessage = "Native LTB allocation header is incomplete.";
                return false;
            }

            position += sizeof(uint);
        }

        // ModelAllocations stores node, piece and aggregate LOD counts at words 2, 3 and 8.
        uint allocatedNodeCount = allocations[2];
        uint allocatedPieceCount = allocations[3];
        uint allocatedLodCount = allocations[8];
        if (allocatedNodeCount is 0 or > 65536 ||
            allocatedPieceCount > MaximumPieces ||
            allocatedLodCount > MaximumPieces * MaximumLodsPerPiece ||
            (allocatedPieceCount == 0) != (allocatedLodCount == 0))
        {
            errorMessage = "Native LTB allocation counts are outside supported bounds.";
            return false;
        }

        // The command string is length-prefixed and is followed by radius and OBB count.
        if (!TryReadString(data, ref position, out _) ||
            !TrySkip(data, ref position, sizeof(float)) ||
            !TryReadUInt32(data, position, out uint obbCount))
        {
            errorMessage = "Native LTB model header is truncated before its piece table.";
            return false;
        }

        position += sizeof(uint);
        if (obbCount != 0)
        {
            // Version 25 files use a 68-byte serialized OBB record. This was
            // cross-checked against multiple RF266 models by requiring the piece
            // count and first piece name to line up immediately after the records.
            // Keep unknown versions on the strict legacy/converter path.
            const int version25ObbRecordSize = 68;
            if (modelFileVersion != 25 || obbCount > MaximumPieces)
            {
                errorMessage = $"Native LTB contains unsupported version-dependent OBB records (model version {modelFileVersion}, count {obbCount}).";
                return false;
            }

            long obbByteCount = (long)obbCount * version25ObbRecordSize;
            if (obbByteCount > int.MaxValue || !TrySkip(data, ref position, (int)obbByteCount))
            {
                errorMessage = "Native LTB OBB record block is truncated.";
                return false;
            }
        }

        if (!TryReadUInt32(data, position, out uint pieceCount) ||
            pieceCount > MaximumPieces ||
            pieceCount != allocatedPieceCount)
        {
            errorMessage = "Native LTB piece count does not match its allocation table.";
            return false;
        }

        position += sizeof(uint);
        if (pieceCount == 0)
        {
            if (!TryDecodeGeometrylessModel(data, ref position, allocations, allocatedNodeCount, out errorMessage))
            {
                return false;
            }

            document = new LithTechModelDocument(
                fallbackName,
                Array.Empty<LithTechMesh>(),
                $"{storageDescription} (native LTB hierarchy; no renderable pieces)",
                sourceByteCount,
                decodedByteCount);
            return true;
        }

        if (allocatedLodCount == 0)
        {
            errorMessage = "Native LTB contains pieces but allocates no LODs.";
            return false;
        }

        var meshes = new List<LithTechMesh>((int)Math.Min(pieceCount, 128));
        ulong parsedLodCount = 0;

        for (uint pieceIndex = 0; pieceIndex < pieceCount; pieceIndex++)
        {
            if (!TryReadString(data, ref position, out string pieceName) ||
                !TryReadUInt32(data, position, out uint lodCount) ||
                lodCount == 0 ||
                lodCount > MaximumLodsPerPiece)
            {
                errorMessage = $"Native LTB piece {pieceIndex + 1} has an invalid name or LOD count.";
                return false;
            }

            position += sizeof(uint);
            parsedLodCount += lodCount;
            if (parsedLodCount > allocatedLodCount ||
                !TrySkip(data, ref position, checked((int)lodCount * sizeof(float) + sizeof(uint) * 2)))
            {
                errorMessage = $"Native LTB piece '{pieceName}' has an incomplete LOD table.";
                return false;
            }

            for (uint lodIndex = 0; lodIndex < lodCount; lodIndex++)
            {
                // LOD preamble: texture count, four texture IDs, render style, byte priority,
                // and render-object type. This mirrors ModelPiece::Load in LithTech.
                if (!TryReadUInt32(data, position, out uint textureCount) || textureCount > 4)
                {
                    errorMessage = $"Native LTB piece '{pieceName}' has an invalid texture-slot count.";
                    return false;
                }

                position += sizeof(uint);
                var textureSlotIndices = new int[4];
                for (int slot = 0; slot < 4; slot++)
                {
                    if (!TryReadInt32(data, position, out textureSlotIndices[slot]))
                    {
                        errorMessage = $"Native LTB piece '{pieceName}' has an incomplete texture-slot table.";
                        return false;
                    }

                    position += sizeof(int);
                }

                if (!TryReadInt32(data, position, out int renderStyleIndex) ||
                    !TrySkip(data, ref position, sizeof(int) + sizeof(byte)) ||
                    !TryReadUInt32(data, position, out uint renderObjectType))
                {
                    errorMessage = $"Native LTB piece '{pieceName}' has an incomplete render-object header.";
                    return false;
                }

                position += sizeof(uint);
                if (!TryReadUInt32(data, position, out uint objectSize) || objectSize > int.MaxValue)
                {
                    errorMessage = $"Native LTB piece '{pieceName}' has an invalid render-object size.";
                    return false;
                }

                position += sizeof(uint);
                int objectBodyStart = position;
                long objectEndLong = (long)objectBodyStart + objectSize;
                if (objectEndLong > data.Length)
                {
                    errorMessage = $"Native LTB piece '{pieceName}' render object exceeds the decoded file.";
                    return false;
                }

                int objectEnd = (int)objectEndLong;
                if (lodIndex == 0 && renderObjectType is RenderObjectRigidMesh or RenderObjectSkelMesh)
                {
                    int[] activeTextureSlotIndices = textureSlotIndices.Take((int)textureCount).ToArray();
                    bool parsedMesh = renderObjectType == RenderObjectRigidMesh
                        ? TryReadRigidMesh(data, objectBodyStart, objectEnd, pieceName, activeTextureSlotIndices, out LithTechMesh? mesh, out string? meshError)
                        : TryReadSkelMesh(data, objectBodyStart, objectEnd, pieceName, activeTextureSlotIndices, out mesh, out meshError);
                    if (parsedMesh &&
                        mesh is not null)
                    {
                        meshes.Add(mesh with
                        {
                            RenderStyleIndex = renderStyleIndex,
                            HasAuthoritativeTextureBindings = true
                        });
                    }
                    else
                    {
                        errorMessage = meshError;
                        return false;
                    }
                }

                position = objectEnd;
                if (position >= data.Length)
                {
                    errorMessage = $"Native LTB piece '{pieceName}' is missing its used-node list.";
                    return false;
                }

                int usedNodeCount = data[position++];
                if (!TrySkip(data, ref position, usedNodeCount))
                {
                    errorMessage = $"Native LTB piece '{pieceName}' has a truncated used-node list.";
                    return false;
                }
            }
        }

        if (parsedLodCount != allocatedLodCount)
        {
            errorMessage = "Native LTB aggregate LOD count does not match its allocation table.";
            return false;
        }

        if (meshes.Count == 0)
        {
            errorMessage = "Native LTB piece table parsed, but it contains no supported skinned LOD0 meshes.";
            return false;
        }

        document = new LithTechModelDocument(fallbackName, meshes, storageDescription, sourceByteCount, decodedByteCount);
        return true;
    }

    private static bool TryDecodeGeometrylessModel(
        ReadOnlySpan<byte> data,
        ref int position,
        ReadOnlySpan<uint> allocations,
        uint allocatedNodeCount,
        out string? errorMessage)
    {
        errorMessage = null;
        if (allocations[0] != 0 || allocations[1] != 0 || allocations[4] != 1 ||
            allocations[5] != 0 || allocations[6] != 0 || allocations[7] != 0 ||
            allocations[9] != 0 || allocations[13] != 0 || allocations[14] != 0)
        {
            errorMessage = "Native zero-piece LTB uses animation, socket, vertex, or child-model data outside the supported hierarchy-only layout.";
            return false;
        }

        uint parsedStringCount = 0;
        ulong parsedStringBytes = 0;
        uint parsedNodeCount = 0;
        var nodeIndices = new HashSet<uint>();
        if (!TryReadGeometrylessNode(
                data,
                ref position,
                0,
                allocatedNodeCount,
                nodeIndices,
                ref parsedNodeCount,
                ref parsedStringCount,
                ref parsedStringBytes,
                out errorMessage) ||
            parsedNodeCount != allocatedNodeCount)
        {
            errorMessage ??= "Native zero-piece LTB node hierarchy does not match its allocation table.";
            return false;
        }

        if (!TryReadUInt32(data, position, out uint weightSetCount) || weightSetCount != allocations[10] || weightSetCount > 65536)
        {
            errorMessage = "Native zero-piece LTB weight-set count does not match its allocation table.";
            return false;
        }

        position += sizeof(uint);
        for (uint setIndex = 0; setIndex < weightSetCount; setIndex++)
        {
            if (!TryReadNativeModelString(data, ref position, ref parsedStringCount, ref parsedStringBytes, out _) ||
                !TryReadUInt32(data, position, out uint weightCount) || weightCount != allocatedNodeCount)
            {
                errorMessage = $"Native zero-piece LTB weight set {setIndex + 1} is malformed.";
                return false;
            }

            position += sizeof(uint);
            for (uint weightIndex = 0; weightIndex < weightCount; weightIndex++)
            {
                if (!TryReadSingle(data, position, out float weight) || !float.IsFinite(weight))
                {
                    errorMessage = $"Native zero-piece LTB weight set {setIndex + 1} contains invalid weights.";
                    return false;
                }

                position += sizeof(float);
            }
        }

        if (!TryReadUInt32(data, position, out uint childModelCount) || childModelCount != allocations[4] || childModelCount != 1)
        {
            errorMessage = "Native zero-piece LTB child-model count is not the single required self model.";
            return false;
        }

        position += sizeof(uint);
        if (!TryReadUInt32(data, position, out uint animationBindingCount) || animationBindingCount != 0)
        {
            errorMessage = "Native zero-piece LTB contains unsupported animation bindings.";
            return false;
        }

        position += sizeof(uint);
        if (!TryReadUInt32(data, position, out uint socketCount) || socketCount != allocations[9] || socketCount != 0)
        {
            errorMessage = "Native zero-piece LTB socket table does not match its allocation table.";
            return false;
        }

        position += sizeof(uint);
        if (!TryReadUInt32(data, position, out uint animationCount) || animationCount != allocations[1] || animationCount != 0)
        {
            errorMessage = "Native zero-piece LTB animation count does not match its allocation table.";
            return false;
        }

        position += sizeof(uint);
        if (parsedStringCount != allocations[11] || parsedStringBytes != allocations[12])
        {
            errorMessage = "Native zero-piece LTB string totals do not match its allocation table.";
            return false;
        }

        int trailingByteCount = data.Length - position;
        if (trailingByteCount is < 0 or > 3 || data[position..].IndexOfAnyExcept((byte)0) >= 0)
        {
            errorMessage = "Native zero-piece LTB has unexpected bytes after its hierarchy tables.";
            return false;
        }

        return true;
    }

    private static bool TryReadGeometrylessNode(
        ReadOnlySpan<byte> data,
        ref int position,
        int depth,
        uint allocatedNodeCount,
        HashSet<uint> nodeIndices,
        ref uint parsedNodeCount,
        ref uint parsedStringCount,
        ref ulong parsedStringBytes,
        out string? errorMessage)
    {
        errorMessage = null;
        if (depth >= MaximumGeometrylessNodeDepth ||
            parsedNodeCount >= allocatedNodeCount ||
            !TryReadNativeModelString(data, ref position, ref parsedStringCount, ref parsedStringBytes, out _) ||
            !TryReadUInt16(data, position, out ushort nodeIndex) ||
            (uint)nodeIndex >= allocatedNodeCount ||
            !nodeIndices.Add(nodeIndex))
        {
            errorMessage = "Native zero-piece LTB contains an invalid or duplicate model node.";
            return false;
        }

        position += sizeof(ushort);
        if (!TrySkip(data, ref position, sizeof(byte)))
        {
            errorMessage = "Native zero-piece LTB node flags are truncated.";
            return false;
        }

        for (int matrixIndex = 0; matrixIndex < 16; matrixIndex++)
        {
            if (!TryReadSingle(data, position, out float matrixValue) || !float.IsFinite(matrixValue))
            {
                errorMessage = "Native zero-piece LTB node transform is truncated or non-finite.";
                return false;
            }

            position += sizeof(float);
        }

        if (!TryReadUInt32(data, position, out uint childCount) || childCount > allocatedNodeCount - parsedNodeCount - 1)
        {
            errorMessage = "Native zero-piece LTB node has an invalid child count.";
            return false;
        }

        position += sizeof(uint);
        parsedNodeCount++;
        for (uint childIndex = 0; childIndex < childCount; childIndex++)
        {
            if (!TryReadGeometrylessNode(
                    data,
                    ref position,
                    depth + 1,
                    allocatedNodeCount,
                    nodeIndices,
                    ref parsedNodeCount,
                    ref parsedStringCount,
                    ref parsedStringBytes,
                    out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadNativeModelString(
        ReadOnlySpan<byte> data,
        ref int position,
        ref uint parsedStringCount,
        ref ulong parsedStringBytes,
        out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(data, position, out ushort byteCount) || byteCount > 4096)
        {
            return false;
        }

        position += sizeof(ushort);
        if ((long)position + byteCount > data.Length)
        {
            return false;
        }

        value = byteCount == 0
            ? string.Empty
            : Encoding.GetEncoding(949).GetString(data.Slice(position, byteCount)).TrimEnd('\0');
        position += byteCount;
        parsedStringCount++;
        parsedStringBytes += byteCount;
        return true;
    }

    private static bool TryReadSkelMesh(
        ReadOnlySpan<byte> data,
        int bodyStart,
        int bodyEnd,
        string meshName,
        IReadOnlyList<int> textureSlotIndices,
        out LithTechMesh? mesh,
        out string? errorMessage)
    {
        mesh = null;
        errorMessage = null;
        if (bodyStart < 0 || bodyEnd > data.Length || bodyEnd - bodyStart < 34 ||
            !TryReadUInt32(data, bodyStart, out uint vertexCount) ||
            !TryReadUInt32(data, bodyStart + 4, out uint triangleCount) ||
            !TryReadUInt32(data, bodyStart + 8, out uint maxBonesPerTriangle) ||
            !TryReadUInt32(data, bodyStart + 12, out uint maxBonesPerVertex))
        {
            errorMessage = $"Native skinned mesh '{meshName}' has an incomplete header.";
            return false;
        }

        if (vertexCount is 0 or > MaximumVertices ||
            triangleCount is 0 or > MaximumTriangles ||
            maxBonesPerTriangle > 32 ||
            maxBonesPerVertex > 4)
        {
            errorMessage = $"Native skinned mesh '{meshName}' has implausible geometry counts.";
            return false;
        }

        bool reindexedBones = data[bodyStart + 16] != 0;
        Span<uint> streamFlags = stackalloc uint[4];
        int cursor = bodyStart + 17;
        bool hasPositionStream = false;
        for (int i = 0; i < streamFlags.Length; i++)
        {
            if (!TryReadUInt32(data, cursor, out streamFlags[i]) || (streamFlags[i] & ~VertexDataMask) != 0)
            {
                errorMessage = $"Native skinned mesh '{meshName}' has unsupported vertex stream flags.";
                return false;
            }

            hasPositionStream |= (streamFlags[i] & VertexPosition) != 0;
            cursor += sizeof(uint);
        }

        byte matrixPaletteValue = data[cursor++];
        if (!hasPositionStream || matrixPaletteValue > 1)
        {
            errorMessage = $"Native skinned mesh '{meshName}' has no usable position stream.";
            return false;
        }

        bool matrixPalette = matrixPaletteValue != 0;
        int skinningPrefixBytes;
        if (matrixPalette)
        {
            if (maxBonesPerVertex is < 2 or > 4 ||
                !TrySkip(data, ref cursor, sizeof(uint) * 2))
            {
                errorMessage = $"Native skinned mesh '{meshName}' has invalid matrix-palette metadata.";
                return false;
            }

            if (reindexedBones)
            {
                if (!TryReadUInt32(data, cursor, out uint boneMapCount) || boneMapCount > 10000)
                {
                    errorMessage = $"Native skinned mesh '{meshName}' has an invalid bone-remap table.";
                    return false;
                }

                cursor += sizeof(uint);
                if (!TrySkip(data, ref cursor, checked((int)boneMapCount * sizeof(uint))))
                {
                    errorMessage = $"Native skinned mesh '{meshName}' has a truncated bone-remap table.";
                    return false;
                }
            }

            skinningPrefixBytes = checked((int)(maxBonesPerVertex - 1) * sizeof(float) + 4);
        }
        else
        {
            if (maxBonesPerTriangle is 0 or > 4)
            {
                errorMessage = $"Native skinned mesh '{meshName}' has an invalid direct-skinning format.";
                return false;
            }

            skinningPrefixBytes = checked((int)(maxBonesPerTriangle - 1) * sizeof(float));
        }

        return TryReadGeometry(
            data,
            bodyEnd,
            meshName,
            vertexCount,
            triangleCount,
            streamFlags,
            cursor,
            skinningPrefixBytes,
            textureSlotIndices,
            out mesh,
            out errorMessage);
    }

    private static bool TryReadRigidMesh(
        ReadOnlySpan<byte> data,
        int bodyStart,
        int bodyEnd,
        string meshName,
        IReadOnlyList<int> textureSlotIndices,
        out LithTechMesh? mesh,
        out string? errorMessage)
    {
        mesh = null;
        errorMessage = null;
        if (bodyStart < 0 || bodyEnd > data.Length || bodyEnd - bodyStart < 36 ||
            !TryReadUInt32(data, bodyStart, out uint vertexCount) ||
            !TryReadUInt32(data, bodyStart + 4, out uint triangleCount) ||
            !TryReadUInt32(data, bodyStart + 8, out uint maxBonesPerTriangle) ||
            !TryReadUInt32(data, bodyStart + 12, out uint maxBonesPerVertex) ||
            vertexCount is 0 or > MaximumVertices ||
            triangleCount is 0 or > MaximumTriangles ||
            maxBonesPerTriangle != 1 || maxBonesPerVertex != 1)
        {
            errorMessage = $"Native rigid mesh '{meshName}' has an invalid geometry header.";
            return false;
        }

        Span<uint> streamFlags = stackalloc uint[4];
        int cursor = bodyStart + 16;
        bool hasPositionStream = false;
        for (int i = 0; i < streamFlags.Length; i++)
        {
            if (!TryReadUInt32(data, cursor, out streamFlags[i]) || (streamFlags[i] & ~VertexDataMask) != 0)
            {
                errorMessage = $"Native rigid mesh '{meshName}' has unsupported vertex stream flags.";
                return false;
            }

            hasPositionStream |= (streamFlags[i] & VertexPosition) != 0;
            cursor += sizeof(uint);
        }

        if (!hasPositionStream || !TrySkip(data, ref cursor, sizeof(uint)))
        {
            errorMessage = $"Native rigid mesh '{meshName}' has no position stream or bone-effector field.";
            return false;
        }

        return TryReadGeometry(
            data,
            bodyEnd,
            meshName,
            vertexCount,
            triangleCount,
            streamFlags,
            cursor,
            skinningPrefixBytes: 0,
            textureSlotIndices,
            out mesh,
            out errorMessage);
    }

    private static bool TryReadGeometry(
        ReadOnlySpan<byte> data,
        int bodyEnd,
        string meshName,
        uint vertexCount,
        uint triangleCount,
        ReadOnlySpan<uint> streamFlags,
        int cursor,
        int skinningPrefixBytes,
        IReadOnlyList<int> textureSlotIndices,
        out LithTechMesh? mesh,
        out string? errorMessage)
    {
        mesh = null;
        errorMessage = null;
        Span<VertexStreamLayout> streams = stackalloc VertexStreamLayout[4];
        int positionStreamIndex = -1;
        for (int i = 0; i < streamFlags.Length; i++)
        {
            uint flags = streamFlags[i];
            if (flags == 0)
            {
                streams[i] = default;
                continue;
            }

            if (!TryGetStreamLayout(flags, skinningPrefixBytes, out VertexStreamLayout layout))
            {
                errorMessage = $"Native mesh '{meshName}' has an invalid vertex stream layout.";
                return false;
            }

            layout = layout with { Offset = cursor };
            if ((flags & VertexPosition) != 0 && positionStreamIndex < 0)
            {
                positionStreamIndex = i;
            }

            long streamEnd = (long)cursor + (long)layout.Stride * vertexCount;
            if (streamEnd > bodyEnd)
            {
                errorMessage = $"Native mesh '{meshName}' vertex stream exceeds its declared object size.";
                return false;
            }

            streams[i] = layout;
            cursor = (int)streamEnd;
        }

        long indexCount = (long)triangleCount * 3;
        long indexEnd = (long)cursor + indexCount * sizeof(ushort);
        if (positionStreamIndex < 0 || indexEnd > bodyEnd)
        {
            errorMessage = $"Native mesh '{meshName}' index data exceeds its declared object size.";
            return false;
        }

        var vertices = new List<LithTechVector3>((int)vertexCount);
        VertexStreamLayout positionLayout = streams[positionStreamIndex];
        for (int i = 0; i < vertexCount; i++)
        {
            int vertexOffset = positionLayout.Offset + i * positionLayout.Stride + positionLayout.PositionOffset;
            if (!TryReadSingle(data, vertexOffset, out float x) ||
                !TryReadSingle(data, vertexOffset + 4, out float y) ||
                !TryReadSingle(data, vertexOffset + 8, out float z) ||
                !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                errorMessage = $"Native mesh '{meshName}' contains invalid position data.";
                return false;
            }

            vertices.Add(new LithTechVector3(x, y, z));
        }

        var textureCoordinatesBySet = new List<LithTechVector2>?[4];
        var textureCoordinateSourceStreamIndices = new int?[4];
        var unavailableUvSets = new bool[4];
        for (int i = 0; i < streams.Length; i++)
        {
            VertexStreamLayout uvLayout = streams[i];
            if (uvLayout.UvChannelMask == 0)
            {
                continue;
            }

            int compactUvIndex = 0;
            for (int setIndex = 0; setIndex < 4; setIndex++)
            {
                uint uvFlag = 1u << (setIndex + 4);
                if ((uvLayout.UvChannelMask & uvFlag) == 0)
                {
                    continue;
                }

                var coordinates = new List<LithTechVector2>((int)vertexCount);
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    int uvOffset = uvLayout.Offset + vertexIndex * uvLayout.Stride +
                                   uvLayout.TextureCoordinateOffset + compactUvIndex * sizeof(float) * 2;
                    if (!TryReadSingle(data, uvOffset, out float u) ||
                        !TryReadSingle(data, uvOffset + sizeof(float), out float v) ||
                        !float.IsFinite(u) || !float.IsFinite(v))
                    {
                        unavailableUvSets[setIndex] = true;
                        break;
                    }

                    coordinates.Add(new LithTechVector2(u, v));
                }
                compactUvIndex++;

                if (coordinates.Count != vertexCount)
                {
                    continue;
                }

                if (unavailableUvSets[setIndex])
                {
                    continue;
                }

                if (textureCoordinatesBySet[setIndex] is null)
                {
                    textureCoordinatesBySet[setIndex] = coordinates;
                    textureCoordinateSourceStreamIndices[setIndex] = i;
                }
                else if (!textureCoordinatesBySet[setIndex]!.SequenceEqual(coordinates))
                {
                    textureCoordinatesBySet[setIndex] = null;
                    textureCoordinateSourceStreamIndices[setIndex] = null;
                    unavailableUvSets[setIndex] = true;
                }
            }
        }

        IReadOnlyList<IReadOnlyList<LithTechVector2>?>? textureCoordinateSets =
            textureCoordinatesBySet.Any(coordinates => coordinates is not null)
                ? textureCoordinatesBySet.Select(coordinates => (IReadOnlyList<LithTechVector2>?)coordinates).ToArray()
                : null;
        IReadOnlyList<LithTechVector2>? textureCoordinates = textureCoordinatesBySet[0];

        var indices = new List<int>(checked((int)indexCount));
        int indexPosition = cursor;
        for (long i = 0; i < indexCount; i++)
        {
            if (!TryReadUInt16(data, indexPosition, out ushort index) || index >= vertexCount)
            {
                errorMessage = $"Native mesh '{meshName}' contains an out-of-range triangle index.";
                return false;
            }

            indices.Add(index);
            indexPosition += sizeof(ushort);
        }

        mesh = new LithTechMesh(
            string.IsNullOrWhiteSpace(meshName) ? "Native LTB mesh" : meshName,
            vertices,
            indices,
            textureCoordinates?.Count == vertices.Count ? textureCoordinates : null,
            TextureSlotIndices: textureSlotIndices)
        {
            TextureCoordinateSets = textureCoordinateSets,
            TextureCoordinateSourceStreamIndex = textureCoordinateSourceStreamIndices[0],
            TextureCoordinateSourceStreamIndices = textureCoordinateSets is null ? null : textureCoordinateSourceStreamIndices
        };
        return true;
    }

    private static bool TryGetStreamLayout(uint flags, int skinningPrefixBytes, out VertexStreamLayout layout)
    {
        layout = default;
        bool hasPosition = (flags & VertexPosition) != 0;
        bool hasNormal = (flags & VertexNormal) != 0;
        bool hasSkinning = hasPosition && hasNormal;
        int positionBytes = hasPosition ? 12 + (hasSkinning ? skinningPrefixBytes : 0) : 0;
        int normalBytes = hasNormal ? 12 : 0;
        int diffuseBytes = (flags & VertexDiffuse) != 0 ? sizeof(uint) : 0;
        uint uvChannelMask = flags & VertexUvMask;
        int uvSetCount = ((uvChannelMask & VertexUv1) != 0 ? 1 : 0) +
                         ((uvChannelMask & VertexUv2) != 0 ? 1 : 0) +
                         ((uvChannelMask & VertexUv3) != 0 ? 1 : 0) +
                         ((uvChannelMask & VertexUv4) != 0 ? 1 : 0);
        int uvBytes = uvSetCount * sizeof(float) * 2;
        int basisBytes = (flags & VertexBasisVectors) != 0 ? sizeof(float) * 6 : 0;
        int stride = positionBytes + normalBytes + diffuseBytes + uvBytes + basisBytes;
        if (stride == 0)
        {
            return false;
        }

        layout = new VertexStreamLayout(
            Offset: 0,
            Stride: stride,
            PositionOffset: 0,
            TextureCoordinateOffset: positionBytes + normalBytes + diffuseBytes,
            UvChannelMask: uvChannelMask);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int position, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(data, position, out ushort byteCount))
        {
            return false;
        }

        position += sizeof(ushort);
        if (position < 0 || position + byteCount > data.Length)
        {
            return false;
        }

        value = byteCount == 0
            ? string.Empty
            : Encoding.GetEncoding(949).GetString(data.Slice(position, byteCount)).TrimEnd('\0');
        position += byteCount;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int position, int byteCount)
    {
        if (position < 0 || byteCount < 0 || (long)position + byteCount > data.Length)
        {
            return false;
        }

        position += byteCount;
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || (long)offset + sizeof(ushort) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || (long)offset + sizeof(uint) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> data, int offset, out int value)
    {
        value = 0;
        if (offset < 0 || (long)offset + sizeof(int) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, int offset, out float value)
    {
        value = 0;
        if (offset < 0 || (long)offset + sizeof(float) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, sizeof(float)));
        return true;
    }

    private readonly record struct VertexStreamLayout(
        int Offset,
        int Stride,
        int PositionOffset,
        int TextureCoordinateOffset,
        uint UvChannelMask);
}
