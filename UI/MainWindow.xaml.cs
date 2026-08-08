using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CFRezManager;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty TileItemWidthProperty =
        DependencyProperty.Register(nameof(TileItemWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(136.0));

    public static readonly DependencyProperty TileItemHeightProperty =
        DependencyProperty.Register(nameof(TileItemHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(128.0));

    public static readonly DependencyProperty TileCellWidthProperty =
        DependencyProperty.Register(nameof(TileCellWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(148.0));

    public static readonly DependencyProperty TileCellHeightProperty =
        DependencyProperty.Register(nameof(TileCellHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(140.0));

    public static readonly DependencyProperty TileIconSizeProperty =
        DependencyProperty.Register(nameof(TileIconSize), typeof(double), typeof(MainWindow), new PropertyMetadata(61.0));

    public static readonly DependencyProperty TileIconRowHeightProperty =
        DependencyProperty.Register(nameof(TileIconRowHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(71.0));

    public static readonly DependencyProperty TileFileIconWidthProperty =
        DependencyProperty.Register(nameof(TileFileIconWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(50.0));

    public static readonly DependencyProperty TileFilePageWidthProperty =
        DependencyProperty.Register(nameof(TileFilePageWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(42.0));

    public static readonly DependencyProperty TileFilePageHeightProperty =
        DependencyProperty.Register(nameof(TileFilePageHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(54.0));

    public static readonly DependencyProperty TileNameMaxHeightProperty =
        DependencyProperty.Register(nameof(TileNameMaxHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(44.0));

    private const double ListViewThreshold = 35.0;
    private const int InitialBankPreviewStreams = 1;
    private const int BankPreviewPrefixStepBytes = 8 * 1024 * 1024;
    private const int BankBackgroundPrefetchStepBytes = 8 * 1024 * 1024;
    private const int MaxObjModelBytes = 128 * 1024 * 1024;
    private const int MaxObjWorldDatBytes = 256 * 1024 * 1024;
    private static readonly int[] FastBankPreviewPrefixSizes =
    [
        BankPreviewPrefixStepBytes,
        32 * 1024 * 1024,
        FmodBankDecoder.MaxThumbnailSourceBytes
    ];
    private static readonly int[] ProgressiveBankPreviewPrefixSizes =
    [
        2 * BankPreviewPrefixStepBytes,
        3 * BankPreviewPrefixStepBytes,
        32 * 1024 * 1024,
        48 * 1024 * 1024,
        64 * 1024 * 1024,
        FmodBankDecoder.MaxThumbnailSourceBytes
    ];

    private sealed record BankAudioInitialLoadResult(FmodBankAudioSource Source, BankAudioStreamSession Session);

    private sealed class BankAudioStreamSession : IDisposable
    {
        public BankAudioStreamSession(
            string fileName,
            Stream decodedStream,
            bool compressed,
            int sourceByteCount,
            long totalDecodedBytes)
        {
            FileName = fileName;
            DecodedStream = decodedStream;
            Compressed = compressed;
            SourceByteCount = sourceByteCount;
            TotalDecodedBytes = totalDecodedBytes;
        }

        public string FileName { get; }
        public Stream DecodedStream { get; }
        public bool Compressed { get; }
        public int SourceByteCount { get; }
        public long TotalDecodedBytes { get; }
        public MemoryStream DecodedData { get; } = new();
        public int DecodedByteCount => checked((int)Math.Min(DecodedData.Length, int.MaxValue));
        public bool IsComplete => DecodedData.Length >= TotalDecodedBytes;

        public void Dispose()
        {
            DecodedStream.Dispose();
            DecodedData.Dispose();
        }
    }

    private sealed class BankAudioStreamState : IDisposable
    {
        public BankAudioStreamState(BankAudioStreamSession session, FmodBankAudioSource source)
        {
            Session = session;
            CurrentSource = source;
        }

        public object SyncRoot { get; } = new();
        public BankAudioStreamSession? Session { get; private set; }
        public FmodBankAudioSource CurrentSource { get; private set; }

        public void UpdateSource(FmodBankAudioSource source)
        {
            CurrentSource = source;
        }

        public void DisposeSession()
        {
            Session?.Dispose();
            Session = null;
        }

        public void Dispose()
        {
            DisposeSession();
        }
    }

    private enum AppLanguage
    {
        Chinese,
        English
    }

    private enum FolderDialogKind
    {
        RezSource,
        PackSource,
        Output
    }

    private enum ContentFilter
    {
        All,
        Folders,
        Images,
        Models,
        Audio,
        Text,
        Other
    }

    private ExplorerItem? _rootItem;
    private ExplorerItem? _currentItem;
    private readonly List<ExplorerItem> _backHistory = new();
    private readonly List<ExplorerItem> _forwardHistory = new();
    private readonly UserSettings _settings;
    private readonly HashSet<ExplorerItem> _dragInitialSelection = new();
    private List<SearchEntry> _searchIndex = new();
    private AppLanguage _language = AppLanguage.Chinese;
    private AppTheme _theme = AppTheme.Light;
    private ContentFilter _contentFilter = ContentFilter.All;
    private string _statusKey = "Ready";
    private object[] _statusArgs = [];
    private string _selectedDirectory = string.Empty;
    private System.Windows.Point? _dragStartPoint;
    private int _archiveCount;
    private int _extractableFileCount;
    private bool _isBusy;
    private bool _isDragSelecting;
    private bool _isListViewMode;
    private bool _isSearchIndexReady;
    private bool _isSearchMode;
    private bool _suppressSearchTextChanged;
    private int _searchRequestVersion;
    private Task? _searchIndexTask;
    private ExplorerItem? _searchIndexTaskRoot;
    private SettingsWindow? _settingsWindow;

    private readonly record struct ExtractionProgress(int Completed, int Total, string FileName);
    private readonly record struct ExtractionJob(
        ExplorerItem Item,
        string SourceRelativePath,
        string OutputRelativePath,
        bool DecodeImageToPng);
    private readonly record struct ExtractionBatchResult(int DecodedImageCount, int DecodeFallbackCount);
    private readonly record struct ModelObjExportProgress(int Completed, int Total, string FileName);
    private readonly record struct ModelObjExportJob(ExplorerItem Item, string RelativePath);
    private readonly record struct SearchEntry(ExplorerItem Item, string SearchText);
    private sealed record ModelObjExportBatchResult(LithTechObjExportResult ExportResult, int SkippedCount, string MappingReportPath);

    public double TileItemWidth
    {
        get => (double)GetValue(TileItemWidthProperty);
        set => SetValue(TileItemWidthProperty, value);
    }

    public double TileItemHeight
    {
        get => (double)GetValue(TileItemHeightProperty);
        set => SetValue(TileItemHeightProperty, value);
    }

    public double TileCellWidth
    {
        get => (double)GetValue(TileCellWidthProperty);
        set => SetValue(TileCellWidthProperty, value);
    }

    public double TileCellHeight
    {
        get => (double)GetValue(TileCellHeightProperty);
        set => SetValue(TileCellHeightProperty, value);
    }

    public double TileIconSize
    {
        get => (double)GetValue(TileIconSizeProperty);
        set => SetValue(TileIconSizeProperty, value);
    }

    public double TileIconRowHeight
    {
        get => (double)GetValue(TileIconRowHeightProperty);
        set => SetValue(TileIconRowHeightProperty, value);
    }

    public double TileFileIconWidth
    {
        get => (double)GetValue(TileFileIconWidthProperty);
        set => SetValue(TileFileIconWidthProperty, value);
    }

    public double TileFilePageWidth
    {
        get => (double)GetValue(TileFilePageWidthProperty);
        set => SetValue(TileFilePageWidthProperty, value);
    }

    public double TileFilePageHeight
    {
        get => (double)GetValue(TileFilePageHeightProperty);
        set => SetValue(TileFilePageHeightProperty, value);
    }

    public double TileNameMaxHeight
    {
        get => (double)GetValue(TileNameMaxHeightProperty);
        set => SetValue(TileNameMaxHeightProperty, value);
    }

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string Chinese, string English)>
        {
            ["BrowseFolder"] = ("选择文件夹...", "Select Folder..."),
            ["ExtractAll"] = ("全部导出...", "Extract All..."),
            ["PackFolder"] = ("打包文件夹...", "Pack Folder..."),
            ["HeaderDefault"] = ("选择一个文件夹扫描 REZ 资源包", "Choose a folder to scan REZ archives"),
            ["Contents"] = ("内容", "Contents"),
            ["ContentFilterTooltip"] = ("按内容类型过滤", "Filter by content type"),
            ["FilterAll"] = ("全部", "All types"),
            ["FilterFolders"] = ("文件夹", "Folders"),
            ["FilterImages"] = ("图片", "Images"),
            ["FilterModels"] = ("模型", "Models"),
            ["FilterAudio"] = ("音频", "Audio"),
            ["FilterText"] = ("文本", "Text"),
            ["FilterOther"] = ("其他", "Other"),
            ["FilterEmpty"] = ("没有符合过滤条件的项目", "No items match this filter"),
            ["SearchEmpty"] = ("没有搜索结果", "No search results"),
            ["SearchLabel"] = ("搜索:", "Search:"),
            ["SearchTooltip"] = ("输入关键字快速搜索已扫描的文件和目录", "Type keywords to quickly search indexed files and folders"),
            ["ClearSearch"] = ("清除搜索", "Clear search"),
            ["ViewSizeLabel"] = ("大小", "Size"),
            ["ViewSizeTooltip"] = ("缩小切换到列表，放大切换到平铺图标", "Shrink for list view, enlarge for tile view"),
            ["BuildingSearchIndex"] = ("正在建立搜索索引...", "Building search index..."),
            ["SearchIndexReady"] = ("搜索索引已就绪，共 {0:N0} 项", "Search index ready, {0:N0} items"),
            ["SearchResults"] = ("搜索 “{0}”：找到 {1:N0} 项", "Search \"{0}\": {1:N0} items found"),
            ["SearchNoResults"] = ("搜索 “{0}”：没有结果", "Search \"{0}\": no results"),
            ["SearchFailed"] = ("搜索失败", "Search failed"),
            ["EmptyFolder"] = ("空目录", "Empty folder"),
            ["Ready"] = ("就绪", "Ready"),
            ["NoFolderSelected"] = ("未选择文件夹", "No folder selected"),
            ["SelectRezFolderDescription"] = ("选择包含 REZ 资源包的文件夹。", "Select the folder that contains REZ archives."),
            ["SelectPackFolderDescription"] = ("选择要打包成 REZ 的文件夹。", "Select the folder to pack into a REZ archive."),
            ["SelectOutputFolderDescription"] = ("选择输出文件夹。", "Select an output folder."),
            ["SaveRezArchiveTitle"] = ("保存 REZ 资源包", "Save REZ archive"),
            ["RezFileFilter"] = ("REZ 资源包 (*.rez)|*.rez|所有文件 (*.*)|*.*", "REZ archives (*.rez)|*.rez|All files (*.*)|*.*"),
            ["PreparingRezArchives"] = ("正在准备 REZ 资源包...", "Preparing REZ archives..."),
            ["SelectedItemsLabel"] = ("{0:N0} 个选中项", "{0:N0} selected items"),
            ["PreparingItem"] = ("正在准备 {0}...", "Preparing {0}..."),
            ["PackingArchive"] = ("正在打包 REZ 资源包...", "Packing REZ archive..."),
            ["PackingProgress"] = ("正在打包 {0:N0}/{1:N0}: {2}", "Packing {0:N0}/{1:N0}: {2}"),
            ["PackedResult"] = ("已将 {0:N0} 个文件打包到 {1}", "Packed {0:N0} files into {1}"),
            ["PackFailed"] = ("打包失败", "Pack failed"),
            ["NoFilesToExtract"] = ("没有可导出的文件", "No files to extract"),
            ["ExtractingStart"] = ("正在使用 {1} 个线程导出 {0:N0} 个文件...", "Extracting {0:N0} files with {1} workers..."),
            ["ExtractingProgress"] = ("正在导出 {0:N0}/{1:N0}: {2}", "Extracting {0:N0}/{1:N0}: {2}"),
            ["ExtractedResult"] = ("已将 {0:N0} 个文件导出到 {1}", "Extracted {0:N0} files to {1}"),
            ["ExtractedResultWithImages"] = ("已将 {0:N0} 个文件导出到 {1}；解码图片 {2:N0} 个，解码失败并保留源文件 {3:N0} 个", "Extracted {0:N0} files to {1}; decoded {2:N0} images, kept {3:N0} source files after decode was not applicable or failed"),
            ["ExtractFailed"] = ("导出失败", "Extract failed"),
            ["ScanningRezArchives"] = ("正在扫描 REZ 资源包...", "Scanning REZ archives..."),
            ["FoundRezArchives"] = ("找到 {0:N0} 个 REZ 资源包", "Found {0:N0} REZ archives"),
            ["FoundScanItems"] = ("找到 {0:N0} 个 REZ 资源包和 {1:N0} 个文件", "Found {0:N0} REZ archives and {1:N0} files"),
            ["ScanFailed"] = ("扫描失败", "Scan failed"),
            ["LoadFailed"] = ("加载失败", "Load failed"),
            ["LoadingItem"] = ("正在加载 {0}...", "Loading {0}..."),
            ["LoadedItems"] = ("已从 {1} 加载 {0:N0} 项", "Loaded {0:N0} items from {1}"),
            ["ShowingItems"] = ("显示 {0:N0} 项", "Showing {0:N0} items"),
            ["ShowingFilteredItems"] = ("过滤后显示 {0:N0}/{1:N0} 项", "Showing {0:N0} of {1:N0} filtered items"),
            ["ExtractThisItem"] = ("导出此项...", "Extract This Item..."),
            ["ExtractSelectedItems"] = ("导出 {0:N0} 个选中项...", "Extract {0:N0} Selected Items..."),
            ["ExtractSelectedDefault"] = ("导出选中项...", "Extract Selected..."),
            ["OpenPreview"] = ("打开预览...", "Open Preview..."),
            ["LocateFile"] = ("\u5b9a\u4f4d\u5230\u6587\u4ef6", "Locate File"),
            ["LocatedItem"] = ("\u5df2\u5b9a\u4f4d\u5230: {0}", "Located: {0}"),
            ["LocateFileFailed"] = ("\u5b9a\u4f4d\u5230\u6587\u4ef6\u5931\u8d25", "Locate file failed"),
            ["CopyName"] = ("\u590d\u5236\u540d\u79f0", "Copy Name"),
            ["CopySelectedNames"] = ("\u590d\u5236 {0:N0} \u4e2a\u540d\u79f0", "Copy {0:N0} Names"),
            ["CopiedName"] = ("\u5df2\u590d\u5236\u540d\u79f0: {0}", "Copied name: {0}"),
            ["CopiedNames"] = ("\u5df2\u590d\u5236 {0:N0} \u4e2a\u540d\u79f0", "Copied {0:N0} names"),
            ["CopyNameFailed"] = ("\u590d\u5236\u540d\u79f0\u5931\u8d25", "Copy name failed"),
            ["CopyNameClipboardBusy"] = ("\u526a\u8d34\u677f\u6b63\u88ab\u5176\u4ed6\u7a0b\u5e8f\u5360\u7528\uff0c\u8bf7\u7a0d\u540e\u518d\u8bd5\u3002", "Clipboard is busy. Please try again."),
            ["DecodeBank"] = ("\u89e3\u7801 BANK...", "Decode BANK..."),
            ["ExportObj"] = ("\u5bfc\u51fa OBJ...", "Export OBJ..."),
            ["SaveObjTitle"] = ("\u4fdd\u5b58 OBJ \u6a21\u578b", "Save OBJ model"),
            ["ObjFileFilter"] = ("OBJ \u6a21\u578b (*.obj)|*.obj|\u6240\u6709\u6587\u4ef6 (*.*)|*.*", "OBJ models (*.obj)|*.obj|All files (*.*)|*.*"),
            ["PreparingModelObjExport"] = ("\u6b63\u5728\u51c6\u5907 OBJ \u5bfc\u51fa...", "Preparing OBJ export..."),
            ["IndexingModelTextures"] = ("\u6b63\u5728\u5efa\u7acb\u5168\u5c40\u8d34\u56fe\u7d22\u5f15...", "Building global texture index..."),
            ["NoModelsToExport"] = ("\u6ca1\u6709\u53ef\u5bfc\u51fa\u7684\u6a21\u578b", "No exportable models"),
            ["DecodingModelObjExport"] = ("\u6b63\u5728\u89e3\u7801\u6a21\u578b {0:N0}/{1:N0}: {2}", "Decoding model {0:N0}/{1:N0}: {2}"),
            ["WritingModelObjExport"] = ("\u6b63\u5728\u5199\u5165 OBJ...", "Writing OBJ..."),
            ["ExportedModelObjResult"] = ("\u5df2\u5bfc\u51fa OBJ: {0:N0} \u4e2a\u6a21\u578b, {1:N0} \u4e2a\u7f51\u683c, \u5df2\u5bfc\u51fa\u8d34\u56fe {2:N0} \u4e2a, \u7f3a\u5931 {3:N0} \u4e2a, \u8df3\u8fc7 {4:N0} \u4e2a; OBJ: {5}; MTL: {6}; \u8d34\u56fe\u76ee\u5f55: {7}; \u8bca\u65ad\u62a5\u544a: {8}", "Exported OBJ: {0:N0} models, {1:N0} meshes, {2:N0} textures exported, {3:N0} missing, skipped {4:N0}; OBJ: {5}; MTL: {6}; texture folder: {7}; diagnostics: {8}"),
            ["NoDiagnosticsReport"] = ("\u65e0", "none"),
            ["ExportObjFailed"] = ("OBJ \u5bfc\u51fa\u5931\u8d25", "OBJ export failed"),
            ["OpenAudioPreview"] = ("播放音频...", "Play Audio..."),
            ["OpenImagePreview"] = ("查看图片...", "View Image..."),
            ["OpenModelPreview"] = ("查看模型...", "View Model..."),
            ["OpenTextPreview"] = ("查看文本...", "View Text..."),
            ["DecodingBank"] = ("\u6b63\u5728\u89e3\u7801 BANK {0}...", "Decoding BANK {0}..."),
            ["DecodedBankResult"] = ("\u5df2\u8f93\u51fa BANK: {0:N0} \u4e2a FSB \u5230 {1}", "Decoded BANK: {0:N0} FSB blocks to {1}"),
            ["DecodeBankFailed"] = ("\u89e3\u7801 BANK \u5931\u8d25", "BANK decode failed"),
            ["LoadingModelPreview"] = ("正在打开模型 {0}...", "Opening model {0}..."),
            ["ModelPreviewOpened"] = ("已打开模型 {0}", "Opened model {0}"),
            ["ModelPreviewUnsupported"] = ("无法解码模型 {0}", "Cannot decode model {0}"),
            ["ModelPreviewFailed"] = ("模型预览失败", "Model preview failed"),
            ["ModelPreviewInfo"] = ("{0}，{1:N0} 个网格，{2:N0} 个顶点，{3:N0} 个三角面，{4:N0} 字节 -> {5:N0} 字节", "{0}, {1:N0} meshes, {2:N0} vertices, {3:N0} triangles, {4:N0} bytes -> {5:N0} bytes"),
            ["LoadingPreview"] = ("正在打开预览 {0}...", "Opening preview {0}..."),
            ["PreviewOpened"] = ("已打开预览 {0}", "Opened preview {0}"),
            ["PreviewUnsupported"] = ("无法预览 {0}", "Cannot preview {0}"),
            ["PreviewFailed"] = ("预览失败", "Preview failed"),
            ["LoadingTextPreview"] = ("正在打开文本 {0}...", "Opening text {0}..."),
            ["TextPreviewOpened"] = ("已打开文本 {0}", "Opened text {0}"),
            ["TextPreviewUnsupported"] = ("无法解码文本 {0}", "Cannot decode text {0}"),
            ["TextPreviewFailed"] = ("文本预览失败", "Text preview failed"),
            ["TextPreviewInfoEncoded"] = ("ENC / Base64 / {0}，{1:N0} 字节 -> {2:N0} 字节", "ENC / Base64 / {0}, {1:N0} bytes -> {2:N0} bytes"),
            ["TextPreviewInfoPlain"] = ("{0}，{1:N0} 字节", "{0}, {1:N0} bytes"),
            ["PreviewDtxLzma"] = ("DTX - LZMA 压缩", "DTX - LZMA compressed"),
            ["PreviewDtxRaw"] = ("DTX - 未压缩", "DTX - uncompressed"),
            ["PreviewDdsDxt"] = ("DDS - DXT 压缩", "DDS - DXT compressed"),
            ["PreviewDdsRaw"] = ("DDS - 未压缩", "DDS - uncompressed"),
            ["PreviewTgaLzma"] = ("TGA - LZMA 压缩", "TGA - LZMA compressed"),
            ["PreviewTgaRaw"] = ("TGA - 未压缩", "TGA - uncompressed"),
            ["PreviewTgaRepaired"] = ("TGA - 拼接修复", "TGA - repaired layout"),
            ["PreviewTgaRawPixels"] = ("TGA - 原始像素修复", "TGA - repaired raw pixels"),
            ["PreviewImageBin"] = ("BIN - CF10/XOR \u56fe\u7247", "BIN - CF10/XOR image"),
            ["PreviewImageBinLzma"] = ("BIN - LZMA 外壳图片", "BIN - LZMA-wrapped image"),
            ["PreviewImageBinZstd"] = ("BIN - Zstandard \u538b\u7f29 BGRA \u56fe\u7247", "BIN - Zstandard BGRA image"),
            ["Settings"] = ("\u8bbe\u7f6e...", "Settings..."),
            ["ClearThumbnailCache"] = ("\u6e05\u7f29\u7565\u56fe", "Clear Cache"),
            ["ClearThumbnailCacheTooltip"] = ("\u5220\u9664\u7a0b\u5e8f\u76ee\u5f55\u4e0b\u7684\u7f29\u7565\u56fe\u78c1\u76d8\u7f13\u5b58", "Delete cached thumbnail PNGs under the program directory"),
            ["ClearingThumbnailCache"] = ("\u6b63\u5728\u6e05\u9664\u7f29\u7565\u56fe\u7f13\u5b58...", "Clearing thumbnail cache..."),
            ["ThumbnailCacheCleared"] = ("\u5df2\u6e05\u9664 {0:N0} \u4e2a\u7f29\u7565\u56fe\u7f13\u5b58\u6587\u4ef6\uff0c\u91ca\u653e {1}", "Cleared {0:N0} thumbnail cache files, freed {1}"),
            ["ThumbnailCacheEmpty"] = ("\u6ca1\u6709\u627e\u5230\u9700\u8981\u6e05\u9664\u7684\u7f29\u7565\u56fe\u7f13\u5b58", "No thumbnail cache files found"),
            ["ClearThumbnailCacheFailed"] = ("\u6e05\u9664\u7f29\u7565\u56fe\u7f13\u5b58\u5931\u8d25", "Clear thumbnail cache failed"),
            ["ErrorStatus"] = ("{0}: {1}", "{0}: {1}")
        };

    public MainWindow()
    {
        _settings = UserSettings.Load();
        _language = ParseLanguage(_settings.Language);
        _theme = ThemeManager.Parse(_settings.Theme);
        LocalizedText.SetLanguage(ToLanguageCode(_language));
        ThemeManager.Apply(_theme);
        InitializeComponent();
        WindowThemeHelper.Apply(this, _theme);
        ViewSizeSlider.Value = ClampViewSize(_settings.ViewSize);
        ApplyViewSize(ViewSizeSlider.Value);
        ApplyLanguage();
        LoadEmptyStateImage();
        ContentRendered += MainWindow_ContentRendered;
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;

        string? folder = SelectFolder(T("SelectRezFolderDescription"), FolderDialogKind.RezSource);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
        else
        {
            SetStatus("NoFolderSelected");
        }
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? folder = SelectFolder(T("SelectRezFolderDescription"), FolderDialogKind.RezSource, _selectedDirectory);
        if (folder is not null)
        {
            await LoadDirectoryAsync(folder);
        }
    }

    private async void PackFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? sourceDirectory = SelectFolder(T("SelectPackFolderDescription"), FolderDialogKind.PackSource, _selectedDirectory);
        if (sourceDirectory is null)
        {
            return;
        }

        string? outputPath = SelectRezOutputFile(sourceDirectory);
        if (outputPath is null)
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatus("PackingArchive");

            var progress = new Progress<RezArchiveWriteProgress>(state =>
            {
                SetStatus("PackingProgress", state.CompletedFiles, state.TotalFiles, state.FileName);
            });

            RezArchiveWriteResult result = await Task.Run(() => RezArchiveWriter.WriteFromDirectory(sourceDirectory, outputPath, progress));
            SetStatus("PackedResult", result.FileCount, outputPath);
        }
        catch (Exception ex)
        {
            ShowError("PackFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(ToLanguageCode(_language), _theme)
        {
            Owner = this
        };
        window.LanguageChanged += SettingsWindow_LanguageChanged;
        window.ThemeChanged += SettingsWindow_ThemeChanged;
        window.ExportSettingsRequested += SettingsWindow_ExportSettingsRequested;
        window.ClearThumbnailCacheRequested += SettingsWindow_ClearThumbnailCacheRequested;
        window.Closed += SettingsWindow_Closed;
        _settingsWindow = window;
        window.ShowDialog();
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow window)
        {
            window.LanguageChanged -= SettingsWindow_LanguageChanged;
            window.ThemeChanged -= SettingsWindow_ThemeChanged;
            window.ExportSettingsRequested -= SettingsWindow_ExportSettingsRequested;
            window.ClearThumbnailCacheRequested -= SettingsWindow_ClearThumbnailCacheRequested;
            window.Closed -= SettingsWindow_Closed;
        }

        _settingsWindow = null;
    }

    private void SettingsWindow_LanguageChanged(string languageCode)
    {
        _language = ParseLanguage(languageCode);
        LocalizedText.SetLanguage(ToLanguageCode(_language));
        _settings.Language = ToLanguageCode(_language);
        _settings.Save();
        ApplyLanguage();
    }

    private void SettingsWindow_ThemeChanged(AppTheme theme)
    {
        _theme = theme;
        ThemeManager.Apply(theme);
        WindowThemeHelper.Apply(this, theme);
        _settings.Theme = ThemeManager.ToSettingsValue(theme);
        _settings.Save();
        _settingsWindow?.ApplyTheme(theme);
    }

    private void SettingsWindow_ExportSettingsRequested(object? sender, EventArgs e)
    {
        Window owner = sender as Window ?? this;
        SelectImageExportOptions(owner, continueExport: false);
    }

    private async void SettingsWindow_ClearThumbnailCacheRequested(object? sender, EventArgs e)
    {
        await ClearThumbnailCacheAsync();
    }

    private async Task ClearThumbnailCacheAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        _settingsWindow?.SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatus("ClearingThumbnailCache");
            _settingsWindow?.SetCacheStatus(T("ClearingThumbnailCache"));

            ThumbnailCacheClearResult result = await Task.Run(ThumbnailDiskCache.Clear);
            if (result.DeletedFileCount == 0)
            {
                SetStatus("ThumbnailCacheEmpty");
                _settingsWindow?.SetCacheStatus(T("ThumbnailCacheEmpty"));
                return;
            }

            string clearedText = FormatText("ThumbnailCacheCleared", result.DeletedFileCount, FormatByteSize(result.DeletedByteCount));
            SetStatusText(clearedText);
            _settingsWindow?.SetCacheStatus(clearedText);
        }
        catch (Exception ex)
        {
            _settingsWindow?.SetCacheStatus($"{T("ClearThumbnailCacheFailed")}: {ex.Message}");
            ShowError("ClearThumbnailCacheFailed", ex);
        }
        finally
        {
            SetBusy(false);
            _settingsWindow?.SetBusy(false);
        }
    }

    private async void ExtractAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rootItem is null || (_archiveCount == 0 && _extractableFileCount == 0))
        {
            return;
        }

        await ExtractItemsAsync(new[] { _rootItem }, T("PreparingRezArchives"), preserveOutputRelativePaths: true);
    }

    private async void ExtractSelectedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        string label = selectedItems.Count == 1 ? selectedItems[0].Name : FormatText("SelectedItemsLabel", selectedItems.Count);
        await ExtractItemsAsync(selectedItems, FormatText("PreparingItem", label), preserveOutputRelativePaths: false);
    }

    private async void CopyNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        try
        {
            if (selectedItems.Count == 1)
            {
                await SetClipboardTextAsync(selectedItems[0].Name);
                SetStatus("CopiedName", selectedItems[0].Name);
                return;
            }

            string names = string.Join(Environment.NewLine, selectedItems.Select(item => item.Name));
            await SetClipboardTextAsync(names);
            SetStatus("CopiedNames", selectedItems.Count);
        }
        catch (Exception ex)
        {
            ShowError("CopyNameFailed", ex);
        }
    }

    private async void LocateFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count != 1)
        {
            return;
        }

        try
        {
            await LocateExplorerItemAsync(selectedItems[0]);
        }
        catch (Exception ex)
        {
            ShowError("LocateFileFailed", ex);
        }
    }

    private async Task LocateExplorerItemAsync(ExplorerItem item)
    {
        if (item.Parent is not { } parent)
        {
            return;
        }

        await ShowFolderAsync(parent);
        if (!ReferenceEquals(_currentItem, parent))
        {
            return;
        }

        ContentsList.SelectedItems.Clear();
        ContentsList.SelectedItem = item;
        ContentsList.ScrollIntoView(item);
        await Dispatcher.InvokeAsync(
            () =>
            {
                ContentsList.UpdateLayout();
                if (ContentsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                {
                    container.Focus();
                }
                else
                {
                    ContentsList.Focus();
                }
            },
            DispatcherPriority.Background);
        SetStatus("LocatedItem", item.Name);
    }

    private async Task SetClipboardTextAsync(string text)
    {
        const int maxAttempts = 24;
        int delayMilliseconds = 25;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TrySetClipboardText(text))
            {
                return;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delayMilliseconds);
                delayMilliseconds = Math.Min(delayMilliseconds + 25, 250);
            }
        }

        throw new InvalidOperationException(T("CopyNameClipboardBusy"));
    }

    private bool TrySetClipboardText(string text)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        IntPtr memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, new UIntPtr(checked((uint)bytes.Length)));
        if (memory == IntPtr.Zero)
        {
            throw new InvalidOperationException(Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
        }

        bool clipboardOpened = false;
        try
        {
            IntPtr lockedMemory = NativeMethods.GlobalLock(memory);
            if (lockedMemory == IntPtr.Zero)
            {
                throw new InvalidOperationException(Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                Marshal.Copy(bytes, 0, lockedMemory, bytes.Length);
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(memory);
            }

            IntPtr owner = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.OpenClipboard(owner))
            {
                return false;
            }

            clipboardOpened = true;
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, memory) == IntPtr.Zero)
            {
                return false;
            }

            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (clipboardOpened)
            {
                _ = NativeMethods.CloseClipboard();
            }

            if (memory != IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(memory);
            }
        }
    }

    private static class NativeMethods
    {
        public const uint CfUnicodeText = 13;
        public const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr hMem);
    }

    private async void DecodeBankMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count != 1 || !FmodBankDecoder.IsCandidate(selectedItems[0].FileExtension))
        {
            return;
        }

        await DecodeBankAsync(selectedItems[0]);
    }

    private async void ExportObjMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        string? outputPath = SelectObjOutputFile(CreateDefaultObjExportName(selectedItems));
        if (outputPath is null)
        {
            return;
        }

        await ExportModelsToObjAsync(selectedItems, outputPath);
    }

    private async void OpenPreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        if (selectedItems.Count != 1)
        {
            return;
        }

        ExplorerItem item = selectedItems[0];
        if (IsBankAudioPreviewCandidate(item))
        {
            await ShowBankAudioPreviewAsync(item);
        }
        else if (IsSpritePreviewCandidate(item))
        {
            await ShowSpritePreviewAsync(item);
        }
        else if (IsAudioPreviewCandidate(item))
        {
            await ShowAudioPreviewAsync(item);
        }
        else if (item.IsModelPreviewCandidate)
        {
            await ShowModelPreviewAsync(item);
        }
        else if (item.IsTextPreviewCandidate)
        {
            await ShowTextPreviewAsync(item);
        }
        else if (item.IsImagePreviewCandidate)
        {
            await ShowImagePreviewAsync(item);
        }
    }

    private async Task ExtractItemsAsync(
        IReadOnlyCollection<ExplorerItem> items,
        string preparingMessage,
        bool preserveOutputRelativePaths)
    {
        if (items.Count == 0)
        {
            return;
        }

        ImageExportOptions? imageExportOptions = _settings.SkipImageExportOptionsDialog
            ? ImageExportOptions.FromSettings(_settings.ImageExportModes)
            : SelectImageExportOptions(this, continueExport: true);
        if (imageExportOptions is null)
        {
            return;
        }

        string? outputDirectory = SelectFolder(T("SelectOutputFolderDescription"), FolderDialogKind.Output, _selectedDirectory);
        if (outputDirectory is null)
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatusText(preparingMessage);

            await Task.Run(() => LoadItemsForExtraction(items));

            List<ExtractionJob> jobs = BuildExtractionJobs(items, preserveOutputRelativePaths, imageExportOptions);
            _extractableFileCount = jobs.Count;
            if (jobs.Count == 0)
            {
                SetStatus("NoFilesToExtract");
                return;
            }

            int workerCount = GetExtractionWorkerCount(jobs.Count);
            SetStatus("ExtractingStart", jobs.Count, workerCount);
            WorkProgress.IsIndeterminate = false;
            WorkProgress.Minimum = 0;
            WorkProgress.Maximum = jobs.Count;
            WorkProgress.Value = 0;

            var progress = new Progress<ExtractionProgress>(state =>
            {
                WorkProgress.Value = state.Completed;
                SetStatus("ExtractingProgress", state.Completed, state.Total, state.FileName);
            });

            ExtractionBatchResult result = await Task.Run(() => ExtractFilesParallel(outputDirectory, jobs, workerCount, progress));

            if (result.DecodedImageCount > 0 || result.DecodeFallbackCount > 0)
            {
                SetStatus(
                    "ExtractedResultWithImages",
                    jobs.Count,
                    outputDirectory,
                    result.DecodedImageCount,
                    result.DecodeFallbackCount);
            }
            else
            {
                SetStatus("ExtractedResult", jobs.Count, outputDirectory);
            }
        }
        catch (Exception ex)
        {
            ShowError("ExtractFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DecodeBankAsync(ExplorerItem item)
    {
        string? outputDirectory = SelectFolder(T("SelectOutputFolderDescription"), FolderDialogKind.Output, _selectedDirectory);
        if (outputDirectory is null)
        {
            return;
        }

        SetBusy(true, keepSearchEnabled: true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatus("DecodingBank", item.Name);

            FmodBankExportResult result = await Task.Run(() =>
            {
                byte[] data = ReadExplorerFileBytes(item, FmodBankDecoder.MaxSourceBytes);
                return FmodBankDecoder.ExportDecodedFiles(data, item.Name, outputDirectory);
            });

            SetStatus("DecodedBankResult", result.FsbBlockPaths.Count, outputDirectory);
        }
        catch (Exception ex)
        {
            ShowError("DecodeBankFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }
    }

    private async Task ExportModelsToObjAsync(IReadOnlyCollection<ExplorerItem> items, string outputPath)
    {
        SetBusy(true, keepSearchEnabled: true);

        try
        {
            WorkProgress.IsIndeterminate = true;
            SetStatus("PreparingModelObjExport");

            ExplorerItem? exportRoot = _rootItem;
            if (exportRoot is not null)
            {
                await Task.Run(() => LoadItemsForExtraction(new[] { exportRoot }));
            }
            else
            {
                await Task.Run(() => LoadItemsForExtraction(items));
            }

            List<ModelObjExportJob> jobs = BuildModelObjExportJobs(items);
            if (jobs.Count == 0)
            {
                SetStatus("NoModelsToExport");
                return;
            }

            SetStatus("IndexingModelTextures");
            Func<string, ImageSource?>? globalTextureResolver = exportRoot is null
                ? null
                : await Task.Run(() => LithTechModelTextureLoader.CreateGlobalResolver(exportRoot));
            Func<IEnumerable<string>, IReadOnlyList<string>>? textureConfigResolver = exportRoot is null
                ? null
                : await Task.Run(() => LithTechModelTextureConfigIndex.CreateResolver(exportRoot));
            Func<IEnumerable<string>, IReadOnlyList<string>>? datTextureReferenceResolver = exportRoot is null
                ? null
                : await Task.Run(() => LithTechDatTextureReferenceIndex.CreateResolver(exportRoot));
            Func<IEnumerable<string>, IReadOnlyList<string>>? textureReferenceResolver =
                TextureReferenceResolver.Combine(textureConfigResolver, datTextureReferenceResolver);

            WorkProgress.IsIndeterminate = false;
            WorkProgress.Minimum = 0;
            WorkProgress.Maximum = jobs.Count;
            WorkProgress.Value = 0;

            var progress = new Progress<ModelObjExportProgress>(state =>
            {
                WorkProgress.Value = state.Completed;
                SetStatus("DecodingModelObjExport", state.Completed, state.Total, state.FileName);
            });

            ModelObjExportBatchResult result = await Task.Run(() => ExportModelObjJobs(outputPath, jobs, exportRoot, globalTextureResolver, textureReferenceResolver, progress));
            SetStatus(
                "ExportedModelObjResult",
                result.ExportResult.SourceCount,
                result.ExportResult.MeshCount,
                result.ExportResult.TextureCount,
                result.ExportResult.MissingTextureCount,
                result.SkippedCount,
                result.ExportResult.ObjPath,
                result.ExportResult.MtlPath,
                result.ExportResult.TextureDirectoryPath,
                GetObjExportDiagnosticPath(result.ExportResult, result.MappingReportPath));
        }
        catch (Exception ex)
        {
            ShowError("ExportObjFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }
    }

    private ModelObjExportBatchResult ExportModelObjJobs(
        string outputPath,
        IReadOnlyList<ModelObjExportJob> jobs,
        ExplorerItem? exportRoot,
        Func<string, ImageSource?>? globalTextureResolver,
        Func<IEnumerable<string>, IReadOnlyList<string>>? textureConfigResolver,
        IProgress<ModelObjExportProgress> progress)
    {
        var sources = new List<LithTechObjExportSource>();
        int skippedCount = 0;
        for (int index = 0; index < jobs.Count; index++)
        {
            ModelObjExportJob job = jobs[index];
            progress.Report(new ModelObjExportProgress(index + 1, jobs.Count, job.Item.Name));
            if (TryLoadModelDocument(job.Item, out LithTechModelDocument? document, out _) &&
                document is not null)
            {
                sources.Add(new LithTechObjExportSource(
                    CreateObjSourceName(job),
                    GetObjSourceResourcePath(job.Item),
                    document,
                    CreateObjTextureResolver(job.Item, globalTextureResolver),
                    textureConfigResolver));
            }
            else
            {
                skippedCount++;
            }
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException(T("NoModelsToExport"));
        }

        Dispatcher.Invoke(() => SetStatus("WritingModelObjExport"));
        LithTechObjExportResult result = LithTechObjExporter.Export(outputPath, sources);
        string mappingReportPath = result.MissingTextureCount == 0
            ? string.Empty
            : LithTechTextureMappingScanner.WriteReport(result.ObjPath, exportRoot, sources);
        return new ModelObjExportBatchResult(result, skippedCount, mappingReportPath);
    }

    private string GetObjExportDiagnosticPath(LithTechObjExportResult result, string mappingReportPath)
    {
        if (!string.IsNullOrWhiteSpace(mappingReportPath))
        {
            return mappingReportPath;
        }

        return string.IsNullOrWhiteSpace(result.TextureReportPath)
            ? T("NoDiagnosticsReport")
            : result.TextureReportPath;
    }

    private static Func<string, ImageSource?>? CreateObjTextureResolver(
        ExplorerItem item,
        Func<string, ImageSource?>? globalTextureResolver)
    {
        Func<string, ImageSource?>? primaryResolver = LithTechModelTextureLoader.CreateResolver(item);
        if (primaryResolver is null)
        {
            return globalTextureResolver;
        }

        if (globalTextureResolver is null)
        {
            return primaryResolver;
        }

        return item.Kind == ExplorerItemKind.LocalFile
            ? texturePath => globalTextureResolver(texturePath) ?? primaryResolver(texturePath)
            : texturePath => primaryResolver(texturePath) ?? globalTextureResolver(texturePath);
    }

    private static bool TryLoadModelDocument(ExplorerItem item, out LithTechModelDocument? document, out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        try
        {
            string extension = item.FileExtension;
            int maxBytes = LithTechWorldDatDecoder.IsCandidate(extension)
                ? MaxObjWorldDatBytes
                : MaxObjModelBytes;
            byte[] data = ReadExplorerFileBytes(item, maxBytes);

            if (LithTechWorldDatDecoder.IsCandidate(extension) &&
                LithTechWorldDatDecoder.TryDecode(data, item.Name, out LithTechModelDocument? worldDocument, out errorMessage) &&
                worldDocument is not null)
            {
                document = worldDocument;
                return true;
            }

            return LithTechModelDecoder.TryDecode(data, item.Name, extension, out document, out errorMessage) &&
                   document is not null;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private async void ContentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ExplorerItem item)
        {
            return;
        }

        if (item.IsContainer)
        {
            await ShowFolderAsync(item);
        }
        else if (IsBankAudioPreviewCandidate(item))
        {
            await ShowBankAudioPreviewAsync(item);
        }
        else if (IsSpritePreviewCandidate(item))
        {
            await ShowSpritePreviewAsync(item);
        }
        else if (IsAudioPreviewCandidate(item))
        {
            await ShowAudioPreviewAsync(item);
        }
        else if (item.IsModelPreviewCandidate)
        {
            await ShowModelPreviewAsync(item);
        }
        else if (item.IsTextPreviewCandidate)
        {
            await ShowTextPreviewAsync(item);
        }
        else if (item.IsImagePreviewCandidate)
        {
            await ShowImagePreviewAsync(item);
        }
    }

    private async Task ShowModelPreviewAsync(ExplorerItem item)
    {
        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingModelPreview", item.Name);
        bool openTextFallback = false;

        try
        {
            LithTechModelDocument? document = await item.LoadModelPreviewAsync();
            if (document is null)
            {
                if (item.IsTextPreviewCandidate)
                {
                    openTextFallback = true;
                }
                else
                {
                    SetStatus("ModelPreviewUnsupported", item.Name);
                }
            }
            else
            {
                var window = new ModelPreviewWindow(
                    item.Name,
                    document,
                    FormatModelInfo(document),
                    LithTechModelTextureLoader.CreateResolver(item))
                {
                    Owner = this,
                    ShowInTaskbar = true
                };
                window.Show();
                SetStatus("ModelPreviewOpened", item.Name);
            }
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }

        if (openTextFallback)
        {
            await ShowTextPreviewAsync(item);
        }
    }

    private async Task ShowTextPreviewAsync(ExplorerItem item)
    {
        await ShowPreviewToolAsync(item);
    }

    private async Task ShowAudioPreviewAsync(ExplorerItem item)
    {
        List<ExplorerItem> audioItems = GetCurrentAudioPreviewItems();
        int audioIndex = audioItems.IndexOf(item);
        if (audioIndex < 0)
        {
            audioItems = [item];
            audioIndex = 0;
        }

        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingPreview", item.Name);
        bool openTextFallback = false;

        try
        {
            AudioPreviewDocument? document = await LoadAudioPreviewDocumentAsync(item);
            if (document is null)
            {
                if (item.IsTextPreviewCandidate)
                {
                    openTextFallback = true;
                }
                else
                {
                    SetStatus("PreviewUnsupported", item.Name);
                }
            }
            else
            {
                Task<AudioPreviewDocument?> LoadDocumentAtAsync(int index)
                {
                    return index >= 0 && index < audioItems.Count
                        ? LoadAudioPreviewDocumentAsync(audioItems[index])
                        : Task.FromResult<AudioPreviewDocument?>(null);
                }

                var window = new AudioPreviewWindow(
                    document,
                    audioItems.Count > 1 ? LoadDocumentAtAsync : null,
                    audioIndex,
                    audioItems.Count,
                    _settings,
                    audioItems.Select(audioItem => audioItem.Name).ToList())
                {
                    ShowInTaskbar = true
                };
                ShowIndependentAudioPreviewWindow(window);
                SetStatus("PreviewOpened", item.Name);
            }
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }

        if (openTextFallback)
        {
            await ShowTextPreviewAsync(item);
        }
    }

    private List<ExplorerItem> GetCurrentAudioPreviewItems()
    {
        return ContentsList.Items
            .OfType<ExplorerItem>()
            .Where(IsAudioPreviewCandidate)
            .ToList();
    }

    private async Task ShowBankAudioPreviewAsync(ExplorerItem item)
    {
        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingPreview", item.Name);
        bool openTextFallback = false;

        try
        {
            BankAudioInitialLoadResult? loadResult = await Task.Run(() => LoadInitialBankAudioSource(item));

            if (loadResult is null || loadResult.Source.StreamCount == 0)
            {
                loadResult?.Session.Dispose();
                openTextFallback = true;
            }
            else
            {
                FmodBankAudioSource source = loadResult.Source;
                var bankState = new BankAudioStreamState(loadResult.Session, source);
                FmodBankAudioSource initialSource = CreateInitialBankAudioSource(source);
                AudioPreviewDocument? document = await Task.Run(() =>
                    FmodBankAudioPreviewDocumentFactory.TryCreate(initialSource, 0, out AudioPreviewDocument? firstDocument, out _)
                        ? firstDocument
                        : null);
                if (document is null)
                {
                    bankState.Dispose();
                    openTextFallback = true;
                }
                else
                {
                    Func<int, Task<AudioPreviewDocument?>> loadDocumentAtAsync = CreateBankDocumentLoader(bankState);

                    var window = new AudioPreviewWindow(
                        document,
                        source.StreamCount > 1 ? loadDocumentAtAsync : null,
                        0,
                        initialSource.StreamCount,
                        _settings,
                        initialSource.GetStreamNames())
                    {
                        ShowInTaskbar = true
                    };
                    ShowIndependentAudioPreviewWindow(window);
                    window.SetNavigationLoading(source.Partial || source.TotalStreamCount > window.DocumentCount);
                    QueueBankAudioSourceProgressiveAppend(window, bankState);
                    SetStatus("PreviewOpened", item.Name);
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }

        if (openTextFallback)
        {
            await ShowTextPreviewAsync(item);
        }
    }

    private static BankAudioInitialLoadResult? LoadInitialBankAudioSource(ExplorerItem item)
    {
        BankAudioStreamSession? session = TryCreateBankAudioStreamSession(item);
        if (session is null)
        {
            return null;
        }

        try
        {
            foreach (int prefixSize in FastBankPreviewPrefixSizes)
            {
                ReadBankAudioSessionTo(session, prefixSize);
                if (TryCreateBankAudioSource(session, out FmodBankAudioSource? source) &&
                    source is not null)
                {
                    return new BankAudioInitialLoadResult(source, session);
                }

                if (session.IsComplete)
                {
                    break;
                }
            }

            ReadBankAudioSessionTo(session, FmodBankDecoder.MaxDecodedBytes);
            if (TryCreateBankAudioSource(session, out FmodBankAudioSource? fullSource) &&
                fullSource is not null)
            {
                return new BankAudioInitialLoadResult(fullSource, session);
            }
        }
        catch
        {
        }

        session.Dispose();
        return null;
    }

    private static BankAudioStreamSession? TryCreateBankAudioStreamSession(ExplorerItem item)
    {
        long fileByteCount = GetExplorerFileByteCount(item);
        if (fileByteCount <= 0 || fileByteCount > FmodBankDecoder.MaxSourceBytes)
        {
            return null;
        }

        byte[] header = ReadExplorerFilePrefixBytes(item, 13);
        bool compressed = FmodBankDecoder.IsCompressedBank(header);
        int sourceByteCount = checked((int)Math.Min(fileByteCount, int.MaxValue));
        Stream sourceStream = OpenExplorerFileReadStream(item);

        if (compressed)
        {
            if (!LzmaAloneDecoder.TryGetDecodedByteCount(header, out long decodedBytes) ||
                decodedBytes <= 0 ||
                decodedBytes > FmodBankDecoder.MaxDecodedBytes ||
                decodedBytes > int.MaxValue)
            {
                sourceStream.Dispose();
                return null;
            }

            Stream? decodedStream = LzmaAloneDecoder.TryCreateDecompressStream(sourceStream, fileByteCount, out long streamDecodedBytes);
            if (decodedStream is null || streamDecodedBytes != decodedBytes)
            {
                decodedStream?.Dispose();
                sourceStream.Dispose();
                return null;
            }

            return new BankAudioStreamSession(item.Name, decodedStream, true, sourceByteCount, decodedBytes);
        }

        if (fileByteCount > FmodBankDecoder.MaxDecodedBytes || fileByteCount > int.MaxValue)
        {
            sourceStream.Dispose();
            return null;
        }

        return new BankAudioStreamSession(item.Name, sourceStream, false, sourceByteCount, fileByteCount);
    }

    private static Stream OpenExplorerFileReadStream(ExplorerItem item)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            return File.OpenRead(item.SourcePath);
        }

        if (item.Archive is null || item.ArchiveFile is null)
        {
            throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
        }

        FileStream archiveSource = File.OpenRead(item.Archive.FilePath);
        archiveSource.Position = item.ArchiveFile.DataOffset;
        return archiveSource;
    }

    private static bool ReadBankAudioSessionTo(BankAudioStreamSession session, int targetDecodedBytes)
    {
        long target = Math.Min(Math.Min(targetDecodedBytes, FmodBankDecoder.MaxDecodedBytes), session.TotalDecodedBytes);
        if (target <= session.DecodedData.Length)
        {
            return false;
        }

        byte[] buffer = new byte[Math.Min(BankPreviewPrefixStepBytes, checked((int)(target - session.DecodedData.Length)))];
        bool readAny = false;
        while (session.DecodedData.Length < target)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, target - session.DecodedData.Length);
            int read = session.DecodedStream.Read(buffer, 0, bytesToRead);
            if (read == 0)
            {
                break;
            }

            session.DecodedData.Write(buffer, 0, read);
            readAny = true;
        }

        return readAny;
    }

    private static bool TryCreateBankAudioSource(BankAudioStreamSession session, out FmodBankAudioSource? source)
    {
        source = null;
        if (session.DecodedData.Length <= 0)
        {
            return false;
        }

        int decodedByteCount = session.DecodedByteCount;
        byte[] bankData;
        int bankDataLength;
        if (session.DecodedData.TryGetBuffer(out ArraySegment<byte> decodedBuffer) &&
            decodedBuffer.Array is not null &&
            decodedBuffer.Offset == 0)
        {
            bankData = decodedBuffer.Array;
            bankDataLength = decodedByteCount;
        }
        else
        {
            bankData = session.DecodedData.ToArray();
            bankDataLength = bankData.Length;
        }

        return FmodBankAudioPreviewDocumentFactory.TryCreateSourceFromPreparedData(
            session.FileName,
            bankData,
            bankDataLength,
            session.Compressed,
            session.SourceByteCount,
            decodedByteCount,
            !session.IsComplete,
            out source,
            out _);
    }

    private static FmodBankAudioSource EnsureBankAudioSourceDecoded(BankAudioStreamState state, int requiredByteCount)
    {
        lock (state.SyncRoot)
        {
            return ReadBankAudioStateTo(state, requiredByteCount);
        }
    }

    private static FmodBankAudioSource ReadBankAudioStateTo(BankAudioStreamState state, int targetDecodedBytes)
    {
        BankAudioStreamSession? session = state.Session;
        if (session is null)
        {
            return state.CurrentSource;
        }

        ReadBankAudioSessionTo(session, targetDecodedBytes);
        if (TryCreateBankAudioSource(session, out FmodBankAudioSource? source) &&
            source is not null)
        {
            state.UpdateSource(source);
            if (!source.Partial)
            {
                state.DisposeSession();
            }

            return source;
        }

        return state.CurrentSource;
    }

    private static int ReadBankAudioStateBytesTo(BankAudioStreamState state, int targetDecodedBytes)
    {
        BankAudioStreamSession? session = state.Session;
        if (session is null)
        {
            return state.CurrentSource.DecodedByteCount;
        }

        ReadBankAudioSessionTo(session, targetDecodedBytes);
        return session.DecodedByteCount;
    }

    private static FmodBankAudioSource CompleteBankAudioState(BankAudioStreamState state)
    {
        BankAudioStreamSession? session = state.Session;
        if (session is null)
        {
            return state.CurrentSource;
        }

        if (TryCreateBankAudioSource(session, out FmodBankAudioSource? source) &&
            source is not null)
        {
            state.UpdateSource(source);
        }

        state.DisposeSession();
        return state.CurrentSource;
    }

    private static FmodBankAudioSource CreateInitialBankAudioSource(FmodBankAudioSource source)
    {
        if (source.StreamCount <= InitialBankPreviewStreams)
        {
            return source;
        }

        return source with
        {
            StreamCountLimit = InitialBankPreviewStreams,
            Partial = true
        };
    }

    private static Func<int, Task<AudioPreviewDocument?>> CreateBankDocumentLoader(FmodBankAudioSource source)
    {
        return index => index >= 0 && index < source.StreamCount
            ? Task.Run(() => FmodBankAudioPreviewDocumentFactory.TryCreate(source, index, out AudioPreviewDocument? nextDocument, out _)
                ? nextDocument
                : null)
            : Task.FromResult<AudioPreviewDocument?>(null);
    }

    private static Func<int, Task<AudioPreviewDocument?>> CreateBankDocumentLoader(BankAudioStreamState state)
    {
        return index => Task.Run(() =>
        {
            FmodBankAudioSource source;
            lock (state.SyncRoot)
            {
                source = state.CurrentSource;
            }

            if (index < 0 || index >= source.StreamCount)
            {
                return null;
            }

            if (FmodBankAudioPreviewDocumentFactory.TryGetRequiredDecodedByteCount(source, index, out int requiredByteCount))
            {
                source = EnsureBankAudioSourceDecoded(state, requiredByteCount);
            }

            return FmodBankAudioPreviewDocumentFactory.TryCreate(source, index, out AudioPreviewDocument? nextDocument, out _)
                ? nextDocument
                : null;
        });
    }

    private void QueueBankAudioSourceProgressiveAppend(
        AudioPreviewWindow window,
        BankAudioStreamState state)
    {
        window.Closed += (_, _) =>
        {
            lock (state.SyncRoot)
            {
                state.DisposeSession();
            }
        };
        _ = AppendAndUpgradeBankAudioSourceAsync(window, state);
    }

    private static async Task AppendAndUpgradeBankAudioSourceAsync(
        AudioPreviewWindow window,
        BankAudioStreamState state)
    {
        try
        {
            FmodBankAudioSource source;
            lock (state.SyncRoot)
            {
                source = state.CurrentSource;
            }

            if (source.TotalStreamCount > await GetAudioPreviewDocumentCountAsync(window))
            {
                await AppendBankAudioSourceAsync(window, source, CreateBankDocumentLoader(state));
            }

            lock (state.SyncRoot)
            {
                source = state.CurrentSource;
            }

            bool directoryComplete =
                source.Partial &&
                HasMetadataOnlyFsbBlock(source) &&
                source.StreamCount <= await GetAudioPreviewDocumentCountAsync(window);
            if (directoryComplete)
            {
                await SetAudioPreviewNavigationLoadingAsync(window, false);
            }

            FmodBankAudioSource currentSource = source;
            while (currentSource.Partial)
            {
                if (await IsAudioPreviewClosedAsync(window))
                {
                    return;
                }

                int decodedByteCount;
                long totalDecodedBytes;
                lock (state.SyncRoot)
                {
                    if (state.Session is null)
                    {
                        break;
                    }

                    decodedByteCount = state.Session.DecodedByteCount;
                    totalDecodedBytes = state.Session.TotalDecodedBytes;
                }

                int nextTarget = directoryComplete
                    ? GetNextBankBackgroundPrefetchTarget(decodedByteCount, totalDecodedBytes)
                    : GetNextBankProgressiveTarget(decodedByteCount, totalDecodedBytes);
                if (nextTarget <= decodedByteCount)
                {
                    break;
                }

                int nextDecodedByteCount = await Task.Run(() =>
                {
                    lock (state.SyncRoot)
                    {
                        return directoryComplete
                            ? ReadBankAudioStateBytesTo(state, nextTarget)
                            : ReadBankAudioStateTo(state, nextTarget).DecodedByteCount;
                    }
                });

                bool readAny = nextDecodedByteCount > decodedByteCount;
                if (!directoryComplete)
                {
                    FmodBankAudioSource nextSource;
                    lock (state.SyncRoot)
                    {
                        nextSource = state.CurrentSource;
                    }

                    currentSource = nextSource;
                    if (nextSource.StreamCount > await GetAudioPreviewDocumentCountAsync(window))
                    {
                        await AppendBankAudioSourceAsync(window, nextSource, CreateBankDocumentLoader(state));
                    }

                    if (HasMetadataOnlyFsbBlock(nextSource) &&
                        nextSource.StreamCount <= await GetAudioPreviewDocumentCountAsync(window))
                    {
                        directoryComplete = true;
                        await SetAudioPreviewNavigationLoadingAsync(window, false);
                    }
                }

                bool isComplete;
                lock (state.SyncRoot)
                {
                    isComplete = state.Session is null || state.Session.IsComplete;
                }

                if (isComplete)
                {
                    lock (state.SyncRoot)
                    {
                        currentSource = CompleteBankAudioState(state);
                    }

                    await SetAudioPreviewNavigationLoadingAsync(window, false);
                    return;
                }

                if (!readAny)
                {
                    break;
                }
            }

            await SetAudioPreviewNavigationLoadingAsync(window, false);
        }
        catch
        {
            await SetAudioPreviewNavigationLoadingAsync(window, false);
        }
    }

    private static bool HasMetadataOnlyFsbBlock(FmodBankAudioSource source)
    {
        foreach (FmodBankFsbBlock block in source.FsbBlocks)
        {
            if (block.Offset + (long)block.MetadataLength <= source.DecodedByteCount &&
                block.Offset + (long)block.Length > source.DecodedByteCount)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task AppendBankAudioSourceAsync(
        AudioPreviewWindow window,
        FmodBankAudioSource source,
        Func<int, Task<AudioPreviewDocument?>>? loaderOverride = null)
    {
        try
        {
            IReadOnlyList<string> streamNames = source.GetStreamNames();
            Func<int, Task<AudioPreviewDocument?>> loader = loaderOverride ?? CreateBankDocumentLoader(source);
            await window.Dispatcher.InvokeAsync(() => window.SetNavigationLoading(source.Partial || source.StreamCount > window.DocumentCount));
            int currentCount = await window.Dispatcher.InvokeAsync(() => window.DocumentCount);
            while (currentCount < source.StreamCount)
            {
                int nextCount = Math.Min(source.StreamCount, currentCount + GetBankStreamAppendBatchSize(currentCount));

                bool keepGoing = await window.Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (!window.IsVisible || window.Dispatcher.HasShutdownStarted)
                        {
                            return false;
                        }

                        window.AppendDocumentNavigation(
                            nextCount > 1 ? loader : null,
                            nextCount,
                            streamNames);
                        return true;
                    },
                    DispatcherPriority.Background);

                if (!keepGoing)
                {
                    break;
                }

                currentCount = nextCount;
                await Task.Delay(GetBankStreamAppendDelayMilliseconds(currentCount));
            }

            await window.Dispatcher.InvokeAsync(() => window.SetNavigationLoading(source.Partial));
        }
        catch
        {
            if (!window.Dispatcher.HasShutdownStarted)
            {
                await window.Dispatcher.InvokeAsync(() => window.SetNavigationLoading(false));
            }
        }
    }

    private static int GetNextBankProgressiveTarget(int decodedByteCount, long totalDecodedBytes)
    {
        long targetLimit = Math.Min(totalDecodedBytes, FmodBankDecoder.MaxDecodedBytes);
        foreach (int prefixSize in ProgressiveBankPreviewPrefixSizes)
        {
            if (prefixSize > decodedByteCount)
            {
                return checked((int)Math.Min(prefixSize, targetLimit));
            }
        }

        int stepBytes = decodedByteCount < 512 * 1024 * 1024
            ? 128 * 1024 * 1024
            : 256 * 1024 * 1024;
        return checked((int)Math.Min(decodedByteCount + (long)stepBytes, targetLimit));
    }

    private static int GetNextBankBackgroundPrefetchTarget(int decodedByteCount, long totalDecodedBytes)
    {
        long targetLimit = Math.Min(totalDecodedBytes, FmodBankDecoder.MaxDecodedBytes);
        return checked((int)Math.Min(decodedByteCount + (long)BankBackgroundPrefetchStepBytes, targetLimit));
    }

    private static async Task<int> GetAudioPreviewDocumentCountAsync(AudioPreviewWindow window)
    {
        if (window.Dispatcher.HasShutdownStarted)
        {
            return int.MaxValue;
        }

        return await window.Dispatcher.InvokeAsync(() => window.DocumentCount);
    }

    private static async Task<bool> IsAudioPreviewClosedAsync(AudioPreviewWindow window)
    {
        if (window.Dispatcher.HasShutdownStarted)
        {
            return true;
        }

        return await window.Dispatcher.InvokeAsync(() => !window.IsVisible);
    }

    private static async Task SetAudioPreviewNavigationLoadingAsync(AudioPreviewWindow window, bool isLoading)
    {
        if (window.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        await window.Dispatcher.InvokeAsync(() => window.SetNavigationLoading(isLoading));
    }

    private static int GetBankStreamAppendBatchSize(int currentCount)
    {
        return currentCount switch
        {
            < 24 => 1,
            < 64 => 2,
            < 128 => 4,
            < 256 => 8,
            < 512 => 16,
            < 1024 => 32,
            < 4096 => 128,
            < 16384 => 512,
            < 65536 => 1024,
            _ => 2048
        };
    }

    private static int GetBankStreamAppendDelayMilliseconds(int currentCount)
    {
        return currentCount switch
        {
            < 24 => 80,
            < 128 => 45,
            < 512 => 24,
            < 4096 => 16,
            < 16384 => 10,
            < 65536 => 6,
            _ => 4
        };
    }

    private static Task<AudioPreviewDocument?> LoadAudioPreviewDocumentAsync(ExplorerItem item)
    {
        return Task.Run(() =>
        {
            byte[] data = ReadExplorerFileBytes(item, AudioPreviewDocumentFactory.MaxAudioPreviewBytes);
            return AudioPreviewDocumentFactory.TryCreate(
                item.Name,
                item.Kind == ExplorerItemKind.LocalFile ? item.SourcePath : null,
                data,
                canUseSourcePath: item.Kind == ExplorerItemKind.LocalFile,
                out AudioPreviewDocument? document,
                out _) ? document : null;
        });
    }

    private void ShowIndependentAudioPreviewWindow(AudioPreviewWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        CenterWindowOverMainWindow(window);
        window.Show();
    }

    private void CenterWindowOverMainWindow(Window window)
    {
        double windowWidth = GetWindowDimension(window.Width, window.ActualWidth, window.MinWidth);
        double windowHeight = GetWindowDimension(window.Height, window.ActualHeight, window.MinHeight);
        Rect mainBounds = GetMainWindowBounds();
        Rect workArea = GetCurrentScreenWorkArea();

        window.Left = mainBounds.Left + Math.Max(0, (mainBounds.Width - windowWidth) / 2);
        window.Top = mainBounds.Top + Math.Max(0, (mainBounds.Height - windowHeight) / 2);

        if (windowWidth <= workArea.Width)
        {
            window.Left = Math.Min(Math.Max(window.Left, workArea.Left), workArea.Right - windowWidth);
        }
        else
        {
            window.Left = workArea.Left;
        }

        if (windowHeight <= workArea.Height)
        {
            window.Top = Math.Min(Math.Max(window.Top, workArea.Top), workArea.Bottom - windowHeight);
        }
        else
        {
            window.Top = workArea.Top;
        }
    }

    private Rect GetMainWindowBounds()
    {
        if (WindowState == WindowState.Maximized)
        {
            return GetCurrentScreenWorkArea();
        }

        if (WindowState == WindowState.Minimized)
        {
            return RestoreBounds;
        }

        double width = GetWindowDimension(Width, ActualWidth, MinWidth);
        double height = GetWindowDimension(Height, ActualHeight, MinHeight);
        return new Rect(Left, Top, width, height);
    }

    private Rect GetCurrentScreenWorkArea()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Forms.Screen? screen = handle != IntPtr.Zero
            ? Forms.Screen.FromHandle(handle)
            : Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        Rect deviceRect = new(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height);

        return DeviceRectToDips(deviceRect);
    }

    private Rect DeviceRectToDips(Rect deviceRect)
    {
        PresentationSource? source = PresentationSource.FromVisual(this);
        Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        System.Windows.Point topLeft = transform.Transform(new System.Windows.Point(deviceRect.Left, deviceRect.Top));
        System.Windows.Point bottomRight = transform.Transform(new System.Windows.Point(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static double GetWindowDimension(double configuredValue, double actualValue, double minimumValue)
    {
        if (!double.IsNaN(configuredValue) && configuredValue > 0)
        {
            return configuredValue;
        }

        if (actualValue > 0)
        {
            return actualValue;
        }

        return minimumValue;
    }

    private async Task ShowImagePreviewAsync(ExplorerItem item)
    {
        List<ExplorerItem> imageItems = GetCurrentImagePreviewItems();
        int imageIndex = imageItems.IndexOf(item);
        if (imageIndex < 0)
        {
            imageItems = [item];
            imageIndex = 0;
        }

        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingPreview", item.Name);

        try
        {
            ImagePreviewDocument? document = await LoadImagePreviewDocumentAsync(item);
            if (document is null)
            {
                SetStatus("PreviewUnsupported", item.Name);
                return;
            }

            Task<ImagePreviewDocument?> LoadDocumentAtAsync(int index)
            {
                return index >= 0 && index < imageItems.Count
                    ? LoadImagePreviewDocumentAsync(imageItems[index])
                    : Task.FromResult<ImagePreviewDocument?>(null);
            }

            var window = new ImagePreviewWindow(
                document,
                imageItems.Count > 1 ? LoadDocumentAtAsync : null,
                imageIndex,
                imageItems.Count)
            {
                Owner = this,
                ShowInTaskbar = true
            };
            window.Show();
            SetStatus("PreviewOpened", item.Name);
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }
    }

    private List<ExplorerItem> GetCurrentImagePreviewItems()
    {
        return ContentsList.Items
            .OfType<ExplorerItem>()
            .Where(item => item.IsImagePreviewCandidate)
            .ToList();
    }

    private async Task<ImagePreviewDocument?> LoadImagePreviewDocumentAsync(ExplorerItem item)
    {
        IReadOnlyList<ImagePreviewFrame> frames = await item.LoadPreviewFramesAsync();
        return frames.Count == 0
            ? null
            : new ImagePreviewDocument(item.Name, frames, FormatImagePreviewInfo(item));
    }

    private string? FormatImagePreviewInfo(ExplorerItem item)
    {
        return item.ImageStorageKind switch
        {
            ImageStorageKind.DtxLzmaCompressed => T("PreviewDtxLzma"),
            ImageStorageKind.DtxUncompressed => T("PreviewDtxRaw"),
            ImageStorageKind.DdsBlockCompressed => T("PreviewDdsDxt"),
            ImageStorageKind.DdsUncompressed => T("PreviewDdsRaw"),
            ImageStorageKind.TgaLzmaCompressed => T("PreviewTgaLzma"),
            ImageStorageKind.TgaInsertedFooterHeader => T("PreviewTgaRepaired"),
            ImageStorageKind.TgaRawPixels => T("PreviewTgaRawPixels"),
            ImageStorageKind.TgaUncompressed => T("PreviewTgaRaw"),
            ImageStorageKind.CrossFireImageBin => T("PreviewImageBin"),
            ImageStorageKind.CrossFireImageBinLzma => T("PreviewImageBinLzma"),
            ImageStorageKind.CrossFireImageBinZstd => T("PreviewImageBinZstd"),
            _ => null
        };
    }

    private async Task ShowSpritePreviewAsync(ExplorerItem item)
    {
        List<ExplorerItem> spriteItems = GetCurrentSpritePreviewItems();
        int spriteIndex = spriteItems.IndexOf(item);
        if (spriteIndex < 0)
        {
            spriteItems = [item];
            spriteIndex = 0;
        }

        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingPreview", item.Name);

        try
        {
            ImagePreviewDocument? document = await LoadSpritePreviewDocumentAsync(item);
            if (document is null)
            {
                SetBusy(false, keepSearchEnabled: true);
                await ShowTextPreviewAsync(item);
                return;
            }

            Task<ImagePreviewDocument?> LoadDocumentAtAsync(int index)
            {
                return index >= 0 && index < spriteItems.Count
                    ? LoadSpritePreviewDocumentAsync(spriteItems[index])
                    : Task.FromResult<ImagePreviewDocument?>(null);
            }

            var window = new ImagePreviewWindow(
                document,
                spriteItems.Count > 1 ? LoadDocumentAtAsync : null,
                spriteIndex,
                spriteItems.Count)
            {
                Owner = this,
                ShowInTaskbar = true
            };
            window.Show();
            SetStatus("PreviewOpened", item.Name);
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }
    }

    private List<ExplorerItem> GetCurrentSpritePreviewItems()
    {
        return ContentsList.Items
            .OfType<ExplorerItem>()
            .Where(IsSpritePreviewCandidate)
            .ToList();
    }

    private static Task<ImagePreviewDocument?> LoadSpritePreviewDocumentAsync(ExplorerItem item)
    {
        return Task.Run(() =>
        {
            LithTechSpritePreviewDocument? preview;
            bool loaded;

            if (item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
            {
                loaded = LithTechSpritePreviewLoader.TryLoadFromArchive(
                    item.Archive,
                    item.ArchiveFile,
                    item.Name,
                    out preview,
                    out _);
            }
            else if (item.Kind == ExplorerItemKind.LocalFile && !string.IsNullOrWhiteSpace(item.SourcePath))
            {
                loaded = LithTechSpritePreviewLoader.TryLoadFromLocalFile(
                    item.SourcePath,
                    item.Name,
                    out preview,
                    out _);
            }
            else
            {
                return null;
            }

            return loaded && preview is not null
                ? CreateSpriteImageDocument(item.Name, preview)
                : null;
        });
    }

    private static ImagePreviewDocument CreateSpriteImageDocument(
        string name,
        LithTechSpritePreviewDocument preview)
    {
        return new ImagePreviewDocument(name, preview.Frames, preview.Info, preview.FrameRate);
    }

    private async Task ShowPreviewToolAsync(ExplorerItem item)
    {
        SetBusy(true, keepSearchEnabled: true);
        SetStatus("LoadingPreview", item.Name);

        try
        {
            string previewPath = await Task.Run(() => CreatePreviewToolFile(item));
            StartPreviewTool(previewPath);
            SetStatus("PreviewOpened", item.Name);
        }
        catch (Exception ex)
        {
            ShowError("PreviewFailed", ex);
        }
        finally
        {
            SetBusy(false, keepSearchEnabled: true);
        }
    }

    private static string CreatePreviewToolFile(ExplorerItem item)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            return item.SourcePath;
        }

        if (item.Archive is null || item.ArchiveFile is null)
        {
            throw new InvalidOperationException(LocalizedText.T("PreviewItemNotFile"));
        }

        string previewDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "PreviewTool");
        Directory.CreateDirectory(previewDirectory);
        string fileName = $"{Guid.NewGuid():N}_{SanitizePathSegment(item.Name)}";
        string previewPath = Path.Combine(previewDirectory, fileName);
        RezArchiveReader.ExtractFile(item.Archive, item.ArchiveFile, previewPath);
        return previewPath;
    }

    private static byte[] ReadExplorerFileBytes(ExplorerItem item, int maxBytes)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(item.SourcePath);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes || info.Length > int.MaxValue)
            {
                throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
            }

            return File.ReadAllBytes(item.SourcePath);
        }

        if (item.Archive is null ||
            item.ArchiveFile is null ||
            item.ArchiveFile.Size < 0 ||
            item.ArchiveFile.Size > maxBytes)
        {
            throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
        }

        byte[] data = new byte[item.ArchiveFile.Size];
        using FileStream source = File.OpenRead(item.Archive.FilePath);
        source.Position = item.ArchiveFile.DataOffset;
        source.ReadExactly(data);
        return data;
    }

    private static byte[] ReadExplorerFilePrefixBytes(ExplorerItem item, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return [];
        }

        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(item.SourcePath);
            if (!info.Exists || info.Length < 0)
            {
                throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
            }

            int byteCount = checked((int)Math.Min(Math.Min(info.Length, maxBytes), int.MaxValue));
            byte[] data = new byte[byteCount];
            using FileStream source = File.OpenRead(item.SourcePath);
            source.ReadExactly(data);
            return data;
        }

        if (item.Archive is null ||
            item.ArchiveFile is null ||
            item.ArchiveFile.Size < 0)
        {
            throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
        }

        int archiveByteCount = checked((int)Math.Min(Math.Min(item.ArchiveFile.Size, maxBytes), int.MaxValue));
        byte[] archiveData = new byte[archiveByteCount];
        using FileStream archiveSource = File.OpenRead(item.Archive.FilePath);
        archiveSource.Position = item.ArchiveFile.DataOffset;
        archiveSource.ReadExactly(archiveData);
        return archiveData;
    }

    private static long GetExplorerFileByteCount(ExplorerItem item)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            var info = new FileInfo(item.SourcePath);
            if (!info.Exists || info.Length < 0)
            {
                throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
            }

            return info.Length;
        }

        if (item.ArchiveFile is not null && item.ArchiveFile.Size >= 0)
        {
            return item.ArchiveFile.Size;
        }

        throw new InvalidOperationException(LocalizedText.Format("PreviewFileTooLarge", item.Name));
    }

    private static bool IsSpritePreviewCandidate(ExplorerItem item)
    {
        return item.IsFile && LithTechSpriteDecoder.IsCandidate(item.FileExtension);
    }

    private static bool IsBankAudioPreviewCandidate(ExplorerItem item)
    {
        return item.IsFile &&
               FmodBankDecoder.IsCandidate(item.FileExtension) &&
               FmodBankAudioPreviewDocumentFactory.IsAvailable;
    }

    private static bool IsAudioPreviewCandidate(ExplorerItem item)
    {
        return item.IsFile && AudioMetadataDecoder.IsSupportedExtension(item.FileExtension);
    }

    private string FormatModelInfo(LithTechModelDocument document)
    {
        return FormatText(
            "ModelPreviewInfo",
            document.StorageDescription,
            document.Meshes.Count,
            document.VertexCount,
            document.TriangleCount,
            document.SourceByteCount,
            document.DecodedByteCount);
    }

    private static void StartPreviewTool(string filePath)
    {
        string executablePath = ResolvePreviewToolExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--preview");
        startInfo.ArgumentList.Add(filePath);
        Process.Start(startInfo);
    }

    private static string ResolvePreviewToolExecutable()
    {
        string bundledExe = Path.Combine(AppContext.BaseDirectory, "CFRezManager.exe");
        if (File.Exists(bundledExe))
        {
            return bundledExe;
        }

        return Environment.ProcessPath ?? throw new InvalidOperationException(LocalizedText.T("PreviewToolExecutableMissing"));
    }

    private void ExplorerItemTemplate_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            QueueThumbnailLoad(element.DataContext);
        }
    }

    private void ExplorerItemTemplate_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        QueueThumbnailLoad(e.NewValue);
    }

    private static void QueueThumbnailLoad(object? dataContext)
    {
        if (dataContext is ExplorerItem item && item.IsThumbnailCandidate)
        {
            _ = item.LoadThumbnailAsync();
        }
    }

    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            await NavigateBackAsync();
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            await NavigateForwardAsync();
        }
    }

    private void ContentsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy ||
            e.ChangedButton != MouseButton.Left ||
            FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _dragStartPoint = e.GetPosition(SelectionOverlay);
        _dragInitialSelection.Clear();
        foreach (ExplorerItem item in ContentsList.SelectedItems.OfType<ExplorerItem>())
        {
            _dragInitialSelection.Add(item);
        }
    }

    private void ContentsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isBusy || _dragStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        System.Windows.Point currentPoint = e.GetPosition(SelectionOverlay);
        if (!_isDragSelecting)
        {
            double horizontalMove = Math.Abs(currentPoint.X - _dragStartPoint.Value.X);
            double verticalMove = Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y);
            if (horizontalMove < SystemParameters.MinimumHorizontalDragDistance &&
                verticalMove < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isDragSelecting = true;
            SelectionBox.Visibility = Visibility.Visible;
            ContentsList.CaptureMouse();
        }

        UpdateSelectionBox(currentPoint);
        UpdateDragSelection(GetSelectionRect(currentPoint));
        e.Handled = true;
    }

    private void ContentsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragSelecting)
        {
            e.Handled = true;
        }

        EndDragSelection();
    }

    private void ContentsList_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EndDragSelection();
    }

    private void ContentsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } listBoxItem)
        {
            return;
        }

        if (!listBoxItem.IsSelected)
        {
            ContentsList.SelectedItems.Clear();
            listBoxItem.IsSelected = true;
        }

        listBoxItem.Focus();
    }

    private void ContentsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = GetSelectedExplorerItems();
        ExplorerItem? previewItem = selectedItems.Count == 1 &&
                                    (IsBankAudioPreviewCandidate(selectedItems[0]) ||
                                     IsSpritePreviewCandidate(selectedItems[0]) ||
                                     IsAudioPreviewCandidate(selectedItems[0]) ||
                                     selectedItems[0].IsModelPreviewCandidate ||
                                     selectedItems[0].IsTextPreviewCandidate ||
                                     selectedItems[0].IsImagePreviewCandidate)
            ? selectedItems[0]
            : null;
        OpenPreviewMenuItem.Visibility = previewItem is null ? Visibility.Collapsed : Visibility.Visible;
        ExplorerItem? bankItem = selectedItems.Count == 1 && FmodBankDecoder.IsCandidate(selectedItems[0].FileExtension)
            ? selectedItems[0]
            : null;
        DecodeBankMenuItem.Visibility = bankItem is null ? Visibility.Collapsed : Visibility.Visible;
        DecodeBankMenuItem.IsEnabled = !_isBusy && bankItem is not null;
        DecodeBankMenuItem.Header = T("DecodeBank");
        bool canExportObj = selectedItems.Count > 0 &&
                            selectedItems.Any(item => item.IsContainer || item.IsModelPreviewCandidate);
        ExportObjMenuItem.Visibility = canExportObj ? Visibility.Visible : Visibility.Collapsed;
        ExportObjMenuItem.IsEnabled = !_isBusy && canExportObj;
        ExportObjMenuItem.Header = T("ExportObj");
        PreviewMenuSeparator.Visibility = OpenPreviewMenuItem.Visibility == Visibility.Visible ||
                                          DecodeBankMenuItem.Visibility == Visibility.Visible ||
                                          ExportObjMenuItem.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenPreviewMenuItem.IsEnabled = !_isBusy && previewItem is not null;
        OpenPreviewMenuItem.Header = previewItem is not null && IsSpritePreviewCandidate(previewItem)
            ? T("OpenImagePreview")
            : previewItem switch
            {
                ExplorerItem playableBankItem when IsBankAudioPreviewCandidate(playableBankItem) => T("OpenAudioPreview"),
                ExplorerItem audioItem when IsAudioPreviewCandidate(audioItem) => T("OpenAudioPreview"),
                { IsModelPreviewCandidate: true } => T("OpenModelPreview"),
                { IsTextPreviewCandidate: true } => T("OpenTextPreview"),
                _ => T("OpenImagePreview")
            };

        ExtractSelectedMenuItem.IsEnabled = !_isBusy && selectedItems.Count > 0;
        ExtractSelectedMenuItem.Header = selectedItems.Count == 1
            ? T("ExtractThisItem")
            : FormatText("ExtractSelectedItems", selectedItems.Count);
        LocateFileMenuItem.IsEnabled = !_isBusy && selectedItems.Count == 1 && selectedItems[0].Parent is not null;
        LocateFileMenuItem.Header = T("LocateFile");
        CopyNameMenuItem.IsEnabled = selectedItems.Count > 0;
        CopyNameMenuItem.Header = selectedItems.Count <= 1
            ? T("CopyName")
            : FormatText("CopySelectedNames", selectedItems.Count);
    }

    private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextChanged)
        {
            return;
        }

        string query = SearchTextBox.Text.Trim();
        ClearSearchButton.IsEnabled = query.Length > 0;
        int requestVersion = ++_searchRequestVersion;

        if (query.Length == 0)
        {
            ExitSearchMode();
            return;
        }

        if (_rootItem is null)
        {
            return;
        }

        if (!_isSearchIndexReady)
        {
            await EnsureSearchIndexAsync();
        }

        if (_isSearchIndexReady && requestVersion == _searchRequestVersion && SearchTextBox.Text.Trim().Length > 0)
        {
            ApplySearch(SearchTextBox.Text.Trim());
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSearchText();
    }

    private void ContentFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentFilterComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out ContentFilter filter))
        {
            return;
        }

        _contentFilter = filter;
        if (!IsInitialized)
        {
            return;
        }

        string query = SearchTextBox.Text.Trim();
        if (query.Length > 0)
        {
            if (_isSearchIndexReady)
            {
                ApplySearch(query);
            }

            return;
        }

        ShowCurrentFolderItems();
    }

    private void ViewSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyViewSize(e.NewValue);
        _settings.ViewSize = e.NewValue;
        _settings.Save();
    }

    private List<ExplorerItem> GetSelectedExplorerItems()
    {
        return ContentsList.SelectedItems.OfType<ExplorerItem>().ToList();
    }

    private void UpdateSelectionBox(System.Windows.Point currentPoint)
    {
        Rect rect = GetSelectionRect(currentPoint);
        Canvas.SetLeft(SelectionBox, rect.Left);
        Canvas.SetTop(SelectionBox, rect.Top);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
    }

    private Rect GetSelectionRect(System.Windows.Point currentPoint)
    {
        System.Windows.Point startPoint = _dragStartPoint ?? currentPoint;
        return new Rect(startPoint, currentPoint);
    }

    private void UpdateDragSelection(Rect selectionRect)
    {
        var hitItems = new HashSet<ExplorerItem>();
        foreach (object item in ContentsList.Items)
        {
            if (ContentsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
                container.DataContext is not ExplorerItem explorerItem ||
                !container.IsVisible)
            {
                continue;
            }

            Rect itemRect = container
                .TransformToVisual(SelectionOverlay)
                .TransformBounds(new Rect(new System.Windows.Size(container.ActualWidth, container.ActualHeight)));
            if (selectionRect.IntersectsWith(itemRect))
            {
                hitItems.Add(explorerItem);
            }
        }

        IEnumerable<ExplorerItem> selectedItems;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var toggledSelection = new HashSet<ExplorerItem>(_dragInitialSelection);
            foreach (ExplorerItem item in hitItems)
            {
                if (!toggledSelection.Add(item))
                {
                    toggledSelection.Remove(item);
                }
            }

            selectedItems = toggledSelection;
        }
        else if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var appendedSelection = new HashSet<ExplorerItem>(_dragInitialSelection);
            appendedSelection.UnionWith(hitItems);
            selectedItems = appendedSelection;
        }
        else
        {
            selectedItems = hitItems;
        }

        ApplySelectedItems(selectedItems);
    }

    private void ApplySelectedItems(IEnumerable<ExplorerItem> selectedItems)
    {
        var selectedSet = selectedItems.ToHashSet();
        ContentsList.SelectedItems.Clear();
        foreach (ExplorerItem item in selectedSet)
        {
            ContentsList.SelectedItems.Add(item);
        }
    }

    private void EndDragSelection()
    {
        _dragStartPoint = null;
        _dragInitialSelection.Clear();
        _isDragSelecting = false;
        SelectionBox.Visibility = Visibility.Collapsed;
        if (ContentsList.IsMouseCaptured)
        {
            ContentsList.ReleaseMouseCapture();
        }
    }

    private void ApplyViewSize(double value)
    {
        bool useListView = value < ListViewThreshold;
        if (useListView)
        {
            if (!_isListViewMode)
            {
                _isListViewMode = true;
                ContentsList.ItemTemplate = (DataTemplate)FindResource("ExplorerListTemplate");
                ContentsList.ItemContainerStyle = (Style)FindResource("ExplorerListItemStyle");
                ContentsList.ItemsPanel = (ItemsPanelTemplate)FindResource("ExplorerListPanelTemplate");
            }

            ContentsList.InvalidateMeasure();
            return;
        }

        double factor = Math.Clamp((value - ListViewThreshold) / (100.0 - ListViewThreshold), 0, 1);
        TileItemWidth = 112 + (44 * factor);
        TileItemHeight = 104 + (44 * factor);
        TileCellWidth = TileItemWidth + 12;
        TileCellHeight = TileItemHeight + 12;
        TileIconSize = 48 + (24 * factor);
        TileIconRowHeight = TileIconSize + 10;
        TileFileIconWidth = TileIconSize * 0.82;
        TileFilePageWidth = TileIconSize * 0.68;
        TileFilePageHeight = TileIconSize * 0.88;
        TileNameMaxHeight = 34 + (18 * factor);
        UpdateTileMetricResources();

        if (_isListViewMode)
        {
            _isListViewMode = false;
            ContentsList.ItemTemplate = (DataTemplate)FindResource("ExplorerIconTemplate");
            ContentsList.ItemContainerStyle = (Style)FindResource("ExplorerTileItemStyle");
            ContentsList.ItemsPanel = (ItemsPanelTemplate)FindResource("ExplorerTilePanelTemplate");
        }

        ContentsList.InvalidateMeasure();
    }

    private void UpdateTileMetricResources()
    {
        Resources["TileItemWidthValue"] = TileItemWidth;
        Resources["TileItemHeightValue"] = TileItemHeight;
        Resources["TileCellWidthValue"] = TileCellWidth;
        Resources["TileCellHeightValue"] = TileCellHeight;
        Resources["TileIconSizeValue"] = TileIconSize;
        Resources["TileIconRowHeightValue"] = new GridLength(TileIconRowHeight);
        Resources["TileFileIconWidthValue"] = TileFileIconWidth;
        Resources["TileFilePageWidthValue"] = TileFilePageWidth;
        Resources["TileFilePageHeightValue"] = TileFilePageHeight;
        Resources["TileNameMaxHeightValue"] = TileNameMaxHeight;
    }

    private static double ClampViewSize(double value)
    {
        if (double.IsNaN(value))
        {
            return 72;
        }

        return Math.Clamp(value, 0, 100);
    }

    private async Task EnsureSearchIndexAsync()
    {
        if (_isSearchIndexReady || _rootItem is null)
        {
            return;
        }

        ExplorerItem root = _rootItem;
        if (_searchIndexTask is null || !ReferenceEquals(_searchIndexTaskRoot, root))
        {
            _searchIndexTaskRoot = root;
            _searchIndexTask = BuildSearchIndexAsync(root);
        }

        await _searchIndexTask;
    }

    private async Task BuildSearchIndexAsync(ExplorerItem root)
    {
        SetBusy(true, keepSearchEnabled: true);
        SetStatus("BuildingSearchIndex");

        try
        {
            List<SearchEntry> index = await Task.Run(() => BuildSearchIndex(root));
            if (ReferenceEquals(root, _rootItem))
            {
                _searchIndex = index;
                _isSearchIndexReady = true;
                SetStatus("SearchIndexReady", index.Count);
            }
        }
        catch (Exception ex)
        {
            ShowError("SearchFailed", ex);
        }
        finally
        {
            SetBusy(false);
            if (ReferenceEquals(_searchIndexTaskRoot, root))
            {
                _searchIndexTask = null;
                _searchIndexTaskRoot = null;
            }
        }
    }

    private static List<SearchEntry> BuildSearchIndex(ExplorerItem root)
    {
        LoadAllArchives(root);

        var entries = new List<SearchEntry>();
        foreach (ExplorerItem child in root.Children)
        {
            CollectSearchEntries(child, entries);
        }

        return entries;
    }

    private static void CollectSearchEntries(ExplorerItem item, List<SearchEntry> entries)
    {
        entries.Add(new SearchEntry(item, CreateSearchText(item)));
        foreach (ExplorerItem child in item.Children)
        {
            CollectSearchEntries(child, entries);
        }
    }

    private static string CreateSearchText(ExplorerItem item)
    {
        return string.Join('\n', item.Name, item.OutputRelativePath, item.SourcePath);
    }

    private void ApplySearch(string query)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ExplorerItem> matches = _searchIndex
            .Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Item)
            .Distinct()
            .ToList();
        IReadOnlyList<ExplorerItem> results = ApplyContentFilter(matches);

        _isSearchMode = true;
        ContentsList.SelectedItems.Clear();
        ContentsList.ItemsSource = results;
        UpdateEmptyState(results.Count, matches.Count, isSearch: true);

        if (results.Count == 0)
        {
            SetStatus("SearchNoResults", query);
        }
        else
        {
            SetStatus("SearchResults", query, results.Count);
            ContentsList.ScrollIntoView(results[0]);
        }
    }

    private void ExitSearchMode()
    {
        if (!_isSearchMode)
        {
            return;
        }

        _isSearchMode = false;
        if (_currentItem is null)
        {
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowCurrentFolderItems();
    }

    private void ShowCurrentFolderItems()
    {
        if (_currentItem is null)
        {
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<ExplorerItem> visibleItems = ApplyContentFilter(_currentItem.Children);
        ContentsList.SelectedItems.Clear();
        ContentsList.ItemsSource = visibleItems;
        UpdateEmptyState(visibleItems.Count, _currentItem.Children.Count, isSearch: false);
        SetFolderStatus(_currentItem);
        if (visibleItems.Count > 0)
        {
            ContentsList.ScrollIntoView(visibleItems[0]);
        }
    }

    private IReadOnlyList<ExplorerItem> ApplyContentFilter(IReadOnlyList<ExplorerItem> items)
    {
        if (_contentFilter == ContentFilter.All)
        {
            return items;
        }

        return items
            .Where(item => ClassifyContent(item) == _contentFilter)
            .ToList();
    }

    private static ContentFilter ClassifyContent(ExplorerItem item)
    {
        if (item.IsContainer)
        {
            return ContentFilter.Folders;
        }

        if (item.IsModelPreviewCandidate)
        {
            return ContentFilter.Models;
        }

        if (item.IsFile &&
            (AudioMetadataDecoder.IsSupportedExtension(item.FileExtension) ||
             FmodBankDecoder.IsCandidate(item.FileExtension)))
        {
            return ContentFilter.Audio;
        }

        if (item.IsImagePreviewCandidate || IsSpritePreviewCandidate(item))
        {
            return ContentFilter.Images;
        }

        return item.IsTextPreviewCandidate
            ? ContentFilter.Text
            : ContentFilter.Other;
    }

    private void UpdateEmptyState(int visibleCount, int unfilteredCount, bool isSearch)
    {
        EmptyStatePanel.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleCount != 0)
        {
            return;
        }

        EmptyStateText.Text = isSearch
            ? T("SearchEmpty")
            : _contentFilter != ContentFilter.All && unfilteredCount > 0
                ? T("FilterEmpty")
                : T("EmptyFolder");
    }

    private void ClearSearchText()
    {
        if (SearchTextBox.Text.Length == 0)
        {
            ExitSearchMode();
            return;
        }

        SearchTextBox.Clear();
    }

    private void ClearSearchTextSilently()
    {
        _suppressSearchTextChanged = true;
        SearchTextBox.Clear();
        ClearSearchButton.IsEnabled = false;
        _suppressSearchTextChanged = false;
        _searchRequestVersion++;
        _isSearchMode = false;
    }

    private void ResetSearchState(bool clearText)
    {
        _searchIndex.Clear();
        _isSearchIndexReady = false;
        _isSearchMode = false;
        _searchIndexTask = null;
        _searchIndexTaskRoot = null;
        _searchRequestVersion++;
        if (clearText)
        {
            ClearSearchTextSilently();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ApplyLanguage()
    {
        BrowseFolderButton.Content = T("BrowseFolder");
        ExtractAllButton.Content = T("ExtractAll");
        PackFolderButton.Content = T("PackFolder");
        SettingsButton.Content = T("Settings");
        ContentsHeaderText.Text = T("Contents");
        FilterAllItem.Content = T("FilterAll");
        FilterFoldersItem.Content = T("FilterFolders");
        FilterImagesItem.Content = T("FilterImages");
        FilterModelsItem.Content = T("FilterModels");
        FilterAudioItem.Content = T("FilterAudio");
        FilterTextItem.Content = T("FilterText");
        FilterOtherItem.Content = T("FilterOther");
        ContentFilterComboBox.ToolTip = T("ContentFilterTooltip");
        UpdateEmptyStateText();
        OpenPreviewMenuItem.Header = T("OpenPreview");
        DecodeBankMenuItem.Header = T("DecodeBank");
        ExportObjMenuItem.Header = T("ExportObj");
        LocateFileMenuItem.Header = T("LocateFile");
        CopyNameMenuItem.Header = T("CopyName");
        ExtractSelectedMenuItem.Header = T("ExtractSelectedDefault");
        SearchLabelText.Text = T("SearchLabel");
        SearchTextBox.ToolTip = T("SearchTooltip");
        ClearSearchButton.ToolTip = T("ClearSearch");
        ViewSizeLabelText.Text = T("ViewSizeLabel");
        ViewSizeSlider.ToolTip = T("ViewSizeTooltip");

        if (_currentItem is null)
        {
            HeaderText.Text = T("HeaderDefault");
        }

        RefreshStatusText();
        _settingsWindow?.ApplyLanguage(ToLanguageCode(_language));
    }

    private void UpdateEmptyStateText()
    {
        if (_isSearchMode)
        {
            EmptyStateText.Text = T("SearchEmpty");
            return;
        }

        EmptyStateText.Text = _contentFilter != ContentFilter.All &&
                              _currentItem is not null &&
                              _currentItem.Children.Count > 0
            ? T("FilterEmpty")
            : T("EmptyFolder");
    }

    private static AppLanguage ParseLanguage(string? value)
    {
        return string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Chinese;
    }

    private static string ToLanguageCode(AppLanguage language)
    {
        return language == AppLanguage.English ? "en" : "zh";
    }

    private string T(string key)
    {
        if (!Texts.TryGetValue(key, out (string Chinese, string English) text))
        {
            return key;
        }

        return _language == AppLanguage.English ? text.English : text.Chinese;
    }

    private string FormatText(string key, params object[] args)
    {
        return args.Length == 0
            ? T(key)
            : string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        RefreshStatusText();
    }

    private void SetStatusText(string text)
    {
        _statusKey = string.Empty;
        _statusArgs = [];
        StatusText.Text = text;
    }

    private void RefreshStatusText()
    {
        if (string.IsNullOrEmpty(_statusKey))
        {
            return;
        }

        StatusText.Text = FormatText(_statusKey, _statusArgs);
    }

    private void SetFolderStatus(ExplorerItem item)
    {
        if (item.Children.Count == 0)
        {
            SetStatus("EmptyFolder");
            return;
        }

        if (_contentFilter == ContentFilter.All)
        {
            SetStatus("ShowingItems", item.Children.Count);
            return;
        }

        int visibleCount = ApplyContentFilter(item.Children).Count;
        SetStatus("ShowingFilteredItems", visibleCount, item.Children.Count);
    }

    private async void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ExplorerItem item })
        {
            await ShowFolderAsync(item);
        }
    }

    private async Task NavigateBackAsync()
    {
        if (_isBusy || _currentItem is null || _backHistory.Count == 0)
        {
            return;
        }

        ExplorerItem target = PopHistoryItem(_backHistory);
        _forwardHistory.Add(_currentItem);
        await ShowFolderAsync(target, addToHistory: false);
    }

    private async Task NavigateForwardAsync()
    {
        if (_isBusy || _currentItem is null || _forwardHistory.Count == 0)
        {
            return;
        }

        ExplorerItem target = PopHistoryItem(_forwardHistory);
        _backHistory.Add(_currentItem);
        await ShowFolderAsync(target, addToHistory: false);
    }

    private static ExplorerItem PopHistoryItem(List<ExplorerItem> history)
    {
        int lastIndex = history.Count - 1;
        ExplorerItem item = history[lastIndex];
        history.RemoveAt(lastIndex);
        return item;
    }

    private async Task LoadDirectoryAsync(string folder)
    {
        SetBusy(true);
        ResetSearchState(clearText: true);
        HeaderText.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        SetStatus("ScanningRezArchives");
        WorkProgress.IsIndeterminate = true;

        try
        {
            ExplorerItem root = await Task.Run(() => BuildDirectoryTree(folder));
            _rootItem = root;
            _selectedDirectory = folder;
            _archiveCount = CountItems(root, ExplorerItemKind.RezArchive);
            _extractableFileCount = CountItems(root, ExplorerItemKind.LocalFile);
            _backHistory.Clear();
            _forwardHistory.Clear();

            await ShowFolderAsync(root, addToHistory: false);

            SetStatus("FoundScanItems", _archiveCount, _extractableFileCount);
        }
        catch (Exception ex)
        {
            _rootItem = null;
            _currentItem = null;
            _backHistory.Clear();
            _forwardHistory.Clear();
            ContentsList.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ShowError("ScanFailed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ExplorerItem BuildDirectoryTree(string folder)
    {
        string rootName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = folder;
        }

        var root = new ExplorerItem
        {
            Name = rootName,
            Kind = ExplorerItemKind.Directory,
            SourcePath = folder,
            OutputRelativePath = string.Empty
        };

        PopulateDirectory(root, folder, folder);
        root.SortChildren();
        return root;
    }

    private static void PopulateDirectory(ExplorerItem parent, string folder, string rootFolder)
    {
        foreach (string directory in SafeEnumerateDirectories(folder))
        {
            string relativePath = Path.GetRelativePath(rootFolder, directory);
            var directoryItem = new ExplorerItem
            {
                Name = Path.GetFileName(directory),
                Kind = ExplorerItemKind.Directory,
                SourcePath = directory,
                OutputRelativePath = SanitizeRelativePath(relativePath)
            };

            PopulateDirectory(directoryItem, directory, rootFolder);
            if (directoryItem.Children.Count > 0)
            {
                parent.AddChild(directoryItem);
            }
        }

        foreach (string rezPath in SafeEnumerateRezFiles(folder))
        {
            parent.AddChild(CreateArchivePlaceholderItem(rezPath, rootFolder));
        }

        foreach (string filePath in SafeEnumerateResourceFiles(folder))
        {
            parent.AddChild(CreateLocalFileItem(filePath, rootFolder));
        }
    }

    private static ExplorerItem CreateArchivePlaceholderItem(string rezPath, string rootFolder)
    {
        string relativePath = Path.ChangeExtension(Path.GetRelativePath(rootFolder, rezPath), null) ?? Path.GetFileNameWithoutExtension(rezPath);
        string outputRelativePath = SanitizeRelativePath(relativePath);

        return new ExplorerItem
        {
            Name = Path.GetFileNameWithoutExtension(rezPath),
            Kind = ExplorerItemKind.RezArchive,
            SourcePath = rezPath,
            OutputRelativePath = outputRelativePath,
            IsLoaded = false
        };
    }

    private static ExplorerItem CreateLocalFileItem(string filePath, string rootFolder)
    {
        string relativePath = Path.GetRelativePath(rootFolder, filePath);
        return new ExplorerItem
        {
            Name = Path.GetFileName(filePath),
            Kind = ExplorerItemKind.LocalFile,
            SourcePath = filePath,
            OutputRelativePath = SanitizeRelativePath(relativePath)
        };
    }

    private static ExplorerItem CreateArchiveChildItem(RezNode node, RezArchive archive, string parentOutputPath)
    {
        string outputRelativePath = CombineRelativePath(parentOutputPath, SanitizePathSegment(node.Name));

        if (node is RezDirectoryNode directory)
        {
            return new ExplorerItem
            {
                Name = directory.Name,
                Kind = ExplorerItemKind.RezDirectory,
                SourcePath = archive.FilePath,
                OutputRelativePath = outputRelativePath,
                Archive = archive,
                ArchiveDirectory = directory,
                IsLoaded = false
            };
        }

        var file = (RezFileNode)node;
        return new ExplorerItem
        {
            Name = file.Name,
            Kind = ExplorerItemKind.RezFile,
            SourcePath = archive.FilePath,
            OutputRelativePath = outputRelativePath,
            Archive = archive,
            ArchiveFile = file
        };
    }

    private async Task ShowFolderAsync(ExplorerItem item, bool addToHistory = true)
    {
        try
        {
            await EnsureContainerLoadedAsync(item);
        }
        catch (Exception ex)
        {
            ShowError("LoadFailed", ex);
            return;
        }

        if (addToHistory && _currentItem is not null && !ReferenceEquals(_currentItem, item))
        {
            _backHistory.Add(_currentItem);
            _forwardHistory.Clear();
        }

        _currentItem = item;
        ClearSearchTextSilently();
        HeaderText.Text = item.Name;
        RenderBreadcrumb(item);
        UpdateCommandState();
        ShowCurrentFolderItems();
    }

    private async Task EnsureContainerLoadedAsync(ExplorerItem item)
    {
        if (item.Kind is not (ExplorerItemKind.RezArchive or ExplorerItemKind.RezDirectory) || item.IsLoaded)
        {
            return;
        }

        SetBusy(true);
        WorkProgress.IsIndeterminate = true;
        string loadingTarget = item.Kind == ExplorerItemKind.RezArchive ? $"{item.Name}.rez" : item.Name;
        SetStatus("LoadingItem", loadingTarget);

        try
        {
            await Task.Run(() => LoadContainerChildren(item));
            SetStatus("LoadedItems", item.Children.Count, item.Name);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy, bool keepSearchEnabled = false)
    {
        _isBusy = isBusy;
        WorkProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        WorkProgress.IsIndeterminate = isBusy;
        BrowseFolderButton.IsEnabled = !isBusy;
        PackFolderButton.IsEnabled = !isBusy;
        SettingsButton.IsEnabled = !isBusy;
        bool searchEnabled = !isBusy || keepSearchEnabled;
        SearchTextBox.IsEnabled = searchEnabled;
        ClearSearchButton.IsEnabled = searchEnabled && SearchTextBox.Text.Length > 0;
        ContentFilterComboBox.IsEnabled = searchEnabled;
        ContentsList.IsHitTestVisible = !isBusy;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        BrowseFolderButton.IsEnabled = !_isBusy;
        PackFolderButton.IsEnabled = !_isBusy;
        SettingsButton.IsEnabled = !_isBusy;
        ExtractAllButton.IsEnabled = !_isBusy && _rootItem is not null && (_archiveCount > 0 || _extractableFileCount > 0);
    }

    private string? SelectFolder(string description, FolderDialogKind kind, string? fallbackDirectory = null)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = ResolveInitialDirectory(GetRememberedDirectory(kind), fallbackDirectory)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        RememberDirectory(kind, dialog.SelectedPath);
        return dialog.SelectedPath;
    }

    private ImageExportOptions? SelectImageExportOptions(Window owner, bool continueExport)
    {
        var window = new ExportOptionsWindow(
            ToLanguageCode(_language),
            _theme,
            _settings.ImageExportModes,
            _settings.SkipImageExportOptionsDialog,
            continueExport)
        {
            Owner = owner
        };

        if (window.ShowDialog() != true || window.SelectedOptions is null)
        {
            return null;
        }

        _settings.ImageExportModes = new Dictionary<string, string>(
            window.SelectedOptions.ToSettingsDictionary(),
            StringComparer.OrdinalIgnoreCase);
        _settings.SkipImageExportOptionsDialog = window.DoNotShowAgain;
        _settings.Save();
        return window.SelectedOptions;
    }

    private string? SelectRezOutputFile(string sourceDirectory)
    {
        string sourceName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "packed";
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = T("SaveRezArchiveTitle"),
            Filter = T("RezFileFilter"),
            DefaultExt = "rez",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = ResolveInitialDirectory(_settings.LastSaveDirectory, sourceDirectory),
            FileName = $"{sourceName}.rez"
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        string? outputDirectory = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            _settings.LastSaveDirectory = outputDirectory;
            _settings.LastDirectory = outputDirectory;
            _settings.Save();
        }

        return dialog.FileName;
    }

    private string? SelectObjOutputFile(string defaultName)
    {
        string safeName = SanitizePathSegment(Path.GetFileNameWithoutExtension(defaultName));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "model";
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = T("SaveObjTitle"),
            Filter = T("ObjFileFilter"),
            DefaultExt = "obj",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = ResolveInitialDirectory(_settings.LastOutputDirectory, _selectedDirectory),
            FileName = $"{safeName}.obj"
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        string? outputDirectory = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            _settings.LastOutputDirectory = outputDirectory;
            _settings.LastDirectory = outputDirectory;
            _settings.Save();
        }

        return dialog.FileName;
    }

    private string GetRememberedDirectory(FolderDialogKind kind)
    {
        return kind switch
        {
            FolderDialogKind.RezSource => _settings.LastRezDirectory,
            FolderDialogKind.PackSource => _settings.LastPackDirectory,
            FolderDialogKind.Output => _settings.LastOutputDirectory,
            _ => string.Empty
        };
    }

    private void RememberDirectory(FolderDialogKind kind, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        switch (kind)
        {
            case FolderDialogKind.RezSource:
                _settings.LastRezDirectory = directory;
                break;
            case FolderDialogKind.PackSource:
                _settings.LastPackDirectory = directory;
                break;
            case FolderDialogKind.Output:
                _settings.LastOutputDirectory = directory;
                break;
        }

        _settings.LastDirectory = directory;
        _settings.Save();
    }

    private string ResolveInitialDirectory(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (Directory.Exists(_settings.LastDirectory))
        {
            return _settings.LastDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateRezFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file => string.Equals(Path.GetExtension(file), ".rez", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateResourceFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file => !string.Equals(Path.GetExtension(file), ".rez", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<ExtractionJob> BuildExtractionJobs(
        IEnumerable<ExplorerItem> items,
        bool preserveOutputRelativePaths,
        ImageExportOptions imageExportOptions)
    {
        var jobs = new List<ExtractionJob>();
        var seenItems = new HashSet<ExplorerItem>();
        var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in items)
        {
            if (preserveOutputRelativePaths)
            {
                CollectExtractionJobsPreservingPaths(item, jobs, seenItems, usedRelativePaths, imageExportOptions);
            }
            else
            {
                string selectedRootPath = SanitizePathSegment(item.Name);
                CollectExtractionJobsRelativeToSelection(
                    item,
                    selectedRootPath,
                    jobs,
                    seenItems,
                    usedRelativePaths,
                    imageExportOptions);
            }
        }

        return jobs;
    }

    private static List<ModelObjExportJob> BuildModelObjExportJobs(IEnumerable<ExplorerItem> items)
    {
        var jobs = new List<ModelObjExportJob>();
        var seenItems = new HashSet<ExplorerItem>();
        var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ExplorerItem item in LithTechModelPartGrouper.ExpandNumberedSiblingParts(items))
        {
            string selectedRootPath = item.IsFile
                ? SanitizePathSegment(Path.GetFileNameWithoutExtension(item.Name))
                : SanitizePathSegment(item.Name);
            CollectModelObjExportJobs(item, selectedRootPath, jobs, seenItems, usedRelativePaths);
        }

        return jobs;
    }

    private static void CollectModelObjExportJobs(
        ExplorerItem item,
        string relativePath,
        List<ModelObjExportJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths)
    {
        if (item.Kind == ExplorerItemKind.LocalFile ||
            item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
        {
            if (item.IsModelPreviewCandidate)
            {
                AddModelObjExportJob(item, relativePath, jobs, seenItems, usedRelativePaths);
            }

            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            string childRelativePath = CombineRelativePath(relativePath, SanitizePathSegment(child.Name));
            CollectModelObjExportJobs(child, childRelativePath, jobs, seenItems, usedRelativePaths);
        }
    }

    private static void AddModelObjExportJob(
        ExplorerItem item,
        string relativePath,
        List<ModelObjExportJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths)
    {
        if (!seenItems.Add(item))
        {
            return;
        }

        string safeRelativePath = string.IsNullOrWhiteSpace(relativePath)
            ? SanitizePathSegment(Path.GetFileNameWithoutExtension(item.Name))
            : relativePath;
        string withoutExtension = Path.ChangeExtension(safeRelativePath, null) ?? safeRelativePath;
        jobs.Add(new ModelObjExportJob(item, MakeUniqueRelativePath(withoutExtension, usedRelativePaths)));
    }

    private string CreateDefaultObjExportName(IReadOnlyList<ExplorerItem> items)
    {
        if (items.Count == 1)
        {
            string name = Path.GetFileNameWithoutExtension(items[0].Name);
            return string.IsNullOrWhiteSpace(name) ? items[0].Name : name;
        }

        string currentName = _currentItem?.Name ?? "models";
        return $"{currentName}_selection";
    }

    private static string CreateObjSourceName(ModelObjExportJob job)
    {
        string name = Path.ChangeExtension(job.RelativePath, null) ?? job.RelativePath;
        return name
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_')
            .Replace('/', '_')
            .Replace('\\', '_');
    }

    private static string GetObjSourceResourcePath(ExplorerItem item)
    {
        return string.IsNullOrWhiteSpace(item.OutputRelativePath)
            ? item.Name
            : item.OutputRelativePath;
    }

    private static void CollectExtractionJobsPreservingPaths(
        ExplorerItem item,
        List<ExtractionJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths,
        ImageExportOptions imageExportOptions)
    {
        if (item.Kind == ExplorerItemKind.LocalFile ||
            item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
        {
            AddExtractionJob(item, item.OutputRelativePath, jobs, seenItems, usedRelativePaths, imageExportOptions);
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectExtractionJobsPreservingPaths(child, jobs, seenItems, usedRelativePaths, imageExportOptions);
        }
    }

    private static void CollectExtractionJobsRelativeToSelection(
        ExplorerItem item,
        string relativePath,
        List<ExtractionJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths,
        ImageExportOptions imageExportOptions)
    {
        if (item.Kind == ExplorerItemKind.LocalFile ||
            item.Kind == ExplorerItemKind.RezFile && item.Archive is not null && item.ArchiveFile is not null)
        {
            AddExtractionJob(item, relativePath, jobs, seenItems, usedRelativePaths, imageExportOptions);
            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            string childRelativePath = CombineRelativePath(relativePath, SanitizePathSegment(child.Name));
            CollectExtractionJobsRelativeToSelection(
                child,
                childRelativePath,
                jobs,
                seenItems,
                usedRelativePaths,
                imageExportOptions);
        }
    }

    private static void AddExtractionJob(
        ExplorerItem item,
        string relativePath,
        List<ExtractionJob> jobs,
        HashSet<ExplorerItem> seenItems,
        HashSet<string> usedRelativePaths,
        ImageExportOptions imageExportOptions)
    {
        if (!seenItems.Add(item))
        {
            return;
        }

        string safeRelativePath = string.IsNullOrWhiteSpace(relativePath)
            ? SanitizePathSegment(item.Name)
            : relativePath;
        bool decodeImageToPng = imageExportOptions.ShouldDecodeToPng(item.FileExtension) &&
                                !CrossFireScriptBinDecoder.IsCandidate(item.Name, item.FileExtension);
        if (decodeImageToPng)
        {
            string sourceRelativePath = MakeUniqueRelativePath(safeRelativePath, usedRelativePaths);
            string decodedRelativePath = Path.ChangeExtension(safeRelativePath, ".png") ?? $"{safeRelativePath}.png";
            string outputRelativePath = MakeUniqueRelativePath(decodedRelativePath, usedRelativePaths);
            jobs.Add(new ExtractionJob(item, sourceRelativePath, outputRelativePath, DecodeImageToPng: true));
            return;
        }

        string outputPath = MakeUniqueRelativePath(safeRelativePath, usedRelativePaths);
        jobs.Add(new ExtractionJob(item, outputPath, outputPath, DecodeImageToPng: false));
    }

    private static string MakeUniqueRelativePath(string relativePath, HashSet<string> usedRelativePaths)
    {
        if (usedRelativePaths.Add(relativePath))
        {
            return relativePath;
        }

        string? directory = Path.GetDirectoryName(relativePath);
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);
        for (int index = 2; ; index++)
        {
            string candidateName = $"{fileName} ({index}){extension}";
            string candidate = string.IsNullOrEmpty(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
            if (usedRelativePaths.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static void LoadItemsForExtraction(IEnumerable<ExplorerItem> items)
    {
        foreach (ExplorerItem item in items)
        {
            LoadItemForExtraction(item);
        }
    }

    private static void LoadItemForExtraction(ExplorerItem item)
    {
        switch (item.Kind)
        {
            case ExplorerItemKind.Directory:
                LoadAllArchives(item);
                break;
            case ExplorerItemKind.RezArchive:
            case ExplorerItemKind.RezDirectory:
                LoadContainerChildren(item);
                LoadAllRezDirectories(item);
                break;
        }
    }

    private static void LoadAllArchives(ExplorerItem rootItem)
    {
        var archives = new List<ExplorerItem>();
        CollectArchiveItems(rootItem, archives);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        };

        Parallel.ForEach(archives, options, archive =>
        {
            LoadContainerChildren(archive);
            LoadAllRezDirectories(archive);
        });
    }

    private static void CollectArchiveItems(ExplorerItem item, List<ExplorerItem> archives)
    {
        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            archives.Add(item);
            return;
        }

        foreach (ExplorerItem child in item.Children)
        {
            CollectArchiveItems(child, archives);
        }
    }

    private static void LoadAllRezDirectories(ExplorerItem item)
    {
        foreach (ExplorerItem child in item.Children.ToArray())
        {
            if (child.Kind == ExplorerItemKind.RezDirectory)
            {
                LoadContainerChildren(child);
                LoadAllRezDirectories(child);
            }
        }
    }

    private static void LoadContainerChildren(ExplorerItem item)
    {
        if (item.IsLoaded)
        {
            return;
        }

        if (item.Kind == ExplorerItemKind.RezArchive)
        {
            var reader = new RezArchiveReader();
            RezArchive archive = reader.Read(item.SourcePath);
            item.Archive = archive;
            item.ArchiveDirectory = archive.Root;
        }

        if (item.Archive is null || item.ArchiveDirectory is null)
        {
            item.IsLoaded = true;
            return;
        }

        item.Children.Clear();
        foreach (RezNode child in item.ArchiveDirectory.Children)
        {
            item.AddChild(CreateArchiveChildItem(child, item.Archive, item.OutputRelativePath));
        }

        item.SortChildren();
        item.IsLoaded = true;
    }

    private static ExtractionBatchResult ExtractFilesParallel(
        string outputDirectory,
        IReadOnlyList<ExtractionJob> jobs,
        int workerCount,
        IProgress<ExtractionProgress> progress)
    {
        foreach (string directory in CollectOutputDirectories(jobs))
        {
            Directory.CreateDirectory(Path.Combine(outputDirectory, directory));
        }

        int completed = 0;
        int decodedImageCount = 0;
        int decodeFallbackCount = 0;
        long nextReportAt = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = workerCount };

        Parallel.ForEach(jobs, options, job =>
        {
            ExplorerItem item = job.Item;
            string destinationPath = Path.Combine(outputDirectory, job.OutputRelativePath);
            if (job.DecodeImageToPng)
            {
                byte[] data = ReadExplorerFileBytes(item, int.MaxValue);
                if (DecodedImageExporter.TryWritePng(data, item.FileExtension, destinationPath))
                {
                    Interlocked.Increment(ref decodedImageCount);
                }
                else
                {
                    destinationPath = Path.Combine(outputDirectory, job.SourceRelativePath);
                    WriteSourceFile(item, destinationPath);
                    Interlocked.Increment(ref decodeFallbackCount);
                }
            }
            else
            {
                WriteSourceFile(item, destinationPath);
            }

            int done = Interlocked.Increment(ref completed);
            if (done == jobs.Count || ShouldReportProgress(ref nextReportAt))
            {
                progress.Report(new ExtractionProgress(done, jobs.Count, item.Name));
            }
        });

        return new ExtractionBatchResult(decodedImageCount, decodeFallbackCount);
    }

    private static void WriteSourceFile(ExplorerItem item, string destinationPath)
    {
        if (item.Kind == ExplorerItemKind.LocalFile)
        {
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(item.SourcePath, destinationPath, overwrite: true);
            return;
        }

        RezArchiveReader.ExtractFile(item.Archive!, item.ArchiveFile!, destinationPath);
    }

    private static bool ShouldReportProgress(ref long nextReportAt)
    {
        long now = Environment.TickCount64;
        long due = Volatile.Read(ref nextReportAt);
        return now >= due && Interlocked.CompareExchange(ref nextReportAt, now + 80, due) == due;
    }

    private static int GetExtractionWorkerCount(int fileCount)
    {
        if (fileCount <= 1)
        {
            return 1;
        }

        return Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    private static IEnumerable<string> CollectOutputDirectories(IEnumerable<ExtractionJob> jobs)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExtractionJob job in jobs)
        {
            string? directory = Path.GetDirectoryName(job.OutputRelativePath);
            if (!string.IsNullOrEmpty(directory))
            {
                directories.Add(directory);
            }

            string? sourceDirectory = Path.GetDirectoryName(job.SourceRelativePath);
            if (!string.IsNullOrEmpty(sourceDirectory))
            {
                directories.Add(sourceDirectory);
            }
        }

        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CountItems(ExplorerItem item, ExplorerItemKind kind)
    {
        int count = item.Kind == kind ? 1 : 0;
        foreach (ExplorerItem child in item.Children)
        {
            count += CountItems(child, kind);
        }

        return count;
    }

    private void RenderBreadcrumb(ExplorerItem item)
    {
        BreadcrumbPanel.Children.Clear();

        var path = new Stack<ExplorerItem>();
        ExplorerItem? cursor = item;
        while (cursor is not null)
        {
            path.Push(cursor);
            cursor = cursor.Parent;
        }

        bool first = true;
        foreach (ExplorerItem part in path)
        {
            if (!first)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = ">",
                    Foreground = (System.Windows.Media.Brush)FindResource("AppSubtleTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0)
                });
            }

            var button = new System.Windows.Controls.Button
            {
                Content = part.Name,
                Tag = part,
                Style = (Style)FindResource("BreadcrumbButtonStyle")
            };
            button.Click += BreadcrumbButton_Click;
            BreadcrumbPanel.Children.Add(button);
            first = false;
        }
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(parts.Select(SanitizePathSegment).ToArray());
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    private static string CombineRelativePath(string parent, string child)
    {
        return string.IsNullOrEmpty(parent) ? child : Path.Combine(parent, child);
    }

    private void ShowError(string titleKey, Exception ex)
    {
        string title = T(titleKey);
        SetStatus("ErrorStatus", title, ex.Message);
        System.Windows.MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LoadEmptyStateImage()
    {
        string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "empty.png");
        if (File.Exists(imagePath))
        {
            EmptyStateImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
        }
    }
}
