using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CFRezManager;

/// <summary>
/// Proves texture-layout compatibility between CFT-registered player-view arms,
/// same-side arm variants, and embedded item-preview meshes. Compatibility does
/// not establish the donor model's default skin.
/// </summary>
internal static class CftPlayerViewArmEquivalence
{
    public static bool IsEmbeddedPreviewModel(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("Models/PLAYERVIEW/PV-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlayerViewArmModel(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("ArmModel/Arm_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Models/PLAYERVIEW/Arm_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Models/PLAYERVIEW/ArmModel/Arm_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFamilyVariantOf(string targetPath, string sourcePath)
    {
        return TryGetFamily(targetPath, out string targetFamily, out string targetSide) &&
               TryGetFamily(sourcePath, out string sourceFamily, out string sourceSide) &&
               targetSide.Equals(sourceSide, StringComparison.OrdinalIgnoreCase) &&
               sourceFamily.StartsWith(targetFamily + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetFamily(string path, out string family, out string side)
    {
        family = side = string.Empty;
        string normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("ArmModel/Arm_", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("Models/PLAYERVIEW/Arm_", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("Models/PLAYERVIEW/ArmModel/Arm_", StringComparison.OrdinalIgnoreCase))
            return false;
        string stem = Path.GetFileNameWithoutExtension(normalized);
        int separator = stem.LastIndexOf('_');
        if (separator <= 4) return false;
        side = stem[(separator + 1)..];
        if (!side.Equals("BL", StringComparison.OrdinalIgnoreCase) &&
            !side.Equals("GR", StringComparison.OrdinalIgnoreCase) &&
            !side.Equals("SP", StringComparison.OrdinalIgnoreCase)) return false;
        family = stem[4..separator];
        return family.Length > 0;
    }

    public static string? GetConsistentLayoutSignature(IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0) return null;
        string? signature = null;
        foreach (LithTechModelDocument document in documents)
        {
            string? current = GetLayoutSignature(document);
            if (current is null || signature is not null && current != signature) return null;
            signature = current;
        }
        return signature;
    }

    public static IReadOnlyDictionary<int, string>? GetConsistentTextureSlotLayoutSignatures(
        IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0) return null;
        Dictionary<int, string>? expected = null;
        foreach (LithTechModelDocument document in documents)
        {
            Dictionary<int, string>? current = GetTextureSlotLayoutSignatures(document);
            if (current is null || expected is not null &&
                (expected.Count != current.Count || expected.Any(pair =>
                    !current.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            {
                return null;
            }

            expected ??= current;
        }

        return expected;
    }

    public static IReadOnlyDictionary<int, string>? GetConsistentTextureSlotGeometryUvSignatures(
        IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0) return null;
        Dictionary<int, string>? expected = null;
        foreach (LithTechModelDocument document in documents)
        {
            Dictionary<int, string>? current = GetTextureSlotGeometryUvSignatures(document);
            if (current is null || expected is not null &&
                (expected.Count != current.Count || expected.Any(pair =>
                    !current.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            {
                return null;
            }

            expected ??= current;
        }

        return expected;
    }

    public static IReadOnlyDictionary<(int Slot, string MeshName), string>? GetConsistentRoleMeshGeometryUvSignatures(
        IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0) return null;
        Dictionary<(int Slot, string MeshName), string>? expected = null;
        foreach (LithTechModelDocument document in documents)
        {
            var current = new Dictionary<(int Slot, string MeshName), string>();
            foreach (LithTechMesh mesh in document.Meshes)
            {
                bool isHand = mesh.Name.Contains("hand", StringComparison.OrdinalIgnoreCase);
                bool isArm = mesh.Name.Contains("arm", StringComparison.OrdinalIgnoreCase);
                if (isHand && isArm) return null;
                if (!isHand && !isArm) continue;
                if (mesh.TextureSlotIndices is not { Count: 1 } slots || slots[0] < 0)
                {
                    return null;
                }

                string meshName = mesh.Name.Trim().ToUpperInvariant();
                string? signature = GetTextureGeometryUvSignature(mesh);
                if (signature is null || !current.TryAdd((slots[0], meshName), signature))
                {
                    return null;
                }
            }

            if (current.Count == 0 || expected is not null &&
                (expected.Count != current.Count || expected.Any(pair =>
                    !current.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            {
                return null;
            }

            expected ??= current;
        }

        return expected;
    }

    public static IReadOnlyDictionary<int, string>? GetConsistentTextureSlotUvTriangleSignatures(
        IReadOnlyList<LithTechModelDocument> documents)
    {
        if (documents.Count == 0) return null;
        Dictionary<int, string>? expected = null;
        foreach (LithTechModelDocument document in documents)
        {
            Dictionary<int, string>? current = GetTextureSlotUvTriangleSignatures(document);
            if (current is null || expected is not null &&
                (expected.Count != current.Count || expected.Any(pair =>
                    !current.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))))
            {
                return null;
            }

            expected ??= current;
        }

        return expected;
    }

    private static Dictionary<int, string>? GetTextureSlotLayoutSignatures(LithTechModelDocument document)
    {
        if (document.Meshes.Count == 0) return null;
        int[] slots = document.Meshes
            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
            .Distinct()
            .Order()
            .ToArray();
        if (slots.Length == 0) return null;

        var signatures = new Dictionary<int, string>();
        foreach (int slot in slots)
        {
            string?[] meshSignatures = document.Meshes
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slot))
                .Select(GetTextureLayoutSignature)
                .ToArray();
            if (meshSignatures.Length == 0 || meshSignatures.Any(string.IsNullOrEmpty)) return null;
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            writer.Write(slot);
            writer.Write(meshSignatures.Length);
            foreach (string meshSignature in meshSignatures.Select(signature => signature!).Order(StringComparer.Ordinal))
            {
                writer.Write(meshSignature);
            }

            writer.Flush();
            signatures.Add(slot, Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
        }

        return signatures;
    }

    private static string? GetTextureLayoutSignature(LithTechMesh mesh)
    {
        if (!mesh.HasAuthoritativeTextureBindings || !TryGetTextureCoordinateSets(mesh, out IReadOnlyList<IReadOnlyList<LithTechVector2>?> uvSets) ||
            mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0 ||
            mesh.TextureSlotIndices is not { Count: > 0 } slots || slots.Any(slot => slot < 0) ||
            !string.IsNullOrWhiteSpace(mesh.TexturePath))
        {
            return null;
        }

        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(mesh.Name.ToUpperInvariant());
        writer.Write(slots.Count);
        foreach (int slot in slots) writer.Write(slot);
        writer.Write(mesh.RenderStyleIndex.HasValue);
        writer.Write(mesh.RenderStyleIndex ?? -1);
        writer.Write(uvSets.Count);
        writer.Write(mesh.Vertices.Count);
        foreach (LithTechVector3 vertex in mesh.Vertices)
        {
            if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)) return null;
        }

        for (int setIndex = 0; setIndex < uvSets.Count; setIndex++)
        {
            IReadOnlyList<LithTechVector2>? uvSet = uvSets[setIndex];
            writer.Write(uvSet is not null);
            writer.Write(mesh.GetTextureCoordinateSourceStreamIndex(setIndex) ?? -1);
            if (uvSet is null) continue;
            foreach (LithTechVector2 uv in uvSet)
            {
                if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return null;
                writer.Write(uv.X);
                writer.Write(uv.Y);
            }
        }

        writer.Write(mesh.TriangleIndices.Count);
        foreach (int index in mesh.TriangleIndices)
        {
            if (index < 0 || index >= mesh.Vertices.Count) return null;
            writer.Write(index);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private static Dictionary<int, string>? GetTextureSlotGeometryUvSignatures(LithTechModelDocument document)
    {
        if (document.Meshes.Count == 0) return null;
        int[] slots = document.Meshes
            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
            .Distinct()
            .Order()
            .ToArray();
        if (slots.Length == 0) return null;

        var signatures = new Dictionary<int, string>();
        foreach (int slot in slots)
        {
            string?[] meshSignatures = document.Meshes
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slot))
                .Select(GetTextureGeometryUvSignature)
                .ToArray();
            if (meshSignatures.Length == 0 || meshSignatures.Any(string.IsNullOrEmpty)) return null;
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            writer.Write(slot);
            writer.Write(meshSignatures.Length);
            foreach (string meshSignature in meshSignatures.Select(signature => signature!).Order(StringComparer.Ordinal))
            {
                writer.Write(meshSignature);
            }

            writer.Flush();
            signatures.Add(slot, Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
        }

        return signatures;
    }

    private static Dictionary<int, string>? GetTextureSlotUvTriangleSignatures(LithTechModelDocument document)
    {
        if (document.Meshes.Count == 0) return null;
        int[] slots = document.Meshes
            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
            .Distinct()
            .Order()
            .ToArray();
        if (slots.Length == 0) return null;

        var signatures = new Dictionary<int, string>();
        foreach (int slot in slots)
        {
            string?[] meshSignatures = document.Meshes
                .Where(mesh => (mesh.TextureSlotIndices ?? []).Contains(slot))
                .Select(GetTextureUvTriangleSignature)
                .ToArray();
            if (meshSignatures.Length == 0 || meshSignatures.Any(string.IsNullOrEmpty)) return null;
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
            writer.Write(slot);
            writer.Write(meshSignatures.Length);
            foreach (string meshSignature in meshSignatures.Select(signature => signature!).Order(StringComparer.Ordinal))
            {
                writer.Write(meshSignature);
            }

            writer.Flush();
            signatures.Add(slot, Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
        }

        return signatures;
    }

    private static string? GetTextureUvTriangleSignature(LithTechMesh mesh)
    {
        if (!mesh.HasAuthoritativeTextureBindings || !TryGetTextureCoordinateSets(mesh, out IReadOnlyList<IReadOnlyList<LithTechVector2>?> uvSets) ||
            mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0 ||
            mesh.TextureSlotIndices is not { Count: > 0 } slots || slots.Any(slot => slot < 0) ||
            !string.IsNullOrWhiteSpace(mesh.TexturePath))
        {
            return null;
        }

        string Number(double value) => (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture);
        var triangles = new List<string>(mesh.TriangleIndices.Count / 3);
        for (int offset = 0; offset < mesh.TriangleIndices.Count; offset += 3)
        {
            var corners = new string[3];
            for (int corner = 0; corner < 3; corner++)
            {
                int index = mesh.TriangleIndices[offset + corner];
                if (index < 0 || index >= mesh.Vertices.Count) return null;
                var values = new List<string>(uvSets.Count * 2);
                foreach (IReadOnlyList<LithTechVector2>? uvSet in uvSets)
                {
                    if (uvSet is null)
                    {
                        values.Add("-");
                        continue;
                    }

                    LithTechVector2 uv = uvSet[index];
                    if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return null;
                    values.Add(Number(uv.X));
                    values.Add(Number(uv.Y));
                }

                corners[corner] = string.Join(",", values);
            }

            Array.Sort(corners, StringComparer.Ordinal);
            triangles.Add(string.Join('|', corners));
        }

        triangles.Sort(StringComparer.Ordinal);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(mesh.Name.ToUpperInvariant());
        writer.Write(slots.Count);
        foreach (int slot in slots) writer.Write(slot);
        writer.Write(mesh.RenderStyleIndex.HasValue);
        writer.Write(mesh.RenderStyleIndex ?? -1);
        writer.Write(uvSets.Count);
        for (int setIndex = 0; setIndex < uvSets.Count; setIndex++)
        {
            writer.Write(uvSets[setIndex] is not null);
            writer.Write(mesh.GetTextureCoordinateSourceStreamIndex(setIndex) ?? -1);
        }
        writer.Write(triangles.Count);
        foreach (string triangle in triangles) writer.Write(triangle);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private static string? GetTextureGeometryUvSignature(LithTechMesh mesh)
    {
        if (!mesh.HasAuthoritativeTextureBindings || !TryGetTextureCoordinateSets(mesh, out IReadOnlyList<IReadOnlyList<LithTechVector2>?> uvSets) ||
            mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0 ||
            mesh.TextureSlotIndices is not { Count: > 0 } slots || slots.Any(slot => slot < 0) ||
            !string.IsNullOrWhiteSpace(mesh.TexturePath))
        {
            return null;
        }

        string Number(double value) => (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture);
        var triangles = new List<string>(mesh.TriangleIndices.Count / 3);
        for (int offset = 0; offset < mesh.TriangleIndices.Count; offset += 3)
        {
            var corners = new string[3];
            for (int corner = 0; corner < 3; corner++)
            {
                int index = mesh.TriangleIndices[offset + corner];
                if (index < 0 || index >= mesh.Vertices.Count) return null;
                LithTechVector3 position = mesh.Vertices[index];
                if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) return null;
                var values = new List<string>(3 + uvSets.Count * 2)
                {
                    Number(position.X), Number(position.Y), Number(position.Z)
                };
                foreach (IReadOnlyList<LithTechVector2>? uvSet in uvSets)
                {
                    if (uvSet is null)
                    {
                        values.Add("-");
                        continue;
                    }

                    LithTechVector2 uv = uvSet[index];
                    if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return null;
                    values.Add(Number(uv.X));
                    values.Add(Number(uv.Y));
                }

                corners[corner] = string.Join(",", values);
            }

            Array.Sort(corners, StringComparer.Ordinal);
            triangles.Add(string.Join('|', corners));
        }

        triangles.Sort(StringComparer.Ordinal);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(mesh.Name.ToUpperInvariant());
        writer.Write(slots.Count);
        foreach (int slot in slots) writer.Write(slot);
        writer.Write(mesh.RenderStyleIndex.HasValue);
        writer.Write(mesh.RenderStyleIndex ?? -1);
        writer.Write(uvSets.Count);
        for (int setIndex = 0; setIndex < uvSets.Count; setIndex++)
        {
            writer.Write(uvSets[setIndex] is not null);
            writer.Write(mesh.GetTextureCoordinateSourceStreamIndex(setIndex) ?? -1);
        }
        writer.Write(mesh.Vertices.Count);
        writer.Write(mesh.TriangleIndices.Count / 3);
        foreach (string triangle in triangles) writer.Write(triangle);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private static string? GetLayoutSignature(LithTechModelDocument document)
    {
        if (document.Meshes.Count == 0) return null;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(document.Meshes.Count);
        foreach (LithTechMesh mesh in document.Meshes)
        {
            if (!mesh.HasAuthoritativeTextureBindings || !TryGetTextureCoordinateSets(mesh, out IReadOnlyList<IReadOnlyList<LithTechVector2>?> uvSets) ||
                mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0 ||
                mesh.TextureSlotIndices is not { Count: > 0 } slots || slots.Any(slot => slot < 0) ||
                !string.IsNullOrWhiteSpace(mesh.TexturePath)) return null;
            writer.Write(mesh.Name.ToUpperInvariant());
            writer.Write(slots.Count);
            foreach (int slot in slots) writer.Write(slot);
            writer.Write(mesh.RenderStyleIndex.HasValue);
            writer.Write(mesh.RenderStyleIndex ?? -1);
            writer.Write(uvSets.Count);
            for (int setIndex = 0; setIndex < uvSets.Count; setIndex++)
            {
                writer.Write(uvSets[setIndex] is not null);
                writer.Write(mesh.GetTextureCoordinateSourceStreamIndex(setIndex) ?? -1);
            }
            writer.Write(mesh.Vertices.Count);
            foreach (LithTechVector3 vertex in mesh.Vertices)
            {
                if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)) return null;
                writer.Write(vertex.X); writer.Write(vertex.Y); writer.Write(vertex.Z);
            }
            for (int setIndex = 0; setIndex < uvSets.Count; setIndex++)
            {
                IReadOnlyList<LithTechVector2>? uvSet = uvSets[setIndex];
                if (uvSet is null) continue;
                foreach (LithTechVector2 uv in uvSet)
                {
                    if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return null;
                    writer.Write(uv.X); writer.Write(uv.Y);
                }
            }
            writer.Write(mesh.TriangleIndices.Count);
            foreach (int index in mesh.TriangleIndices)
            {
                if (index < 0 || index >= mesh.Vertices.Count) return null;
                writer.Write(index);
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private static bool TryGetTextureCoordinateSets(
        LithTechMesh mesh,
        out IReadOnlyList<IReadOnlyList<LithTechVector2>?> textureCoordinateSets)
    {
        var sets = new IReadOnlyList<LithTechVector2>?[4];
        bool hasSet = false;
        for (int setIndex = 0; setIndex < sets.Length; setIndex++)
        {
            IReadOnlyList<LithTechVector2>? coordinates = mesh.GetTextureCoordinateSet(setIndex);
            if (coordinates is null)
            {
                continue;
            }

            if (coordinates.Count != mesh.Vertices.Count)
            {
                textureCoordinateSets = Array.Empty<IReadOnlyList<LithTechVector2>?>();
                return false;
            }

            hasSet = true;
            sets[setIndex] = coordinates;
        }

        textureCoordinateSets = sets;
        return hasSet;
    }
}
