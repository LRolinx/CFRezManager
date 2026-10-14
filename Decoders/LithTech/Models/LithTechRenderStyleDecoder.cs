using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record LithTechRenderStyleDocument(
    ushort FileType,
    ushort Version,
    uint DeclaredStyleBytes,
    IReadOnlyList<LithTechRenderStyle> Styles,
    bool UseEffectShader,
    int EffectShaderId,
    int SourceByteCount,
    int DecodedByteCount,
    string StorageDescription)
{
    public string ToText(string fileName)
    {
        var text = new StringBuilder();
        text.AppendLine("LithTech packed RenderStyle");
        text.AppendLine($"File: {fileName}");
        text.AppendLine($"Storage: {StorageDescription}");
        text.AppendLine($"Header: type=0x{FileType:X4}, version={Version}");
        text.AppendLine($"Styles: {Styles.Count}, declared style payload bytes: {DeclaredStyleBytes:N0}");
        text.AppendLine($"D3D effect shader: enabled={UseEffectShader}, id={EffectShaderId}");
        text.AppendLine($"Source/decoded bytes: {SourceByteCount:N0} / {DecodedByteCount:N0}");

        for (int styleIndex = 0; styleIndex < Styles.Count; styleIndex++)
        {
            LithTechRenderStyle style = Styles[styleIndex];
            text.AppendLine();
            text.AppendLine($"Style {styleIndex}: recordBytes={style.RecordBytes}, recordTag={style.RecordTag}, passes={style.Passes.Count}");
            text.AppendLine($"  LightingMaterial (RGBA): ambient={FormatFloats(style.LightingMaterial.Take(4))}; diffuse={FormatFloats(style.LightingMaterial.Skip(4).Take(4))}; emissive={FormatFloats(style.LightingMaterial.Skip(8).Take(4))}; specular={FormatFloats(style.LightingMaterial.Skip(12).Take(4))}; specularPower={FormatFloat(style.LightingMaterial[16])}");

            for (int passIndex = 0; passIndex < style.Passes.Count; passIndex++)
            {
                LithTechRenderPass pass = style.Passes[passIndex];
                string alphaReference = Version == 2
                    ? $"{FormatFloat(BitConverter.Int32BitsToSingle(unchecked((int)pass.AlphaRef)))} (raw 0x{pass.AlphaRef:X8})"
                    : pass.AlphaRef.ToString(CultureInfo.InvariantCulture);
                string zBufferTestMode = pass.HasZBufferTestMode
                    ? pass.ZBufferTestMode.ToString(CultureInfo.InvariantCulture)
                    : "not serialized in this version";
                text.AppendLine($"  Pass {passIndex}: blend={pass.BlendMode}, zBuffer={pass.ZBufferMode}, cull={pass.CullMode}, textureFactor=0x{pass.TextureFactor:X8}, alphaRef={alphaReference}, dynamicLight={pass.DynamicLight}, zTest={zBufferTestMode}, alphaTest={pass.AlphaTestMode}, fill={pass.FillMode}, bumpEnv={pass.UseBumpEnvironmentMap}/{pass.BumpEnvironmentMapStage} scale={FormatFloat(pass.BumpEnvironmentMapScale)} offset={FormatFloat(pass.BumpEnvironmentMapOffset)}");

                for (int stageIndex = 0; stageIndex < pass.TextureStages.Count; stageIndex++)
                {
                    LithTechTextureStage stage = pass.TextureStages[stageIndex];
                    string coordinateCount = stage.HasTextureCoordinateCount ? stage.TextureCoordinateCount.ToString(CultureInfo.InvariantCulture) : "not serialized in this version";
                    text.AppendLine($"    Texture stage {stageIndex}: textureParam={stage.TextureParam}, colorOp={stage.ColorOp}({stage.ColorArg1},{stage.ColorArg2}), alphaOp={stage.AlphaOp}({stage.AlphaArg1},{stage.AlphaArg2}), uvSource={stage.UvSource}, address=({stage.UAddress},{stage.VAddress}), filter={stage.TextureFilter}, uvTransform={stage.UvTransformEnabled}, project={stage.ProjectTextureCoordinates}, coordCount={coordinateCount}");
                    text.AppendLine($"      uvMatrix=[{FormatFloats(stage.UvTransformMatrix)}]");
                }

                text.AppendLine($"    D3D shaders: present={pass.HasD3dOptions}, vertex={pass.UseVertexShader}/{pass.VertexShaderId}, pixel={pass.UsePixelShader}/{pass.PixelShaderId}");
            }
        }

        return text.ToString();
    }

    private static string FormatFloats(IEnumerable<float> values) =>
        string.Join(", ", values.Select(FormatFloat));

    private static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed record LithTechRenderStyleShaderExtensionDocument(string Text, int ShaderAssemblyBlockCount);

internal sealed record LithTechLegacyShaderOptions(
    bool UseVertexShader,
    bool ExpandForSkinning,
    IReadOnlyList<int> ConstantVectorRegisters,
    IReadOnlyList<IReadOnlyList<float>> ConstantVectorValues,
    IReadOnlyList<int> RenderStateRegisters,
    int LastConstantRegister,
    IReadOnlyList<bool> PositionStreams,
    IReadOnlyList<bool> NormalStreams,
    IReadOnlyList<bool> UvSetStreams,
    IReadOnlyList<bool> BasisVectorStreams,
    IReadOnlyList<uint> UvCounts);

internal sealed record LithTechRenderStyle(
    uint RecordBytes,
    uint RecordTag,
    IReadOnlyList<float> LightingMaterial,
    IReadOnlyList<LithTechRenderPass> Passes);

internal sealed record LithTechRenderPass(
    IReadOnlyList<LithTechTextureStage> TextureStages,
    int BlendMode,
    int ZBufferMode,
    int CullMode,
    uint TextureFactor,
    uint AlphaRef,
    bool DynamicLight,
    int ZBufferTestMode,
    bool HasZBufferTestMode,
    int AlphaTestMode,
    int FillMode,
    bool UseBumpEnvironmentMap,
    uint BumpEnvironmentMapStage,
    float BumpEnvironmentMapScale,
    float BumpEnvironmentMapOffset,
    bool HasD3dOptions,
    bool UseVertexShader,
    int VertexShaderId,
    bool UsePixelShader,
    int PixelShaderId);

internal sealed record LithTechTextureStage(
    int TextureParam,
    int ColorOp,
    int ColorArg1,
    int ColorArg2,
    int AlphaOp,
    int AlphaArg1,
    int AlphaArg2,
    int UvSource,
    int UAddress,
    int VAddress,
    int TextureFilter,
    bool UvTransformEnabled,
    IReadOnlyList<float> UvTransformMatrix,
    bool ProjectTextureCoordinates,
    bool HasTextureCoordinateCount,
    uint TextureCoordinateCount);

internal static class LithTechRenderStyleDecoder
{
    private const ushort SupportedVersion = 3;
    private const ushort LegacySupportedVersion = 2;
    private const int HeaderSize = 20;
    private const int RenderPassBytes = 532;
    private const int MaximumStyles = 32;
    private const int MaximumStylePayloadBytes = 1_000_000;
    private static readonly byte[] ShaderAssemblySignature = Encoding.ASCII.GetBytes("D3DX8 Shader Assembler Version 0.91");
    private static readonly byte[] ShaderAssemblyBlockPreamble = [0x01, 0x01, 0xFE, 0xFF, 0xFE, 0xFF, 0x09, 0x00];

    public static bool IsCandidate(string fileName, string extension)
    {
        if (!string.Equals(extension.TrimStart('.'), "ltb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalizedPath = fileName.Replace('/', '\\');
        string[] segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string stem = Path.GetFileNameWithoutExtension(normalizedPath);
        return segments.Any(segment => string.Equals(segment, "RS", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(segment, "RENDERSTYLES", StringComparison.OrdinalIgnoreCase)) ||
               stem.Equals("ADDITIVE", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NINJATRANSLUCENT", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("TRANSLUCENT_NODIFFUSE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(
        byte[] data,
        string fileName,
        string storageDescription,
        int sourceByteCount,
        out LithTechRenderStyleDocument? document,
        out string? errorMessage)
    {
        return TryDecode(data.AsSpan(), fileName, storageDescription, sourceByteCount, out document, out errorMessage);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        string fileName,
        string storageDescription,
        int sourceByteCount,
        out LithTechRenderStyleDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (data.Length < HeaderSize + 16 ||
            !TryReadUInt16(data, 0, out ushort fileType) ||
            (fileType & 0x00FF) != 0x05 ||
            !TryReadUInt16(data, 2, out ushort version) ||
            version is not (SupportedVersion or LegacySupportedVersion))
        {
            errorMessage = "Packed LithTech RenderStyle header is unsupported.";
            return false;
        }

        if (!TryReadUInt32(data, HeaderSize, out uint declaredStyleBytes) ||
            declaredStyleBytes > MaximumStylePayloadBytes ||
            !TryReadUInt32(data, HeaderSize + sizeof(uint), out uint styleCount) ||
            styleCount > MaximumStyles)
        {
            errorMessage = "Packed LithTech RenderStyle size or style count is invalid.";
            return false;
        }

        int position = HeaderSize + sizeof(uint) * 2;
        ulong parsedStyleBytes = 0;
        var styles = new List<LithTechRenderStyle>((int)styleCount);
        for (uint styleIndex = 0; styleIndex < styleCount; styleIndex++)
        {
            if (!TryReadUInt32(data, position, out uint recordBytes) || recordBytes > MaximumStylePayloadBytes)
            {
                errorMessage = $"RenderStyle record {styleIndex} has an invalid size.";
                return false;
            }

            position += sizeof(uint);
            int recordEnd;
            try
            {
                recordEnd = checked(position + (int)recordBytes);
            }
            catch (OverflowException)
            {
                errorMessage = $"RenderStyle record {styleIndex} size overflows the file bounds.";
                return false;
            }

            uint recordTag = 0;
            if (version == LegacySupportedVersion)
            {
                if (!TryReadUInt32(data, position, out recordTag))
                {
                    errorMessage = $"Legacy RenderStyle record {styleIndex} is missing its record tag.";
                    return false;
                }

                position += sizeof(uint);
            }

            if (recordEnd > data.Length || !TryReadLightingMaterial(data, ref position, recordEnd, out List<float> lighting) ||
                !TryReadUInt32(data, position, out uint passCount) || passCount > 4)
            {
                errorMessage = $"RenderStyle record {styleIndex} has incomplete lighting or pass data.";
                return false;
            }

            position += sizeof(uint);
            var passes = new List<LithTechRenderPass>((int)passCount);
            for (uint passIndex = 0; passIndex < passCount; passIndex++)
            {
                LithTechRenderPass pass;
                bool passDecoded = version == LegacySupportedVersion
                    ? TryReadLegacyRenderPass(data, ref position, recordEnd, out pass)
                    : TryReadRenderPass(data, ref position, recordEnd, out pass);
                if (!passDecoded)
                {
                    errorMessage = $"RenderStyle record {styleIndex}, pass {passIndex} is truncated or invalid at byte {position}.";
                    return false;
                }

                passes.Add(pass);
            }

            if (position != recordEnd)
            {
                errorMessage = $"RenderStyle record {styleIndex} size does not match its decoded fields.";
                return false;
            }

            parsedStyleBytes += recordBytes;
            styles.Add(new LithTechRenderStyle(recordBytes, recordTag, lighting, passes));
        }

        bool useEffectShader = false;
        int effectShaderId = 0;
        bool hasTrailingD3dOptions = data.Length - position == 8;
        if (parsedStyleBytes != declaredStyleBytes ||
            (data.Length != position && !hasTrailingD3dOptions) ||
            (hasTrailingD3dOptions &&
             (!TryReadBoolWithPadding(data, ref position, data.Length, out useEffectShader) ||
              !TryReadInt32(data, ref position, data.Length, out effectShaderId))) ||
            position != data.Length)
        {
            errorMessage = "Packed LithTech RenderStyle payload size or trailing D3D options are inconsistent.";
            return false;
        }

        document = new LithTechRenderStyleDocument(
            fileType,
            version,
            declaredStyleBytes,
            styles,
            useEffectShader,
            effectShaderId,
            sourceByteCount,
            data.Length,
            storageDescription);
        return true;
    }

    public static bool TryDecodeShaderAssemblyExtension(
        byte[] data,
        string fileName,
        string storageDescription,
        int sourceByteCount,
        out LithTechRenderStyleShaderExtensionDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;
        ReadOnlySpan<byte> bytes = data;
        if (bytes.Length < HeaderSize + sizeof(uint) * 3 ||
            !TryReadUInt16(bytes, 0, out ushort fileType) ||
            (fileType & 0x00FF) != 0x05 ||
            !TryReadUInt16(bytes, 2, out ushort version) ||
            version is not (SupportedVersion or LegacySupportedVersion) ||
            !TryReadUInt32(bytes, HeaderSize, out uint declaredStyleBytes) ||
            declaredStyleBytes > MaximumStylePayloadBytes ||
            !TryReadUInt32(bytes, HeaderSize + sizeof(uint), out uint styleCount) ||
            styleCount != 1 ||
            !TryReadUInt32(bytes, HeaderSize + sizeof(uint) * 2, out uint recordBytes))
        {
            errorMessage = "Shader-bearing RenderStyle header or record dimensions are unsupported.";
            return false;
        }

        int recordStart = HeaderSize + sizeof(uint) * 3;
        long recordEndLong = (long)recordStart + recordBytes;
        if (recordBytes != declaredStyleBytes || recordEndLong != bytes.Length)
        {
            errorMessage = "Shader-bearing RenderStyle record size does not exactly cover the decoded file.";
            return false;
        }

        int recordEnd = (int)recordEndLong;
        int cursor = recordStart;
        if (version == LegacySupportedVersion)
        {
            if (!TryReadUInt32(bytes, cursor, out _))
            {
                errorMessage = "Legacy shader-bearing RenderStyle has no record tag.";
                return false;
            }

            cursor += sizeof(uint);
        }

        if (!TryReadLightingMaterial(bytes, ref cursor, recordEnd, out List<float> lighting) ||
            !TryReadUInt32(bytes, cursor, out uint passCount) || passCount is 0 or > 4)
        {
            errorMessage = "Shader-bearing RenderStyle has no valid lighting material or pass table.";
            return false;
        }

        cursor += sizeof(uint);
        int passDataStart = cursor;

        var blocks = new List<(int Start, int End, uint DeclaredBytes, int ShaderOffset)>();
        int searchOffset = recordStart;
        while (searchOffset < recordEnd)
        {
            int relativeMarker = bytes[searchOffset..recordEnd].IndexOf(ShaderAssemblySignature);
            if (relativeMarker < 0)
            {
                break;
            }

            int markerOffset = searchOffset + relativeMarker;
            int blockStart = markerOffset - sizeof(uint) - ShaderAssemblyBlockPreamble.Length;
            if (blockStart < recordStart ||
                !TryReadUInt32(bytes, blockStart, out uint blockBytes) ||
                blockBytes < ShaderAssemblyBlockPreamble.Length + ShaderAssemblySignature.Length ||
                blockBytes > MaximumStylePayloadBytes ||
                !bytes.Slice(blockStart + sizeof(uint), ShaderAssemblyBlockPreamble.Length).SequenceEqual(ShaderAssemblyBlockPreamble))
            {
                errorMessage = $"Shader Assembly marker at byte {markerOffset} has no valid length-prefixed block header.";
                return false;
            }

            long blockEndLong = (long)blockStart + sizeof(uint) + blockBytes;
            int shaderOffset = markerOffset + ShaderAssemblySignature.Length;
            if (blockEndLong > recordEnd || shaderOffset > blockEndLong)
            {
                errorMessage = $"Shader Assembly block at byte {blockStart} exceeds its RenderStyle record.";
                return false;
            }

            int blockEnd = (int)blockEndLong;
            if (blocks.Count > 0 && blocks[^1].End > blockStart)
            {
                errorMessage = $"Shader Assembly blocks overlap before byte {blockStart}.";
                return false;
            }

            blocks.Add((blockStart, blockEnd, blockBytes, shaderOffset));
            searchOffset = blockEnd;
        }

        if (blocks.Count == 0 ||
            (version == LegacySupportedVersion && blocks.Count % 4 != 0) ||
            (version == SupportedVersion && blocks[^1].End != recordEnd))
        {
            errorMessage = "Shader-bearing RenderStyle has an incomplete length-delimited Shader Assembly block sequence.";
            return false;
        }

        var passes = new List<(int Start, int End, LithTechRenderPass Value)>();
        if (version == LegacySupportedVersion)
        {
            int regionStart = passDataStart;
            foreach ((int start, int end, _, _) in blocks)
            {
                if (!TryFindLegacyPasses(bytes, regionStart, start, passes, out errorMessage))
                {
                    return false;
                }

                regionStart = Math.Max(regionStart, end);
            }

            if (!TryFindLegacyPasses(bytes, regionStart, recordEnd, passes, out errorMessage) ||
                passes.Count != passCount ||
                blocks.Count / 4 > passes.Count)
            {
                errorMessage ??= $"Legacy shader-bearing RenderStyle declares {passCount} passes, but {passes.Count} complete pass records were located.";
                return false;
            }
        }
        else
        {
            int passCursor = passDataStart;
            LithTechRenderPass pass;
            if (!TryReadRenderPass(bytes, ref passCursor, recordEnd, out pass))
            {
                errorMessage = "Shader-bearing RenderStyle has no structurally valid standard pass.";
                return false;
            }

            passes.Add((passDataStart, passCursor, pass));
        }

        var shaderGroups = new List<(int Start, int End, int PassIndex, LithTechLegacyShaderOptions? Options)>();
        int shaderGroupCount = blocks.Count / 4;
        if (version == LegacySupportedVersion)
        {
            var assignedPasses = new HashSet<int>();
            for (int groupIndex = 0; groupIndex < shaderGroupCount; groupIndex++)
            {
                int firstBlockIndex = groupIndex * 4;
                int groupStart = blocks[firstBlockIndex].Start;
                int groupEnd = blocks[firstBlockIndex + 3].End;
                if (blocks[firstBlockIndex].End != blocks[firstBlockIndex + 1].Start ||
                    blocks[firstBlockIndex + 1].End != blocks[firstBlockIndex + 2].Start ||
                    blocks[firstBlockIndex + 2].End != blocks[firstBlockIndex + 3].Start)
                {
                    errorMessage = $"Legacy Shader Assembly group {groupIndex} has an unexpected gap between its four blocks.";
                    return false;
                }

                int optionsStart = groupStart - 162;
                int passIndex = passes.FindIndex(pass => pass.End == optionsStart && pass.Value.HasD3dOptions);
                if (optionsStart < passDataStart || passIndex < 0 || !assignedPasses.Add(passIndex) ||
                    !TryReadLegacyShaderOptions(bytes, optionsStart, groupStart, out LithTechLegacyShaderOptions? options) ||
                    options is null)
                {
                    errorMessage = $"Legacy Shader Assembly group {groupIndex} is missing its complete 162-byte D3D pass-options record.";
                    return false;
                }

                shaderGroups.Add((groupStart, groupEnd, passIndex, options));
            }

            if (assignedPasses.Count != passes.Count(pass => pass.Value.HasD3dOptions))
            {
                errorMessage = "Legacy D3D pass-option records and Shader Assembly groups do not form a one-to-one mapping.";
                return false;
            }
        }
        else
        {
            for (int groupIndex = 0; groupIndex < shaderGroupCount; groupIndex++)
            {
                int firstBlockIndex = groupIndex * 4;
                shaderGroups.Add((blocks[firstBlockIndex].Start, blocks[firstBlockIndex + 3].End, groupIndex, null));
            }
        }

        var text = new StringBuilder();
        text.AppendLine("LithTech packed RenderStyle with D3DX8 Shader Assembly extension");
        text.AppendLine($"File: {fileName}");
        text.AppendLine($"Storage: {storageDescription}");
        text.AppendLine($"Header: type=0x{fileType:X4}, version={version}");
        text.AppendLine($"Styles: {styleCount}, declared style payload bytes: {declaredStyleBytes:N0}, record bytes: {recordBytes:N0}");
        text.AppendLine($"LightingMaterial (17 floats): {FormatFloats(lighting)}");
        text.AppendLine($"Declared passes: {passCount}; standard pass records decoded: {passes.Count}");
        foreach ((int start, int end, LithTechRenderPass pass) in passes)
        {
            string alphaReference = version == LegacySupportedVersion
                ? $"{BitConverter.Int32BitsToSingle(unchecked((int)pass.AlphaRef)).ToString("R", CultureInfo.InvariantCulture)} (raw 0x{pass.AlphaRef:X8})"
                : pass.AlphaRef.ToString(CultureInfo.InvariantCulture);
            string zBufferTestMode = pass.HasZBufferTestMode
                ? pass.ZBufferTestMode.ToString(CultureInfo.InvariantCulture)
                : "not serialized in this version";
            text.AppendLine($"  Pass at byte {start}: bytes={end - start}, blend={pass.BlendMode}, zBuffer={pass.ZBufferMode}, cull={pass.CullMode}, textureFactor=0x{pass.TextureFactor:X8}, alphaRef={alphaReference}, dynamicLight={pass.DynamicLight}, zTest={zBufferTestMode}, alphaTest={pass.AlphaTestMode}, fill={pass.FillMode}, bumpEnv={pass.UseBumpEnvironmentMap}/{pass.BumpEnvironmentMapStage}, bumpScale={pass.BumpEnvironmentMapScale.ToString("R", CultureInfo.InvariantCulture)}, bumpOffset={pass.BumpEnvironmentMapOffset.ToString("R", CultureInfo.InvariantCulture)}, hasD3dOptions={pass.HasD3dOptions}");
            for (int stageIndex = 0; stageIndex < pass.TextureStages.Count; stageIndex++)
            {
                LithTechTextureStage stage = pass.TextureStages[stageIndex];
                text.AppendLine($"    Stage {stageIndex}: texture={stage.TextureParam}, color={stage.ColorOp}({stage.ColorArg1},{stage.ColorArg2}), alpha={stage.AlphaOp}({stage.AlphaArg1},{stage.AlphaArg2}), uv={stage.UvSource}, address=({stage.UAddress},{stage.VAddress}), filter={stage.TextureFilter}, uvTransform={stage.UvTransformEnabled}, project={stage.ProjectTextureCoordinates}, coordinateCount=not serialized in version {version}");
                text.AppendLine($"      UV matrix: [{FormatFloats(stage.UvTransformMatrix)}]");
            }
        }

        for (int groupIndex = 0; groupIndex < shaderGroups.Count; groupIndex++)
        {
            (int start, int end, int passIndex, LithTechLegacyShaderOptions? options) = shaderGroups[groupIndex];
            text.AppendLine($"Shader Assembly group {groupIndex}: pass={passIndex}, offset={start}, bytes={end - start}");
            if (options is not null)
            {
                AppendLegacyShaderOptions(text, options);
            }
        }

        text.AppendLine($"Length-validated Shader Assembly blocks: {blocks.Count} in {shaderGroups.Count} four-block groups");
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            (int start, int end, uint declaredBytes, int shaderOffset) = blocks[blockIndex];
            int shaderByteCount = end - shaderOffset;
            int groupPass = shaderGroups[Math.Min(blockIndex / 4, shaderGroups.Count - 1)].PassIndex;
            text.AppendLine($"  Block {blockIndex}: offset={start}, totalBytes={sizeof(uint) + declaredBytes}, payloadBytes={declaredBytes}, shaderTokenBytes={shaderByteCount}, group={blockIndex / 4}, pass={groupPass}, ordinal={blockIndex % 4}");
            text.AppendLine($"    Raw shader-token payload (base64): {Convert.ToBase64String(bytes.Slice(shaderOffset, shaderByteCount))}");
        }

        var decodedRanges = passes.Select(pass => (Start: pass.Start, End: pass.End))
            .Concat(shaderGroups.Where(group => group.Options is not null).Select(group => (Start: group.Start - 162, End: group.Start)))
            .Concat(blocks.Select(block => (Start: block.Start, End: block.End)))
            .OrderBy(range => range.Start)
            .ToArray();
        int coverageCursor = passDataStart;
        foreach ((int rangeStart, int rangeEnd) in decodedRanges)
        {
            if (rangeStart < coverageCursor)
            {
                errorMessage = $"Decoded RenderStyle ranges overlap at byte {rangeStart}.";
                return false;
            }

            if (rangeStart > coverageCursor)
            {
                int length = rangeStart - coverageCursor;
                text.AppendLine($"Opaque auxiliary bytes: offset={coverageCursor}, length={length}, rawBase64={Convert.ToBase64String(bytes.Slice(coverageCursor, length))}");
            }

            coverageCursor = rangeEnd;
        }

        if (coverageCursor < recordEnd)
        {
            int length = recordEnd - coverageCursor;
            text.AppendLine($"Opaque auxiliary bytes: offset={coverageCursor}, length={length}, rawBase64={Convert.ToBase64String(bytes.Slice(coverageCursor, length))}");
        }

        text.AppendLine("Every byte after the parsed pass table is accounted for by a decoded legacy pass, a decoded D3D shader-options record, or a length-validated Shader Assembly block.");
        document = new LithTechRenderStyleShaderExtensionDocument(text.ToString(), blocks.Count);
        return true;
    }

    private static bool TryFindLegacyPasses(
        ReadOnlySpan<byte> data,
        int regionStart,
        int regionEnd,
        List<(int Start, int End, LithTechRenderPass Value)> passes,
        out string? errorMessage)
    {
        errorMessage = null;
        int candidate = regionStart;
        int attempts = 0;
        while (candidate + 513 <= regionEnd && attempts++ < 2 * 4)
        {
            if (TryReadUInt32(data, candidate, out uint firstTextureParam) && firstTextureParam <= 4)
            {
                int end = candidate;
                if (TryReadLegacyRenderPass(data, ref end, regionEnd, out LithTechRenderPass pass) &&
                    end - candidate == 513 &&
                    IsPlausibleLegacyPass(pass))
                {
                    passes.Add((candidate, end, pass));
                    candidate = end;
                    continue;
                }
            }

            if (candidate + 513 + 162 <= regionEnd)
            {
                candidate += 162;
                continue;
            }

            break;
        }

        return true;
    }

    private static bool TryReadLegacyShaderOptions(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        out LithTechLegacyShaderOptions? options)
    {
        options = null;
        if (end - start != 162)
        {
            return false;
        }

        int position = start;
        if (!TryReadLegacyBool(data, ref position, end, out bool useVertexShader) ||
            !TryReadLegacyBool(data, ref position, end, out bool expandForSkinning))
        {
            return false;
        }

        var constantRegisters = new List<int>(3);
        var constantValues = new List<IReadOnlyList<float>>(3);
        for (int vector = 0; vector < 3; vector++)
        {
            if (!TryReadInt32(data, ref position, end, out int register))
            {
                return false;
            }

            var values = new List<float>(4);
            for (int component = 0; component < 4; component++)
            {
                if (!TryReadSingle(data, ref position, end, out float value) || !float.IsFinite(value))
                {
                    return false;
                }

                values.Add(value);
            }

            constantRegisters.Add(register);
            constantValues.Add(values);
        }

        string[] registerNames =
        [
            "WorldViewTransform_ConstReg", "WorldViewTransform_Count", "ProjTransform_ConstReg",
            "WorldViewProjTransform_ConstReg", "ViewProjTransform_ConstReg", "CamPos_MSpc_ConstReg",
            "Light_Count", "LightPosition_MSpc_ConstReg", "LightPosition_CSpc_ConstReg",
            "LightColor_ConstReg", "LightAtt_ConstReg", "Material_AmbDifEm_ConstReg",
            "Material_Specular_ConstReg", "AmbientLight_ConstReg", "PrevWorldViewTrans_ConstReg",
            "PrevWorldViewTrans_Count"
        ];
        var registerValues = new List<int>(registerNames.Length);
        foreach (string _ in registerNames)
        {
            if (!TryReadInt32(data, ref position, end, out int value))
            {
                return false;
            }

            registerValues.Add(value);
        }

        if (!TryReadInt32(data, ref position, end, out int lastConstantRegister) ||
            lastConstantRegister is < -1 or > 96 ||
            !TryReadLegacyBooleanArray(data, ref position, end, out List<bool> positionStreams) ||
            !TryReadLegacyBooleanArray(data, ref position, end, out List<bool> normalStreams) ||
            !TryReadLegacyBooleanArray(data, ref position, end, out List<bool> uvSetStreams) ||
            !TryReadLegacyBooleanArray(data, ref position, end, out List<bool> basisVectorStreams))
        {
            return false;
        }

        var uvCounts = new List<uint>(4);
        for (int stream = 0; stream < 4; stream++)
        {
            if (!TryReadUInt32(data, ref position, end, out uint count) || count > 4)
            {
                return false;
            }

            uvCounts.Add(count);
        }

        if (position != end)
        {
            return false;
        }

        options = new LithTechLegacyShaderOptions(
            useVertexShader,
            expandForSkinning,
            constantRegisters,
            constantValues,
            registerValues,
            lastConstantRegister,
            positionStreams,
            normalStreams,
            uvSetStreams,
            basisVectorStreams,
            uvCounts);
        return true;
    }

    private static bool TryReadLegacyBooleanArray(ReadOnlySpan<byte> data, ref int position, int end, out List<bool> values)
    {
        values = new List<bool>(4);
        for (int index = 0; index < 4; index++)
        {
            if (!TryReadByte(data, ref position, end, out byte raw) || raw > 1)
            {
                return false;
            }

            values.Add(raw != 0);
        }

        return true;
    }

    private static void AppendLegacyShaderOptions(StringBuilder text, LithTechLegacyShaderOptions options)
    {
        text.AppendLine($"  D3D shader metadata: vertexShader={options.UseVertexShader}, expandForSkinning={options.ExpandForSkinning}; shader filename/ID replaced by embedded assembly blocks");
        for (int vector = 0; vector < 3; vector++)
        {
            text.AppendLine($"    ConstVector{vector + 1}: register={options.ConstantVectorRegisters[vector]}, values=[{FormatFloats(options.ConstantVectorValues[vector])}]");
        }

        string[] registerNames =
        [
            "WorldViewTransform_ConstReg", "WorldViewTransform_Count", "ProjTransform_ConstReg",
            "WorldViewProjTransform_ConstReg", "ViewProjTransform_ConstReg", "CamPos_MSpc_ConstReg",
            "Light_Count", "LightPosition_MSpc_ConstReg", "LightPosition_CSpc_ConstReg",
            "LightColor_ConstReg", "LightAtt_ConstReg", "Material_AmbDifEm_ConstReg",
            "Material_Specular_ConstReg", "AmbientLight_ConstReg", "PrevWorldViewTrans_ConstReg",
            "PrevWorldViewTrans_Count"
        ];
        text.AppendLine("    Registers: " + string.Join(", ", registerNames.Select((name, index) => $"{name}={options.RenderStateRegisters[index]}")));
        text.AppendLine($"    Last_ConstReg={options.LastConstantRegister}");
        text.AppendLine($"    Declaration streams (0..3): position=[{FormatBools(options.PositionStreams)}], normal=[{FormatBools(options.NormalStreams)}], uvSets=[{FormatBools(options.UvSetStreams)}], basisVectors=[{FormatBools(options.BasisVectorStreams)}], uvCounts=[{string.Join(", ", options.UvCounts)}]");
    }

    private static string FormatBools(IEnumerable<bool> values) =>
        string.Join(", ", values.Select(value => value ? "1" : "0"));

    private static bool IsPlausibleLegacyPass(LithTechRenderPass pass)
    {
        if (pass.TextureStages.Count != 4 ||
            !float.IsFinite(pass.BumpEnvironmentMapScale) ||
            !float.IsFinite(pass.BumpEnvironmentMapOffset))
        {
            return false;
        }

        foreach (LithTechTextureStage stage in pass.TextureStages)
        {
            if (stage.TextureParam is < 0 or > 4 || stage.UvTransformMatrix.Count != 16 ||
                stage.UvTransformMatrix.Any(value => !float.IsFinite(value)))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatFloats(IEnumerable<float> values) =>
        string.Join(", ", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    private static bool TryReadLightingMaterial(ReadOnlySpan<byte> data, ref int position, int end, out List<float> values)
    {
        values = null!;
        if ((long)position + 17 * sizeof(float) > end)
        {
            return false;
        }

        values = new List<float>(17);
        for (int i = 0; i < 17; i++)
        {
            if (!TryReadSingle(data, position, out float value))
            {
                return false;
            }

            values.Add(value);
            position += sizeof(float);
        }

        return true;
    }

    private static bool TryReadLegacyRenderPass(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        out LithTechRenderPass pass)
    {
        pass = null!;
        int passStart = position;
        const int legacyTextureStageBytes = 11 * sizeof(int) + sizeof(byte) + 3 + 16 * sizeof(float) + sizeof(byte) + 3;
        const int legacyPassBytes = 4 * legacyTextureStageBytes + 5 * sizeof(uint) + sizeof(byte) + 3 + 3 * sizeof(int) + sizeof(bool) + 3 + 2 * sizeof(float) + sizeof(byte);
        if ((long)position + legacyPassBytes > end)
        {
            return false;
        }

        var stages = new List<LithTechTextureStage>(4);
        for (int stageIndex = 0; stageIndex < 4; stageIndex++)
        {
            if (!TryReadLegacyTextureStage(data, ref position, end, out LithTechTextureStage stage))
            {
                return false;
            }

            stages.Add(stage);
        }

        if (!TryReadInt32(data, ref position, end, out int blendMode) ||
            !TryReadInt32(data, ref position, end, out int zBufferMode) ||
            !TryReadInt32(data, ref position, end, out int cullMode) ||
            !TryReadUInt32(data, ref position, end, out uint textureFactor) ||
            !TryReadUInt32(data, ref position, end, out uint alphaRef) ||
            !TryReadLegacyBoolWithPadding(data, ref position, end, out bool dynamicLight) ||
            !TryReadInt32(data, ref position, end, out int alphaTestMode) ||
            !TryReadInt32(data, ref position, end, out int fillMode) ||
            !TryReadLegacyBoolWithPadding(data, ref position, end, out bool useBumpEnvironmentMap) ||
            !TryReadUInt32(data, ref position, end, out uint bumpEnvironmentMapStage) ||
            !TryReadSingle(data, ref position, end, out float bumpEnvironmentMapScale) ||
            !TryReadSingle(data, ref position, end, out float bumpEnvironmentMapOffset) ||
            !TryReadLegacyBool(data, ref position, end, out bool hasD3dOptions))
        {
            return false;
        }

        if (position - passStart != legacyPassBytes)
        {
            return false;
        }

        pass = new LithTechRenderPass(
            stages,
            blendMode,
            zBufferMode,
            cullMode,
            textureFactor,
            alphaRef,
            dynamicLight,
            0,
            false,
            alphaTestMode,
            fillMode,
            useBumpEnvironmentMap,
            bumpEnvironmentMapStage,
            bumpEnvironmentMapScale,
            bumpEnvironmentMapOffset,
            hasD3dOptions,
            false,
            0,
            false,
            0);
        return true;
    }

    private static bool TryReadLegacyTextureStage(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        out LithTechTextureStage stage)
    {
        stage = null!;
        if (!TryReadInt32(data, ref position, end, out int textureParam) ||
            !TryReadInt32(data, ref position, end, out int colorOp) ||
            !TryReadInt32(data, ref position, end, out int colorArg1) ||
            !TryReadInt32(data, ref position, end, out int colorArg2) ||
            !TryReadInt32(data, ref position, end, out int alphaOp) ||
            !TryReadInt32(data, ref position, end, out int alphaArg1) ||
            !TryReadInt32(data, ref position, end, out int alphaArg2) ||
            !TryReadInt32(data, ref position, end, out int uvSource) ||
            !TryReadInt32(data, ref position, end, out int uAddress) ||
            !TryReadInt32(data, ref position, end, out int vAddress) ||
            !TryReadInt32(data, ref position, end, out int textureFilter) ||
            !TryReadLegacyBoolWithPadding(data, ref position, end, out bool uvTransformEnabled))
        {
            return false;
        }

        var matrix = new List<float>(16);
        for (int i = 0; i < 16; i++)
        {
            if (!TryReadSingle(data, ref position, end, out float value))
            {
                return false;
            }

            matrix.Add(value);
        }

        if (!TryReadLegacyBoolWithPadding(data, ref position, end, out bool projectTextureCoordinates))
        {
            return false;
        }

        stage = new LithTechTextureStage(
            textureParam,
            colorOp,
            colorArg1,
            colorArg2,
            alphaOp,
            alphaArg1,
            alphaArg2,
            uvSource,
            uAddress,
            vAddress,
            textureFilter,
            uvTransformEnabled,
            matrix,
            projectTextureCoordinates,
            false,
            0);
        return true;
    }

    private static bool TryReadLegacyBoolWithPadding(ReadOnlySpan<byte> data, ref int position, int end, out bool value)
    {
        if (!TryReadLegacyBool(data, ref position, end, out value) || (long)position + 3 > end)
        {
            return false;
        }

        position += 3;
        return true;
    }

    private static bool TryReadLegacyBool(ReadOnlySpan<byte> data, ref int position, int end, out bool value)
    {
        value = false;
        if (!TryReadByte(data, ref position, end, out byte raw))
        {
            return false;
        }

        value = raw != 0;
        return true;
    }

    private static bool TryReadRenderPass(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        out LithTechRenderPass pass)
    {
        pass = null!;
        if ((long)position + RenderPassBytes + 1 > end)
        {
            return false;
        }

        var stages = new List<LithTechTextureStage>(4);
        for (int stageIndex = 0; stageIndex < 4; stageIndex++)
        {
            if (!TryReadTextureStage(data, ref position, end, out LithTechTextureStage stage))
            {
                return false;
            }

            stages.Add(stage);
        }

        if (!TryReadInt32(data, ref position, end, out int blendMode) ||
            !TryReadInt32(data, ref position, end, out int zBufferMode) ||
            !TryReadInt32(data, ref position, end, out int cullMode) ||
            !TryReadUInt32(data, ref position, end, out uint textureFactor) ||
            !TryReadUInt32(data, ref position, end, out uint alphaRef) ||
            !TryReadBoolWithPadding(data, ref position, end, out bool dynamicLight) ||
            !TryReadInt32(data, ref position, end, out int zBufferTestMode) ||
            !TryReadInt32(data, ref position, end, out int alphaTestMode) ||
            !TryReadInt32(data, ref position, end, out int fillMode) ||
            !TryReadBoolWithPadding(data, ref position, end, out bool useBumpEnvironmentMap) ||
            !TryReadUInt32(data, ref position, end, out uint bumpEnvironmentMapStage) ||
            !TryReadSingle(data, ref position, end, out float bumpEnvironmentMapScale) ||
            !TryReadSingle(data, ref position, end, out float bumpEnvironmentMapOffset) ||
            !TryReadByte(data, ref position, end, out byte hasD3dOptions) ||
            hasD3dOptions > 1)
        {
            return false;
        }

        bool useVertexShader = false;
        bool usePixelShader = false;
        int vertexShaderId = 0;
        int pixelShaderId = 0;
        if (hasD3dOptions != 0 &&
            (!TryReadBool(data, ref position, end, out useVertexShader) ||
             !TryReadInt32(data, ref position, end, out vertexShaderId) ||
             !TryReadBool(data, ref position, end, out usePixelShader) ||
             !TryReadInt32(data, ref position, end, out pixelShaderId)))
        {
            return false;
        }

        pass = new LithTechRenderPass(
            stages,
            blendMode,
            zBufferMode,
            cullMode,
            textureFactor,
            alphaRef,
            dynamicLight,
            zBufferTestMode,
            true,
            alphaTestMode,
            fillMode,
            useBumpEnvironmentMap,
            bumpEnvironmentMapStage,
            bumpEnvironmentMapScale,
            bumpEnvironmentMapOffset,
            hasD3dOptions != 0,
            useVertexShader,
            vertexShaderId,
            usePixelShader,
            pixelShaderId);
        return true;
    }

    private static bool TryReadTextureStage(
        ReadOnlySpan<byte> data,
        ref int position,
        int end,
        out LithTechTextureStage stage)
    {
        stage = null!;
        if (!TryReadInt32(data, ref position, end, out int textureParam) ||
            !TryReadInt32(data, ref position, end, out int colorOp) ||
            !TryReadInt32(data, ref position, end, out int colorArg1) ||
            !TryReadInt32(data, ref position, end, out int colorArg2) ||
            !TryReadInt32(data, ref position, end, out int alphaOp) ||
            !TryReadInt32(data, ref position, end, out int alphaArg1) ||
            !TryReadInt32(data, ref position, end, out int alphaArg2) ||
            !TryReadInt32(data, ref position, end, out int uvSource) ||
            !TryReadInt32(data, ref position, end, out int uAddress) ||
            !TryReadInt32(data, ref position, end, out int vAddress) ||
            !TryReadInt32(data, ref position, end, out int textureFilter) ||
            !TryReadBoolWithPadding(data, ref position, end, out bool uvTransformEnabled))
        {
            return false;
        }

        if ((long)position + 16 * sizeof(float) > end)
        {
            return false;
        }

        var matrix = new List<float>(16);
        for (int i = 0; i < 16; i++)
        {
            if (!TryReadSingle(data, position, out float value))
            {
                return false;
            }

            matrix.Add(value);
            position += sizeof(float);
        }

        if (!TryReadBoolWithPadding(data, ref position, end, out bool projectTextureCoordinates) ||
            !TryReadUInt32(data, ref position, end, out uint textureCoordinateCount))
        {
            return false;
        }

        stage = new LithTechTextureStage(
            textureParam,
            colorOp,
            colorArg1,
            colorArg2,
            alphaOp,
            alphaArg1,
            alphaArg2,
            uvSource,
            uAddress,
            vAddress,
            textureFilter,
            uvTransformEnabled,
            matrix,
            projectTextureCoordinates,
            true,
            textureCoordinateCount);
        return true;
    }

    private static bool TryReadBoolWithPadding(ReadOnlySpan<byte> data, ref int position, int end, out bool value)
    {
        value = false;
        if (!TryReadBool(data, ref position, end, out value) || (long)position + 3 > end)
        {
            return false;
        }

        position += 3;
        return true;
    }

    private static bool TryReadBool(ReadOnlySpan<byte> data, ref int position, int end, out bool value)
    {
        value = false;
        if (!TryReadByte(data, ref position, end, out byte raw) || raw > 1)
        {
            return false;
        }

        value = raw != 0;
        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> data, ref int position, int end, out byte value)
    {
        value = 0;
        if (position < 0 || position >= end || end > data.Length)
        {
            return false;
        }

        value = data[position++];
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> data, ref int position, int end, out int value)
    {
        value = 0;
        if (position < 0 || (long)position + sizeof(int) > end || end > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position, sizeof(int)));
        position += sizeof(int);
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int position, int end, out uint value)
    {
        value = 0;
        if (position < 0 || (long)position + sizeof(uint) > end || end > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position, sizeof(uint)));
        position += sizeof(uint);
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, int position, out float value)
    {
        value = 0;
        if (position < 0 || (long)position + sizeof(float) > data.Length)
        {
            return false;
        }

        value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position, sizeof(float))));
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, ref int position, int end, out float value)
    {
        if (!TryReadSingle(data, position, out value) || (long)position + sizeof(float) > end)
        {
            return false;
        }

        position += sizeof(float);
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, int position, out ushort value)
    {
        value = 0;
        if (position < 0 || (long)position + sizeof(ushort) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, int position, out uint value)
    {
        value = 0;
        if (position < 0 || (long)position + sizeof(uint) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position, sizeof(uint)));
        return true;
    }
}
