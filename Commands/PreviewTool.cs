using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

internal static class PreviewTool
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bmp",
        "dds",
        "dtx",
        "gif",
        "jpg",
        "jpeg",
        "png",
        "tga",
        "tif",
        "tiff",
        "bin"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "ogg",
        "wav",
        "wave"
    };

    public static bool TryGetPreviewPath(string[] args, out string filePath)
    {
        filePath = string.Empty;
        if (args.Length == 0)
        {
            return false;
        }

        string candidate = args[0];
        if (string.Equals(candidate, "--preview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "/preview", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                return false;
            }

            candidate = args[1];
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        filePath = Path.GetFullPath(candidate);
        return true;
    }

    public static bool IsPreviewInvocation(string[] args)
    {
        return args.Length > 0 &&
               (string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "/preview", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(args[0]));
    }

    public static bool IsSupported(string fileName, string extension)
    {
        return LithTechModelDecoder.IsCandidate(extension) ||
               LithTechWorldDatDecoder.IsCandidate(extension) ||
               ImageExtensions.Contains(extension) ||
               AudioExtensions.Contains(extension) ||
               EncTextDecoder.IsCandidate(fileName, extension) ||
               CrossFireDatDecoder.IsCandidate(extension) ||
               CrossFireScriptBinDecoder.IsCandidate(fileName, extension) ||
               LithTechSpriteDecoder.IsCandidate(extension) ||
               FmodBankDecoder.IsCandidate(extension) ||
               ResourceTextDecoder.IsCandidate(fileName, extension) ||
               TextPreviewDecoder.IsPlainTextExtension(extension);
    }

    public static bool TryCreateWindow(string filePath, out Window? window, out string? errorMessage)
    {
        window = null;
        errorMessage = null;

        try
        {
            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(filePath).TrimStart('.');
            byte[] data = File.ReadAllBytes(filePath);
            string? modelFallbackError = null;

            if (LithTechModelDecoder.IsCandidate(extension))
            {
                if (LithTechModelDecoder.TryDecode(data, fileName, extension, out LithTechModelDocument? modelDocument, out string? modelError) &&
                    modelDocument is not null)
                {
                    window = new ModelPreviewWindow(
                        fileName,
                        modelDocument,
                        FormatModelInfo(modelDocument),
                        LithTechModelTextureLoader.CreateLocalResolver(filePath));
                    window.ShowInTaskbar = true;
                    return true;
                }

                if (!CrossFireLtcDecoder.IsCandidate(extension))
                {
                    errorMessage = string.IsNullOrWhiteSpace(modelError)
                        ? LocalizedText.Format("PreviewModelDecodeFailedFileName", fileName)
                        : modelError;
                    return false;
                }

                modelFallbackError = modelError;
            }

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, fileName, out LithTechModelDocument? worldDocument, out _) &&
                worldDocument is not null)
            {
                window = new ModelPreviewWindow(
                    fileName,
                    worldDocument,
                    FormatModelInfo(worldDocument),
                    LithTechModelTextureLoader.CreateLocalResolver(filePath));
                window.ShowInTaskbar = true;
                return true;
            }

            if (LithTechSpriteDecoder.IsCandidate(extension) &&
                TryCreateSpriteWindow(fileName, filePath, data, out window))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            if (ImageExtensions.Contains(extension) &&
                TryCreateImageWindow(fileName, extension, data, out window))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            string? audioError = null;
            if (AudioExtensions.Contains(extension) &&
                TryCreateAudioWindow(fileName, filePath, data, out window, out audioError))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            if (FmodBankDecoder.IsCandidate(extension) &&
                FmodBankAudioPreviewDocumentFactory.IsAvailable &&
                TryCreateBankAudioWindow(fileName, data, out window, out audioError))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            if (TryCreateTextWindow(fileName, filePath, extension, data, out window, out string? textError))
            {
                window!.ShowInTaskbar = true;
                return true;
            }

            errorMessage = !string.IsNullOrWhiteSpace(audioError)
                ? audioError
                : !string.IsNullOrWhiteSpace(textError)
                ? textError
                : !string.IsNullOrWhiteSpace(modelFallbackError)
                    ? modelFallbackError
                    : LocalizedText.Format("PreviewUnsupportedFileName", fileName);
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryCreateImageWindow(string fileName, string extension, byte[] data, out Window? window)
    {
        window = null;
        IReadOnlyList<ImagePreviewFrame> frames;
        string? info = null;

        if (CrossFireImageBinDecoder.IsCandidate(extension) ||
            CrossFireImageBinDecoder.HasEncodedHeader(data))
        {
            frames = CrossFireImageBinDecoder.TryDecodePreviewFrames(data, out ImageStorageKind storageKind);
            info = CrossFireImageBinDecoder.GetStorageDescription(storageKind);
        }
        else if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DtxThumbnailDecoder.TryDecodeOriginal(data);
            frames = image is null ? [] : new[] { new ImagePreviewFrame("Original", image) };
            info = DtxThumbnailDecoder.IsLzmaCompressed(data) ? "DTX - LZMA compressed" : "DTX - uncompressed";
        }
        else if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
        {
            frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            info = FormatTgaInfo(data);
        }
        else if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
        {
            ImageSource? image = DdsThumbnailDecoder.TryDecodeOriginal(data);
            frames = image is null ? [] : new[] { new ImagePreviewFrame("Original", image) };
            info = DdsThumbnailDecoder.IsBlockCompressed(data)
                ? "DDS - DXT compressed"
                : "DDS - uncompressed";
        }
        else
        {
            if (TryLoadBitmapImage(data, out ImageSource? image) && image is not null)
            {
                frames = new[] { new ImagePreviewFrame("Original", image) };
            }
            else if (string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase))
            {
                frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
                info = frames.Count > 0 ? $"PNG - {FormatTgaInfo(data)}" : null;
            }
            else
            {
                frames = [];
            }
        }

        if (frames.Count == 0)
        {
            return false;
        }

        window = new ImagePreviewWindow(fileName, frames, info);
        return true;
    }

    private static bool TryCreateAudioWindow(
        string fileName,
        string filePath,
        byte[] data,
        out Window? window,
        out string? errorMessage)
    {
        window = null;
        errorMessage = null;

        if (!AudioPreviewDocumentFactory.TryCreate(
                fileName,
                filePath,
                data,
                canUseSourcePath: true,
                out AudioPreviewDocument? document,
                out errorMessage) ||
            document is null)
        {
            return false;
        }

        window = new AudioPreviewWindow(document);
        return true;
    }

    private static bool TryCreateBankAudioWindow(
        string fileName,
        byte[] data,
        out Window? window,
        out string? errorMessage)
    {
        window = null;
        errorMessage = null;

        if (!FmodBankAudioPreviewDocumentFactory.TryCreateSource(
                fileName,
                data,
                out FmodBankAudioSource? source,
                out errorMessage) ||
            source is null ||
            !FmodBankAudioPreviewDocumentFactory.TryCreate(source, 0, out AudioPreviewDocument? document, out errorMessage) ||
            document is null)
        {
            return false;
        }

        Task<AudioPreviewDocument?> LoadDocumentAtAsync(int index)
        {
            return index >= 0 && index < source.StreamCount
                ? Task.Run(() => FmodBankAudioPreviewDocumentFactory.TryCreate(source, index, out AudioPreviewDocument? nextDocument, out _)
                    ? nextDocument
                    : null)
                : Task.FromResult<AudioPreviewDocument?>(null);
        }

        window = new AudioPreviewWindow(
            document,
            source.StreamCount > 1 ? LoadDocumentAtAsync : null,
            0,
            source.StreamCount,
            documentNames: source.GetStreamNames());
        return true;
    }

    private static bool TryCreateSpriteWindow(string fileName, string filePath, byte[] data, out Window? window)
    {
        window = null;
        if (!LithTechSpriteDecoder.TryDecode(data, fileName, out LithTechSpriteDocument? spriteDocument, out _) ||
            spriteDocument is null)
        {
            return false;
        }

        const int maxPreviewFrames = 512;
        var frames = new List<ImagePreviewFrame>(Math.Min(spriteDocument.FramePaths.Count, maxPreviewFrames));
        int missingFrames = 0;
        int skippedFrames = 0;
        for (int i = 0; i < spriteDocument.FramePaths.Count && frames.Count < maxPreviewFrames; i++)
        {
            if (!TryFindLocalSpriteFrame(filePath, spriteDocument.FramePaths[i], out string? framePath) ||
                framePath is null)
            {
                missingFrames++;
                continue;
            }

            ImageSource? image = TryReadLocalDtxFrame(framePath);
            if (image is null)
            {
                skippedFrames++;
                continue;
            }

            frames.Add(new ImagePreviewFrame($"Frame {i:0000}", image));
        }

        if (frames.Count == 0)
        {
            return false;
        }

        string info = $"{spriteDocument.StorageDescription}, {spriteDocument.FrameCount:N0} frames @ {spriteDocument.FrameRate:N0} fps";
        if (frames.Count != spriteDocument.FrameCount)
        {
            info += $", loaded {frames.Count:N0}";
        }

        if (missingFrames > 0)
        {
            info += $", missing {missingFrames:N0}";
        }

        if (skippedFrames > 0)
        {
            info += $", skipped {skippedFrames:N0}";
        }

        if (spriteDocument.FrameCount > maxPreviewFrames)
        {
            info += $", capped at {maxPreviewFrames:N0}";
        }

        window = new ImagePreviewWindow(fileName, frames, info, spriteDocument.FrameRate);
        return true;
    }

    private static bool TryCreateTextWindow(
        string fileName,
        string resourcePath,
        string extension,
        byte[] data,
        out Window? window,
        out string? errorMessage)
    {
        window = null;
        errorMessage = null;

        if (CrossFireLtcDecoder.IsCandidate(extension))
        {
            if (!CrossFireLtcDecoder.TryDecodeText(data, fileName, out CrossFireLtcTextDocument? ltcDocument, out errorMessage) ||
                ltcDocument is null)
            {
                return false;
            }

            string info = $"{ltcDocument.StorageDescription} / {ltcDocument.EncodingName}, {ltcDocument.SourceByteCount:N0} bytes -> {ltcDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, ltcDocument.Text, info);
            return true;
        }

        if (EncTextDecoder.IsCandidate(resourcePath, extension))
        {
            if (!EncTextDecoder.TryDecode(data, out byte[] decodedBytes, out bool lzmaWrapped) ||
                !TextPreviewDecoder.TryDecode(decodedBytes, preferKorean: true, out string decodedText, out string decodedEncoding))
            {
                return false;
            }

            string storage = lzmaWrapped ? "LZMA + ENC / Base64" : "ENC / Base64";
            string info = $"{storage} / {decodedEncoding}, {data.Length:N0} bytes -> {decodedBytes.Length:N0} bytes";
            window = new TextPreviewWindow(fileName, decodedText, info);
            return true;
        }

        if (CrossFireDatDecoder.IsCandidate(extension))
        {
            if (!CrossFireDatDecoder.TryDecode(data, fileName, out CrossFireDatDocument? datDocument, out errorMessage) ||
                datDocument is null)
            {
                return false;
            }

            string info = $"{datDocument.StorageDescription}, version {datDocument.Version}, {datDocument.ObjectCount:N0} {datDocument.ObjectKind}, {datDocument.SourceByteCount:N0} bytes -> {datDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, datDocument.Text, info);
            return true;
        }

        if (CrossFireScriptBinDecoder.IsCandidate(resourcePath, extension))
        {
            if (!CrossFireScriptBinDecoder.TryDecode(data, resourcePath, out CrossFireScriptBinDocument? scriptBinDocument, out errorMessage) ||
                scriptBinDocument is null)
            {
                return false;
            }

            string info = $"{scriptBinDocument.Description}, {scriptBinDocument.SourceByteCount:N0} bytes -> {scriptBinDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, scriptBinDocument.Text, info);
            return true;
        }

        if (LithTechSpriteDecoder.IsCandidate(extension))
        {
            if (!LithTechSpriteDecoder.TryDecode(data, fileName, out LithTechSpriteDocument? sprDocument, out errorMessage) ||
                sprDocument is null)
            {
                return false;
            }

            string info = $"{sprDocument.StorageDescription}, {sprDocument.FrameCount:N0} frames @ {sprDocument.FrameRate:N0} fps, {sprDocument.SourceByteCount:N0} bytes -> {sprDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, sprDocument.Text, info);
            return true;
        }

        if (FmodBankDecoder.IsCandidate(extension))
        {
            if (!FmodBankDecoder.TryDecode(data, fileName, out FmodBankDocument? bankDocument, out errorMessage) ||
                bankDocument is null)
            {
                return false;
            }

            string info = $"{bankDocument.StorageDescription}, {bankDocument.FsbBlockCount:N0} FSB5 blocks, {bankDocument.StreamCount:N0} streams, {bankDocument.SourceByteCount:N0} bytes -> {bankDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, bankDocument.Text, info);
            return true;
        }

        if (ResourceTextDecoder.IsCandidate(resourcePath, extension))
        {
            if (!ResourceTextDecoder.TryDecode(data, resourcePath, extension, out ResourceTextDocument? resourceDocument, out errorMessage) ||
                resourceDocument is null)
            {
                return false;
            }

            string info = $"{resourceDocument.Description}, {resourceDocument.SourceByteCount:N0} bytes -> {resourceDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, resourceDocument.Text, info);
            return true;
        }

        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            if (!CfgTextDecoder.TryDecode(data, fileName, data.Length, out CfgTextDocument? cfgDocument) ||
                cfgDocument is null)
            {
                return false;
            }

            string info = $"{cfgDocument.Description}, {cfgDocument.SourceByteCount:N0} bytes -> {cfgDocument.DecodedByteCount:N0} bytes";
            window = new TextPreviewWindow(fileName, cfgDocument.Text, info);
            return true;
        }

        if (!TextPreviewDecoder.IsPlainTextExtension(extension) ||
            !TextPreviewDecoder.TryDecode(data, preferKorean: false, out string text, out string encoding))
        {
            return false;
        }

        window = new TextPreviewWindow(fileName, text, $"{encoding}, {data.Length:N0} bytes");
        return true;
    }

    private static bool TryFindLocalSpriteFrame(string spriteFilePath, string spriteFramePath, out string? framePath)
    {
        framePath = null;
        string? startDirectory = Path.GetDirectoryName(spriteFilePath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return false;
        }

        string relativePath = spriteFramePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (string? directory = startDirectory; !string.IsNullOrWhiteSpace(directory); directory = Directory.GetParent(directory)?.FullName)
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                framePath = candidate;
                return true;
            }
        }

        return false;
    }

    private static ImageSource? TryReadLocalDtxFrame(string framePath)
    {
        try
        {
            const long maxFrameBytes = 64 * 1024 * 1024;
            var info = new FileInfo(framePath);
            if (!info.Exists || info.Length < 0 || info.Length > maxFrameBytes)
            {
                return null;
            }

            return DtxThumbnailDecoder.TryDecodeOriginal(File.ReadAllBytes(framePath));
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource LoadBitmapImage(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool TryLoadBitmapImage(byte[] data, out ImageSource? image)
    {
        try
        {
            image = LoadBitmapImage(data);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static string FormatTgaInfo(byte[] data)
    {
        if (TgaThumbnailDecoder.IsLzmaCompressed(data))
        {
            return "TGA - LZMA compressed";
        }

        return TgaThumbnailDecoder.HasInsertedFooterHeader(data)
            ? "TGA - repaired layout"
            : TgaThumbnailDecoder.HasRawPixelData(data)
                ? "TGA - repaired raw pixels"
                : "TGA - uncompressed";
    }

    private static string FormatModelInfo(LithTechModelDocument document)
    {
        return $"{document.StorageDescription}, {document.Meshes.Count:N0} meshes, {document.VertexCount:N0} vertices, {document.TriangleCount:N0} triangles";
    }
}
