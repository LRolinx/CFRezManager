using System.IO;
using System.Numerics;

namespace CFRezManager;

public readonly record struct LithTechNodeSample(int NodeIndex, Vector3 Position, Quaternion Rotation);

// Matrices use System.Numerics row vectors, the transpose of serialized LTB matrices.
public sealed record LithTechNativePose(IReadOnlyList<Matrix4x4> LocalTransforms,
    IReadOnlyList<Matrix4x4> GlobalTransforms, IReadOnlyList<Matrix4x4> SkinTransforms);

public static class LithTechPoseEvaluator
{
    public static LithTechNativePose Evaluate(IReadOnlyList<LithTechSkeletonNode> skeleton,
        IReadOnlyList<LithTechNodeSample> previous, IReadOnlyList<LithTechNodeSample> next,
        float fraction, Vector3 animationTranslation = default)
    {
        int count = skeleton.Count;
        if (count is 0 or > 65536 || !float.IsFinite(fraction) || fraction < 0 || fraction > 1 || !Finite(animationTranslation))
            throw new InvalidDataException("Invalid native pose input.");
        var nodes = new LithTechSkeletonNode?[count]; var bind = new Matrix4x4[count];
        foreach (var node in skeleton)
        {
            if (node.Index < 0 || node.Index >= count || nodes[node.Index] is not null || node.ParentIndex < -1 || node.ParentIndex >= count)
                throw new InvalidDataException("Skeleton indices are incomplete, duplicated or out of range.");
            nodes[node.Index] = node; bind[node.Index] = ReadBind(node.GlobalBindMatrix);
        }
        if (nodes.Count(n => n?.ParentIndex == -1) != 1) throw new InvalidDataException("A native pose requires one root node.");
        var from = IndexSamples(previous, count); var to = IndexSamples(next, count);
        var local = new Matrix4x4[count]; var global = new Matrix4x4[count]; var skin = new Matrix4x4[count];
        var states = new byte[count];
        void Visit(int index, int depth)
        {
            if (states[index] == 2) return;
            if (states[index] == 1 || depth >= 256) throw new InvalidDataException("Skeleton parent cycle or excessive depth.");
            states[index] = 1; var node = nodes[index]!; int parent = node.ParentIndex;
            if (parent >= 0) Visit(parent, depth + 1);
            var a = from[index]; var b = to[index];
            Vector3 position = a.Position + (b.Position - a.Position) * fraction;
            if (parent < 0) position += animationTranslation;
            if ((node.Flags & 2) != 0)
                position = parent < 0 ? Vector3.Zero : (bind[index] * RigidInverse(bind[parent])).Translation;
            local[index] = RotationMatrix(Interpolate(a.Rotation, b.Rotation, fraction));
            local[index].Translation = position;
            global[index] = parent < 0 ? local[index] : local[index] * global[parent];
            skin[index] = RigidInverse(bind[index]) * global[index];
            if (!Finite(local[index]) || !Finite(global[index]) || !Finite(skin[index])) throw new InvalidDataException("Non-finite evaluated pose.");
            states[index] = 2;
        }
        for (int i = 0; i < count; i++) Visit(i, 0);
        return new(local, global, skin);
    }

    public static Vector3 SkinVertex(LithTechVector3 vertex, LithTechVertexSkin binding, IReadOnlyList<Matrix4x4> transforms)
    {
        var source = new Vector3((float)vertex.X, (float)vertex.Y, (float)vertex.Z);
        if (!Finite(source)) throw new InvalidDataException("Non-finite source vertex.");
        Vector3 result = default; bool weighted = false;
        for (int slot = 0; slot < 4; slot++)
        {
            float weight = binding.GetWeight(slot); int node = binding.GetNode(slot);
            if (!float.IsFinite(weight)) throw new InvalidDataException("Non-finite skin weight.");
            if (weight == 0) continue;
            if (node < 0 || node >= transforms.Count || !Finite(transforms[node])) throw new InvalidDataException("Weighted node has no finite pose transform.");
            result += Vector3.Transform(source, transforms[node]) * weight; weighted = true;
        }
        if (!weighted || !Finite(result)) throw new InvalidDataException("Vertex has no binding or evaluates to a non-finite position.");
        return result;
    }

    private static LithTechNodeSample[] IndexSamples(IReadOnlyList<LithTechNodeSample> source, int count)
    {
        if (source.Count != count) throw new InvalidDataException("Pose samples do not cover the skeleton.");
        var output = new LithTechNodeSample[count]; var seen = new bool[count];
        foreach (var sample in source)
        {
            int i = sample.NodeIndex;
            float magnitude = sample.Rotation.LengthSquared();
            if (i < 0 || i >= count || seen[i] || !Finite(sample.Position) || !Finite(sample.Rotation) || !float.IsFinite(magnitude) || magnitude <= 0)
                throw new InvalidDataException("Invalid, duplicate or missing node sample.");
            output[i] = sample; seen[i] = true;
        }
        return output;
    }

    private static Quaternion Interpolate(Quaternion a, Quaternion b, float fraction)
    {
        float dot = Quaternion.Dot(a, b);
        if (!float.IsFinite(dot)) throw new InvalidDataException("Quaternion dot product is non-finite.");
        if (dot < 0) { dot = -dot; b = -b; }
        float left = 1 - fraction, right = fraction;
        if (1 - dot > .0001f)
        {
            float angle = MathF.Acos(dot), inverseSin = 1 / MathF.Sin(angle);
            left = MathF.Sin((1 - fraction) * angle) * inverseSin; right = MathF.Sin(fraction * angle) * inverseSin;
        }
        return a * left + b * right;
    }

    private static Matrix4x4 RotationMatrix(Quaternion q)
    {
        float length = q.LengthSquared();
        if (!float.IsFinite(length) || length <= 0) throw new InvalidDataException("Quaternion has no finite nonzero magnitude.");
        float scale = 2 / length, xs = q.X * scale, ys = q.Y * scale, zs = q.Z * scale;
        float xx = q.X * xs, xy = q.X * ys, xz = q.X * zs, yy = q.Y * ys, yz = q.Y * zs, zz = q.Z * zs;
        float wx = q.W * xs, wy = q.W * ys, wz = q.W * zs;
        return new(1 - (yy + zz), xy + wz, xz - wy, 0,
            xy - wz, 1 - (xx + zz), yz + wx, 0,
            xz + wy, yz - wx, 1 - (xx + yy), 0,
            0, 0, 0, 1);
    }

    private static Matrix4x4 ReadBind(IReadOnlyList<float> a)
    {
        if (a.Count != 16 || a.Any(v => !float.IsFinite(v)) || a[12] != 0 || a[13] != 0 || a[14] != 0 || a[15] != 1)
            throw new InvalidDataException("Bind matrix must be finite and affine.");
        return new(a[0], a[4], a[8], 0, a[1], a[5], a[9], 0, a[2], a[6], a[10], 0, a[3], a[7], a[11], 1);
    }

    private static Matrix4x4 RigidInverse(Matrix4x4 m)
    {
        // Match the native loader's rigid inverse: transpose the basis and rotate the negative translation.
        var result = new Matrix4x4(m.M11, m.M21, m.M31, 0, m.M12, m.M22, m.M32, 0, m.M13, m.M23, m.M33, 0, 0, 0, 0, 1);
        result.Translation = Vector3.TransformNormal(-m.Translation, result);
        return result;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    private static bool Finite(Matrix4x4 m) => Finite(new Vector3(m.M11, m.M12, m.M13)) && Finite(new Vector3(m.M21, m.M22, m.M23))
        && Finite(new Vector3(m.M31, m.M32, m.M33)) && Finite(m.Translation) && m.M14 == 0 && m.M24 == 0 && m.M34 == 0 && m.M44 == 1;
}
