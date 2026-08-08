namespace CFRezManager;

internal enum ImageExportMode
{
    SourceFile,
    DecodedPng
}

internal sealed record ImageExportFormatDefinition(
    string Extension,
    string ChineseName,
    string EnglishName,
    string ChineseDescription,
    string EnglishDescription,
    ImageExportMode DefaultMode);

internal sealed class ImageExportOptions
{
    public static readonly IReadOnlyList<ImageExportFormatDefinition> KnownFormats =
    [
        new(
            "bin",
            "CrossFire 图片 BIN (.bin)",
            "CrossFire image BIN (.bin)",
            "CF10/XOR、LZMA 外壳、Zstandard BGRA32，以及内嵌 DDS / DTX / TGA / PNG / JPG / BMP",
            "CF10/XOR, LZMA wrappers, Zstandard BGRA32, and embedded DDS / DTX / TGA / PNG / JPG / BMP",
            ImageExportMode.DecodedPng),
        new(
            "dtx",
            "LithTech 纹理 (.dtx)",
            "LithTech texture (.dtx)",
            "普通或 LZMA 外壳，支持 BGRA / RGBA、调色板和 DXT1 / DXT3 / DXT5",
            "Raw or LZMA-wrapped, including BGRA / RGBA, palettes, and DXT1 / DXT3 / DXT5",
            ImageExportMode.SourceFile),
        new(
            "tga",
            "TGA 图片 (.tga)",
            "TGA image (.tga)",
            "普通、RLE、LZMA 外壳，以及已知的拼接头和原始像素修复布局",
            "Raw, RLE, LZMA-wrapped, and known inserted-header or raw-pixel repair layouts",
            ImageExportMode.SourceFile),
        new(
            "dds",
            "DDS 纹理 (.dds)",
            "DDS texture (.dds)",
            "未压缩 DDS，以及 DXT1 / DXT3 / DXT5 块压缩纹理",
            "Uncompressed DDS and DXT1 / DXT3 / DXT5 block-compressed textures",
            ImageExportMode.SourceFile)
    ];

    private readonly IReadOnlyDictionary<string, ImageExportMode> _modeByExtension;

    public ImageExportOptions(IReadOnlyDictionary<string, ImageExportMode> modeByExtension)
    {
        _modeByExtension = new Dictionary<string, ImageExportMode>(modeByExtension, StringComparer.OrdinalIgnoreCase);
    }

    public bool ShouldDecodeToPng(string? extension)
    {
        string normalizedExtension = NormalizeExtension(extension);
        return _modeByExtension.TryGetValue(normalizedExtension, out ImageExportMode mode) &&
               mode == ImageExportMode.DecodedPng;
    }

    public static ImageExportOptions FromSettings(IReadOnlyDictionary<string, string>? savedModes)
    {
        var modes = new Dictionary<string, ImageExportMode>(StringComparer.OrdinalIgnoreCase);
        foreach (ImageExportFormatDefinition definition in KnownFormats)
        {
            modes[definition.Extension] = ResolveSavedMode(savedModes, definition);
        }

        return new ImageExportOptions(modes);
    }

    public IReadOnlyDictionary<string, string> ToSettingsDictionary()
    {
        return _modeByExtension.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static ImageExportMode ResolveSavedMode(
        IReadOnlyDictionary<string, string>? savedModes,
        ImageExportFormatDefinition definition)
    {
        if (savedModes is not null)
        {
            string? value = savedModes
                .FirstOrDefault(pair => string.Equals(
                    NormalizeExtension(pair.Key),
                    definition.Extension,
                    StringComparison.OrdinalIgnoreCase))
                .Value;
            if (Enum.TryParse(value, ignoreCase: true, out ImageExportMode savedMode))
            {
                return savedMode;
            }
        }

        return definition.DefaultMode;
    }

    public static string NormalizeExtension(string? extension)
    {
        return (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
    }
}
