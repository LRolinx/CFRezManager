using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

public sealed record LithTechModelDocument(
    string Name,
    IReadOnlyList<LithTechMesh> Meshes,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount)
{
    public int VertexCount => Meshes.Sum(mesh => mesh.Vertices.Count);

    public int TriangleCount => Meshes.Sum(mesh => mesh.TriangleIndices.Count / 3);
}

internal sealed record LithTechShapeTextureBinding(
    string ShapeName,
    IReadOnlyList<int> TextureIndices,
    int? RenderStyleIndex);

public sealed record LithTechMesh(
    string Name,
    IReadOnlyList<LithTechVector3> Vertices,
    IReadOnlyList<int> TriangleIndices,
    IReadOnlyList<LithTechVector2>? TextureCoordinates = null,
    string? TexturePath = null,
    IReadOnlyList<string>? MaterialHints = null,
    IReadOnlyList<int>? TextureSlotIndices = null,
    int? RenderStyleIndex = null,
    bool HasAuthoritativeTextureBindings = false)
{
    /// <summary>
    /// Model-data UV sets in engine order; absent or conflicting channels are null. TextureCoordinates remains UV set 1 for existing callers.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<LithTechVector2>?>? TextureCoordinateSets { get; init; }

    /// <summary>
    /// Native LTB vertex stream indices for UV sets 1–4. Missing or conflicting sets have no source index.
    /// </summary>
    public IReadOnlyList<int?>? TextureCoordinateSourceStreamIndices { get; init; }

    /// <summary>
    /// Native LTB vertex stream that supplied UV set 1, when known.
    /// </summary>
    public int? TextureCoordinateSourceStreamIndex { get; init; }

    public int TextureCoordinateSetCount => TextureCoordinateSets is { Count: > 0 } sets
        ? sets.Count(coordinates => coordinates is { } values && values.Count == Vertices.Count)
        : TextureCoordinates is { } textureCoordinates && textureCoordinates.Count == Vertices.Count ? 1 : 0;

    public IReadOnlyList<LithTechVector2>? GetTextureCoordinateSet(int setIndex)
    {
        if (setIndex < 0)
        {
            return null;
        }

        if (TextureCoordinateSets is { Count: > 0 } sets)
        {
            return setIndex < sets.Count ? sets[setIndex] : null;
        }

        return setIndex == 0 ? TextureCoordinates : null;
    }

    public bool HasTextureCoordinateSet(int setIndex)
    {
        return GetTextureCoordinateSet(setIndex) is { } coordinates && coordinates.Count == Vertices.Count;
    }

    public int? GetTextureCoordinateSourceStreamIndex(int setIndex)
    {
        if (setIndex < 0)
        {
            return null;
        }

        if (TextureCoordinateSourceStreamIndices is { } streamIndices)
        {
            return setIndex < streamIndices.Count ? streamIndices[setIndex] : null;
        }

        return setIndex == 0 ? TextureCoordinateSourceStreamIndex : null;
    }

    public bool HasTextureCoordinates => HasTextureCoordinateSet(0);
}

public readonly record struct LithTechVector3(double X, double Y, double Z);

public readonly record struct LithTechVector2(double X, double Y);

internal static class LithTechModelDecoder
{
    private const int MaxParseDepth = 256;
    private const int MaxLtbMeshCount = 4096;
    private const int ExternalConverterTimeoutMilliseconds = 15_000;

    // Offsets follow Cote-Duke's LTB2X loader notes for LithTech Jupiter LTB meshes.
    private const int LtbCommandLineLengthOffset = 84;
    private const int LtbMeshCountOffset = 86 + 8;
    private const int LtbFirstMeshOffset = 86 + 12;

    private const int LtbMeshVertexCountOffset = 49;
    private const int LtbMeshFaceCountOffset = 53;
    private const int LtbMeshTypeHeadOffset = 57;
    private const int LtbMeshTypeOffset = 61;
    private const int LtbFirstVertexOffset = 83;
    private const int LtbFirstVertexDoubleStartOffset = 85;

    private const ushort LtbMeshTypeNotSkinned = 1;
    private const ushort LtbMeshTypeExtraFloat = 2;
    private const ushort LtbMeshTypeSkinnedAlt = 3;
    private const ushort LtbMeshTypeSkinned = 4;
    private const ushort LtbMeshTypeTwoExtraFloats = 5;
    private const ushort LtbMeshTypeSkinnedExtraFloat = 6;
    private static readonly string[] TextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"];
    private static readonly string[] LtaTextureCoordinateHeads =
    [
        "uv",
        "uvs",
        "uv-fs",
        "uvs-fs",
        "tvert",
        "tverts",
        "texvert",
        "texverts",
        "texcoord",
        "texcoords",
        "texturecoord",
        "texturecoords",
        "texture-coordinate",
        "texture-coordinates"
    ];

    static LithTechModelDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "lta", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(byte[] data, string fallbackName, out LithTechModelDocument? document)
    {
        return TryDecode(data, fallbackName, "lta", out document, out _);
    }

    public static bool TryDecode(
        byte[] data,
        string fallbackName,
        string extension,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeLtc(data, fallbackName, out document, out errorMessage);
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            errorMessage = LocalizedText.T("ModelOuterCompressionFailed");
            return false;
        }

        if (string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase))
        {
            string nativeStorageDescription = ReferenceEquals(prepared, data) ? "LTB binary" : "LZMA-compressed LTB";
            if (LithTechNativeLtbDecoder.TryDecode(
                    prepared,
                    fallbackName,
                    nativeStorageDescription,
                    data.Length,
                    prepared.Length,
                    out document,
                    out string? nativeStreamError))
            {
                errorMessage = null;
                return true;
            }

            if (TryParseLtbBinary(prepared, fallbackName, nativeStorageDescription, data.Length, prepared.Length, out document, out errorMessage))
            {
                if (document is not null && !string.IsNullOrWhiteSpace(nativeStreamError))
                {
                    document = document with
                    {
                        StorageDescription = $"{document.StorageDescription}; native parser rejected: {nativeStreamError}"
                    };
                }

                errorMessage = null;
                return true;
            }

            string? nativeError = errorMessage;
            string? ltaText = TryConvertLtbToLtaText(prepared, fallbackName, out string? converterError);
            if (ltaText is null)
            {
                errorMessage = string.IsNullOrWhiteSpace(converterError)
                    ? JoinErrors(nativeError, nativeStreamError)
                    : $"{JoinErrors(nativeError, nativeStreamError)} {converterError}";
                return false;
            }

            string ltbStorageDescription = ReferenceEquals(prepared, data)
                ? "LTB -> LTA"
                : "LZMA-compressed LTB -> LTA";
            return TryParseLtaText(ltaText, fallbackName, ltbStorageDescription, data.Length, prepared.Length, out document, out errorMessage);
        }

        string storageDescription = ReferenceEquals(prepared, data) ? "LTA text" : "LZMA-compressed LTA";
        string text = Encoding.ASCII.GetString(prepared);
        return TryParseLtaText(text, fallbackName, storageDescription, data.Length, prepared.Length, out document, out errorMessage);
    }

    private static string? JoinErrors(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }

    private static bool TryDecodeLtc(
        byte[] data,
        string fallbackName,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is not null)
        {
            string ltaText = Encoding.ASCII.GetString(prepared);
            if (ltaText.Contains("(lt-model", StringComparison.OrdinalIgnoreCase) &&
                TryParseLtaText(
                    ltaText,
                    fallbackName,
                    ReferenceEquals(prepared, data) ? "LTC text" : "LZMA-compressed LTC",
                    data.Length,
                    prepared.Length,
                    out document,
                    out errorMessage))
            {
                return true;
            }
        }

        if (CrossFireLtcDecoder.TryConvertToText(data, fallbackName, out CrossFireLtcTextDocument? converted, out string? converterError) &&
            converted is not null)
        {
            return TryParseLtaText(converted.Text, fallbackName, converted.StorageDescription, data.Length, converted.DecodedByteCount, out document, out errorMessage);
        }

        if (CrossFireLtcDecoder.HasCrossFireMagic(data))
        {
            errorMessage = CrossFireLtcDecoder.GetUnsupportedMessage(converterError);
            return false;
        }

        errorMessage = string.IsNullOrWhiteSpace(converterError)
            ? LocalizedText.T("ModelLtcNotRecognized")
            : converterError;
        return false;
    }

    private static bool TryParseLtbBinary(
        byte[] rawLtb,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;
        ReadOnlySpan<byte> data = rawLtb;

        string? firstParseError = null;
        foreach (LtbMeshTableCandidate candidate in GetLtbMeshTableCandidates(data))
        {
            if (TryParseLtbMeshes(data, candidate.FirstMeshOffset, candidate.MeshCount, out List<LithTechMesh>? meshes, out string? candidateError) &&
                meshes is not null)
            {
                document = new LithTechModelDocument(fallbackName, meshes, storageDescription, sourceByteCount, decodedByteCount);
                return true;
            }

            firstParseError ??= candidateError;
        }

        if (!string.IsNullOrWhiteSpace(firstParseError))
        {
            errorMessage = firstParseError;
            return false;
        }

        if (!TryReadUInt16(data, LtbCommandLineLengthOffset, out ushort commandLineLength))
        {
            errorMessage = LocalizedText.T("LtbTooShort");
            return false;
        }

        int meshCountOffset = LtbMeshCountOffset + commandLineLength;
        int position = LtbFirstMeshOffset + commandLineLength;
        if (!TryReadUInt32(data, meshCountOffset, out uint meshCount))
        {
            errorMessage = LocalizedText.T("LtbHeaderIncompleteMeshCount");
            return false;
        }

        if (meshCount == 0 || meshCount > MaxLtbMeshCount)
        {
            errorMessage = LocalizedText.Format("LtbInvalidMeshCount", meshCount);
            return false;
        }

        errorMessage = LocalizedText.T("LtbNoPreviewMesh");
        return false;
    }

    private static List<LtbMeshTableCandidate> GetLtbMeshTableCandidates(ReadOnlySpan<byte> data)
    {
        var candidates = new List<LtbMeshTableCandidate>();
        var seenFirstMeshOffsets = new HashSet<int>();

        if (TryReadUInt16(data, LtbCommandLineLengthOffset, out ushort commandLineLength))
        {
            int meshCountOffset = LtbMeshCountOffset + commandLineLength;
            int firstMeshOffset = LtbFirstMeshOffset + commandLineLength;
            if (TryReadUInt32(data, meshCountOffset, out uint meshCount) &&
                IsPlausibleLtbMeshCount(meshCount) &&
                seenFirstMeshOffsets.Add(firstMeshOffset))
            {
                candidates.Add(new LtbMeshTableCandidate(meshCountOffset, firstMeshOffset, meshCount));
            }
        }

        int scanLimit = Math.Min(data.Length - 2, 4096);
        for (int firstMeshOffset = 4; firstMeshOffset <= scanLimit; firstMeshOffset++)
        {
            int meshCountOffset = firstMeshOffset - sizeof(uint);
            if (!TryReadUInt32(data, meshCountOffset, out uint meshCount) ||
                !IsPlausibleLtbMeshCount(meshCount) ||
                !seenFirstMeshOffsets.Add(firstMeshOffset))
            {
                continue;
            }

            if (LooksLikeLtbMeshAt(data, firstMeshOffset))
            {
                candidates.Add(new LtbMeshTableCandidate(meshCountOffset, firstMeshOffset, meshCount));
            }
        }

        return candidates;
    }

    private static bool IsPlausibleLtbMeshCount(uint meshCount)
    {
        return meshCount > 0 && meshCount <= MaxLtbMeshCount;
    }

    private static bool LooksLikeLtbMeshAt(ReadOnlySpan<byte> data, int position)
    {
        if (!TryReadUInt16(data, position, out ushort nameLength) ||
            nameLength > 512)
        {
            return false;
        }

        int nameStart = position + sizeof(ushort);
        int meshBaseOffset = nameStart + nameLength;
        if (nameStart < 0 ||
            meshBaseOffset < 0 ||
            meshBaseOffset + LtbFirstVertexOffset >= data.Length ||
            !LooksLikeLtbStringBytes(data.Slice(nameStart, nameLength)))
        {
            return false;
        }

        if (!TryReadUInt16(data, meshBaseOffset + LtbMeshVertexCountOffset, out ushort vertexCount) ||
            !TryReadUInt16(data, meshBaseOffset + LtbMeshFaceCountOffset, out ushort faceCount) ||
            vertexCount == 0 ||
            faceCount == 0)
        {
            return false;
        }

        return TryFindLtbMeshLayout(
            data,
            meshBaseOffset,
            vertexCount,
            faceCount,
            GetLtbVertexLookupByteCount(data),
            requireFollowingMesh: false,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);
    }

    private static bool LooksLikeLtbStringBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        bool sawText = false;
        foreach (byte value in bytes)
        {
            if (value == 0)
            {
                continue;
            }

            if (value < 0x20 && value != (byte)'\t')
            {
                return false;
            }

            sawText = true;
        }

        return sawText;
    }

    private static bool LooksLikeLtbMeshHeaderAt(ReadOnlySpan<byte> data, int position)
    {
        if (!TryReadUInt16(data, position, out ushort nameLength) ||
            nameLength > 512)
        {
            return false;
        }

        int nameStart = position + sizeof(ushort);
        int meshBaseOffset = nameStart + nameLength;
        if (nameStart < 0 ||
            meshBaseOffset < 0 ||
            meshBaseOffset + LtbFirstVertexOffset >= data.Length ||
            !LooksLikeLtbStringBytes(data.Slice(nameStart, nameLength)))
        {
            return false;
        }

        return TryReadUInt16(data, meshBaseOffset + LtbMeshVertexCountOffset, out ushort vertexCount) &&
               TryReadUInt16(data, meshBaseOffset + LtbMeshFaceCountOffset, out ushort faceCount) &&
               vertexCount > 0 &&
               faceCount > 0;
    }

    private static bool TryParseLtbMeshes(
        ReadOnlySpan<byte> data,
        int firstMeshOffset,
        uint meshCount,
        out List<LithTechMesh>? meshes,
        out string? errorMessage)
    {
        meshes = null;
        errorMessage = null;

        var parsedMeshes = new List<LithTechMesh>((int)Math.Min(meshCount, 64));
        int position = firstMeshOffset;
        int vertexLookupByteCount = GetLtbVertexLookupByteCount(data);
        List<string> texturePaths = ExtractEmbeddedTexturePaths(data);
        for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            if (!TryReadLtbString(data, ref position, out string meshName))
            {
                errorMessage = LocalizedText.Format("LtbMeshNameIncomplete", meshIndex + 1);
                return false;
            }

            int meshBaseOffset = position;
            bool requireFollowingMesh = meshIndex + 1 < meshCount;
            if (!TryReadLtbMesh(
                    data,
                    meshName,
                    meshIndex,
                    meshBaseOffset,
                    vertexLookupByteCount,
                    requireFollowingMesh,
                    texturePaths,
                    out LithTechMesh? mesh,
                    out position,
                    out errorMessage))
            {
                return false;
            }

            if (mesh is not null)
            {
                parsedMeshes.Add(mesh);
            }
        }

        if (parsedMeshes.Count == 0)
        {
            errorMessage = LocalizedText.T("LtbNoPreviewMesh");
            return false;
        }

        meshes = parsedMeshes;
        return true;
    }

    private static bool TryReadLtbMesh(
        ReadOnlySpan<byte> data,
        string meshName,
        int meshIndex,
        int meshBaseOffset,
        int vertexLookupByteCount,
        bool requireFollowingMesh,
        IReadOnlyList<string> texturePaths,
        out LithTechMesh? mesh,
        out int nextPosition,
        out string? errorMessage)
    {
        mesh = null;
        nextPosition = meshBaseOffset;
        errorMessage = null;

        if (!TryReadUInt16(data, meshBaseOffset + LtbMeshVertexCountOffset, out ushort vertexCount) ||
            !TryReadUInt16(data, meshBaseOffset + LtbMeshFaceCountOffset, out ushort faceCount))
        {
            errorMessage = LocalizedText.Format("LtbMeshHeaderIncomplete", meshName);
            return false;
        }

        if (!TryFindLtbMeshLayout(
                data,
                meshBaseOffset,
                vertexCount,
                faceCount,
                vertexLookupByteCount,
                requireFollowingMesh,
                out LtbMeshLayout layout,
                out ushort meshType,
                out int vertexDataOffset,
                out int indexDataOffset,
                out nextPosition,
                out errorMessage))
        {
            errorMessage ??= LocalizedText.Format("LtbUnsupportedMeshType", meshName, meshType);
            return false;
        }

        int indexCount = faceCount * 3;
        var vertices = new List<LithTechVector3>(vertexCount);
        var textureCoordinates = new List<LithTechVector2>(vertexCount);
        int readPosition = vertexDataOffset;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            if (!TryReadSingle(data, readPosition, out float x) ||
                !TryReadSingle(data, readPosition + 4, out float y) ||
                !TryReadSingle(data, readPosition + 8, out float z) ||
                !float.IsFinite(x) ||
                !float.IsFinite(y) ||
                !float.IsFinite(z))
            {
                errorMessage = $"Mesh '{meshName}' has incomplete or non-finite position data at vertex {vertexIndex + 1}.";
                return false;
            }

            vertices.Add(new LithTechVector3(x, y, z));
            if (TryReadSingle(data, readPosition + layout.TextureCoordinateOffset, out float u) &&
                TryReadSingle(data, readPosition + layout.TextureCoordinateOffset + sizeof(float), out float v))
            {
                if (!float.IsFinite(u) || !float.IsFinite(v))
                {
                    errorMessage = $"Mesh '{meshName}' has non-finite texture coordinates at vertex {vertexIndex + 1}.";
                    return false;
                }

                textureCoordinates.Add(new LithTechVector2(u, v));
            }

            readPosition += layout.VertexStride;
        }

        var triangleIndices = new List<int>(indexCount);
        readPosition = indexDataOffset;
        for (int index = 0; index < indexCount; index++)
        {
            if (!TryReadUInt16(data, readPosition, out ushort triangleIndex))
            {
                errorMessage = LocalizedText.Format("LtbMeshIndexDataIncomplete", meshName);
                return false;
            }

            if (triangleIndex >= vertexCount)
            {
                errorMessage = LocalizedText.Format("LtbMeshIndexOutOfRange", meshName, triangleIndex);
                return false;
            }

            triangleIndices.Add(triangleIndex);
            readPosition += sizeof(ushort);
        }

        if (vertices.Count > 0 && triangleIndices.Count >= 3)
        {
            string displayName = string.IsNullOrWhiteSpace(meshName) ? $"Mesh {meshIndex + 1}" : meshName;
            mesh = new LithTechMesh(
                displayName,
                vertices,
                triangleIndices,
                textureCoordinates.Count == vertices.Count ? textureCoordinates : null,
                ResolveTexturePath(texturePaths, displayName, meshIndex));
        }

        return true;
    }

    private static bool TryFindLtbMeshLayout(
        ReadOnlySpan<byte> data,
        int meshBaseOffset,
        ushort vertexCount,
        ushort faceCount,
        int vertexLookupByteCount,
        bool requireFollowingMesh,
        out LtbMeshLayout layout,
        out ushort meshType,
        out int vertexDataOffset,
        out int indexDataOffset,
        out int nextPosition,
        out string? errorMessage)
    {
        layout = default;
        meshType = 0;
        vertexDataOffset = 0;
        indexDataOffset = 0;
        nextPosition = meshBaseOffset;
        errorMessage = null;

        Span<int> vertexDataOffsets = stackalloc int[96];
        int vertexDataOffsetCount = GetLtbVertexDataOffsetCandidates(data, meshBaseOffset, vertexLookupByteCount, vertexDataOffsets);
        if (vertexDataOffsetCount == 0)
        {
            errorMessage = LocalizedText.T("LtbMeshVertexDataIncomplete");
            return false;
        }

        Span<ushort> meshTypes = stackalloc ushort[8];
        int meshTypeCount = GetLtbMeshTypeCandidates(data, meshBaseOffset, meshTypes);
        if (meshTypeCount == 0)
        {
            errorMessage = LocalizedText.T("LtbMeshTypeDataIncomplete");
            return false;
        }

        int indexCount = faceCount * 3;
        long indexByteCount = (long)indexCount * sizeof(ushort);
        Span<int> candidateNextPositions = stackalloc int[4];
        for (int offsetIndex = 0; offsetIndex < vertexDataOffsetCount; offsetIndex++)
        {
            int candidateVertexDataOffset = vertexDataOffsets[offsetIndex];
            for (int i = 0; i < meshTypeCount; i++)
            {
                ushort candidateType = meshTypes[i];
                if (!TryCreateLtbMeshLayout(candidateType, out LtbMeshLayout candidateLayout))
                {
                    continue;
                }

                long vertexByteCount = (long)vertexCount * candidateLayout.VertexStride;
                if (vertexByteCount > int.MaxValue ||
                    indexByteCount > int.MaxValue ||
                    candidateVertexDataOffset < 0 ||
                    candidateVertexDataOffset + vertexByteCount + indexByteCount > data.Length)
                {
                    errorMessage = LocalizedText.T("LtbMeshGeometryOutOfRange");
                    continue;
                }

                int candidateIndexDataOffset = candidateVertexDataOffset + (int)vertexByteCount;
                if (!AreLtbTriangleIndicesInRange(data, candidateIndexDataOffset, indexCount, vertexCount))
                {
                    errorMessage = LocalizedText.T("LtbMeshIndexOutOfRangeGeneric");
                    continue;
                }

                int postDataPosition = candidateIndexDataOffset + (int)indexByteCount;
                int candidateNextPositionCount = GetLtbMeshPostDataEndCandidates(
                    data,
                    candidateLayout,
                    postDataPosition,
                    allowNoPostData: !requireFollowingMesh,
                    scanForNextMesh: requireFollowingMesh,
                    candidateNextPositions);
                if (candidateNextPositionCount == 0)
                {
                    errorMessage = LocalizedText.T("LtbMeshTrailingDataIncomplete");
                    continue;
                }

                for (int nextIndex = 0; nextIndex < candidateNextPositionCount; nextIndex++)
                {
                    int candidateNextPosition = candidateNextPositions[nextIndex];
                    if (requireFollowingMesh && !LooksLikeLtbMeshAt(data, candidateNextPosition))
                    {
                        errorMessage = LocalizedText.T("LtbMeshTrailingAlignmentFailed");
                        continue;
                    }

                    layout = candidateLayout;
                    meshType = candidateType;
                    vertexDataOffset = candidateVertexDataOffset;
                    indexDataOffset = candidateIndexDataOffset;
                    nextPosition = candidateNextPosition;
                    errorMessage = null;
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetLtbVertexLookupByteCount(ReadOnlySpan<byte> data)
    {
        if (!TryReadUInt32(data, 32, out uint nodeCount) ||
            nodeCount == 0 ||
            nodeCount > 4096)
        {
            return 0;
        }

        return ((int)nodeCount + 1) * sizeof(uint);
    }

    private static int GetLtbVertexDataOffsetCandidates(ReadOnlySpan<byte> data, int meshBaseOffset, int vertexLookupByteCount, Span<int> offsets)
    {
        int count = 0;
        if (TryGetLtbVertexDataOffset(data, meshBaseOffset, out int detectedOffset))
        {
            AddLtbVertexDataOffsetCandidate(offsets, ref count, detectedOffset, data.Length);
        }

        AddLtbVertexDataOffsetCandidate(offsets, ref count, meshBaseOffset + LtbFirstVertexOffset, data.Length);
        AddLtbVertexDataOffsetCandidate(offsets, ref count, meshBaseOffset + LtbFirstVertexDoubleStartOffset, data.Length);
        AddLtbVertexDataOffsetCandidate(offsets, ref count, meshBaseOffset + LtbFirstVertexDoubleStartOffset + sizeof(ushort), data.Length);
        if (vertexLookupByteCount > 0)
        {
            AddLtbVertexDataOffsetCandidate(offsets, ref count, meshBaseOffset + LtbFirstVertexOffset + vertexLookupByteCount, data.Length);
        }

        int vertexLookupFlagOffset = meshBaseOffset + 65;
        if (vertexLookupFlagOffset >= 0 &&
            vertexLookupFlagOffset < data.Length &&
            data[vertexLookupFlagOffset] != 0)
        {
            int scanStart = meshBaseOffset + LtbFirstVertexOffset;
            int scanEnd = Math.Min(scanStart + 320, data.Length - sizeof(float) * 3);
            for (int offset = scanStart; offset <= scanEnd; offset += sizeof(uint))
            {
                AddLtbVertexDataOffsetCandidate(offsets, ref count, offset, data.Length);
            }
        }

        return count;
    }

    private static void AddLtbVertexDataOffsetCandidate(Span<int> offsets, ref int count, int offset, int dataLength)
    {
        if (offset < 0 || offset >= dataLength)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (offsets[i] == offset)
            {
                return;
            }
        }

        if (count < offsets.Length)
        {
            offsets[count++] = offset;
        }
    }

    private static int GetLtbMeshTypeCandidates(ReadOnlySpan<byte> data, int meshBaseOffset, Span<ushort> meshTypes)
    {
        if (!TryReadUInt16(data, meshBaseOffset + LtbMeshTypeHeadOffset, out ushort meshTypeHead) ||
            !TryReadUInt16(data, meshBaseOffset + LtbMeshTypeOffset, out ushort meshTypeOffset))
        {
            return 0;
        }

        int count = 0;
        if (meshTypeHead == LtbMeshTypeSkinnedAlt)
        {
            AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeTwoExtraFloats);
        }
        else if (IsKnownLtbMeshType(meshTypeHead))
        {
            AddLtbMeshTypeCandidate(meshTypes, ref count, meshTypeHead);
        }

        if (IsKnownLtbMeshType(meshTypeOffset))
        {
            AddLtbMeshTypeCandidate(meshTypes, ref count, meshTypeOffset);
        }

        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeNotSkinned);
        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeExtraFloat);
        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeTwoExtraFloats);
        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeSkinnedExtraFloat);
        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeSkinned);
        AddLtbMeshTypeCandidate(meshTypes, ref count, LtbMeshTypeSkinnedAlt);
        return count;
    }

    private static void AddLtbMeshTypeCandidate(Span<ushort> meshTypes, ref int count, ushort meshType)
    {
        for (int i = 0; i < count; i++)
        {
            if (meshTypes[i] == meshType)
            {
                return;
            }
        }

        if (count < meshTypes.Length)
        {
            meshTypes[count++] = meshType;
        }
    }

    private static bool IsKnownLtbMeshType(ushort meshType)
    {
        return meshType is LtbMeshTypeNotSkinned or
               LtbMeshTypeExtraFloat or
               LtbMeshTypeSkinnedAlt or
               LtbMeshTypeSkinned or
               LtbMeshTypeTwoExtraFloats or
               LtbMeshTypeSkinnedExtraFloat;
    }

    private static bool TryCreateLtbMeshLayout(ushort meshType, out LtbMeshLayout layout)
    {
        layout = meshType switch
        {
            LtbMeshTypeNotSkinned => new LtbMeshLayout(IncludeWeights: false, IncludePostData: false, PostNormalByteCount: 0),
            LtbMeshTypeExtraFloat => new LtbMeshLayout(IncludeWeights: false, IncludePostData: true, PostNormalByteCount: sizeof(float)),
            LtbMeshTypeSkinned => new LtbMeshLayout(IncludeWeights: true, IncludePostData: true, PostNormalByteCount: 0),
            LtbMeshTypeSkinnedAlt => new LtbMeshLayout(IncludeWeights: true, IncludePostData: true, PostNormalByteCount: 0),
            LtbMeshTypeTwoExtraFloats => new LtbMeshLayout(IncludeWeights: false, IncludePostData: true, PostNormalByteCount: sizeof(float) * 2),
            LtbMeshTypeSkinnedExtraFloat => new LtbMeshLayout(IncludeWeights: true, IncludePostData: true, PostNormalByteCount: sizeof(float)),
            _ => default
        };

        return IsKnownLtbMeshType(meshType);
    }

    private static bool AreLtbTriangleIndicesInRange(ReadOnlySpan<byte> data, int position, int indexCount, ushort vertexCount)
    {
        for (int index = 0; index < indexCount; index++)
        {
            if (!TryReadUInt16(data, position, out ushort triangleIndex) ||
                triangleIndex >= vertexCount)
            {
                return false;
            }

            position += sizeof(ushort);
        }

        return true;
    }

    private static bool TryGetLtbVertexDataOffset(ReadOnlySpan<byte> data, int meshBaseOffset, out int vertexDataOffset)
    {
        vertexDataOffset = 0;
        int markerOffset = meshBaseOffset + LtbFirstVertexOffset;
        if (markerOffset < 0 || markerOffset + 4 > data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> marker = data[markerOffset..(markerOffset + 4)];
        int relativeOffset = marker[0] == 0 && marker[1] == 0 && marker[2] != 0 && marker[3] != 0
            ? LtbFirstVertexDoubleStartOffset
            : LtbFirstVertexOffset;

        vertexDataOffset = meshBaseOffset + relativeOffset;
        return vertexDataOffset >= 0 && vertexDataOffset < data.Length;
    }

    private static int GetLtbMeshPostDataEndCandidates(
        ReadOnlySpan<byte> data,
        LtbMeshLayout layout,
        int position,
        bool allowNoPostData,
        bool scanForNextMesh,
        Span<int> positions)
    {
        int count = 0;
        if (allowNoPostData)
        {
            AddLtbPostDataEndCandidate(positions, ref count, position, data.Length);
        }

        if (!layout.IncludePostData)
        {
            AddLtbPostDataEndCandidate(positions, ref count, position + sizeof(ushort), data.Length);
        }

        if (TryReadUInt32(data, position, out uint sectionCount) &&
            sectionCount <= 4096)
        {
            long sectionEnd = (long)position + sizeof(uint) + (long)sectionCount * 12;
            if (sectionEnd >= 0 && sectionEnd <= data.Length)
            {
                AddLtbPostDataEndCandidate(positions, ref count, (int)sectionEnd, data.Length);
                AddLtbPostDataEndCandidate(positions, ref count, (int)sectionEnd + sizeof(uint), data.Length);

                if (sectionEnd < data.Length)
                {
                    int finalSectionSize = data[(int)sectionEnd];
                    AddLtbPostDataEndCandidate(positions, ref count, (int)sectionEnd + 1 + finalSectionSize, data.Length);
                }
            }
        }

        if (scanForNextMesh)
        {
            int scanEnd = Math.Min(position + 128, data.Length - LtbFirstVertexOffset);
            for (int candidatePosition = position; candidatePosition <= scanEnd; candidatePosition++)
            {
                if (LooksLikeLtbMeshHeaderAt(data, candidatePosition))
                {
                    AddLtbPostDataEndCandidate(positions, ref count, candidatePosition, data.Length);
                }
            }
        }

        return count;
    }

    private static void AddLtbPostDataEndCandidate(Span<int> positions, ref int count, int position, int dataLength)
    {
        if (position < 0 || position > dataLength)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (positions[i] == position)
            {
                return;
            }
        }

        if (count < positions.Length)
        {
            positions[count++] = position;
        }
    }

    private static bool TryReadLtbString(ReadOnlySpan<byte> data, ref int position, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(data, position, out ushort length))
        {
            return false;
        }

        position += sizeof(ushort);
        if (position < 0 || position + length > data.Length)
        {
            return false;
        }

        value = DecodeLtbString(data.Slice(position, length));
        position += length;
        return true;
    }

    private static string DecodeLtbString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.GetEncoding(949).GetString(bytes).TrimEnd('\0');
        }
        catch
        {
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(ushort) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(uint) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, int offset, out float value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(float) > data.Length)
        {
            return false;
        }

        int bits = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(float)));
        value = BitConverter.Int32BitsToSingle(bits);
        return true;
    }

    internal static bool TryParseShapeTextureBindings(
        string text,
        out IReadOnlyList<LithTechShapeTextureBinding> bindings,
        out string? errorMessage)
    {
        bindings = [];
        errorMessage = null;
        try
        {
            LtaList root = new LtaParser(text).ParseRoot();
            var parsed = new List<LithTechShapeTextureBinding>();
            foreach (LtaList shape in FindListsByHead(root, "shape"))
            {
                string shapeName = GetAtomValue(shape.Items.Skip(1).FirstOrDefault()) ?? string.Empty;
                if (shapeName.Length == 0)
                {
                    continue;
                }

                LtaList? textureIndicesNode = shape.Children.FirstOrDefault(child => IsHead(child, "texture-indices"));
                int[] textureIndices = textureIndicesNode is null ? [] : ParseIntList(textureIndicesNode).ToArray();
                LtaList? renderStyleNode = shape.Children.FirstOrDefault(child => IsHead(child, "renderstyle-index"));
                int? renderStyleIndex = renderStyleNode is not null &&
                                        int.TryParse(
                                            GetAtomValue(renderStyleNode.Items.Skip(1).FirstOrDefault()),
                                            NumberStyles.Integer,
                                            CultureInfo.InvariantCulture,
                                            out int parsedRenderStyle)
                    ? parsedRenderStyle
                    : null;
                parsed.Add(new LithTechShapeTextureBinding(shapeName, textureIndices, renderStyleIndex));
            }

            bindings = parsed;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    internal static bool TryParseLtaText(
        string text,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        try
        {
            var parser = new LtaParser(text);
            LtaList root = parser.ParseRoot();
            string rootHead = GetAtomValue(root.Items.FirstOrDefault()) ?? string.Empty;
            bool isWorld = string.Equals(rootHead, "world", StringComparison.OrdinalIgnoreCase);
            bool isShape = string.Equals(rootHead, "shape", StringComparison.OrdinalIgnoreCase);
            bool isModel = string.Equals(rootHead, "lt-model", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("(lt-model", StringComparison.OrdinalIgnoreCase);

            List<LithTechMesh> meshes;
            string documentStorageDescription = storageDescription;
            if (isWorld)
            {
                meshes = ParseWorldMeshes(root);
                documentStorageDescription = $"{storageDescription} world";
            }
            else if (isModel || isShape)
            {
                meshes = FindListsByHead(root, "mesh")
                    .Select(ParseMesh)
                    .Where(mesh => mesh is not null)
                    .Cast<LithTechMesh>()
                    .ToList();
            }
            else
            {
                errorMessage = "LTA text is not a recognized model or world document.";
                return false;
            }

            if (meshes.Count == 0)
            {
                errorMessage = isWorld
                    ? "LTA world contains no previewable polyhedron geometry."
                    : "LTA model contains no previewable mesh geometry.";
                return false;
            }

            string name = isWorld
                ? fallbackName
                : isShape
                    ? GetAtomValue(root.Items.Skip(1).FirstOrDefault()) ?? fallbackName
                    : GetAtomValue(root.Items.FirstOrDefault()) ?? fallbackName;
            document = new LithTechModelDocument(name, meshes, documentStorageDescription, sourceByteCount, decodedByteCount);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? TryConvertLtbToLtaText(byte[] rawLtb, string fallbackName, out string? errorMessage)
    {
        errorMessage = null;

        string? unpackerPath = ResolveModelUnpackerPath();
        if (unpackerPath is null)
        {
            errorMessage = LocalizedText.T("LtbConverterMissing");
            return null;
        }

        string workingDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "LtbConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        string baseName = Path.GetFileNameWithoutExtension(fallbackName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        string inputPath = Path.Combine(workingDirectory, $"{baseName}.raw.ltb");
        string outputPath = Path.Combine(workingDirectory, $"{baseName}.lta");
        File.WriteAllBytes(inputPath, rawLtb);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = unpackerPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("d3d");
            startInfo.ArgumentList.Add("-input");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-verbose");

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = LocalizedText.T("ModelUnpackerStartFailed");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ExternalConverterTimeoutMilliseconds))
            {
                TryKillProcess(process);
                errorMessage = LocalizedText.T("ModelUnpackerTimeout");
                return null;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                errorMessage = string.IsNullOrWhiteSpace(detail)
                    ? LocalizedText.Format("ModelUnpackerFailedExitCode", process.ExitCode)
                    : detail.Trim();
                return null;
            }

            return File.ReadAllText(outputPath, Encoding.ASCII);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string? ResolveModelUnpackerPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CFREZ_MODEL_UNPACKER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "Model_Unpacker.exe"),
            Path.Combine(AppContext.BaseDirectory, "Model_Unpacker.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "Model_Unpacker.exe"),
            Path.Combine(Environment.CurrentDirectory, "Model_Unpacker.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // External converters may already have exited after the timeout check.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary conversion files are best-effort cleanup only.
        }
    }

    private static LithTechMesh? ParseMesh(LtaList meshNode)
    {
        string name = GetAtomValue(meshNode.Items.Skip(1).FirstOrDefault()) ?? "Mesh";
        LtaList? vertexNode = meshNode.Children.FirstOrDefault(child => IsHead(child, "vertex"));
        LtaList? triNode = meshNode.Children.FirstOrDefault(child => IsHead(child, "tri-fs"));
        if (vertexNode is null || triNode is null)
        {
            return null;
        }

        List<LithTechVector3> vertices = ParseVector3List(vertexNode);
        List<int> triangleIndices = ParseIntList(triNode)
            .Where(index => index >= 0 && index < vertices.Count)
            .ToList();
        List<LithTechVector2>? textureCoordinates = ParseTextureCoordinates(meshNode, vertices.Count);
        string? texturePath = FindTexturePath(meshNode);
        List<string> materialHints = FindMaterialHints(meshNode);

        int usableIndexCount = triangleIndices.Count - triangleIndices.Count % 3;
        if (vertices.Count == 0 || usableIndexCount < 3)
        {
            return null;
        }

        if (usableIndexCount != triangleIndices.Count)
        {
            triangleIndices.RemoveRange(usableIndexCount, triangleIndices.Count - usableIndexCount);
        }

        return new LithTechMesh(name, vertices, triangleIndices, textureCoordinates, texturePath, materialHints);
    }

    private static List<LithTechMesh> ParseWorldMeshes(LtaList root)
    {
        var meshes = new List<LithTechMesh>();
        int polyhedronIndex = 1;
        foreach (LtaList polyhedronNode in FindListsByHead(root, "polyhedron"))
        {
            LithTechMesh? mesh = ParseWorldPolyhedron(polyhedronNode, polyhedronIndex);
            if (mesh is not null)
            {
                meshes.Add(mesh);
            }

            polyhedronIndex++;
        }

        return meshes;
    }

    private static LithTechMesh? ParseWorldPolyhedron(LtaList polyhedronNode, int polyhedronIndex)
    {
        LtaList? pointListNode = FindListsByHead(polyhedronNode, "pointlist").FirstOrDefault();
        LtaList? polyListNode = FindListsByHead(polyhedronNode, "polylist").FirstOrDefault();
        if (pointListNode is null || polyListNode is null)
        {
            return null;
        }

        List<LithTechVector3> vertices = ParseDirectVector3List(pointListNode);
        if (vertices.Count == 0)
        {
            return null;
        }

        var triangleIndices = new List<int>();
        foreach (LtaList editPolyNode in FindListsByHead(polyListNode, "editpoly"))
        {
            LtaList? faceNode = editPolyNode.Children.FirstOrDefault(child => IsHead(child, "f"));
            if (faceNode is null)
            {
                continue;
            }

            List<int> faceIndices = ParseIntList(faceNode)
                .Where(index => index >= 0 && index < vertices.Count)
                .ToList();
            if (faceIndices.Count < 3)
            {
                continue;
            }

            int firstIndex = faceIndices[0];
            for (int index = 1; index + 1 < faceIndices.Count; index++)
            {
                triangleIndices.Add(firstIndex);
                triangleIndices.Add(faceIndices[index]);
                triangleIndices.Add(faceIndices[index + 1]);
            }
        }

        return triangleIndices.Count >= 3
            ? new LithTechMesh($"Polyhedron {polyhedronIndex}", vertices, triangleIndices)
            : null;
    }

    private static List<LithTechVector3> ParseVector3List(LtaList node)
    {
        var result = new List<LithTechVector3>();
        foreach (LtaList child in node.Children.SelectMany(GetChildren))
        {
            if (child.Items.Count < 3)
            {
                continue;
            }

            if (TryReadDouble(child.Items[0], out double x) &&
                TryReadDouble(child.Items[1], out double y) &&
                TryReadDouble(child.Items[2], out double z))
            {
                result.Add(new LithTechVector3(x, y, z));
            }
        }

        return result;
    }

    private static List<LithTechVector2>? ParseTextureCoordinates(LtaList meshNode, int vertexCount)
    {
        if (vertexCount <= 0)
        {
            return null;
        }

        foreach (LtaList coordinateNode in FindListsByHeads(meshNode, LtaTextureCoordinateHeads))
        {
            List<LithTechVector2> coordinates = ParseVector2List(coordinateNode);
            if (coordinates.Count == vertexCount)
            {
                return coordinates;
            }
        }

        return null;
    }

    private static List<LithTechVector2> ParseVector2List(LtaList node)
    {
        var result = new List<LithTechVector2>();
        CollectVector2(node, result);
        return result;
    }

    private static void CollectVector2(LtaList node, List<LithTechVector2> result)
    {
        int startIndex = node.Items.FirstOrDefault() is LtaAtom firstAtom &&
                         double.TryParse(firstAtom.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? 0
            : 1;
        if (node.Items.Count - startIndex >= 2 &&
            TryReadDouble(node.Items[startIndex], out double x) &&
            TryReadDouble(node.Items[startIndex + 1], out double y))
        {
            result.Add(new LithTechVector2(x, y));
        }

        foreach (LtaList child in node.Children)
        {
            CollectVector2(child, result);
        }
    }

    private static List<LithTechVector3> ParseDirectVector3List(LtaList node)
    {
        var result = new List<LithTechVector3>();
        foreach (LtaList child in node.Children)
        {
            if (child.Items.Count < 3)
            {
                continue;
            }

            if (TryReadDouble(child.Items[0], out double x) &&
                TryReadDouble(child.Items[1], out double y) &&
                TryReadDouble(child.Items[2], out double z))
            {
                result.Add(new LithTechVector3(x, y, z));
            }
        }

        return result;
    }

    private static List<int> ParseIntList(LtaList node)
    {
        var values = new List<int>();
        CollectInts(node.Items.Skip(1), values);
        return values;
    }

    private static string? FindTexturePath(LtaList node)
    {
        var paths = new List<string>();
        CollectTexturePaths(node, paths);
        return paths.Count > 0 ? paths[0] : null;
    }

    private static List<string> FindMaterialHints(LtaList node)
    {
        var hints = new List<string>();
        CollectMaterialHints(node, hints);
        return hints;
    }

    private static void CollectMaterialHints(LtaList node, List<string> hints)
    {
        string head = GetAtomValue(node.Items.FirstOrDefault()) ?? string.Empty;
        bool isMaterialContext = IsMaterialContextHead(head);
        if (isMaterialContext)
        {
            foreach (LtaAtom atom in node.Items.OfType<LtaAtom>().Skip(1))
            {
                AddMaterialHint(atom.Value, hints);
            }
        }

        foreach (LtaList child in node.Children)
        {
            CollectMaterialHints(child, hints);
        }
    }

    private static bool IsMaterialContextHead(string value)
    {
        return IsTextureContextHead(value) ||
               value.Contains("surface", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("renderstyle", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMaterialHint(string value, List<string> hints)
    {
        string normalized = NormalizeTexturePath(value);
        if (!LooksLikeMaterialHint(normalized))
        {
            return;
        }

        string withoutExtension = Path.GetFileNameWithoutExtension(normalized);
        string hint = string.IsNullOrWhiteSpace(withoutExtension) ? normalized : withoutExtension;
        if (!string.IsNullOrWhiteSpace(hint) && !hints.Contains(hint, StringComparer.OrdinalIgnoreCase))
        {
            hints.Add(hint);
        }
    }

    private static bool LooksLikeMaterialHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < 3 ||
            value.Length > 128 ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (value.Contains('(') || value.Contains(')'))
        {
            return false;
        }

        string lower = value.ToLowerInvariant();
        return lower is not "none" and not "null" and not "default" and not "true" and not "false";
    }

    private static void CollectTexturePaths(LtaList node, List<string> paths)
    {
        string head = GetAtomValue(node.Items.FirstOrDefault()) ?? string.Empty;
        bool isTextureContext = IsTextureContextHead(head);
        foreach (LtaAtom atom in node.Items.OfType<LtaAtom>())
        {
            AddTexturePathsFromText(atom.Value, paths);
            if (isTextureContext && LooksLikeTextureReference(atom.Value))
            {
                string path = NormalizeTexturePath(atom.Value);
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
        }

        foreach (LtaList child in node.Children)
        {
            CollectTexturePaths(child, paths);
        }
    }

    private static bool IsTextureContextHead(string value)
    {
        return value.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("material", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("shader", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTextureReference(string value)
    {
        string normalized = NormalizeTexturePath(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (normalized.Contains('/') || normalized.Contains('\\'))
        {
            return true;
        }

        string extension = Path.GetExtension(normalized);
        return TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectInts(IEnumerable<object> items, List<int> values)
    {
        foreach (object item in items)
        {
            if (item is LtaAtom atom &&
                int.TryParse(atom.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                values.Add(value);
            }
            else if (item is LtaList list)
            {
                CollectInts(list.Items, values);
            }
        }
    }

    private static bool TryReadDouble(object item, out double value)
    {
        value = 0;
        return item is LtaAtom atom &&
               double.TryParse(atom.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IEnumerable<LtaList> FindListsByHead(LtaList node, string head)
    {
        if (IsHead(node, head))
        {
            yield return node;
        }

        foreach (LtaList child in node.Children)
        {
            foreach (LtaList match in FindListsByHead(child, head))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<LtaList> FindListsByHeads(LtaList node, IReadOnlyCollection<string> heads)
    {
        string nodeHead = GetAtomValue(node.Items.FirstOrDefault()) ?? string.Empty;
        if (heads.Contains(nodeHead, StringComparer.OrdinalIgnoreCase))
        {
            yield return node;
        }

        foreach (LtaList child in node.Children)
        {
            foreach (LtaList match in FindListsByHeads(child, heads))
            {
                yield return match;
            }
        }
    }

    private static bool IsHead(LtaList list, string value)
    {
        return string.Equals(GetAtomValue(list.Items.FirstOrDefault()), value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAtomValue(object? item)
    {
        return item is LtaAtom atom ? atom.Value : null;
    }

    private static IEnumerable<LtaList> GetChildren(LtaList list)
    {
        return list.Children;
    }

    private static List<string> ExtractEmbeddedTexturePaths(ReadOnlySpan<byte> data)
    {
        var paths = new List<string>();
        var builder = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];
            if (value is >= 0x20 and <= 0x7E)
            {
                builder.Append((char)value);
                if (builder.Length < 4096)
                {
                    continue;
                }
            }

            AddTexturePathsFromText(builder.ToString(), paths);
            builder.Clear();
        }

        if (builder.Length > 0)
        {
            AddTexturePathsFromText(builder.ToString(), paths);
        }

        return paths;
    }

    private static void AddTexturePathsFromText(string text, List<string> paths)
    {
        foreach (string extension in TextureExtensions)
        {
            int searchStart = 0;
            while (searchStart < text.Length)
            {
                int extensionIndex = text.IndexOf(extension, searchStart, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    break;
                }

                int start = extensionIndex;
                while (start > 0 && IsTexturePathCharacter(text[start - 1]))
                {
                    start--;
                }

                int end = extensionIndex + extension.Length;
                string path = NormalizeTexturePath(text[start..end]);
                if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }

                searchStart = end;
            }
        }
    }

    private static bool IsTexturePathCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '_' or '-' or '.' or '/' or '\\' or ' ';
    }

    private static string NormalizeTexturePath(string path)
    {
        return path
            .Trim()
            .Trim('"', '\'')
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string? ResolveTexturePath(IReadOnlyList<string> texturePaths, string meshName, int meshIndex)
    {
        if (texturePaths.Count == 0)
        {
            return null;
        }

        string comparableMeshName = Path.GetFileNameWithoutExtension(meshName);
        if (!string.IsNullOrWhiteSpace(comparableMeshName))
        {
            foreach (string texturePath in texturePaths)
            {
                string textureName = Path.GetFileNameWithoutExtension(texturePath);
                if (!string.IsNullOrWhiteSpace(textureName) &&
                    (textureName.Contains(comparableMeshName, StringComparison.OrdinalIgnoreCase) ||
                     comparableMeshName.Contains(textureName, StringComparison.OrdinalIgnoreCase)))
                {
                    return texturePath;
                }
            }
        }

        if (texturePaths.Count == 1)
        {
            return texturePaths[0];
        }

        return meshIndex >= 0 && meshIndex < texturePaths.Count
            ? texturePaths[meshIndex]
            : null;
    }

    private readonly record struct LtbMeshTableCandidate(int MeshCountOffset, int FirstMeshOffset, uint MeshCount);

    private readonly record struct LtbMeshLayout(bool IncludeWeights, bool IncludePostData, int PostNormalByteCount)
    {
        public int VertexStride => 12 +
                                   (IncludeWeights ? 12 : 0) +
                                   12 +
                                   PostNormalByteCount +
                                   8;

        public int TextureCoordinateOffset => VertexStride - 8;
    }

    private sealed class LtaParser
    {
        private readonly string _text;
        private int _position;

        public LtaParser(string text)
        {
            _text = text;
        }

        public LtaList ParseRoot()
        {
            SkipWhiteSpace();
            return ParseList(0);
        }

        private LtaList ParseList(int depth)
        {
            if (depth > MaxParseDepth)
            {
                throw new FormatException("LTA nesting is too deep.");
            }

            Expect('(');
            var list = new LtaList();
            while (true)
            {
                SkipWhiteSpace();
                if (_position >= _text.Length)
                {
                    throw new FormatException("Unexpected end of LTA data.");
                }

                char current = _text[_position];
                if (current == ')')
                {
                    _position++;
                    return list;
                }

                list.Items.Add(current == '(' ? ParseList(depth + 1) : ParseAtom());
            }
        }

        private LtaAtom ParseAtom()
        {
            if (_text[_position] == '"')
            {
                return new LtaAtom(ParseString());
            }

            int start = _position;
            while (_position < _text.Length &&
                   !char.IsWhiteSpace(_text[_position]) &&
                   _text[_position] is not '(' and not ')')
            {
                _position++;
            }

            return new LtaAtom(_text[start.._position]);
        }

        private string ParseString()
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                char current = _text[_position++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\' && _position < _text.Length)
                {
                    builder.Append(_text[_position++]);
                }
                else
                {
                    builder.Append(current);
                }
            }

            throw new FormatException("Unterminated LTA string.");
        }

        private void Expect(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected)
            {
                throw new FormatException($"Expected '{expected}'.");
            }

            _position++;
        }

        private void SkipWhiteSpace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }
    }

    private sealed class LtaList
    {
        public List<object> Items { get; } = new();

        public IEnumerable<LtaList> Children => Items.OfType<LtaList>();
    }

    private sealed record LtaAtom(string Value);
}
