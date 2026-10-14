using System.Diagnostics;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireLtcTextDocument(
    string Text,
    string EncodingName,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount);

internal static class CrossFireLtcDecoder
{
    private const int ExternalConverterTimeoutMilliseconds = 15_000;

    // CrossFire stores standard LithTech LTC with a small repeating XOR layer.
    private static readonly byte[] CrossFireLtcMagic = [0x54, 0x83, 0xB2, 0xE1];
    private static readonly byte[] CrossFireLtcXorKey =
    [
        0x54, 0x83, 0xB2, 0xE1,
        0x10, 0x3F, 0x6E, 0x9D,
        0xCC, 0xFB, 0x2A, 0x59,
        0x88, 0xB7, 0xE6, 0x15
    ];

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "ltc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasCrossFireMagic(byte[] data)
    {
        return HasPrefix(data, CrossFireLtcMagic);
    }

    public static string GetUnsupportedMessage(string? converterError)
    {
        if (!string.IsNullOrWhiteSpace(converterError))
        {
            return converterError;
        }

        return LocalizedText.T("CrossFireLtcUnsupported");
    }

    public static bool TryUnlockCrossFirePayload(byte[] data, out byte[] unlocked)
    {
        unlocked = data;
        if (!HasCrossFireMagic(data))
        {
            return false;
        }

        unlocked = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            unlocked[i] = (byte)(data[i] ^ CrossFireLtcXorKey[i & 15]);
        }

        return true;
    }

    public static bool TryDecodeText(
        byte[] data,
        string fallbackName,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is not null &&
            TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string directText, out string directEncoding) &&
            LooksLikeStructuredText(directText))
        {
            string directStorage = ReferenceEquals(prepared, data) ? "LTC text" : "LZMA-compressed LTC";
            document = new CrossFireLtcTextDocument(directText, directEncoding, directStorage, data.Length, prepared.Length);
            return true;
        }

        if (TryConvertToText(data, fallbackName, out document, out errorMessage))
        {
            return true;
        }

        if (HasCrossFireMagic(data))
        {
            errorMessage = GetUnsupportedMessage(errorMessage);
            return false;
        }

        errorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? LocalizedText.T("LtcNotRecognized")
            : errorMessage;
        return false;
    }

    /// <summary>
    /// Decodes LTC through the in-process paths only. Mapping-index construction
    /// uses this to avoid launching a user-configured external converter while
    /// walking thousands of mounted resources.
    /// </summary>
    public static bool TryDecodeNativeText(
        byte[] data,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is not null &&
            TextPreviewDecoder.TryDecode(prepared, preferKorean: false, out string directText, out string directEncoding) &&
            LooksLikeStructuredText(directText))
        {
            string storageDescription = ReferenceEquals(prepared, data) ? "LTC text" : "LZMA-compressed LTC";
            document = new CrossFireLtcTextDocument(directText, directEncoding, storageDescription, data.Length, prepared.Length);
            return true;
        }

        bool isCrossFireLocked = TryUnlockCrossFirePayload(data, out byte[] converterInput);
        return TryDecodeNativeToText(converterInput, data.Length, isCrossFireLocked, out document, out errorMessage) &&
               document is not null;
    }

    public static bool TryConvertToText(
        byte[] ltcData,
        string fallbackName,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        bool isCrossFireLocked = TryUnlockCrossFirePayload(ltcData, out byte[] converterInput);
        if (TryDecodeNativeToText(converterInput, ltcData.Length, isCrossFireLocked, out document, out string? nativeError) &&
            document is not null)
        {
            return true;
        }

        string? converterPath = ResolveLtcConverterPath();
        if (converterPath is null)
        {
            errorMessage = string.IsNullOrWhiteSpace(nativeError)
                ? LocalizedText.T("LtcNativeAndConverterMissing")
                : nativeError;
            return false;
        }

        string workingDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "LtcConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        string baseName = Path.GetFileNameWithoutExtension(fallbackName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        string inputPath = Path.Combine(workingDirectory, $"{baseName}.ltc");
        string outputPath = Path.Combine(workingDirectory, $"{baseName}.lta");
        File.WriteAllBytes(inputPath, converterInput);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = converterPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            AddConverterArguments(startInfo, converterPath, inputPath, outputPath);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = LocalizedText.Format("LtcConverterStartFailed", converterPath);
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ExternalConverterTimeoutMilliseconds))
            {
                TryKillProcess(process);
                errorMessage = LocalizedText.T("LtcConverterTimeout");
                return false;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                errorMessage = string.IsNullOrWhiteSpace(detail)
                    ? LocalizedText.Format("LtcConverterFailedExitCode", process.ExitCode)
                    : detail.Trim();
                return false;
            }

            byte[] convertedBytes = File.ReadAllBytes(outputPath);
            if (!TextPreviewDecoder.TryDecode(convertedBytes, preferKorean: isCrossFireLocked, out string text, out string encodingName))
            {
                errorMessage = LocalizedText.T("LtcConverterOutputEncodingUnrecognized");
                return false;
            }

            string storageDescription = isCrossFireLocked ? "CrossFire LTC XOR -> LTA" : "LTC -> LTA";
            document = new CrossFireLtcTextDocument(
                text,
                encodingName,
                storageDescription,
                ltcData.Length,
                convertedBytes.Length);
            return true;
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static bool TryDecodeNativeToText(
        byte[] converterInput,
        int sourceByteCount,
        bool isCrossFireLocked,
        out CrossFireLtcTextDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!LithTechLtcNativeDecoder.TryDecode(converterInput, out byte[] convertedBytes, out string? nativeError))
        {
            errorMessage = nativeError;
            return false;
        }

        if (!TextPreviewDecoder.TryDecode(convertedBytes, preferKorean: isCrossFireLocked, out string text, out string encodingName) ||
            !LooksLikeStructuredText(text))
        {
            errorMessage = LocalizedText.T("LtcNativeOutputNotLta");
            return false;
        }

        string storageDescription = isCrossFireLocked ? "CrossFire LTC native -> LTA" : "LTC native -> LTA";
        document = new CrossFireLtcTextDocument(
            text,
            encodingName,
            storageDescription,
            sourceByteCount,
            convertedBytes.Length);
        return true;
    }

    private static string? ResolveLtcConverterPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CFREZ_LTC_TO_LTA");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] relativeCandidates =
        [
            Path.Combine("tools", "ltc_to_lta.exe"),
            Path.Combine("tools", "ltc_to_lta.cmd"),
            Path.Combine("tools", "ltc_to_lta.bat"),
            Path.Combine("tools", "LTC.exe"),
            Path.Combine("tools", "CFLTC_Converter.exe"),
            Path.Combine("tools", "guao_ltc.exe"),
            Path.Combine("tools", "WinLTC.exe"),
            "LTC.exe"
        ];

        foreach (string root in EnumerateToolSearchRoots())
        {
            foreach (string relativeCandidate in relativeCandidates)
            {
                string candidate = Path.Combine(root, relativeCandidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateToolSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in EnumerateRootAndParents(AppContext.BaseDirectory))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }

        foreach (string root in EnumerateRootAndParents(Environment.CurrentDirectory))
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<string> EnumerateRootAndParents(string startPath)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startPath));
        }
        catch
        {
            yield break;
        }

        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }

    private static void AddConverterArguments(ProcessStartInfo startInfo, string converterPath, string inputPath, string outputPath)
    {
        string fileName = Path.GetFileName(converterPath);
        if (string.Equals(fileName, "LTC.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "WinLTC.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-out");
            startInfo.ArgumentList.Add(outputPath);
            return;
        }

        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);
    }

    private static bool HasPrefix(byte[] data, byte[] prefix)
    {
        return data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private static bool LooksLikeStructuredText(string text)
    {
        ReadOnlySpan<char> sample = text.AsSpan(0, Math.Min(text.Length, 4096));
        if (sample.IsEmpty)
        {
            return true;
        }

        int printableText = 0;
        int structural = 0;
        foreach (char ch in sample)
        {
            // CrossFire LTC often contains Korean comments/config values. The
            // selected decoder may correctly return CP949 Unicode, so counting
            // only ASCII incorrectly rejects fully decoded structured text.
            if (!char.IsControl(ch) || ch is '\r' or '\n' or '\t')
            {
                printableText++;
            }

            if (ch is '\r' or '\n' or '(' or ')' or '[' or ']' or '{' or '}' or '=' or ':' or '_' or '"' or '\'')
            {
                structural++;
            }
        }

        return printableText >= sample.Length * 85 / 100 && structural > 0;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // External converters may already have exited after the timeout check.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary conversion files are best-effort cleanup only.
        }
    }
}
