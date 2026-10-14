using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CFRezManager;

public enum ExplorerItemKind
{
    Directory,
    LocalFile,
    RezArchive,
    RezDirectory,
    RezFile
}

public enum ImageStorageKind
{
    None,
    DtxUncompressed,
    DtxLzmaCompressed,
    DdsBlockCompressed,
    DdsUncompressed,
    TgaUncompressed,
    TgaLzmaCompressed,
    TgaInsertedFooterHeader,
    TgaRawPixels,
    LtcText,
    LtcModel,
    LithTechWorldDat,
    LithTechWorldDatLzma,
    CrossFireDat,
    CrossFireDatLzma,
    CrossFireScriptBin,
    LithTechSprite,
    LithTechSpriteLzma,
    AudioUncompressed,
    AudioLzmaCompressed,
    ResourceText,
    ResourceTextLzma,
    ConfigText,
    ConfigTextLzma,
    ConfigBinary,
    ConfigBinaryStrip,
    RasterUncompressed,
    RasterLzmaCompressed,
    CrossFireImageBin,
    CrossFireImageBinLzma,
    CrossFireImageBinZstd,
    LithTechModel,
    LithTechModelLzma,
    FmodBank,
    FmodBankLzma
}

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private static readonly ImageSource? FolderIconImage = LoadFolderIcon();
    private static readonly SemaphoreSlim ThumbnailSemaphore = new(3);
    private const int MaxThumbnailBytes = 32 * 1024 * 1024;
    private const int MaxPreviewBytes = 128 * 1024 * 1024;
    private const int MaxTextPreviewBytes = 8 * 1024 * 1024;
    private const int MaxModelPreviewBytes = 128 * 1024 * 1024;
    private const int MaxWorldDatPreviewBytes = 256 * 1024 * 1024;
    private const int MaxWorldDatThumbnailTriangles = 40_000;
    private const int ThumbnailDecodeMaxSide = 192;
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png",
        "jpg",
        "jpeg",
        "bmp",
        "gif",
        "tif",
        "tiff",
        "dds",
        "tga",
        "dtx",
        "bin"
    };

    private static readonly HashSet<string> TextThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cft",
        "fcf",
        "txt"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "ogg",
        "wav",
        "wave"
    };

    private readonly object _thumbnailSync = new();
    private Task? _thumbnailLoadTask;
    private ImageSource? _thumbnailSource;
    private ImageStorageKind _imageStorageKind;

    public required string Name { get; init; }
    public required ExplorerItemKind Kind { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string OutputRelativePath { get; init; } = string.Empty;
    public RezArchive? Archive { get; set; }
    public RezDirectoryNode? ArchiveDirectory { get; set; }
    public RezFileNode? ArchiveFile { get; set; }
    public ExplorerItem? Parent { get; set; }
    public bool IsLoaded { get; set; } = true;
    public List<ExplorerItem> Children { get; } = new();

    public bool IsContainer => Kind is ExplorerItemKind.Directory or ExplorerItemKind.RezArchive or ExplorerItemKind.RezDirectory;

    public bool IsFile => Kind is ExplorerItemKind.LocalFile or ExplorerItemKind.RezFile;

    public string FileExtension => Kind switch
    {
        ExplorerItemKind.RezFile => ArchiveFile?.Extension ?? string.Empty,
        ExplorerItemKind.LocalFile => Path.GetExtension(SourcePath).TrimStart('.'),
        _ => string.Empty
    };

    public ImageSource? IconSource => IsContainer ? FolderIconImage : null;

    public bool IsThumbnailCandidate => CanLoadThumbnail();

    public bool IsImagePreviewCandidate => CanLoadImagePreview();

    public bool IsModelPreviewCandidate => CanLoadModelPreview();

    public bool IsTextPreviewCandidate => CanLoadTextPreview();

    public bool HasThumbnail => _thumbnailSource is not null;

    public ImageSource? ThumbnailSource => _thumbnailSource;

    public ImageStorageKind ImageStorageKind => _imageStorageKind;

    public string? ThumbnailBadgeText => _imageStorageKind switch
    {
        ImageStorageKind.CrossFireImageBinZstd => "ZSTD",
        ImageStorageKind.DtxLzmaCompressed or
        ImageStorageKind.TgaLzmaCompressed or
        ImageStorageKind.RasterLzmaCompressed or
        ImageStorageKind.CrossFireImageBinLzma or
        ImageStorageKind.LithTechModelLzma or
        ImageStorageKind.FmodBankLzma => "LZMA",
        ImageStorageKind.CrossFireImageBin => "BIN",
        ImageStorageKind.DdsBlockCompressed => "DXT",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FIX",
        ImageStorageKind.DtxUncompressed or
        ImageStorageKind.DdsUncompressed or
        ImageStorageKind.TgaUncompressed or
        ImageStorageKind.RasterUncompressed or
        ImageStorageKind.LithTechModel => "RAW",
        ImageStorageKind.LtcText or ImageStorageKind.LtcModel => "LTC",
        ImageStorageKind.LithTechWorldDat => "DAT",
        ImageStorageKind.LithTechWorldDatLzma => "LZMA",
        ImageStorageKind.CrossFireDat => "DAT",
        ImageStorageKind.CrossFireDatLzma => "LZMA",
        ImageStorageKind.CrossFireScriptBin => "BIN",
        ImageStorageKind.LithTechSprite => "SPR",
        ImageStorageKind.LithTechSpriteLzma => "LZMA",
        ImageStorageKind.AudioUncompressed => "RAW",
        ImageStorageKind.AudioLzmaCompressed => "LZMA",
        ImageStorageKind.ResourceText => "TXT",
        ImageStorageKind.ResourceTextLzma => "LZMA",
        ImageStorageKind.ConfigText => "CFG",
        ImageStorageKind.ConfigTextLzma => "LZMA",
        ImageStorageKind.ConfigBinary => "CFG?",
        ImageStorageKind.ConfigBinaryStrip => "STRIP",
        ImageStorageKind.FmodBank => "BANK",
        _ => null
    };

    public string? CompactThumbnailBadgeText => _imageStorageKind switch
    {
        ImageStorageKind.CrossFireImageBinZstd => "ZS",
        ImageStorageKind.DtxLzmaCompressed or
        ImageStorageKind.TgaLzmaCompressed or
        ImageStorageKind.RasterLzmaCompressed or
        ImageStorageKind.CrossFireImageBinLzma or
        ImageStorageKind.LithTechModelLzma or
        ImageStorageKind.FmodBankLzma => "LZ",
        ImageStorageKind.CrossFireImageBin => "BN",
        ImageStorageKind.DdsBlockCompressed => "DX",
        ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "FX",
        ImageStorageKind.DtxUncompressed or
        ImageStorageKind.DdsUncompressed or
        ImageStorageKind.TgaUncompressed or
        ImageStorageKind.RasterUncompressed or
        ImageStorageKind.LithTechModel => "R",
        ImageStorageKind.LtcText or ImageStorageKind.LtcModel => "LT",
        ImageStorageKind.LithTechWorldDat => "DT",
        ImageStorageKind.LithTechWorldDatLzma => "LZ",
        ImageStorageKind.CrossFireDat => "DT",
        ImageStorageKind.CrossFireDatLzma => "LZ",
        ImageStorageKind.CrossFireScriptBin => "BC",
        ImageStorageKind.LithTechSprite => "SP",
        ImageStorageKind.LithTechSpriteLzma => "LZ",
        ImageStorageKind.AudioUncompressed => "R",
        ImageStorageKind.AudioLzmaCompressed => "LZ",
        ImageStorageKind.ResourceText => "TX",
        ImageStorageKind.ResourceTextLzma => "LZ",
        ImageStorageKind.ConfigText => "CF",
        ImageStorageKind.ConfigTextLzma => "LZ",
        ImageStorageKind.ConfigBinary => "C?",
        ImageStorageKind.ConfigBinaryStrip => "CS",
        ImageStorageKind.FmodBank => "BK",
        _ => null
    };

    public bool HasThumbnailBadge => ThumbnailBadgeText is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long? SizeBytes
    {
        get
        {
            return Kind switch
            {
                ExplorerItemKind.LocalFile => TryGetFileSize(SourcePath),
                ExplorerItemKind.RezFile => ArchiveFile?.Size,
                ExplorerItemKind.RezArchive => TryGetFileSize(SourcePath),
                ExplorerItemKind.Directory => SumChildSizes(),
                ExplorerItemKind.RezDirectory => SumChildSizes() ?? SumRezNodeSizes(ArchiveDirectory),
                _ => null
            };
        }
    }

    public string SizeText => FormatByteSize(SizeBytes);

    public string KindText => Kind switch
    {
        ExplorerItemKind.Directory => "Folder",
        ExplorerItemKind.LocalFile => "File",
        ExplorerItemKind.RezArchive => "REZ archive",
        ExplorerItemKind.RezDirectory => "REZ folder",
        ExplorerItemKind.RezFile => "REZ file",
        _ => "Item"
    };

    public string LocationText
    {
        get
        {
            if (!string.IsNullOrEmpty(OutputRelativePath))
            {
                return OutputRelativePath;
            }

            return SourcePath;
        }
    }

    public string InfoToolTip
    {
        get
        {
            var lines = new List<string>
            {
                $"Name: {Name}",
                $"Type: {KindText}",
                $"Size: {SizeText}"
            };

            if (!string.IsNullOrWhiteSpace(OutputRelativePath))
            {
                lines.Add($"Path: {OutputRelativePath}");
            }

            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                lines.Add($"Source: {SourcePath}");
            }

            if (ArchiveFile is not null)
            {
                if (_imageStorageKind is ImageStorageKind.CrossFireImageBinLzma)
                {
                    lines.Add("BIN storage: LZMA-wrapped image");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireImageBinZstd)
                {
                    lines.Add("BIN storage: Zstandard-compressed BGRA image");
                }
                else if (_imageStorageKind is ImageStorageKind.DtxLzmaCompressed or
                    ImageStorageKind.TgaLzmaCompressed or
                    ImageStorageKind.RasterLzmaCompressed or
                    ImageStorageKind.LithTechModelLzma or
                    ImageStorageKind.FmodBankLzma)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: LZMA compressed");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireImageBin)
                {
                    lines.Add("BIN storage: CF10/XOR encoded image");
                }
                else if (_imageStorageKind is ImageStorageKind.DtxUncompressed or
                         ImageStorageKind.TgaUncompressed or
                         ImageStorageKind.RasterUncompressed or
                         ImageStorageKind.LithTechModel or
                         ImageStorageKind.FmodBank)
                {
                    lines.Add($"{GetImageFormatLabel(_imageStorageKind)} storage: uncompressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DdsBlockCompressed)
                {
                    lines.Add("DDS storage: DXT block-compressed");
                }
                else if (_imageStorageKind is ImageStorageKind.DdsUncompressed)
                {
                    lines.Add("DDS storage: uncompressed pixels");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaInsertedFooterHeader)
                {
                    lines.Add("TGA storage: repaired inserted footer/header");
                }
                else if (_imageStorageKind is ImageStorageKind.TgaRawPixels)
                {
                    lines.Add("TGA storage: repaired raw pixel data");
                }
                else if (_imageStorageKind is ImageStorageKind.LtcText)
                {
                    lines.Add("LTC preview: decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.LtcModel)
                {
                    lines.Add("LTC preview: decoded model");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechWorldDat)
                {
                    lines.Add("DAT preview: LithTech world");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechWorldDatLzma)
                {
                    lines.Add("DAT preview: LZMA-compressed LithTech world");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireDat)
                {
                    lines.Add("DAT preview: decoded CrossFire object data");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireDatLzma)
                {
                    lines.Add("DAT preview: LZMA-compressed CrossFire object data");
                }
                else if (_imageStorageKind is ImageStorageKind.CrossFireScriptBin)
                {
                    lines.Add("BIN preview: decoded CrossFire UI script table");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechSprite)
                {
                    lines.Add("SPR preview: LithTech sprite animation");
                }
                else if (_imageStorageKind is ImageStorageKind.LithTechSpriteLzma)
                {
                    lines.Add("SPR preview: LZMA-compressed LithTech sprite animation");
                }
                else if (_imageStorageKind is ImageStorageKind.AudioUncompressed)
                {
                    lines.Add("Audio preview: uncompressed audio");
                }
                else if (_imageStorageKind is ImageStorageKind.AudioLzmaCompressed)
                {
                    lines.Add("Audio preview: LZMA-compressed audio");
                }
                else if (_imageStorageKind is ImageStorageKind.ResourceText)
                {
                    lines.Add("Resource preview: decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.ResourceTextLzma)
                {
                    lines.Add("Resource preview: LZMA-compressed decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.ConfigText)
                {
                    lines.Add("CFG preview: decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.ConfigTextLzma)
                {
                    lines.Add("CFG preview: LZMA-compressed decoded text");
                }
                else if (_imageStorageKind is ImageStorageKind.ConfigBinary)
                {
                    lines.Add("CFG preview: text decode failed, possibly encrypted or binary");
                }
                else if (_imageStorageKind is ImageStorageKind.ConfigBinaryStrip)
                {
                    lines.Add("CFG preview: detected binary RGB strip payload");
                }

                lines.Add($"Offset: {ArchiveFile.DataOffset:N0}");
                lines.Add($"MD5: {ArchiveFile.Md5}");
            }

            if (Archive is not null)
            {
                lines.Add($"Archive: {Path.GetFileName(Archive.FilePath)}");
            }

            int childCount = Children.Count > 0 ? Children.Count : ArchiveDirectory?.Children.Count ?? 0;
            if (IsContainer)
            {
                lines.Add($"Items: {childCount:N0}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public void AddChild(ExplorerItem child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void SortChildren()
    {
        Children.Sort((left, right) =>
        {
            int leftGroup = left.IsContainer ? 0 : 1;
            int rightGroup = right.IsContainer ? 0 : 1;
            int groupCompare = leftGroup.CompareTo(rightGroup);
            return groupCompare != 0
                ? groupCompare
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (ExplorerItem child in Children)
        {
            child.SortChildren();
        }
    }

    private long? SumChildSizes()
    {
        if (Children.Count == 0)
        {
            return null;
        }

        long total = 0;
        bool hasSize = false;
        foreach (ExplorerItem child in Children)
        {
            long? size = child.SizeBytes;
            if (size is null)
            {
                continue;
            }

            total += size.Value;
            hasSize = true;
        }

        return hasSize ? total : null;
    }

    public Task LoadThumbnailAsync()
    {
        if (!IsThumbnailCandidate || _thumbnailSource is not null)
        {
            return Task.CompletedTask;
        }

        lock (_thumbnailSync)
        {
            _thumbnailLoadTask ??= LoadThumbnailCoreAsync();
            return _thumbnailLoadTask;
        }
    }

    public async Task<ImageSource?> LoadPreviewImageAsync()
    {
        IReadOnlyList<ImagePreviewFrame> frames = await LoadPreviewFramesAsync();
        return frames.Count > 0 ? frames[0].Source : null;
    }

    public Task<IReadOnlyList<ImagePreviewFrame>> LoadPreviewFramesAsync()
    {
        return IsImagePreviewCandidate
            ? Task.Run(LoadPreviewFrames)
            : Task.FromResult((IReadOnlyList<ImagePreviewFrame>)Array.Empty<ImagePreviewFrame>());
    }

    public Task<TextPreviewDocument?> LoadTextPreviewAsync()
    {
        return IsTextPreviewCandidate
            ? Task.Run(LoadTextPreview)
            : Task.FromResult<TextPreviewDocument?>(null);
    }

    public Task<LithTechModelDocument?> LoadModelPreviewAsync()
    {
        return IsModelPreviewCandidate
            ? Task.Run(LoadModelPreview)
            : Task.FromResult<LithTechModelDocument?>(null);
    }

    private async Task LoadThumbnailCoreAsync()
    {
        try
        {
            CachedThumbnail? cachedThumbnail = await Task.Run(LoadCachedThumbnail);
            if (cachedThumbnail is not null)
            {
                ApplyThumbnail(cachedThumbnail.Source, cachedThumbnail.StorageKind);
                return;
            }

            await ThumbnailSemaphore.WaitAsync();
            try
            {
                cachedThumbnail = await Task.Run(LoadCachedThumbnail);
                if (cachedThumbnail is not null)
                {
                    ApplyThumbnail(cachedThumbnail.Source, cachedThumbnail.StorageKind);
                    return;
                }

                ThumbnailLoadResult result = await Task.Run(LoadThumbnailAndCache);
                if (result.Source is not null)
                {
                    ApplyThumbnail(result.Source, result.StorageKind);
                }
                else
                {
                    ResetThumbnailLoadTask();
                }
            }
            finally
            {
                ThumbnailSemaphore.Release();
            }
        }
        catch
        {
            ResetThumbnailLoadTask();
        }
    }

    private CachedThumbnail? LoadCachedThumbnail()
    {
        return ThumbnailDiskCache.TryLoad(this, out CachedThumbnail? thumbnail)
            ? thumbnail
            : null;
    }

    private ThumbnailLoadResult LoadThumbnailAndCache()
    {
        ImageSource? thumbnail = LoadThumbnail();
        ImageStorageKind storageKind = _imageStorageKind;
        if (thumbnail is not null)
        {
            ThumbnailDiskCache.TrySave(this, thumbnail, storageKind);
        }

        return new ThumbnailLoadResult(thumbnail, storageKind);
    }

    private void ApplyThumbnail(ImageSource thumbnail, ImageStorageKind storageKind)
    {
        SetImageStorageKind(storageKind);
        _thumbnailSource = thumbnail;
        OnPropertyChanged(nameof(ThumbnailSource));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    private void ResetThumbnailLoadTask()
    {
        lock (_thumbnailSync)
        {
            _thumbnailLoadTask = null;
        }
    }

    private sealed record ThumbnailLoadResult(ImageSource? Source, ImageStorageKind StorageKind);

    private bool CanLoadThumbnail()
    {
        string extension = FileExtension;
        string resourcePath = string.IsNullOrWhiteSpace(OutputRelativePath) ? Name : OutputRelativePath;
        return IsFile &&
               (ThumbnailExtensions.Contains(extension) ||
                TextThumbnailExtensions.Contains(extension) ||
                TextPreviewDecoder.IsPlainTextExtension(extension) ||
                AudioExtensions.Contains(extension) ||
                LithTechModelDecoder.IsCandidate(extension) ||
                LithTechWorldDatDecoder.IsCandidate(extension) ||
                CrossFireDatDecoder.IsCandidate(extension) ||
                CrossFireScriptBinDecoder.IsCandidate(resourcePath, extension) ||
                LithTechSpriteDecoder.IsCandidate(extension) ||
                FmodBankDecoder.IsCandidate(extension));
    }

    private bool CanLoadImagePreview()
    {
        return IsFile && ThumbnailExtensions.Contains(FileExtension);
    }

    private bool CanLoadTextPreview()
    {
        string extension = FileExtension;
        string resourcePath = string.IsNullOrWhiteSpace(OutputRelativePath) ? Name : OutputRelativePath;
        return IsFile &&
               (EncTextDecoder.IsCandidate(resourcePath, extension) ||
                CrossFireLtcDecoder.IsCandidate(extension) ||
                CrossFireDatDecoder.IsCandidate(extension) ||
                CrossFireScriptBinDecoder.IsCandidate(resourcePath, extension) ||
                LithTechSpriteDecoder.IsCandidate(extension) ||
                FmodBankDecoder.IsCandidate(extension) ||
                ResourceTextDecoder.IsCandidate(resourcePath, extension) ||
                TextPreviewDecoder.IsPlainTextExtension(extension));
    }

    private bool CanLoadModelPreview()
    {
        string extension = FileExtension;
        return IsFile &&
               (LithTechModelDecoder.IsCandidate(extension) ||
                LithTechWorldDatDecoder.IsCandidate(extension));
    }

    private ImageSource? LoadThumbnail()
    {
        try
        {
            string extension = FileExtension;
            string resourcePath = string.IsNullOrWhiteSpace(OutputRelativePath) ? Name : OutputRelativePath;
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            if (FmodBankDecoder.IsCandidate(extension))
            {
                return LoadFmodBankThumbnail();
            }

            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxWorldDatPreviewBytes
                : LithTechModelDecoder.IsCandidate(extension)
                        ? MaxModelPreviewBytes
                        : CrossFireDatDecoder.IsCandidate(extension)
                            ? MaxModelPreviewBytes
                            : MaxThumbnailBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null)
            {
                return null;
            }

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, Name, out LithTechModelDocument? worldDocument, out _) &&
                worldDocument is not null)
            {
                SetImageStorageKind(GetWorldDatStorageKind(data));
                return LithTechModelThumbnailRenderer.TryRender(
                    LithTechThumbnailGeometryReducer.ReduceForThumbnail(worldDocument));
            }

            if (CrossFireDatDecoder.IsCandidate(extension))
            {
                return LoadCrossFireDatThumbnail(data);
            }

            if (CrossFireScriptBinDecoder.IsCandidate(resourcePath, extension) &&
                CrossFireScriptBinDecoder.TryDecode(data, resourcePath, out CrossFireScriptBinDocument? scriptBinDocument, out _) &&
                scriptBinDocument is not null)
            {
                SetImageStorageKind(ImageStorageKind.CrossFireScriptBin);
                return TextThumbnailRenderer.TryRender(Name, scriptBinDocument.Text, "BIN");
            }

            if (LithTechSpriteDecoder.IsCandidate(extension))
            {
                return LoadLithTechSpriteThumbnail(data);
            }

            if (AudioExtensions.Contains(extension))
            {
                return LoadAudioThumbnail(data);
            }

            if (ResourceTextDecoder.IsCandidate(Name, extension) && TextThumbnailExtensions.Contains(extension))
            {
                return LoadResourceTextThumbnail(data, extension);
            }

            if (TextPreviewDecoder.IsPlainTextExtension(extension))
            {
                return LoadPlainTextThumbnail(data, extension);
            }

            if (CrossFireLtcDecoder.IsCandidate(extension))
            {
                return LoadLtcThumbnail(data);
            }

            if (LithTechModelDecoder.IsCandidate(extension))
            {
                if (!LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _) ||
                    document is null)
                {
                    return null;
                }

                SetImageStorageKind(GetLithTechModelStorageKind(data));
                return LithTechModelThumbnailRenderer.TryRender(
                    LithTechThumbnailGeometryReducer.ReduceForThumbnail(document));
            }

            if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDtxStorageKind(data));
                return DtxThumbnailDecoder.TryDecode(data);
            }

            if (CrossFireImageBinDecoder.IsCandidate(extension) ||
                CrossFireImageBinDecoder.HasEncodedHeader(data))
            {
                if (CrossFireImageBinDecoder.TryDecodeThumbnail(data, out ImageSource? binImage, out ImageStorageKind binStorageKind) &&
                    binImage is not null)
                {
                    SetImageStorageKind(binStorageKind);
                    return binImage;
                }

                return null;
            }

            if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetTgaStorageKind(data));
                return TgaThumbnailDecoder.TryDecode(data);
            }

            if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDdsStorageKind(data));
                return DdsThumbnailDecoder.TryDecode(data);
            }

            return LoadRasterImage(extension, data, decodeThumbnail: true);
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadLtcThumbnail(byte[] data)
    {
        if (!CrossFireLtcDecoder.TryDecodeText(data, Name, out CrossFireLtcTextDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        if (document.Text.Contains("(lt-model", StringComparison.OrdinalIgnoreCase) &&
            LithTechModelDecoder.TryParseLtaText(
                document.Text,
                Name,
                document.StorageDescription,
                data.Length,
                document.DecodedByteCount,
                out LithTechModelDocument? modelDocument,
                out _) &&
            modelDocument is not null)
        {
            ImageSource? modelThumbnail = LithTechModelThumbnailRenderer.TryRender(
                LithTechThumbnailGeometryReducer.ReduceForThumbnail(modelDocument));
            if (modelThumbnail is not null)
            {
                SetImageStorageKind(ImageStorageKind.LtcModel);
                return modelThumbnail;
            }
        }

        SetImageStorageKind(ImageStorageKind.LtcText);
        return TextThumbnailRenderer.TryRender(Name, document.Text, "LTC");
    }

    private ImageSource? LoadCrossFireDatThumbnail(byte[] data)
    {
        if (!CrossFireDatDecoder.TryDecode(data, Name, out CrossFireDatDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(GetCrossFireDatStorageKind(data));
        return TextThumbnailRenderer.TryRender(Name, document.Text, "DAT");
    }

    private ImageSource? LoadLithTechSpriteThumbnail(byte[] data)
    {
        if (!LithTechSpriteDecoder.TryDecode(data, Name, out LithTechSpriteDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(GetLithTechSpriteStorageKind(data));
        ImageSource? frameThumbnail = Kind switch
        {
            ExplorerItemKind.RezFile when Archive is not null => LithTechSpritePreviewLoader.TryLoadThumbnailFromArchive(Archive, document),
            ExplorerItemKind.LocalFile when !string.IsNullOrWhiteSpace(SourcePath) => LithTechSpritePreviewLoader.TryLoadThumbnailFromLocalFile(SourcePath, document),
            _ => null
        };

        return frameThumbnail ?? TextThumbnailRenderer.TryRender(Name, document.Text, "SPR");
    }

    private ImageSource? LoadAudioThumbnail(byte[] data)
    {
        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            return null;
        }

        SetImageStorageKind(LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.AudioLzmaCompressed
            : ImageStorageKind.AudioUncompressed);
        return AudioThumbnailRenderer.TryRender(Name, prepared);
    }

    private ImageSource? LoadResourceTextThumbnail(byte[] data, string extension)
    {
        if (!ResourceTextDecoder.TryDecode(data, Name, extension, out ResourceTextDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        SetImageStorageKind(LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.ResourceTextLzma
            : ImageStorageKind.ResourceText);
        string badge = string.Equals(extension, "txt", StringComparison.OrdinalIgnoreCase)
            ? "TXT"
            : extension.ToUpperInvariant();
        return TextThumbnailRenderer.TryRender(Name, document.Text, badge);
    }

    private ImageSource? LoadPlainTextThumbnail(byte[] data, string extension)
    {
        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase) &&
            CfgTextDecoder.TryDecode(data, Name, MaxThumbnailBytes, out CfgTextDocument? cfgDocument) &&
            cfgDocument is not null)
        {
            SetImageStorageKind(cfgDocument.Description.StartsWith("lzma", StringComparison.OrdinalIgnoreCase)
                ? ImageStorageKind.ConfigTextLzma
                : ImageStorageKind.ConfigText);
            return TextThumbnailRenderer.TryRender(Name, cfgDocument.Text, "CFG");
        }

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxThumbnailBytes);
        bool compressed = prepared is not null && !ReferenceEquals(prepared, data);
        byte[] textBytes = prepared ?? data;
        if (!TextPreviewDecoder.TryDecode(textBytes, preferKorean: false, out string text, out _))
        {
            if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
            {
                if (CfgBinaryStripDecoder.TryRenderThumbnail(textBytes, out ImageSource? stripThumbnail, out _))
                {
                    SetImageStorageKind(ImageStorageKind.ConfigBinaryStrip);
                    return stripThumbnail;
                }

                SetImageStorageKind(ImageStorageKind.ConfigBinary);
                return TextThumbnailRenderer.TryRender(Name, "Text decode failed\nPossible encrypted or binary CFG", "CFG?");
            }

            return null;
        }

        if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            SetImageStorageKind(compressed
                ? ImageStorageKind.ConfigTextLzma
                : ImageStorageKind.ConfigText);
            return TextThumbnailRenderer.TryRender(Name, text, "CFG");
        }

        SetImageStorageKind(compressed
            ? ImageStorageKind.ResourceTextLzma
            : ImageStorageKind.ResourceText);
        return TextThumbnailRenderer.TryRender(Name, text, extension.ToUpperInvariant());
    }

    private ImageSource? LoadFmodBankThumbnail()
    {
        byte[]? data = ReadFmodBankThumbnailBytes(out bool compressed);
        return data is null ? null : LoadFmodBankThumbnail(data, compressed);
    }

    private ImageSource? LoadFmodBankThumbnail(byte[] data, bool compressed)
    {
        SetImageStorageKind(compressed
            ? ImageStorageKind.FmodBankLzma
            : ImageStorageKind.FmodBank);

        if (FmodBankAudioPreviewDocumentFactory.TryCreateThumbnailAudioData(
                Name,
                data,
                out byte[]? audioData,
                out string? audioTitle,
                out _) &&
            audioData is not null)
        {
            ImageSource? audioThumbnail = AudioThumbnailRenderer.TryRender(audioTitle ?? Name, audioData);
            if (audioThumbnail is not null)
            {
                return audioThumbnail;
            }
        }

        if (!FmodBankDecoder.TryDecode(data, Name, out FmodBankDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        string badge = document.FsbBlockCount > 0 ? "FSB" : "BANK";
        return TextThumbnailRenderer.TryRender(Name, document.Text, badge);
    }

    private byte[]? ReadFmodBankThumbnailBytes(out bool compressed)
    {
        compressed = false;
        byte[]? prefix = ReadFilePrefixBytes(FmodBankDecoder.MaxThumbnailSourceBytes);
        if (prefix is null || prefix.Length == 0)
        {
            return null;
        }

        compressed = FmodBankDecoder.IsCompressedBank(prefix);
        if (!compressed)
        {
            return prefix;
        }

        long? fileByteCount = GetFileByteCount();
        if (fileByteCount is null ||
            fileByteCount <= prefix.Length ||
            fileByteCount > FmodBankDecoder.MaxSourceBytes ||
            fileByteCount > int.MaxValue)
        {
            return prefix;
        }

        return ReadLzmaFilePrefixBytes(FmodBankDecoder.MaxThumbnailSourceBytes) ?? prefix;
    }

    private static LithTechModelDocument SimplifyForThumbnail(LithTechModelDocument document, int maxTriangles)
    {
        if (document.TriangleCount <= maxTriangles)
        {
            return document;
        }

        var meshes = new List<LithTechMesh>();
        int remainingTriangles = maxTriangles;
        int totalTriangles = document.TriangleCount;

        foreach (LithTechMesh mesh in document.Meshes)
        {
            if (remainingTriangles <= 0)
            {
                break;
            }

            int meshTriangles = mesh.TriangleIndices.Count / 3;
            if (meshTriangles == 0)
            {
                continue;
            }

            int targetTriangles = Math.Max(1, (int)Math.Round((double)meshTriangles / totalTriangles * maxTriangles));
            targetTriangles = Math.Min(targetTriangles, remainingTriangles);
            LithTechMesh? simplified = SimplifyMeshForThumbnail(mesh, targetTriangles);
            if (simplified is null)
            {
                continue;
            }

            meshes.Add(simplified);
            remainingTriangles -= simplified.TriangleIndices.Count / 3;
        }

        return meshes.Count == 0
            ? document
            : new LithTechModelDocument(
                document.Name,
                meshes,
                $"{document.StorageDescription} thumbnail sample",
                document.SourceByteCount,
                document.DecodedByteCount);
    }

    private static LithTechMesh? SimplifyMeshForThumbnail(LithTechMesh mesh, int targetTriangles)
    {
        int meshTriangles = mesh.TriangleIndices.Count / 3;
        if (targetTriangles <= 0 || meshTriangles == 0)
        {
            return null;
        }

        if (meshTriangles <= targetTriangles)
        {
            return mesh;
        }

        int stride = Math.Max(1, meshTriangles / targetTriangles);
        var vertexMap = new Dictionary<int, int>();
        var vertices = new List<LithTechVector3>();
        var indices = new List<int>(targetTriangles * 3);

        for (int triangle = 0; triangle < meshTriangles && indices.Count / 3 < targetTriangles; triangle += stride)
        {
            int triangleOffset = triangle * 3;
            int a = mesh.TriangleIndices[triangleOffset];
            int b = mesh.TriangleIndices[triangleOffset + 1];
            int c = mesh.TriangleIndices[triangleOffset + 2];
            if (!TryAddMappedTriangle(mesh, a, b, c, vertexMap, vertices, indices))
            {
                continue;
            }
        }

        return indices.Count >= 3
            ? new LithTechMesh(mesh.Name, vertices, indices)
            : null;
    }

    private static bool TryAddMappedTriangle(
        LithTechMesh mesh,
        int a,
        int b,
        int c,
        Dictionary<int, int> vertexMap,
        List<LithTechVector3> vertices,
        List<int> indices)
    {
        if (a < 0 || b < 0 || c < 0 ||
            a >= mesh.Vertices.Count ||
            b >= mesh.Vertices.Count ||
            c >= mesh.Vertices.Count)
        {
            return false;
        }

        indices.Add(GetMappedIndex(mesh, a, vertexMap, vertices));
        indices.Add(GetMappedIndex(mesh, b, vertexMap, vertices));
        indices.Add(GetMappedIndex(mesh, c, vertexMap, vertices));
        return true;
    }

    private static int GetMappedIndex(
        LithTechMesh mesh,
        int sourceIndex,
        Dictionary<int, int> vertexMap,
        List<LithTechVector3> vertices)
    {
        if (vertexMap.TryGetValue(sourceIndex, out int mappedIndex))
        {
            return mappedIndex;
        }

        mappedIndex = vertices.Count;
        vertexMap.Add(sourceIndex, mappedIndex);
        vertices.Add(mesh.Vertices[sourceIndex]);
        return mappedIndex;
    }

    private ImageSource? LoadPreviewImage()
    {
        IReadOnlyList<ImagePreviewFrame> frames = LoadPreviewFrames();
        return frames.Count > 0 ? frames[0].Source : null;
    }

    private IReadOnlyList<ImagePreviewFrame> LoadPreviewFrames()
    {
        try
        {
            string extension = FileExtension;
            byte[]? data = ReadFileBytes(MaxPreviewBytes);
            if (data is null || string.IsNullOrWhiteSpace(extension))
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            if (string.Equals(extension, "dtx", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDtxStorageKind(data));
                return CreatePreviewFrames(DtxThumbnailDecoder.TryDecodeOriginal(data));
            }

            if (CrossFireImageBinDecoder.IsCandidate(extension) ||
                CrossFireImageBinDecoder.HasEncodedHeader(data))
            {
                IReadOnlyList<ImagePreviewFrame> frames = CrossFireImageBinDecoder.TryDecodePreviewFrames(data, out ImageStorageKind binStorageKind);
                if (frames.Count > 0)
                {
                    SetImageStorageKind(binStorageKind);
                }

                return frames;
            }

            if (string.Equals(extension, "tga", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetTgaStorageKind(data));
                return TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            }

            if (string.Equals(extension, "dds", StringComparison.OrdinalIgnoreCase))
            {
                SetImageStorageKind(GetDdsStorageKind(data));
                return CreatePreviewFrames(DdsThumbnailDecoder.TryDecodeOriginal(data));
            }

            return LoadRasterPreviewFrames(extension, data);
        }
        catch
        {
            return Array.Empty<ImagePreviewFrame>();
        }
    }

    private TextPreviewDocument? LoadTextPreview()
    {
        try
        {
            string extension = FileExtension;
            string resourcePath = string.IsNullOrWhiteSpace(OutputRelativePath) ? Name : OutputRelativePath;
            int maxBytes = FmodBankDecoder.IsCandidate(extension)
                ? FmodBankDecoder.MaxSourceBytes
                : MaxTextPreviewBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null || string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            if (EncTextDecoder.IsCandidate(resourcePath, extension))
            {
                if (!EncTextDecoder.TryDecode(data, out byte[] decodedBytes, out bool lzmaWrapped) ||
                    !TextPreviewDecoder.TryDecode(decodedBytes, preferKorean: true, out string decodedText, out string decodedEncoding))
                {
                    return null;
                }

                return new TextPreviewDocument(
                    decodedText,
                    lzmaWrapped ? $"LZMA + ENC / Base64 / {decodedEncoding}" : $"ENC / Base64 / {decodedEncoding}",
                    lzmaWrapped ? TextPreviewStorageKind.EncLzmaBase64 : TextPreviewStorageKind.EncBase64,
                    data.Length,
                    decodedBytes.Length);
            }

            if (CrossFireLtcDecoder.IsCandidate(extension))
            {
                if (!CrossFireLtcDecoder.TryDecodeText(data, Name, out CrossFireLtcTextDocument? ltcDocument, out _) ||
                    ltcDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    ltcDocument.Text,
                    $"{ltcDocument.StorageDescription} / {ltcDocument.EncodingName}",
                    TextPreviewStorageKind.LtcConverted,
                    ltcDocument.SourceByteCount,
                    ltcDocument.DecodedByteCount);
            }

            if (CrossFireDatDecoder.IsCandidate(extension))
            {
                if (!CrossFireDatDecoder.TryDecode(data, Name, out CrossFireDatDocument? datDocument, out _) ||
                    datDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    datDocument.Text,
                    $"{datDocument.StorageDescription} / version {datDocument.Version}, {datDocument.ObjectCount:N0} {datDocument.ObjectKind}",
                    TextPreviewStorageKind.CrossFireDat,
                    datDocument.SourceByteCount,
                    datDocument.DecodedByteCount);
            }

            if (CrossFireScriptBinDecoder.IsCandidate(resourcePath, extension))
            {
                if (!CrossFireScriptBinDecoder.TryDecode(data, resourcePath, out CrossFireScriptBinDocument? scriptBinDocument, out _) ||
                    scriptBinDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    scriptBinDocument.Text,
                    scriptBinDocument.Description,
                    TextPreviewStorageKind.ResourceDecoded,
                    scriptBinDocument.SourceByteCount,
                    scriptBinDocument.DecodedByteCount);
            }

            if (LithTechSpriteDecoder.IsCandidate(extension))
            {
                if (!LithTechSpriteDecoder.TryDecode(data, Name, out LithTechSpriteDocument? sprDocument, out _) ||
                    sprDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    sprDocument.Text,
                    $"{sprDocument.StorageDescription}, {sprDocument.FrameCount:N0} frames @ {sprDocument.FrameRate:N0} fps",
                    TextPreviewStorageKind.LithTechSprite,
                    sprDocument.SourceByteCount,
                    sprDocument.DecodedByteCount);
            }

            if (FmodBankDecoder.IsCandidate(extension))
            {
                if (!FmodBankDecoder.TryDecode(data, Name, out FmodBankDocument? bankDocument, out _) ||
                    bankDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    bankDocument.Text,
                    bankDocument.StorageDescription,
                    TextPreviewStorageKind.FmodBank,
                    bankDocument.SourceByteCount,
                    bankDocument.DecodedByteCount);
            }

            if (ResourceTextDecoder.IsCandidate(resourcePath, extension))
            {
                if (!ResourceTextDecoder.TryDecode(data, resourcePath, extension, out ResourceTextDocument? resourceDocument, out _) ||
                    resourceDocument is null)
                {
                    return null;
                }

                return new TextPreviewDocument(
                    resourceDocument.Text,
                    resourceDocument.Description,
                    TextPreviewStorageKind.ResourceDecoded,
                    resourceDocument.SourceByteCount,
                    resourceDocument.DecodedByteCount);
            }

            if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase) &&
                CfgTextDecoder.TryDecode(data, Name, MaxTextPreviewBytes, out CfgTextDocument? cfgDocument) &&
                cfgDocument is not null)
            {
                return new TextPreviewDocument(
                    cfgDocument.Text,
                    cfgDocument.Description,
                    TextPreviewStorageKind.Plain,
                    cfgDocument.SourceByteCount,
                    cfgDocument.DecodedByteCount);
            }

            byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxTextPreviewBytes);
            bool compressed = prepared is not null && !ReferenceEquals(prepared, data);
            byte[] textBytes = prepared ?? data;
            if (!TextPreviewDecoder.TryDecode(textBytes, preferKorean: false, out string text, out string encoding))
            {
                if (string.Equals(extension, "cfg", StringComparison.OrdinalIgnoreCase))
                {
                    return new TextPreviewDocument(
                        BuildCfgDecodeFailurePreview(data, textBytes),
                        compressed ? "LZMA / decode failed" : "Decode failed",
                        TextPreviewStorageKind.Plain,
                        data.Length,
                        textBytes.Length);
                }

                return null;
            }

            return new TextPreviewDocument(
                text,
                compressed ? $"LZMA / {encoding}" : encoding,
                TextPreviewStorageKind.Plain,
                data.Length,
                textBytes.Length);
        }
        catch
        {
            return null;
        }
    }

    private string BuildCfgDecodeFailurePreview(byte[] sourceBytes, byte[] preparedBytes)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("CFG text decode failed.");
        builder.AppendLine("The file may be encrypted, compressed with an unsupported method, or binary data.");
        builder.AppendLine();
        builder.AppendLine($"File: {Name}");
        builder.AppendLine($"Source bytes: {sourceBytes.Length:N0}");
        builder.AppendLine($"Prepared bytes: {preparedBytes.Length:N0}");
        builder.AppendLine($"LZMA header: {(LzmaAloneDecoder.IsCompressed(sourceBytes) ? "yes" : "no")}");
        if (CfgBinaryStripDecoder.TryDetect(preparedBytes, out CfgBinaryStripInfo stripInfo))
        {
            builder.AppendLine($"Binary CFG pattern: {CfgBinaryStripDecoder.Describe(stripInfo)}");
        }

        builder.AppendLine();
        AppendBytePreview(builder, preparedBytes);
        AppendAsciiStrings(builder, preparedBytes);
        return builder.ToString();
    }

    private static void AppendBytePreview(System.Text.StringBuilder builder, byte[] data)
    {
        int count = Math.Min(data.Length, 128);
        builder.AppendLine($"First {count:N0} bytes:");
        if (count == 0)
        {
            builder.AppendLine("(empty)");
            builder.AppendLine();
            return;
        }

        for (int offset = 0; offset < count; offset += 16)
        {
            int lineCount = Math.Min(16, count - offset);
            string hex = string.Join(" ", data.Skip(offset).Take(lineCount).Select(value => value.ToString("X2")));
            string ascii = new(data.Skip(offset).Take(lineCount).Select(ToPreviewChar).ToArray());
            builder.AppendLine($"{offset:X4}: {hex,-47}  {ascii}");
        }

        builder.AppendLine();
    }

    private static void AppendAsciiStrings(System.Text.StringBuilder builder, byte[] data)
    {
        builder.AppendLine("Printable ASCII strings:");
        int written = 0;
        foreach (string value in ExtractPrintableAsciiStrings(data, minLength: 4, maxCount: 32))
        {
            builder.AppendLine(value);
            written++;
        }

        if (written == 0)
        {
            builder.AppendLine("(none)");
        }
    }

    private static IEnumerable<string> ExtractPrintableAsciiStrings(byte[] data, int minLength, int maxCount)
    {
        var current = new System.Text.StringBuilder();
        foreach (byte value in data)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                current.Append((char)value);
                continue;
            }

            if (current.Length >= minLength)
            {
                yield return current.ToString();
                maxCount--;
                if (maxCount <= 0)
                {
                    yield break;
                }
            }

            current.Clear();
        }

        if (current.Length >= minLength && maxCount > 0)
        {
            yield return current.ToString();
        }
    }

    private static char ToPreviewChar(byte value)
    {
        return value is >= 0x20 and <= 0x7E ? (char)value : '.';
    }

    private LithTechModelDocument? LoadModelPreview()
    {
        try
        {
            string extension = FileExtension;
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxWorldDatPreviewBytes
                : MaxModelPreviewBytes;
            byte[]? data = ReadFileBytes(maxBytes);
            if (data is null)
            {
                return null;
            }

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, Name, out LithTechModelDocument? worldDocument, out _) &&
                worldDocument is not null)
            {
                return worldDocument;
            }

            return LithTechModelDecoder.TryDecode(data, Name, extension, out LithTechModelDocument? document, out _)
                ? document
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ImagePreviewFrame> CreatePreviewFrames(ImageSource? imageSource)
    {
        return imageSource is null
            ? Array.Empty<ImagePreviewFrame>()
            : new[] { new ImagePreviewFrame("Original", imageSource) };
    }

    private ImageSource? LoadRasterImage(string extension, byte[] data, bool decodeThumbnail)
    {
        try
        {
            if (!TryPrepareRasterImageData(data, out byte[]? imageData, out ImageStorageKind storageKind) ||
                imageData is null)
            {
                return TryLoadPngStoredAsTga(extension, data);
            }

            ImageSource image = LoadBitmapImage(imageData, decodeThumbnail);
            SetImageStorageKind(storageKind);
            return image;
        }
        catch
        {
            return TryLoadPngStoredAsTga(extension, data);
        }
    }

    private IReadOnlyList<ImagePreviewFrame> LoadRasterPreviewFrames(string extension, byte[] data)
    {
        try
        {
            if (!TryPrepareRasterImageData(data, out byte[]? imageData, out ImageStorageKind storageKind) ||
                imageData is null)
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            IReadOnlyList<ImagePreviewFrame> frames = CreatePreviewFrames(LoadBitmapImage(imageData, decodeThumbnail: false));
            if (frames.Count > 0)
            {
                SetImageStorageKind(storageKind);
            }

            return frames;
        }
        catch
        {
            if (!IsPngExtension(extension))
            {
                return Array.Empty<ImagePreviewFrame>();
            }

            IReadOnlyList<ImagePreviewFrame> frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            if (frames.Count > 0)
            {
                SetImageStorageKind(GetTgaStorageKind(data));
            }

            return frames;
        }
    }

    private static bool TryPrepareRasterImageData(
        byte[] data,
        out byte[]? imageData,
        out ImageStorageKind storageKind)
    {
        if (!LzmaAloneDecoder.IsCompressed(data))
        {
            imageData = data;
            storageKind = ImageStorageKind.RasterUncompressed;
            return true;
        }

        imageData = LzmaAloneDecoder.TryPrepareData(data);
        storageKind = ImageStorageKind.RasterLzmaCompressed;
        return imageData is not null;
    }

    private ImageSource? TryLoadPngStoredAsTga(string extension, byte[] data)
    {
        if (!IsPngExtension(extension))
        {
            return null;
        }

        ImageSource? image = TgaThumbnailDecoder.TryDecode(data);
        if (image is null)
        {
            IReadOnlyList<ImagePreviewFrame> frames = TgaThumbnailDecoder.TryDecodePreviewFrames(data);
            image = frames.Count > 0 ? frames[0].Source : null;
        }

        if (image is not null)
        {
            SetImageStorageKind(GetTgaStorageKind(data));
        }

        return image;
    }

    private static bool IsPngExtension(string extension)
    {
        return string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase);
    }

    private byte[]? ReadFileBytes(int maxBytes)
    {
        if (Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(SourcePath);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes || info.Length > int.MaxValue)
            {
                return null;
            }

            return File.ReadAllBytes(SourcePath);
        }

        if (Archive is null ||
            ArchiveFile is null ||
            ArchiveFile.Size < 0 ||
            ArchiveFile.Size > maxBytes)
        {
            return null;
        }

        byte[] data = new byte[ArchiveFile.Size];
        using Stream source = RezArchiveReader.OpenFileRead(Archive, ArchiveFile);
        source.ReadExactly(data);
        return data;
    }

    private byte[]? ReadFilePrefixBytes(int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return null;
        }

        if (Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(SourcePath);
            if (!info.Exists || info.Length <= 0)
            {
                return null;
            }

            int byteCount = checked((int)Math.Min(Math.Min(info.Length, maxBytes), int.MaxValue));
            byte[] data = new byte[byteCount];
            using FileStream source = File.OpenRead(SourcePath);
            source.ReadExactly(data);
            return data;
        }

        if (Archive is null ||
            ArchiveFile is null ||
            ArchiveFile.Size <= 0)
        {
            return null;
        }

        int archiveByteCount = checked((int)Math.Min(Math.Min(ArchiveFile.Size, maxBytes), int.MaxValue));
        byte[] archiveData = new byte[archiveByteCount];
        using Stream archiveSource = RezArchiveReader.OpenFileRead(Archive, ArchiveFile);
        archiveSource.ReadExactly(archiveData);
        return archiveData;
    }

    private long? GetFileByteCount()
    {
        if (Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(SourcePath);
            return info.Exists ? info.Length : null;
        }

        return ArchiveFile?.Size;
    }

    private byte[]? ReadLzmaFilePrefixBytes(int maxDecodedBytes)
    {
        if (Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(SourcePath);
            if (!info.Exists || info.Length <= 0 || info.Length > FmodBankDecoder.MaxSourceBytes)
            {
                return null;
            }

            using FileStream source = File.OpenRead(SourcePath);
            return LzmaAloneDecoder.TryDecompressPrefix(source, info.Length, maxDecodedBytes);
        }

        if (Archive is null ||
            ArchiveFile is null ||
            ArchiveFile.Size <= 0 ||
            ArchiveFile.Size > FmodBankDecoder.MaxSourceBytes)
        {
            return null;
        }

        using Stream archiveSource = RezArchiveReader.OpenFileRead(Archive, ArchiveFile);
        return LzmaAloneDecoder.TryDecompressPrefix(archiveSource, ArchiveFile.Size, maxDecodedBytes);
    }

    private static ImageSource LoadBitmapImage(byte[] data, bool decodeThumbnail)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeThumbnail)
        {
            SetThumbnailDecodeSize(image, data);
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void SetThumbnailDecodeSize(BitmapImage image, byte[] data)
    {
        (int width, int height) = ReadBitmapDimensions(data);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (width >= height)
        {
            image.DecodePixelWidth = ThumbnailDecodeMaxSide;
        }
        else
        {
            image.DecodePixelHeight = ThumbnailDecodeMaxSide;
        }
    }

    private static (int Width, int Height) ReadBitmapDimensions(byte[] data)
    {
        using var stream = new MemoryStream(data);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.Default);
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static ImageStorageKind GetDtxStorageKind(byte[] data)
    {
        return DtxThumbnailDecoder.IsLzmaCompressed(data)
            ? ImageStorageKind.DtxLzmaCompressed
            : ImageStorageKind.DtxUncompressed;
    }

    private static ImageStorageKind GetDdsStorageKind(byte[] data)
    {
        return DdsThumbnailDecoder.IsBlockCompressed(data)
            ? ImageStorageKind.DdsBlockCompressed
            : ImageStorageKind.DdsUncompressed;
    }

    private static ImageStorageKind GetTgaStorageKind(byte[] data)
    {
        if (TgaThumbnailDecoder.IsLzmaCompressed(data))
        {
            return ImageStorageKind.TgaLzmaCompressed;
        }

        return TgaThumbnailDecoder.HasInsertedFooterHeader(data)
            ? ImageStorageKind.TgaInsertedFooterHeader
            : TgaThumbnailDecoder.HasRawPixelData(data)
                ? ImageStorageKind.TgaRawPixels
                : ImageStorageKind.TgaUncompressed;
    }

    private static ImageStorageKind GetWorldDatStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechWorldDatLzma
            : ImageStorageKind.LithTechWorldDat;
    }

    private static ImageStorageKind GetCrossFireDatStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.CrossFireDatLzma
            : ImageStorageKind.CrossFireDat;
    }

    private static ImageStorageKind GetLithTechSpriteStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechSpriteLzma
            : ImageStorageKind.LithTechSprite;
    }

    private static ImageStorageKind GetLithTechModelStorageKind(byte[] data)
    {
        return LzmaAloneDecoder.IsCompressed(data)
            ? ImageStorageKind.LithTechModelLzma
            : ImageStorageKind.LithTechModel;
    }

    private static string GetImageFormatLabel(ImageStorageKind storageKind)
    {
        return storageKind switch
        {
            ImageStorageKind.DtxLzmaCompressed or ImageStorageKind.DtxUncompressed => "DTX",
            ImageStorageKind.DdsBlockCompressed or ImageStorageKind.DdsUncompressed => "DDS",
            ImageStorageKind.TgaLzmaCompressed or ImageStorageKind.TgaUncompressed or ImageStorageKind.TgaInsertedFooterHeader or ImageStorageKind.TgaRawPixels => "TGA",
            ImageStorageKind.RasterLzmaCompressed or ImageStorageKind.RasterUncompressed => "Image",
            ImageStorageKind.CrossFireImageBin or ImageStorageKind.CrossFireImageBinLzma or ImageStorageKind.CrossFireImageBinZstd => "BIN image",
            ImageStorageKind.CrossFireScriptBin => "BIN",
            ImageStorageKind.LithTechModelLzma or ImageStorageKind.LithTechModel => "Model",
            ImageStorageKind.LithTechWorldDat or ImageStorageKind.LithTechWorldDatLzma or ImageStorageKind.CrossFireDat or ImageStorageKind.CrossFireDatLzma => "DAT",
            ImageStorageKind.LithTechSprite or ImageStorageKind.LithTechSpriteLzma => "SPR",
            ImageStorageKind.AudioUncompressed or ImageStorageKind.AudioLzmaCompressed => "Audio",
            ImageStorageKind.ResourceText or ImageStorageKind.ResourceTextLzma => "Resource",
            ImageStorageKind.ConfigText or ImageStorageKind.ConfigTextLzma or ImageStorageKind.ConfigBinary or ImageStorageKind.ConfigBinaryStrip => "CFG",
            ImageStorageKind.FmodBank or ImageStorageKind.FmodBankLzma => "BANK",
            _ => "Image"
        };
    }

    private void SetImageStorageKind(ImageStorageKind storageKind)
    {
        if (_imageStorageKind == storageKind)
        {
            return;
        }

        _imageStorageKind = storageKind;
        OnPropertyChanged(nameof(ImageStorageKind));
        OnPropertyChanged(nameof(ThumbnailBadgeText));
        OnPropertyChanged(nameof(CompactThumbnailBadgeText));
        OnPropertyChanged(nameof(HasThumbnailBadge));
        OnPropertyChanged(nameof(InfoToolTip));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static long? SumRezNodeSizes(RezDirectoryNode? directory)
    {
        if (directory is null)
        {
            return null;
        }

        long total = 0;
        bool hasSize = false;
        foreach (RezNode node in directory.Children)
        {
            if (node is RezFileNode file)
            {
                total += file.Size;
                hasSize = true;
            }
            else if (node is RezDirectoryNode childDirectory)
            {
                long? childSize = SumRezNodeSizes(childDirectory);
                if (childSize is not null)
                {
                    total += childSize.Value;
                    hasSize = true;
                }
            }
        }

        return hasSize ? total : null;
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatByteSize(long? bytes)
    {
        if (bytes is null)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes.Value;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} {units[unitIndex]}" : $"{value:N1} {units[unitIndex]}";
    }

    private static ImageSource? LoadFolderIcon()
    {
        string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "folder.png");
        if (!File.Exists(imagePath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
