namespace CFRezManager;

internal static class LithTechResourceHeuristics
{
    public static string NormalizeResourcePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').Trim().Trim('"', '\'').TrimStart('/');
    }

    public static bool IsUiPath(string path)
    {
        string normalized = NormalizeResourcePath(path);
        return normalized.StartsWith("UI/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/UI/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/UI_", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/UI/Scripts/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTEXRootedResourcePath(string path)
    {
        string normalized = NormalizeResourcePath(path);
        return normalized.StartsWith("TEX/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/TEX/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyModelTextureConfigPath(string path, string extension)
    {
        string normalized = NormalizeResourcePath(path);
        string normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || IsUiPath(normalized))
        {
            return false;
        }

        bool supportedExtension = normalizedExtension is "cfg" or "ini" or "txt" or "cft" or "fcf" or "csv" or "dat" or "xml" or "json" or "lua" or "ref" or "apf" or "enc";
        if (!supportedExtension)
        {
            return false;
        }

        if (normalized.Contains("/ModelTextures/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains("/Models/", StringComparison.OrdinalIgnoreCase) &&
            normalizedExtension is "cfg" or "ini" or "txt")
        {
            return true;
        }

        return normalized.Contains("material", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("skin", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("character", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyModelTexturePath(string path, string extension)
    {
        string normalized = NormalizeResourcePath(path);
        string normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || IsUiPath(normalized))
        {
            return false;
        }

        bool supportedExtension = normalizedExtension is "dtx" or "dds" or "tga" or "png" or "jpg" or "jpeg" or "bmp" or "bin";
        if (!supportedExtension)
        {
            return false;
        }

        return normalized.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("UCC/ModelAssets/UCC_Textures/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/UCC/ModelAssets/UCC_Textures/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Models/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/RF017/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/FX/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Weapons/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Characters/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Players/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyModelMappingTablePath(string path, string extension)
    {
        string normalized = NormalizeResourcePath(path);
        string normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || IsLowValueMappingPath(normalized))
        {
            return false;
        }

        bool supportedExtension = normalizedExtension is "cft" or "fcf" or "cfg" or "csv" or "dat" or "txt" or "ini" or "enc";
        if (!supportedExtension)
        {
            return false;
        }

        bool tablePath = normalized.Contains("/Table/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("/Table_", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("/Butes/", StringComparison.OrdinalIgnoreCase);
        if (!tablePath)
        {
            return false;
        }

        return normalized.Contains("character", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("item", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("model", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("skin", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("material", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("texture", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLowValueMappingPath(string path)
    {
        string normalized = NormalizeResourcePath(path);
        return IsUiPath(normalized) ||
               normalized.Contains("/Sound", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Radio", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/LobbyNotice/", StringComparison.OrdinalIgnoreCase);
    }
}
