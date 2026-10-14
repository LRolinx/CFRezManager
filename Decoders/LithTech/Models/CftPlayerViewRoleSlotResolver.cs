namespace CFRezManager;

internal enum CftPlayerViewTextureRole
{
    Hand,
    Arm
}

internal sealed record CftPlayerViewRoleSlotMap(
    IReadOnlyList<int> HandSlots,
    IReadOnlyList<int> ArmSlots,
    string Evidence)
{
    public static CftPlayerViewRoleSlotMap Empty { get; } = new([], [], string.Empty);

    public IReadOnlyList<int> GetSlots(CftPlayerViewTextureRole role) => role switch
    {
        CftPlayerViewTextureRole.Hand => HandSlots,
        CftPlayerViewTextureRole.Arm => ArmSlots,
        _ => []
    };
}

/// <summary>
/// Maps CFT's semantic player-view hand/arm texture fields to LTB slots.
/// Explicit mesh names take priority; two-slot models use the validated
/// hand-slot-0 / arm-slot-1 convention when mesh names are absent or ambiguous.
/// </summary>
internal static class CftPlayerViewRoleSlotResolver
{
    public static bool TryGetTextureRole(string columnName, out CftPlayerViewTextureRole role)
    {
        if (columnName.Contains("PVHandTextureFile", StringComparison.OrdinalIgnoreCase))
        {
            role = CftPlayerViewTextureRole.Hand;
            return true;
        }

        if (columnName.Contains("PVArmTextureFile", StringComparison.OrdinalIgnoreCase))
        {
            role = CftPlayerViewTextureRole.Arm;
            return true;
        }

        role = default;
        return false;
    }

    public static CftPlayerViewRoleSlotMap Resolve(
        IReadOnlyList<LithTechModelDocument> documents,
        bool hasHandTextureReference,
        bool hasArmTextureReference)
    {
        if (documents.Count == 0)
        {
            return CftPlayerViewRoleSlotMap.Empty;
        }

        CftPlayerViewRoleSlotMap? resolved = null;
        foreach (LithTechModelDocument document in documents)
        {
            CftPlayerViewRoleSlotMap current = ResolveDocument(
                document,
                hasHandTextureReference,
                hasArmTextureReference);
            if (current.HandSlots.Count == 0 && current.ArmSlots.Count == 0)
            {
                return CftPlayerViewRoleSlotMap.Empty;
            }

            if (resolved is null)
            {
                resolved = current;
                continue;
            }

            if (!resolved.HandSlots.SequenceEqual(current.HandSlots) ||
                !resolved.ArmSlots.SequenceEqual(current.ArmSlots))
            {
                return CftPlayerViewRoleSlotMap.Empty;
            }
        }

        return resolved ?? CftPlayerViewRoleSlotMap.Empty;
    }

    private static CftPlayerViewRoleSlotMap ResolveDocument(
        LithTechModelDocument document,
        bool hasHandTextureReference,
        bool hasArmTextureReference)
    {
        int[] modelSlots = document.Meshes
            .SelectMany(mesh => mesh.TextureSlotIndices ?? [])
            .Distinct()
            .Order()
            .ToArray();
        int[] handSlots = [];
        int[] armSlots = [];
        bool hasHandSuffix = document.Meshes.Any(mesh => mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase));
        bool hasArmSuffix = document.Meshes.Any(mesh => mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase));
        bool hasPairedHandArmSuffixes = hasHandSuffix && hasArmSuffix;

        foreach (LithTechMesh mesh in document.Meshes)
        {
            bool isHand = mesh.Name.Contains("hand", StringComparison.OrdinalIgnoreCase) ||
                          hasPairedHandArmSuffixes && mesh.Name.EndsWith("_H", StringComparison.OrdinalIgnoreCase);
            bool isArm = mesh.Name.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
                         hasPairedHandArmSuffixes && mesh.Name.EndsWith("_A", StringComparison.OrdinalIgnoreCase);
            if (isHand && isArm)
            {
                return CftPlayerViewRoleSlotMap.Empty;
            }

            int[] slots = (mesh.TextureSlotIndices ?? []).Distinct().ToArray();
            if (isHand)
            {
                handSlots = handSlots.Concat(slots).Distinct().Order().ToArray();
            }

            if (isArm)
            {
                armSlots = armSlots.Concat(slots).Distinct().Order().ToArray();
            }
        }

        string evidence = hasPairedHandArmSuffixes && handSlots.Length > 0 && armSlots.Length > 0
            ? "paired H/A mesh-name suffixes"
            : handSlots.Length > 0 && armSlots.Length > 0
                ? "hand/arm mesh names"
                : string.Empty;

        bool isStandardTwoSlotLayout = modelSlots.SequenceEqual([0, 1]);
        if (isStandardTwoSlotLayout)
        {
            if (handSlots.Length == 1 && armSlots.Length == 0 && hasArmTextureReference)
            {
                armSlots = [handSlots[0] == 0 ? 1 : 0];
                evidence = "named hand mesh plus complementary slot";
            }
            else if (armSlots.Length == 1 && handSlots.Length == 0 && hasHandTextureReference)
            {
                handSlots = [armSlots[0] == 0 ? 1 : 0];
                evidence = "named arm mesh plus complementary slot";
            }

            bool roleSlotsOverlap = handSlots.Intersect(armSlots).Any();
            bool roleNamesCoverEverySlot = handSlots.Concat(armSlots).Distinct().Order().SequenceEqual(modelSlots);
            if (hasHandTextureReference && hasArmTextureReference &&
                (handSlots.Length == 0 || armSlots.Length == 0 || roleSlotsOverlap ||
                 (roleNamesCoverEverySlot && (handSlots.Length != 1 || armSlots.Length != 1))))
            {
                handSlots = [0];
                armSlots = [1];
                evidence = "validated two-slot player-view convention";
            }
        }

        if (handSlots.Intersect(armSlots).Any())
        {
            return CftPlayerViewRoleSlotMap.Empty;
        }

        return handSlots.Length == 0 && armSlots.Length == 0
            ? CftPlayerViewRoleSlotMap.Empty
            : new CftPlayerViewRoleSlotMap(handSlots, armSlots, evidence);
    }
}
