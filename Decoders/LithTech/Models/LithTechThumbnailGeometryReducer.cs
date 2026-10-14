namespace CFRezManager;

internal static class LithTechThumbnailGeometryReducer
{
    private const int DefaultMaxTriangles = 24_000;
    private const int DefaultMaxVertices = 48_000;
    private const int DefaultMaxOutputMeshes = 6;
    private const int InteractiveMaxTriangles = 80_000;
    private const int InteractiveMaxVertices = 120_000;
    private const int InteractiveMaxOutputMeshes = 96;

    public static LithTechModelDocument ReduceForThumbnail(
        LithTechModelDocument document,
        int maxTriangles = DefaultMaxTriangles,
        int maxVertices = DefaultMaxVertices,
        int maxOutputMeshes = DefaultMaxOutputMeshes)
    {
        return Reduce(document, maxTriangles, maxVertices, maxOutputMeshes, "thumbnail sample");
    }

    public static LithTechModelDocument ReduceForInteractivePreview(LithTechModelDocument document)
    {
        return Reduce(
            document,
            InteractiveMaxTriangles,
            InteractiveMaxVertices,
            InteractiveMaxOutputMeshes,
            "interactive sample");
    }

    private static LithTechModelDocument Reduce(
        LithTechModelDocument document,
        int maxTriangles,
        int maxVertices,
        int maxOutputMeshes,
        string sampleDescription)
    {
        if (document.Meshes.Count == 0 || maxTriangles <= 0 || maxVertices <= 0 || maxOutputMeshes <= 0)
        {
            return document;
        }

        int referencedVertexCount = CountReferencedVertices(document);
        if (document.TriangleCount <= maxTriangles &&
            referencedVertexCount <= maxVertices &&
            document.Meshes.Count <= maxOutputMeshes)
        {
            return CompactUnusedVertices(document);
        }

        return SampleGeometry(document, maxTriangles, maxVertices, maxOutputMeshes, sampleDescription);
    }

    private static int CountReferencedVertices(LithTechModelDocument document)
    {
        int count = 0;
        foreach (LithTechMesh mesh in document.Meshes)
        {
            if (mesh.TriangleIndices.Count < 3 || mesh.Vertices.Count == 0)
            {
                continue;
            }

            var referenced = new HashSet<int>();
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                int a = mesh.TriangleIndices[i];
                int b = mesh.TriangleIndices[i + 1];
                int c = mesh.TriangleIndices[i + 2];
                if (IsValidTriangle(mesh, a, b, c))
                {
                    referenced.Add(a);
                    referenced.Add(b);
                    referenced.Add(c);
                }
            }

            count += referenced.Count;
        }

        return count;
    }

    private static LithTechModelDocument CompactUnusedVertices(LithTechModelDocument document)
    {
        var meshes = new List<LithTechMesh>(document.Meshes.Count);
        bool changed = false;
        foreach (LithTechMesh mesh in document.Meshes)
        {
            LithTechMesh? compacted = CompactMesh(mesh, out bool meshChanged);
            if (compacted is null)
            {
                changed = true;
                continue;
            }

            meshes.Add(compacted);
            changed |= meshChanged;
        }

        return !changed
            ? document
            : document with { Meshes = meshes };
    }

    private static LithTechMesh? CompactMesh(LithTechMesh mesh, out bool changed)
    {
        changed = false;
        if (mesh.TriangleIndices.Count < 3 || mesh.Vertices.Count == 0)
        {
            changed = true;
            return null;
        }

        var vertexMap = new Dictionary<int, int>();
        var vertices = new List<LithTechVector3>();
        List<LithTechVector2>? textureCoordinates = mesh.HasTextureCoordinates ? [] : null;
        var indices = new List<int>(mesh.TriangleIndices.Count);

        for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
        {
            int a = mesh.TriangleIndices[i];
            int b = mesh.TriangleIndices[i + 1];
            int c = mesh.TriangleIndices[i + 2];
            if (!IsValidTriangle(mesh, a, b, c))
            {
                changed = true;
                continue;
            }

            indices.Add(GetMappedIndex(mesh, a, vertexMap, vertices, textureCoordinates));
            indices.Add(GetMappedIndex(mesh, b, vertexMap, vertices, textureCoordinates));
            indices.Add(GetMappedIndex(mesh, c, vertexMap, vertices, textureCoordinates));
        }

        if (indices.Count == 0)
        {
            changed = true;
            return null;
        }

        changed |= vertices.Count != mesh.Vertices.Count || indices.Count != mesh.TriangleIndices.Count;
        if (!changed)
        {
            return mesh;
        }

        return new LithTechMesh(
            mesh.Name,
            vertices,
            indices,
            textureCoordinates,
            mesh.TexturePath,
            mesh.MaterialHints,
            mesh.TextureSlotIndices);
    }

    private static LithTechModelDocument SampleGeometry(
        LithTechModelDocument document,
        int maxTriangles,
        int maxVertices,
        int maxOutputMeshes,
        string sampleDescription)
    {
        int totalTriangles = CountValidTriangles(document);
        if (totalTriangles == 0)
        {
            return document;
        }

        int targetTriangles = Math.Min(totalTriangles, maxTriangles);
        targetTriangles = Math.Min(targetTriangles, Math.Max(1, maxVertices / 3));
        int stride = Math.Max(1, (int)Math.Ceiling((double)totalTriangles / targetTriangles));
        int bucketCount = Math.Max(1, maxOutputMeshes);
        OutputBucket[] buckets = Enumerable.Range(0, bucketCount).Select(_ => new OutputBucket()).ToArray();
        int selectedTriangles = 0;
        int outputVertices = 0;
        int globalTriangle = 0;

        for (int meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            LithTechMesh mesh = document.Meshes[meshIndex];
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                int a = mesh.TriangleIndices[i];
                int b = mesh.TriangleIndices[i + 1];
                int c = mesh.TriangleIndices[i + 2];
                if (!IsValidTriangle(mesh, a, b, c))
                {
                    continue;
                }

                if (globalTriangle % stride == 0)
                {
                    OutputBucket bucket = buckets[meshIndex % bucketCount];
                    int requiredNewVertices = bucket.CountNewVertices(meshIndex, a, b, c);
                    if (outputVertices + requiredNewVertices <= maxVertices &&
                        bucket.TryAddTriangle(meshIndex, mesh, a, b, c, out int addedVertices))
                    {
                        outputVertices += addedVertices;
                        selectedTriangles++;
                        if (selectedTriangles >= targetTriangles)
                        {
                            return CreateSampledDocument(document, buckets, selectedTriangles, sampleDescription);
                        }
                    }
                }

                globalTriangle++;
            }
        }

        return selectedTriangles == 0
            ? document
            : CreateSampledDocument(document, buckets, selectedTriangles, sampleDescription);
    }

    private static int CountValidTriangles(LithTechModelDocument document)
    {
        int count = 0;
        foreach (LithTechMesh mesh in document.Meshes)
        {
            for (int i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                if (IsValidTriangle(mesh, mesh.TriangleIndices[i], mesh.TriangleIndices[i + 1], mesh.TriangleIndices[i + 2]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static LithTechModelDocument CreateSampledDocument(
        LithTechModelDocument document,
        IReadOnlyList<OutputBucket> buckets,
        int selectedTriangles,
        string sampleDescription)
    {
        var meshes = new List<LithTechMesh>();
        for (int i = 0; i < buckets.Count; i++)
        {
            LithTechMesh? mesh = buckets[i].ToMesh($"Thumbnail {i + 1}");
            if (mesh is not null)
            {
                meshes.Add(mesh);
            }
        }

        return meshes.Count == 0
            ? document
            : document with
            {
                Meshes = meshes,
                StorageDescription = $"{document.StorageDescription} {sampleDescription} ({selectedTriangles:N0} triangles)"
            };
    }

    private static bool IsValidTriangle(LithTechMesh mesh, int a, int b, int c)
    {
        return a >= 0 && b >= 0 && c >= 0 &&
               a < mesh.Vertices.Count &&
               b < mesh.Vertices.Count &&
               c < mesh.Vertices.Count;
    }

    private static int GetMappedIndex(
        LithTechMesh mesh,
        int sourceIndex,
        Dictionary<int, int> vertexMap,
        List<LithTechVector3> vertices,
        List<LithTechVector2>? textureCoordinates)
    {
        if (vertexMap.TryGetValue(sourceIndex, out int mappedIndex))
        {
            return mappedIndex;
        }

        mappedIndex = vertices.Count;
        vertexMap.Add(sourceIndex, mappedIndex);
        vertices.Add(mesh.Vertices[sourceIndex]);
        if (textureCoordinates is not null && mesh.TextureCoordinates is not null)
        {
            textureCoordinates.Add(mesh.TextureCoordinates[sourceIndex]);
        }

        return mappedIndex;
    }

    private readonly record struct VertexKey(int MeshIndex, int VertexIndex);

    private sealed class OutputBucket
    {
        private readonly Dictionary<VertexKey, int> _vertexMap = [];
        private readonly List<LithTechVector3> _vertices = [];
        private readonly List<int> _indices = [];

        public int CountNewVertices(int meshIndex, int a, int b, int c)
        {
            int count = 0;
            if (!_vertexMap.ContainsKey(new VertexKey(meshIndex, a)))
            {
                count++;
            }

            if (b != a && !_vertexMap.ContainsKey(new VertexKey(meshIndex, b)))
            {
                count++;
            }

            if (c != a && c != b && !_vertexMap.ContainsKey(new VertexKey(meshIndex, c)))
            {
                count++;
            }

            return count;
        }

        public bool TryAddTriangle(
            int meshIndex,
            LithTechMesh mesh,
            int a,
            int b,
            int c,
            out int addedVertices)
        {
            addedVertices = 0;
            _indices.Add(GetMappedIndex(meshIndex, mesh, a, ref addedVertices));
            _indices.Add(GetMappedIndex(meshIndex, mesh, b, ref addedVertices));
            _indices.Add(GetMappedIndex(meshIndex, mesh, c, ref addedVertices));
            return true;
        }

        public LithTechMesh? ToMesh(string name)
        {
            return _indices.Count >= 3
                ? new LithTechMesh(name, _vertices, _indices)
                : null;
        }

        private int GetMappedIndex(
            int meshIndex,
            LithTechMesh mesh,
            int sourceIndex,
            ref int addedVertices)
        {
            var key = new VertexKey(meshIndex, sourceIndex);
            if (_vertexMap.TryGetValue(key, out int mappedIndex))
            {
                return mappedIndex;
            }

            mappedIndex = _vertices.Count;
            _vertexMap.Add(key, mappedIndex);
            _vertices.Add(mesh.Vertices[sourceIndex]);
            addedVertices++;
            return mappedIndex;
        }
    }
}
