using System.IO;

namespace CFRezManager;

internal static class LithTechModelPartGrouper
{
    public static List<ExplorerItem> ExpandNumberedSiblingParts(IEnumerable<ExplorerItem> items)
    {
        var expanded = new List<ExplorerItem>();
        var seen = new HashSet<ExplorerItem>();
        foreach (ExplorerItem item in items)
        {
            foreach (ExplorerItem part in ExpandNumberedSiblingParts(item))
            {
                if (seen.Add(part))
                {
                    expanded.Add(part);
                }
            }
        }

        return expanded;
    }

    public static List<ExplorerItem> ExpandNumberedSiblingParts(ExplorerItem item)
    {
        if (!IsModelFile(item) || item.Parent is null)
        {
            return [item];
        }

        string baseStem = GetModelFamilyBase(Path.GetFileNameWithoutExtension(item.Name));
        List<ExplorerItem> siblings = item.Parent.Children
            .Where(IsModelFile)
            .Where(candidate => string.Equals(
                GetModelFamilyBase(Path.GetFileNameWithoutExtension(candidate.Name)),
                baseStem,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => GetNumberedPartOrder(Path.GetFileNameWithoutExtension(candidate.Name), baseStem))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return siblings.Count > 1 ? siblings : [item];
    }

    public static string GetNumberedPartBase(string stem)
    {
        return TryStripNumericSuffix(stem, out string? baseStem)
            ? baseStem!
            : stem;
    }

    public static bool TryStripNumericSuffix(string stem, out string? baseStem)
    {
        baseStem = null;
        int separatorIndex = stem.LastIndexOf('_');
        if (separatorIndex <= 0 || separatorIndex + 1 >= stem.Length)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = stem.AsSpan(separatorIndex + 1);
        if (!suffix.ToString().All(char.IsDigit))
        {
            return false;
        }

        baseStem = stem[..separatorIndex];
        return !string.IsNullOrWhiteSpace(baseStem);
    }

    private static int GetNumberedPartOrder(string stem, string baseStem)
    {
        if (string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return TryStripNumericSuffix(stem, out string? stripped) &&
               string.Equals(stripped, baseStem, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(stem[(stem.LastIndexOf('_') + 1)..], out int order)
            ? order
            : int.MaxValue;
    }

    public static string GetModelFamilyBase(string stem)
    {
        string numberedBase = GetNumberedPartBase(stem);
        return TryGetSgfxFamilyBase(numberedBase, out string? sgfxBase)
            ? sgfxBase!
            : numberedBase;
    }

    public static IEnumerable<string> EnumerateModelFamilyBaseCandidates(string stem)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string numberedBase = GetNumberedPartBase(stem);
        if (!string.IsNullOrWhiteSpace(numberedBase) && yielded.Add(numberedBase))
        {
            yield return numberedBase;
        }

        if (TryGetSgfxTokens(numberedBase, out string[]? tokens) && tokens is not null)
        {
            for (int tokenCount = tokens.Length - 1; tokenCount >= 3; tokenCount--)
            {
                string candidate = string.Join('_', tokens.Take(tokenCount));
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        string familyBase = GetModelFamilyBase(stem);
        if (!string.IsNullOrWhiteSpace(familyBase) && yielded.Add(familyBase))
        {
            yield return familyBase;
        }
    }

    private static bool TryGetSgfxFamilyBase(string stem, out string? baseStem)
    {
        baseStem = null;
        if (!TryGetSgfxTokens(stem, out string[]? tokens) || tokens is null)
        {
            return false;
        }

        tokens = StripSgfxTerminalPartTokens(tokens);
        if (tokens.Length < 3)
        {
            return false;
        }

        baseStem = LooksLikeCompactWeaponCode(tokens[2])
            ? string.Join('_', tokens.Take(3))
            : string.Join('_', tokens);
        return !string.IsNullOrWhiteSpace(baseStem);
    }

    private static bool TryGetSgfxTokens(string stem, out string[]? tokens)
    {
        tokens = null;
        string current = stem;
        while (TryStripNumericSuffix(current, out string? stripped) && stripped is not null)
        {
            current = stripped;
        }

        string[] parts = current.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "SGFX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        tokens = parts;
        return true;
    }

    private static string[] StripSgfxTerminalPartTokens(string[] tokens)
    {
        int length = tokens.Length;
        while (length > 3 && IsSgfxTerminalPartToken(tokens[length - 1]))
        {
            length--;
        }

        return length == tokens.Length ? tokens : tokens.Take(length).ToArray();
    }

    private static bool IsSgfxTerminalPartToken(string token)
    {
        return token.Equals("MASK", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("LINE", StringComparison.OrdinalIgnoreCase) ||
               token.StartsWith("PLANE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCompactWeaponCode(string token)
    {
        return token.Any(char.IsDigit);
    }

    private static bool IsModelFile(ExplorerItem item)
    {
        return item.IsFile &&
               (LithTechModelDecoder.IsCandidate(item.FileExtension) ||
                LithTechWorldDatDecoder.IsCandidate(item.FileExtension));
    }
}
