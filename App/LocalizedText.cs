using System.Globalization;

namespace CFRezManager;

internal static class LocalizedText
{
    private static bool _useEnglish;

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string Chinese, string English)>
        {
            ["PreviewFailedTitle"] = ("预览失败", "Preview failed"),
            ["PreviewUnsupportedFile"] = ("无法预览此文件。", "Cannot preview this file."),
            ["PreviewUnsupportedFileName"] = ("无法预览此文件: {0}", "Cannot preview this file: {0}"),
            ["PreviewModelDecodeFailedFileName"] = ("无法解码模型: {0}", "Cannot decode model: {0}"),
            ["PreviewItemNotFile"] = ("此项无法作为文件预览。", "This item cannot be previewed as a file."),
            ["PreviewFileTooLarge"] = ("文件过大，无法预览: {0}", "File is too large to preview: {0}"),
            ["PreviewToolExecutableMissing"] = ("无法定位预览工具可执行文件。", "Cannot locate the preview tool executable."),
            ["PreviewPrevious"] = ("\u4e0a\u4e00\u5f20", "Prev"),
            ["PreviewNext"] = ("\u4e0b\u4e00\u5f20", "Next"),
            ["PreviewPause"] = ("\u6682\u505c", "Pause"),
            ["PreviewPlay"] = ("\u64ad\u653e", "Play"),
            ["PreviewFrame"] = ("\u5e27", "Frame"),
            ["CftExportXlsx"] = ("导出 XLSX...", "Export XLSX..."),
            ["CftXlsxFilter"] = ("Excel 工作簿 (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            ["CftTableSize"] = ("{0:N0} 行 · {1:N0} 列", "{0:N0} rows · {1:N0} columns"),
            ["CftExportComplete"] = ("已导出 {0:N0} 行: {1}", "Exported {0:N0} rows: {1}"),
            ["CftExportFailed"] = ("表格导出失败", "Table export failed"),

            ["ModelOuterCompressionFailed"] = ("无法解开模型外层压缩。", "Could not decompress the model wrapper."),
            ["ModelLtcNotRecognized"] = ("LTC 不是可直接识别的 LTA 文本/压缩文本，也未能用内置 LTC 解码器还原为 LTA。可配置 CFREZ_LTC_TO_LTA 作为外部兜底。", "The LTC is not directly recognizable LTA text/compressed text, and the built-in LTC decoder could not restore it to LTA. Configure CFREZ_LTC_TO_LTA as an external fallback."),
            ["LtbTooShort"] = ("LTB 文件太短，无法读取文件头。", "The LTB file is too short to read the header."),
            ["LtbHeaderIncompleteMeshCount"] = ("LTB 文件头不完整，无法读取 mesh 数量。", "The LTB header is incomplete; mesh count cannot be read."),
            ["LtbInvalidMeshCount"] = ("LTB 不是可识别的模型数据，mesh 数量异常: {0}。", "The LTB is not recognizable model data; mesh count is invalid: {0}."),
            ["LtbNoPreviewMesh"] = ("LTB 中没有可预览的 mesh 几何数据。", "The LTB does not contain previewable mesh geometry."),
            ["LtbMeshNameIncomplete"] = ("LTB mesh #{0} 名称数据不完整。", "LTB mesh #{0} has incomplete name data."),
            ["LtbMeshHeaderIncomplete"] = ("LTB mesh '{0}' 头部不完整。", "LTB mesh '{0}' has an incomplete header."),
            ["LtbUnsupportedMeshType"] = ("LTB mesh '{0}' 使用了暂不支持的类型: {1}。", "LTB mesh '{0}' uses an unsupported type: {1}."),
            ["LtbMeshVertexIncomplete"] = ("LTB mesh '{0}' 顶点 #{1} 数据不完整。", "LTB mesh '{0}' vertex #{1} has incomplete data."),
            ["LtbMeshIndexDataIncomplete"] = ("LTB mesh '{0}' 索引数据不完整。", "LTB mesh '{0}' has incomplete index data."),
            ["LtbMeshIndexOutOfRange"] = ("LTB mesh '{0}' 索引 {1} 超出顶点范围。", "LTB mesh '{0}' index {1} is outside the vertex range."),
            ["LtbMeshVertexDataIncomplete"] = ("LTB mesh 顶点数据不完整。", "LTB mesh vertex data is incomplete."),
            ["LtbMeshTypeDataIncomplete"] = ("LTB mesh 类型数据不完整。", "LTB mesh type data is incomplete."),
            ["LtbMeshGeometryOutOfRange"] = ("LTB mesh 几何数据越界。", "LTB mesh geometry data is out of range."),
            ["LtbMeshIndexOutOfRangeGeneric"] = ("LTB mesh 索引超出顶点范围。", "LTB mesh index is outside the vertex range."),
            ["LtbMeshTrailingDataIncomplete"] = ("LTB mesh 后置数据不完整。", "LTB mesh trailing data is incomplete."),
            ["LtbMeshTrailingAlignmentFailed"] = ("LTB mesh 后置数据未能对齐到下一个 mesh。", "LTB mesh trailing data could not align to the next mesh."),
            ["LtbConverterMissing"] = ("缺少 LTB 转换器。请把 Model_Unpacker.exe 放到程序目录的 tools 文件夹，或设置 CFREZ_MODEL_UNPACKER 环境变量。", "Missing LTB converter. Place Model_Unpacker.exe in the program tools folder, or set the CFREZ_MODEL_UNPACKER environment variable."),
            ["ModelUnpackerStartFailed"] = ("无法启动 Model_Unpacker.exe。", "Could not start Model_Unpacker.exe."),
            ["ModelUnpackerTimeout"] = ("Model_Unpacker.exe 转换超时。", "Model_Unpacker.exe conversion timed out."),
            ["ModelUnpackerFailedExitCode"] = ("Model_Unpacker.exe 转换失败，退出码 {0}。", "Model_Unpacker.exe conversion failed with exit code {0}."),

            ["CrossFireLtcUnsupported"] = ("检测到 CrossFire LTC 头 54 83 B2 E1。外层 XOR 密钥已内置，解锁后会优先使用内置 LithTech LTC 解码器；如果仍失败，可在 tools 目录放置 LTC.exe，或设置 CFREZ_LTC_TO_LTA 指向可用的 LTC->LTA 转换器。", "Detected CrossFire LTC header 54 83 B2 E1. The outer XOR key is built in; after unlocking, the built-in LithTech LTC decoder is used first. If it still fails, place LTC.exe in the tools folder or set CFREZ_LTC_TO_LTA to a usable LTC-to-LTA converter."),
            ["LtcNotRecognized"] = ("LTC 不是可直接识别的文本/压缩文本，也未能用内置 LTC 解码器还原为 LTA。可配置 CFREZ_LTC_TO_LTA 作为外部兜底。", "The LTC is not directly recognizable text/compressed text, and the built-in LTC decoder could not restore it to LTA. Configure CFREZ_LTC_TO_LTA as an external fallback."),
            ["LtcNativeAndConverterMissing"] = ("内置 LTC 解码失败，且未找到外部 LTC->LTA 转换器。", "The built-in LTC decoder failed, and no external LTC-to-LTA converter was found."),
            ["LtcConverterStartFailed"] = ("无法启动 LTC 转换器: {0}", "Could not start LTC converter: {0}"),
            ["LtcConverterTimeout"] = ("LTC 转换器转换超时。请检查 CFREZ_LTC_TO_LTA 或 tools\\ltc_to_lta.cmd 的命令行参数。", "LTC converter timed out. Check the command-line arguments for CFREZ_LTC_TO_LTA or tools\\ltc_to_lta.cmd."),
            ["LtcConverterFailedExitCode"] = ("LTC 转换器转换失败，退出码 {0}。", "LTC converter failed with exit code {0}."),
            ["LtcConverterOutputEncodingUnrecognized"] = ("LTC 转换器已输出文件，但文本编码无法识别。", "The LTC converter produced a file, but its text encoding could not be recognized."),
            ["LtcNativeOutputNotLta"] = ("内置 LTC 解码器已输出数据，但结果不是可识别的 LTA 文本。", "The built-in LTC decoder produced data, but the result is not recognizable LTA text."),
            ["LtcDataTooShort"] = ("LTC 数据太短。", "LTC data is too short."),
            ["LtcHeaderInvalid"] = ("LTC 头不是标准 LithTech 压缩流。", "The LTC header is not a standard LithTech compressed stream."),
            ["LtcLiteralIncomplete"] = ("LTC literal 数据不完整。", "LTC literal data is incomplete."),
            ["LtcMatchLengthIncomplete"] = ("LTC match 长度数据不完整。", "LTC match length data is incomplete."),
            ["LtcNoEndMarker"] = ("LTC 数据没有结束标记。", "LTC data has no end marker."),
            ["LtcDecodedTooLarge"] = ("LTC 解码结果过大。", "The decoded LTC result is too large.")
        };

    public static event EventHandler? LanguageChanged;

    public static void SetLanguage(string? languageCode)
    {
        bool useEnglish = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase);
        if (_useEnglish == useEnglish)
        {
            return;
        }

        _useEnglish = useEnglish;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void UseSavedLanguage()
    {
        SetLanguage(UserSettings.Load().Language);
    }

    public static string T(string key)
    {
        return Texts.TryGetValue(key, out (string Chinese, string English) text)
            ? _useEnglish ? text.English : text.Chinese
            : key;
    }

    public static string Format(string key, params object[] args)
    {
        return args.Length == 0
            ? T(key)
            : string.Format(CultureInfo.CurrentCulture, T(key), args);
    }
}
