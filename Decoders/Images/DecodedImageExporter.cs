using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class DecodedImageExporter
{
    public static bool TryWritePng(byte[] data, string extension, string outputPath)
    {
        BitmapSource? bitmap = TryDecode(data, ImageExportOptions.NormalizeExtension(extension));
        if (bitmap is null)
        {
            return false;
        }

        byte[] pngData;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            pngData = output.ToArray();
        }
        catch
        {
            return false;
        }

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllBytes(outputPath, pngData);
        return true;
    }

    private static BitmapSource? TryDecode(byte[] data, string extension)
    {
        try
        {
            ImageSource? image = extension switch
            {
                "bin" => CrossFireImageBinDecoder.TryDecodePreviewFrames(data, out _)
                    .FirstOrDefault()?.Source,
                "dtx" => DtxThumbnailDecoder.TryDecodeOriginal(data),
                "tga" => TgaThumbnailDecoder.TryDecodePreviewFrames(data)
                    .FirstOrDefault()?.Source,
                "dds" => DdsThumbnailDecoder.TryDecodeOriginal(data),
                _ => null
            };
            return image as BitmapSource;
        }
        catch
        {
            return null;
        }
    }
}
