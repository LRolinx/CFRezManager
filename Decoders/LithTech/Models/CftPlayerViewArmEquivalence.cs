using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CFRezManager;

/// <summary>
/// Proves that a same-side family variant has the same skin layout as an older
/// arm model. This establishes compatibility, not the older model's default skin.
/// </summary>
internal static class CftPlayerViewArmEquivalence
{
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

    private static string? GetLayoutSignature(LithTechModelDocument document)
    {
        if (document.Meshes.Count == 0) return null;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(document.Meshes.Count);
        foreach (LithTechMesh mesh in document.Meshes)
        {
            if (!mesh.HasAuthoritativeTextureBindings || !mesh.HasTextureCoordinates ||
                mesh.Vertices.Count == 0 || mesh.TriangleIndices.Count == 0 || mesh.TriangleIndices.Count % 3 != 0 ||
                mesh.TextureSlotIndices is not { Count: > 0 } slots || slots.Any(slot => slot < 0) ||
                !string.IsNullOrWhiteSpace(mesh.TexturePath)) return null;
            writer.Write(mesh.Name.ToUpperInvariant());
            writer.Write(slots.Count);
            foreach (int slot in slots) writer.Write(slot);
            writer.Write(mesh.RenderStyleIndex.HasValue);
            writer.Write(mesh.RenderStyleIndex ?? -1);
            writer.Write(mesh.Vertices.Count);
            foreach (LithTechVector3 vertex in mesh.Vertices)
            {
                if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)) return null;
                writer.Write(vertex.X); writer.Write(vertex.Y); writer.Write(vertex.Z);
            }
            foreach (LithTechVector2 uv in mesh.TextureCoordinates!)
            {
                if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y)) return null;
                writer.Write(uv.X); writer.Write(uv.Y);
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
}
