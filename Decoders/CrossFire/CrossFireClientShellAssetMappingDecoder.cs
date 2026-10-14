using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CFRezManager;

internal sealed record CrossFireClientShellAssetMapping(
    string ModelReference,
    string TextureReference);

/// <summary>
/// Recovers literal model/texture pairs from the x64 ClientShell's compiled
/// resource-registration tables. A pair is accepted only when one runtime
/// function passes one LTB and one DTX path in R8 to the same call target and
/// the corresponding RCX table entries are separated by 0x1000 bytes.
/// </summary>
internal static class CrossFireClientShellAssetMappingDecoder
{
    private const int MaximumSectionCount = 96;
    private const int MaximumPathLength = 192;
    private const int MaximumCallSearchDistance = 40;
    private const int ParallelTableOffset = 0x1000;

    public static bool TryDecode(
        byte[] data,
        out IReadOnlyList<CrossFireClientShellAssetMapping> mappings,
        out string? errorMessage)
    {
        return TryDecodeCore(data, includeMultiAssetFunctions: false, out mappings, out errorMessage);
    }

    public static bool TryDecodeCandidateMappings(
        byte[] data,
        out IReadOnlyList<CrossFireClientShellAssetMapping> mappings,
        out string? errorMessage)
    {
        return TryDecodeCore(data, includeMultiAssetFunctions: true, out mappings, out errorMessage);
    }

    private static bool TryDecodeCore(
        byte[] data,
        bool includeMultiAssetFunctions,
        out IReadOnlyList<CrossFireClientShellAssetMapping> mappings,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(data);
        mappings = [];
        errorMessage = null;

        try
        {
            if (!TryReadSections(data, out PeSection code, out PeSection readOnlyData, out PeSection unwindData))
            {
                errorMessage = "The input is not a supported PE32+ x64 image with .text, .rdata, and .pdata sections.";
                return false;
            }

            Dictionary<uint, ResourcePath> pathsByRva = ReadResourcePaths(data, readOnlyData);
            List<RuntimeFunction> runtimeFunctions = ReadRuntimeFunctions(data, code, unwindData);
            if (pathsByRva.Count == 0 || runtimeFunctions.Count == 0)
            {
                errorMessage = "The image contains no usable resource paths or x64 runtime-function records.";
                return false;
            }

            var referencesByFunction = new Dictionary<uint, List<ResourceCallReference>>();
            int codeFileOffset = checked((int)code.RawOffset);
            int codeByteCount = checked((int)code.RawSize);
            uint codeEndRva = checked(code.VirtualAddress + (uint)codeByteCount);
            for (int codeOffset = 0; codeOffset <= codeByteCount - 7; codeOffset++)
            {
                int instructionOffset = codeFileOffset + codeOffset;
                ResourceArgumentRegister argumentRegister;
                if (data[instructionOffset] == 0x4C &&
                    data[instructionOffset + 1] == 0x8D &&
                    data[instructionOffset + 2] == 0x05)
                {
                    argumentRegister = ResourceArgumentRegister.R8;
                }
                else if (data[instructionOffset] == 0x48 &&
                         data[instructionOffset + 1] == 0x8D &&
                         data[instructionOffset + 2] == 0x15)
                {
                    argumentRegister = ResourceArgumentRegister.Rdx;
                }
                else
                {
                    continue;
                }

                uint instructionRva = checked(code.VirtualAddress + (uint)codeOffset);
                int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(instructionOffset + 3, sizeof(int)));
                long targetRva = (long)instructionRva + 7 + displacement;
                if (targetRva is < 0 or > uint.MaxValue ||
                    !pathsByRva.TryGetValue((uint)targetRva, out ResourcePath? resourcePath))
                {
                    continue;
                }

                RuntimeFunction? function = FindRuntimeFunction(runtimeFunctions, instructionRva);
                if (function is null ||
                    !TryFindResourceCall(
                        data,
                        code,
                        instructionOffset,
                        function.Value,
                        codeEndRva,
                        argumentRegister,
                        out ResourceCallReference? reference) ||
                    reference is null)
                {
                    continue;
                }

                if (!referencesByFunction.TryGetValue(function.Value.BeginRva, out List<ResourceCallReference>? functionReferences))
                {
                    functionReferences = [];
                    referencesByFunction.Add(function.Value.BeginRva, functionReferences);
                }

                functionReferences.Add(reference with
                {
                    Path = resourcePath.Path,
                    Kind = resourcePath.Kind
                });
            }

            var decodedMappings = new Dictionary<string, CrossFireClientShellAssetMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (List<ResourceCallReference> functionReferences in referencesByFunction.Values)
            {
                ResourceCallReference[] models = functionReferences
                    .Where(reference => reference.Kind == ResourcePathKind.Model)
                    .Distinct()
                    .ToArray();
                ResourceCallReference[] textures = functionReferences
                    .Where(reference => reference.Kind == ResourcePathKind.Texture)
                    .Distinct()
                    .ToArray();
                if (models.Length == 0 || textures.Length == 0)
                {
                    continue;
                }

                if (!includeMultiAssetFunctions &&
                    (models.Select(reference => reference.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() != 1 ||
                     textures.Select(reference => reference.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() != 1))
                {
                    continue;
                }

                foreach (ResourceCallReference model in models)
                {
                    foreach (ResourceCallReference texture in textures)
                    {
                        if (model.ArgumentRegister != texture.ArgumentRegister ||
                            !string.Equals(model.ArgumentBaseRegister, texture.ArgumentBaseRegister, StringComparison.Ordinal) ||
                            !IsMatchingTableFieldPair(data, code, model, texture))
                        {
                            continue;
                        }

                        var mapping = new CrossFireClientShellAssetMapping(model.Path, texture.Path);
                        decodedMappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
                    }
                }
            }

            foreach (CrossFireClientShellAssetMapping mapping in ExtractDirectRegistrationPairs(
                         data,
                         code,
                         pathsByRva,
                         runtimeFunctions))
            {
                decodedMappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
            }

            foreach (CrossFireClientShellAssetMapping mapping in ExtractSameStemFunctionPairs(
                         data,
                         code,
                         pathsByRva,
                         runtimeFunctions))
            {
                decodedMappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
            }

            foreach (CrossFireClientShellAssetMapping mapping in ExtractAdjacentCodePathPairs(
                         data,
                         code,
                         pathsByRva,
                         runtimeFunctions))
            {
                decodedMappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
            }

            foreach (CrossFireClientShellAssetMapping mapping in ExtractAdjacentUnreferencedPathPairs(
                         data,
                         code,
                         pathsByRva))
            {
                decodedMappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
            }

            mappings = decodedMappings.Values
                .OrderBy(mapping => mapping.ModelReference, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mapping => mapping.TextureReference, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            errorMessage = $"ClientShell resource mappings could not be decoded: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadSections(
        byte[] data,
        out PeSection code,
        out PeSection readOnlyData,
        out PeSection unwindData)
    {
        code = default;
        readOnlyData = default;
        unwindData = default;
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return false;
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, sizeof(int)));
        if (peOffset < 0x40 || peOffset > data.Length - 24 ||
            !data.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 4, sizeof(ushort))) != 0x8664)
        {
            return false;
        }

        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 6, sizeof(ushort)));
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 20, sizeof(ushort)));
        int optionalHeaderOffset = checked(peOffset + 24);
        if (sectionCount is 0 or > MaximumSectionCount ||
            optionalHeaderSize < 112 ||
            optionalHeaderOffset > data.Length - optionalHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optionalHeaderOffset, sizeof(ushort))) != 0x20B)
        {
            return false;
        }

        int sectionHeadersOffset = checked(optionalHeaderOffset + optionalHeaderSize);
        if (sectionHeadersOffset > data.Length - (sectionCount * 40))
        {
            return false;
        }

        for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            int headerOffset = checked(sectionHeadersOffset + (sectionIndex * 40));
            string name = Encoding.ASCII.GetString(data, headerOffset, 8).TrimEnd('\0');
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(headerOffset + 8, sizeof(uint)));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(headerOffset + 12, sizeof(uint)));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(headerOffset + 16, sizeof(uint)));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(headerOffset + 20, sizeof(uint)));
            if (rawOffset > data.Length || rawSize > data.Length - rawOffset)
            {
                return false;
            }

            var section = new PeSection(name, virtualAddress, virtualSize, rawOffset, rawSize);
            if (name == ".text") code = section;
            else if (name == ".rdata") readOnlyData = section;
            else if (name == ".pdata") unwindData = section;
        }

        return code.RawSize >= 7 && readOnlyData.RawSize > 0 && unwindData.RawSize >= 12;
    }

    private static Dictionary<uint, ResourcePath> ReadResourcePaths(byte[] data, PeSection section)
    {
        var paths = new Dictionary<uint, ResourcePath>();
        int sectionStart = checked((int)section.RawOffset);
        int sectionEnd = checked(sectionStart + (int)section.RawSize);
        int runStart = sectionStart;
        for (int offset = sectionStart; offset <= sectionEnd; offset++)
        {
            bool printable = offset < sectionEnd && data[offset] is >= 0x20 and <= 0x7E;
            if (printable)
            {
                continue;
            }

            if (offset > runStart)
            {
                ReadPathsFromAsciiRun(data, section, runStart, offset, paths);
            }

            runStart = offset + 1;
        }

        return paths;
    }

    private static void ReadPathsFromAsciiRun(
        byte[] data,
        PeSection section,
        int runStart,
        int runEnd,
        Dictionary<uint, ResourcePath> paths)
    {
        for (int offset = runStart; offset < runEnd; offset++)
        {
            int prefixLength;
            ResourcePathKind kind;
            if (MatchesAscii(data, offset, runEnd, "ModelTextures/"u8) || MatchesAscii(data, offset, runEnd, "ModelTextures\\"u8))
            {
                prefixLength = "ModelTextures/".Length;
                kind = ResourcePathKind.Texture;
            }
            else if (MatchesAscii(data, offset, runEnd, "Models/"u8) || MatchesAscii(data, offset, runEnd, "Models\\"u8))
            {
                prefixLength = "Models/".Length;
                kind = ResourcePathKind.Model;
            }
            else
            {
                continue;
            }

            if (offset > runStart && IsPathCharacter((char)data[offset - 1]))
            {
                continue;
            }

            int pathEnd = offset;
            while (pathEnd < runEnd && pathEnd - offset <= MaximumPathLength && IsPathCharacter((char)data[pathEnd]))
            {
                pathEnd++;
            }

            int pathLength = pathEnd - offset;
            if (pathLength <= prefixLength || pathLength > MaximumPathLength ||
                (kind == ResourcePathKind.Model && !EndsWithAsciiIgnoreCase(data, pathEnd - 4, pathEnd, ".ltb")) ||
                (kind == ResourcePathKind.Texture && !EndsWithAsciiIgnoreCase(data, pathEnd - 4, pathEnd, ".dtx")))
            {
                continue;
            }

            string path = Encoding.ASCII.GetString(data, offset, pathLength).Replace('\\', '/');
            if (path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                continue;
            }

            uint pathRva = checked(section.VirtualAddress + (uint)(offset - (int)section.RawOffset));
            paths.TryAdd(pathRva, new ResourcePath(path, kind));
        }
    }

    private static bool MatchesAscii(byte[] data, int offset, int end, ReadOnlySpan<byte> value)
    {
        if (offset > end - value.Length)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            byte current = data[offset + index];
            byte expected = value[index];
            if (current is >= (byte)'A' and <= (byte)'Z') current = (byte)(current + ('a' - 'A'));
            if (expected is >= (byte)'A' and <= (byte)'Z') expected = (byte)(expected + ('a' - 'A'));
            if (current != expected) return false;
        }

        return true;
    }

    private static bool EndsWithAsciiIgnoreCase(byte[] data, int start, int end, string suffix)
    {
        if (start < 0 || end - start < suffix.Length)
        {
            return false;
        }

        return MatchesAscii(data, end - suffix.Length, end, Encoding.ASCII.GetBytes(suffix));
    }

    private static bool IsPathCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.' or '/' or '\\' or ' ';
    }

    private static List<RuntimeFunction> ReadRuntimeFunctions(byte[] data, PeSection code, PeSection unwindData)
    {
        var functions = new List<RuntimeFunction>(checked((int)(unwindData.RawSize / 12)));
        int start = checked((int)unwindData.RawOffset);
        int end = checked(start + (int)unwindData.RawSize);
        uint codeEndRva = checked(code.VirtualAddress + code.RawSize);
        for (int offset = start; offset <= end - 12; offset += 12)
        {
            uint beginRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
            uint endRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, sizeof(uint)));
            if (beginRva >= code.VirtualAddress && beginRva < endRva && endRva <= codeEndRva)
            {
                functions.Add(new RuntimeFunction(beginRva, endRva));
            }
        }

        functions.Sort(static (left, right) => left.BeginRva.CompareTo(right.BeginRva));
        return functions;
    }

    private static RuntimeFunction? FindRuntimeFunction(IReadOnlyList<RuntimeFunction> functions, uint codeRva)
    {
        int low = 0;
        int high = functions.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            RuntimeFunction candidate = functions[middle];
            if (codeRva < candidate.BeginRva)
            {
                high = middle - 1;
            }
            else if (codeRva >= candidate.EndRva)
            {
                low = middle + 1;
            }
            else
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryFindResourceCall(
        byte[] data,
        PeSection code,
        int resourcePathInstructionOffset,
        RuntimeFunction function,
        uint codeEndRva,
        ResourceArgumentRegister argumentRegister,
        out ResourceCallReference? reference)
    {
        reference = null;
        int codeFileOffset = checked((int)code.RawOffset);
        int functionFileStart = checked(codeFileOffset + (int)(function.BeginRva - code.VirtualAddress));
        int functionFileEnd = checked(codeFileOffset + (int)(function.EndRva - code.VirtualAddress));
        int searchStart = resourcePathInstructionOffset + 7;
        int searchEnd = Math.Min(functionFileEnd - 5, searchStart + MaximumCallSearchDistance);

        for (int callOffset = searchStart; callOffset <= searchEnd; callOffset++)
        {
            int callLength;
            long callTarget;
            string targetKind;
            if (data[callOffset] == 0xE8 && callOffset <= functionFileEnd - 5)
            {
                callLength = 5;
                uint nextInstructionRva = checked(code.VirtualAddress + (uint)(callOffset - codeFileOffset + callLength));
                int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(callOffset + 1, sizeof(int)));
                callTarget = (long)nextInstructionRva + displacement;
                targetKind = "CALL";
                if (callTarget < code.VirtualAddress || callTarget >= codeEndRva)
                {
                    continue;
                }
            }
            else if (data[callOffset] == 0xFF && callOffset <= functionFileEnd - 6 && data[callOffset + 1] == 0x15)
            {
                callLength = 6;
                uint nextInstructionRva = checked(code.VirtualAddress + (uint)(callOffset - codeFileOffset + callLength));
                int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(callOffset + 2, sizeof(int)));
                callTarget = (long)nextInstructionRva + displacement;
                targetKind = "IAT";
            }
            else
            {
                continue;
            }

            if (HasControlFlowBarrierBetween(
                    data,
                    resourcePathInstructionOffset + 7,
                    callOffset,
                    functionFileEnd))
            {
                continue;
            }

            bool hasRcxAddress;
            string? baseRegister;
            int argumentDisplacement;
            if (argumentRegister == ResourceArgumentRegister.R8)
            {
                hasRcxAddress = TryReadRcxAddressArgument(
                    data,
                    callOffset,
                    functionFileStart,
                    out baseRegister,
                    out argumentDisplacement);
            }
            else
            {
                hasRcxAddress = TryReadRcxAddressArgument(
                                    data,
                                    callOffset,
                                    functionFileStart,
                                    out baseRegister,
                                    out argumentDisplacement) ||
                                TryReadRcxAddressBeforeResourcePath(
                                    data,
                                    resourcePathInstructionOffset,
                                    callOffset,
                                    functionFileStart,
                                    out baseRegister,
                                    out argumentDisplacement);
            }
            if (!hasRcxAddress)
            {
                continue;
            }

            int callRva = checked((int)(code.VirtualAddress + (uint)(callOffset - codeFileOffset)));
            reference = new ResourceCallReference(
                string.Empty,
                ResourcePathKind.Model,
                $"{targetKind}:{callTarget:X}",
                argumentRegister,
                baseRegister,
                argumentDisplacement,
                callRva,
                resourcePathInstructionOffset);
            return true;
        }

        return false;
    }

    private static bool IsMatchingTableFieldPair(
        byte[] data,
        PeSection code,
        ResourceCallReference model,
        ResourceCallReference texture)
    {
        if (Math.Abs(texture.PathInstructionOffset - model.PathInstructionOffset) > 64)
        {
            return false;
        }

        int displacementDelta = texture.ArgumentDisplacement - model.ArgumentDisplacement;
        bool matchingFields = displacementDelta == ParallelTableOffset
            ? model.CallTarget == texture.CallTarget ||
              model.ArgumentRegister == ResourceArgumentRegister.Rdx &&
              model.CallTarget.StartsWith("IAT:", StringComparison.Ordinal) &&
              texture.CallTarget.StartsWith("IAT:", StringComparison.Ordinal)
            : displacementDelta == 0x20 && model.CallTarget == texture.CallTarget;
        if (!matchingFields)
        {
            return false;
        }

        ResourceCallReference earlier = model.PathInstructionOffset <= texture.PathInstructionOffset ? model : texture;
        ResourceCallReference later = ReferenceEquals(earlier, model) ? texture : model;
        int callFileOffset = checked((int)code.RawOffset + earlier.CallRva - (int)code.VirtualAddress);
        int callLength = callFileOffset >= 0 && callFileOffset < data.Length && data[callFileOffset] == 0xE8
            ? 5
            : callFileOffset >= 0 && callFileOffset <= data.Length - 6 &&
              data[callFileOffset] == 0xFF && data[callFileOffset + 1] == 0x15
                ? 6
                : 0;
        if (callLength == 0)
        {
            return false;
        }

        return !HasControlFlowBarrierBetween(
            data,
            callFileOffset + callLength,
            later.PathInstructionOffset,
            later.PathInstructionOffset);
    }

    private static IReadOnlyList<CrossFireClientShellAssetMapping> ExtractDirectRegistrationPairs(
        byte[] data,
        PeSection code,
        IReadOnlyDictionary<uint, ResourcePath> pathsByRva,
        IReadOnlyList<RuntimeFunction> runtimeFunctions)
    {
        const int modelRegistrationVtableOffset = 0xB38;
        const int textureRegistrationVtableOffset = 0xB40;
        const int maximumCallSearchDistance = 32;
        int codeFileOffset = checked((int)code.RawOffset);
        int codeByteCount = checked((int)code.RawSize);
        int codeEndOffset = checked(codeFileOffset + codeByteCount);
        var registrationsByFunction = new Dictionary<uint, List<DirectRegistrationReference>>();

        for (int pathOffset = codeFileOffset; pathOffset <= codeEndOffset - 7; pathOffset++)
        {
            if (data[pathOffset] != 0x48 || data[pathOffset + 1] != 0x8D || data[pathOffset + 2] != 0x0D)
            {
                continue;
            }

            uint instructionRva = checked(code.VirtualAddress + (uint)(pathOffset - codeFileOffset));
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pathOffset + 3, sizeof(int)));
            long targetRva = (long)instructionRva + 7 + displacement;
            if (targetRva is < 0 or > uint.MaxValue ||
                !pathsByRva.TryGetValue((uint)targetRva, out ResourcePath? path) ||
                FindRuntimeFunction(runtimeFunctions, instructionRva) is not RuntimeFunction function ||
                !TryFindDirectRegistrationCall(
                    data,
                    pathOffset,
                    checked(codeFileOffset + (int)(function.EndRva - code.VirtualAddress)),
                    maximumCallSearchDistance,
                    out int callOffset,
                    out int callLength,
                    out int vtableOffset))
            {
                continue;
            }

            DirectRegistrationRole role;
            if (path.Kind == ResourcePathKind.Model && vtableOffset == modelRegistrationVtableOffset)
            {
                role = DirectRegistrationRole.Model;
            }
            else if (path.Kind == ResourcePathKind.Texture && vtableOffset == textureRegistrationVtableOffset)
            {
                role = DirectRegistrationRole.Texture;
            }
            else
            {
                continue;
            }

            if (!registrationsByFunction.TryGetValue(function.BeginRva, out List<DirectRegistrationReference>? registrations))
            {
                registrations = [];
                registrationsByFunction.Add(function.BeginRva, registrations);
            }

            registrations.Add(new DirectRegistrationReference(
                path.Path,
                role,
                pathOffset,
                callOffset,
                callLength));
        }

        var mappings = new Dictionary<string, CrossFireClientShellAssetMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (List<DirectRegistrationReference> registrations in registrationsByFunction.Values)
        {
            DirectRegistrationReference[] ordered = registrations
                .Distinct()
                .OrderBy(reference => reference.CallOffset)
                .ToArray();
            for (int modelIndex = 0; modelIndex < ordered.Length; modelIndex++)
            {
                DirectRegistrationReference model = ordered[modelIndex];
                if (model.Role != DirectRegistrationRole.Model)
                {
                    continue;
                }

                int modelCallEnd = model.CallOffset + model.CallLength;
                for (int textureIndex = modelIndex + 1; textureIndex < ordered.Length; textureIndex++)
                {
                    DirectRegistrationReference texture = ordered[textureIndex];
                    if (texture.Role == DirectRegistrationRole.Model)
                    {
                        break;
                    }

                    if (HasControlFlowBarrierBetween(
                            data,
                            modelCallEnd,
                            texture.PathInstructionOffset,
                            codeEndOffset))
                    {
                        break;
                    }

                    var mapping = new CrossFireClientShellAssetMapping(model.Path, texture.Path);
                    mappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
                }
            }
        }

        return mappings.Values.ToArray();
    }

    private static bool TryFindDirectRegistrationCall(
        byte[] data,
        int pathOffset,
        int functionEndOffset,
        int maximumCallSearchDistance,
        out int callOffset,
        out int callLength,
        out int vtableOffset)
    {
        callOffset = 0;
        callLength = 0;
        vtableOffset = 0;
        int first = pathOffset + 7;
        int last = Math.Min(Math.Min(functionEndOffset - 6, pathOffset + maximumCallSearchDistance), data.Length - 6);
        for (int candidate = first; candidate <= last; candidate++)
        {
            int candidateLength;
            int targetOffset;
            if (data[candidate] == 0xFF && data[candidate + 1] == 0x90)
            {
                candidateLength = 6;
                targetOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(candidate + 2, sizeof(int)));
            }
            else if (data[candidate] == 0xFF && data[candidate + 1] == 0x50)
            {
                candidateLength = 3;
                targetOffset = unchecked((sbyte)data[candidate + 2]);
            }
            else
            {
                continue;
            }

            if (HasControlFlowBarrierBetween(data, first, candidate, functionEndOffset))
            {
                continue;
            }

            callOffset = candidate;
            callLength = candidateLength;
            vtableOffset = targetOffset;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<CrossFireClientShellAssetMapping> ExtractSameStemFunctionPairs(
        byte[] data,
        PeSection code,
        IReadOnlyDictionary<uint, ResourcePath> pathsByRva,
        IReadOnlyList<RuntimeFunction> runtimeFunctions)
    {
        // Some ClientShell tables load path literals into a scratch register,
        // select/copy that register later, then use a shared call site. Those
        // paths do not appear in the direct R8/RDX call-reference pass above.
        // Recover only exact model/texture filename-stem matches referenced
        // from the same runtime function; ambiguous stems are deliberately
        // excluded instead of cross-producted.
        int codeFileOffset = checked((int)code.RawOffset);
        int codeByteCount = checked((int)code.RawSize);
        var pathsByFunction = new Dictionary<uint, List<(int InstructionOffset, ResourcePath Path)>>();

        for (int instructionOffset = codeFileOffset; instructionOffset <= codeFileOffset + codeByteCount - 7; instructionOffset++)
        {
            byte rex = data[instructionOffset];
            if (rex is < 0x48 or > 0x4F ||
                data[instructionOffset + 1] != 0x8D ||
                (data[instructionOffset + 2] & 0xC7) != 0x05)
            {
                continue;
            }

            uint instructionRva = checked(code.VirtualAddress + (uint)(instructionOffset - codeFileOffset));
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(instructionOffset + 3, sizeof(int)));
            long targetRva = (long)instructionRva + 7 + displacement;
            if (targetRva is < 0 or > uint.MaxValue ||
                !pathsByRva.TryGetValue((uint)targetRva, out ResourcePath? path) ||
                FindRuntimeFunction(runtimeFunctions, instructionRva) is not RuntimeFunction function)
            {
                continue;
            }

            if (!pathsByFunction.TryGetValue(function.BeginRva, out List<(int InstructionOffset, ResourcePath Path)>? references))
            {
                references = [];
                pathsByFunction.Add(function.BeginRva, references);
            }

            references.Add((instructionOffset, path));
        }

        var mappings = new Dictionary<string, CrossFireClientShellAssetMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (List<(int InstructionOffset, ResourcePath Path)> references in pathsByFunction.Values)
        {
            var modelPathsByStem = references
                .Where(reference => reference.Path.Kind == ResourcePathKind.Model)
                .GroupBy(reference => Path.GetFileNameWithoutExtension(reference.Path.Path), StringComparer.OrdinalIgnoreCase);
            var texturePathsByStem = references
                .Where(reference => reference.Path.Kind == ResourcePathKind.Texture)
                .GroupBy(reference => Path.GetFileNameWithoutExtension(reference.Path.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            foreach (var modelGroup in modelPathsByStem)
            {
                if (!texturePathsByStem.TryGetValue(modelGroup.Key, out var matchingTextures))
                {
                    continue;
                }

                (int InstructionOffset, ResourcePath Path)[] matchingModels = modelGroup
                    .DistinctBy(reference => reference.Path.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                (int InstructionOffset, ResourcePath Path)[] distinctTextures = matchingTextures
                    .DistinctBy(reference => reference.Path.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (matchingModels.Length != 1 || distinctTextures.Length != 1 ||
                    Math.Abs(matchingModels[0].InstructionOffset - distinctTextures[0].InstructionOffset) > 0x200)
                {
                    continue;
                }

                var mapping = new CrossFireClientShellAssetMapping(
                    matchingModels[0].Path.Path,
                    distinctTextures[0].Path.Path);
                mappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
            }
        }

        return mappings.Values.ToArray();
    }

    private static IReadOnlyList<CrossFireClientShellAssetMapping> ExtractAdjacentCodePathPairs(
        byte[] data,
        PeSection code,
        IReadOnlyDictionary<uint, ResourcePath> pathsByRva,
        IReadOnlyList<RuntimeFunction> runtimeFunctions)
    {
        // A small set of branch tables emits the model and its selected texture
        // as consecutive RIP-relative LEAs into scratch registers (not R8/RDX).
        // Consecutive seven-byte path loads cannot cross a branch, and retaining
        // only model-then-texture pairs avoids cross-producting switch arms.
        int codeFileOffset = checked((int)code.RawOffset);
        int codeByteCount = checked((int)code.RawSize);
        var references = new Dictionary<int, ResourcePath>();
        for (int instructionOffset = codeFileOffset; instructionOffset <= codeFileOffset + codeByteCount - 7; instructionOffset++)
        {
            byte rex = data[instructionOffset];
            if (rex is < 0x48 or > 0x4F ||
                data[instructionOffset + 1] != 0x8D ||
                (data[instructionOffset + 2] & 0xC7) != 0x05)
            {
                continue;
            }

            uint instructionRva = checked(code.VirtualAddress + (uint)(instructionOffset - codeFileOffset));
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(instructionOffset + 3, sizeof(int)));
            long targetRva = (long)instructionRva + 7 + displacement;
            if (targetRva is < 0 or > uint.MaxValue ||
                !pathsByRva.TryGetValue((uint)targetRva, out ResourcePath? path) ||
                FindRuntimeFunction(runtimeFunctions, instructionRva) is null)
            {
                continue;
            }

            references[instructionOffset] = path;
        }

        var mappings = new Dictionary<string, CrossFireClientShellAssetMapping>(StringComparer.OrdinalIgnoreCase);
        foreach ((int modelOffset, ResourcePath modelPath) in references)
        {
            if (modelPath.Kind != ResourcePathKind.Model ||
                !references.TryGetValue(modelOffset + 7, out ResourcePath? texturePath) ||
                texturePath.Kind != ResourcePathKind.Texture)
            {
                continue;
            }

            var mapping = new CrossFireClientShellAssetMapping(modelPath.Path, texturePath.Path);
            mappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
        }

        return mappings.Values.ToArray();
    }

    private static IReadOnlyList<CrossFireClientShellAssetMapping> ExtractAdjacentUnreferencedPathPairs(
        byte[] data,
        PeSection code,
        IReadOnlyDictionary<uint, ResourcePath> pathsByRva)
    {
        // A few path constants are emitted as adjacent model/texture pairs in
        // .rdata but have no direct LEA xref (for example, standalone weapon
        // pieces). Accept only a unique exact filename-stem match within a very
        // small address window, and only when neither literal is code-referenced.
        int codeFileOffset = checked((int)code.RawOffset);
        int codeByteCount = checked((int)code.RawSize);
        uint codeEndRva = checked(code.VirtualAddress + code.RawSize);
        var referencedPathRvas = new HashSet<uint>();
        for (int instructionOffset = codeFileOffset; instructionOffset <= codeFileOffset + codeByteCount - 7; instructionOffset++)
        {
            byte rex = data[instructionOffset];
            if (rex is < 0x48 or > 0x4F ||
                data[instructionOffset + 1] != 0x8D ||
                (data[instructionOffset + 2] & 0xC7) != 0x05)
            {
                continue;
            }

            uint instructionRva = checked(code.VirtualAddress + (uint)(instructionOffset - codeFileOffset));
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(instructionOffset + 3, sizeof(int)));
            long targetRva = (long)instructionRva + 7 + displacement;
            if (targetRva is >= 0 and <= uint.MaxValue && (uint)targetRva < codeEndRva)
            {
                continue;
            }

            if (targetRva is >= 0 and <= uint.MaxValue && pathsByRva.ContainsKey((uint)targetRva))
            {
                referencedPathRvas.Add((uint)targetRva);
            }
        }

        var modelPathsByStem = pathsByRva
            .Where(entry => entry.Value.Kind == ResourcePathKind.Model && !referencedPathRvas.Contains(entry.Key))
            .GroupBy(entry => Path.GetFileNameWithoutExtension(entry.Value.Path), StringComparer.OrdinalIgnoreCase);
        var texturePathsByStem = pathsByRva
            .Where(entry => entry.Value.Kind == ResourcePathKind.Texture && !referencedPathRvas.Contains(entry.Key))
            .GroupBy(entry => Path.GetFileNameWithoutExtension(entry.Value.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var mappings = new Dictionary<string, CrossFireClientShellAssetMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelGroup in modelPathsByStem)
        {
            if (!texturePathsByStem.TryGetValue(modelGroup.Key, out var textureEntries))
            {
                continue;
            }

            KeyValuePair<uint, ResourcePath>[] modelEntries = modelGroup
                .DistinctBy(entry => entry.Value.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            KeyValuePair<uint, ResourcePath>[] distinctTextureEntries = textureEntries
                .DistinctBy(entry => entry.Value.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modelEntries.Length != 1 || distinctTextureEntries.Length != 1 ||
                Math.Abs((long)modelEntries[0].Key - distinctTextureEntries[0].Key) > 0x80)
            {
                continue;
            }

            var mapping = new CrossFireClientShellAssetMapping(
                modelEntries[0].Value.Path,
                distinctTextureEntries[0].Value.Path);
            mappings.TryAdd($"{mapping.ModelReference}\0{mapping.TextureReference}", mapping);
        }

        return mappings.Values.ToArray();
    }

    private static bool HasControlFlowBarrierBetween(
        byte[] data,
        int startOffset,
        int endOffset,
        int functionEndOffset)
    {
        int cursor = startOffset;
        int end = Math.Min(Math.Min(endOffset, functionEndOffset), data.Length);
        while (cursor < end)
        {
            if (!TryGetGapInstructionLength(data, cursor, end, out int instructionLength, out bool isControlFlow))
            {
                return true;
            }

            if (isControlFlow)
            {
                return true;
            }

            cursor += instructionLength;
        }

        return cursor != endOffset;
    }

    private static bool TryGetGapInstructionLength(
        byte[] data,
        int offset,
        int endOffset,
        out int instructionLength,
        out bool isControlFlow)
    {
        instructionLength = 0;
        isControlFlow = false;
        if (offset < 0 || offset >= endOffset)
        {
            return false;
        }

        byte opcode = data[offset];
        if (opcode is 0xE9 or 0xEB or 0xC3 or 0xC2 || opcode is >= 0x70 and <= 0x7F)
        {
            isControlFlow = true;
            return true;
        }

        if (opcode == 0x0F && offset + 1 < endOffset && data[offset + 1] is >= 0x80 and <= 0x8F)
        {
            isControlFlow = true;
            return true;
        }

        int cursor = offset;
        while (cursor < endOffset && data[cursor] is 0x66 or 0x67 or 0xF2 or 0xF3 or 0x2E or 0x36 or 0x3E or 0x26 or 0x64 or 0x65)
        {
            cursor++;
        }

        byte rex = 0;
        if (cursor < endOffset && data[cursor] is >= 0x40 and <= 0x4F)
        {
            rex = data[cursor++];
            if (cursor >= endOffset) return false;
        }

        opcode = data[cursor++];
        if (opcode == 0xE8)
        {
            instructionLength = cursor - offset + sizeof(int);
            return instructionLength <= endOffset - offset;
        }

        if (opcode is >= 0xB8 and <= 0xBF)
        {
            int immediateLength = (rex & 0x08) != 0 ? sizeof(long) : sizeof(int);
            instructionLength = cursor - offset + immediateLength;
            return instructionLength <= endOffset - offset;
        }

        if (opcode == 0xBA || opcode == 0xB9)
        {
            instructionLength = cursor - offset + sizeof(int);
            return instructionLength <= endOffset - offset;
        }

        if (opcode == 0x0F)
        {
            if (cursor >= endOffset) return false;
            byte secondOpcode = data[cursor++];
            if (secondOpcode is >= 0x80 and <= 0x8F)
            {
                isControlFlow = true;
                return true;
            }

            bool hasTwoByteModRm = secondOpcode is 0x10 or 0x11 or 0x1F or 0x28 or 0x29 or
                0x6F or 0x7F or 0xAF or 0xB6 or 0xB7 or 0xBE or 0xBF ||
                secondOpcode is >= 0x40 and <= 0x4F;
            if (!hasTwoByteModRm || !TryGetModRmInstructionLength(data, cursor, endOffset, out int twoByteModRmLength))
            {
                return false;
            }

            instructionLength = cursor - offset + twoByteModRmLength;
            return instructionLength <= endOffset - offset;
        }

        if (opcode == 0x90 || opcode is >= 0x50 and <= 0x5F)
        {
            instructionLength = cursor - offset;
            return true;
        }

        if (opcode == 0xFF && cursor < endOffset && data[cursor] == 0x15)
        {
            instructionLength = cursor - offset + 5;
            return instructionLength <= endOffset - offset;
        }

        bool hasModRm = opcode is 0x01 or 0x03 or 0x09 or 0x0B or 0x21 or 0x23 or 0x29 or 0x2B or 0x31 or 0x33 or
            0x39 or 0x3B or 0x84 or 0x85 or 0x88 or 0x89 or 0x8A or 0x8B or 0x8D or
            0x80 or 0x81 or 0x82 or 0x83 or 0xC0 or 0xC1 or 0xC6 or 0xC7 or 0xD1 or 0xD3 or 0xF6 or 0xF7 or 0xFF;
        int immediateLengthForModRm = opcode switch
        {
            0x80 or 0x82 or 0xC0 => 1,
            0x81 or 0xC1 => sizeof(int),
            0x83 => 1,
            0xC6 => 1,
            0xC7 => sizeof(int),
            _ => 0
        };
        if (!hasModRm || !TryGetModRmInstructionLength(data, cursor, endOffset, out int modRmLength))
        {
            return false;
        }

        instructionLength = cursor - offset + modRmLength + immediateLengthForModRm;
        return instructionLength <= endOffset - offset;
    }

    private static bool TryGetModRmInstructionLength(
        byte[] data,
        int modRmOffset,
        int endOffset,
        out int instructionLength)
    {
        instructionLength = 0;
        if (modRmOffset >= endOffset) return false;
        byte modRm = data[modRmOffset];
        int mod = modRm >> 6;
        int baseIndex = modRm & 0x07;
        int cursor = modRmOffset + 1;
        if (mod != 3 && baseIndex == 4)
        {
            if (cursor >= endOffset) return false;
            baseIndex = data[cursor++] & 0x07;
        }

        if (mod == 0 && baseIndex == 5)
        {
            cursor += sizeof(int);
        }
        else if (mod == 1)
        {
            cursor++;
        }
        else if (mod == 2)
        {
            cursor += sizeof(int);
        }

        if (cursor > endOffset) return false;
        instructionLength = cursor - modRmOffset;
        return true;
    }

    private static bool TryReadRcxAddressArgument(
        byte[] data,
        int callOffset,
        int functionFileStart,
        out string? baseRegister,
        out int displacement)
    {
        baseRegister = null;
        displacement = 0;
        int firstCandidate = Math.Max(functionFileStart, callOffset - 8);
        for (int instructionOffset = firstCandidate; instructionOffset < callOffset; instructionOffset++)
        {
            if (!TryDecodeRcxLea(data, instructionOffset, out int instructionLength, out string? candidateBase, out int candidateDisplacement) ||
                instructionOffset + instructionLength != callOffset)
            {
                continue;
            }

            baseRegister = candidateBase;
            displacement = candidateDisplacement;
            return true;
        }

        return false;
    }

    private static bool TryReadRcxAddressBeforeResourcePath(
        byte[] data,
        int resourcePathInstructionOffset,
        int callOffset,
        int functionFileStart,
        out string? baseRegister,
        out int displacement)
    {
        baseRegister = null;
        displacement = 0;
        if (callOffset - resourcePathInstructionOffset > 16)
        {
            return false;
        }

        int firstCandidate = Math.Max(functionFileStart, resourcePathInstructionOffset - 16);
        for (int instructionOffset = firstCandidate; instructionOffset < resourcePathInstructionOffset; instructionOffset++)
        {
            if (!TryDecodeRcxLea(data, instructionOffset, out int instructionLength, out string? candidateBase, out int candidateDisplacement) ||
                instructionOffset + instructionLength > resourcePathInstructionOffset)
            {
                continue;
            }

            baseRegister = candidateBase;
            displacement = candidateDisplacement;
        }

        return baseRegister is not null;
    }

    private static bool TryDecodeRcxLea(
        byte[] data,
        int offset,
        out int instructionLength,
        out string? baseRegister,
        out int displacement)
    {
        instructionLength = 0;
        baseRegister = null;
        displacement = 0;
        if (offset < 0 || offset > data.Length - 4)
        {
            return false;
        }

        byte rex = data[offset];
        if ((rex & 0xF8) != 0x48 || (rex & 0x04) != 0 || data[offset + 1] != 0x8D)
        {
            return false;
        }

        byte modRm = data[offset + 2];
        int mod = modRm >> 6;
        int destinationRegister = (modRm >> 3) & 0x07;
        int baseIndex = modRm & 0x07;
        if (mod == 3 || destinationRegister != 1)
        {
            return false;
        }

        int cursor = offset + 3;
        if (baseIndex == 4)
        {
            if (cursor >= data.Length)
            {
                return false;
            }

            byte sib = data[cursor++];
            int index = (sib >> 3) & 0x07;
            baseIndex = sib & 0x07;
            if (index != 4 || ((rex & 0x02) != 0))
            {
                return false;
            }

            if (mod == 0 && baseIndex == 5 && (rex & 0x01) == 0)
            {
                return false;
            }
        }
        else if (mod == 0 && baseIndex == 5 && (rex & 0x01) == 0)
        {
            return false;
        }

        if (mod == 0)
        {
            displacement = 0;
        }
        else if (mod == 1)
        {
            if (cursor >= data.Length) return false;
            displacement = unchecked((sbyte)data[cursor++]);
        }
        else
        {
            if (cursor > data.Length - sizeof(int)) return false;
            displacement = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, sizeof(int)));
            cursor += sizeof(int);
        }

        baseIndex += (rex & 0x01) != 0 ? 8 : 0;
        baseRegister = RegisterNames[baseIndex];
        instructionLength = cursor - offset;
        return true;
    }

    private static readonly string[] RegisterNames =
    [
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"
    ];

    private readonly record struct PeSection(
        string Name,
        uint VirtualAddress,
        uint VirtualSize,
        uint RawOffset,
        uint RawSize);

    private readonly record struct RuntimeFunction(uint BeginRva, uint EndRva);

    private enum ResourcePathKind
    {
        Model,
        Texture
    }

    private enum ResourceArgumentRegister
    {
        R8,
        Rdx
    }

    private enum DirectRegistrationRole
    {
        Model,
        Texture
    }

    private sealed record ResourcePath(string Path, ResourcePathKind Kind);

    private sealed record DirectRegistrationReference(
        string Path,
        DirectRegistrationRole Role,
        int PathInstructionOffset,
        int CallOffset,
        int CallLength);

    private sealed record ResourceCallReference(
        string Path,
        ResourcePathKind Kind,
        string CallTarget,
        ResourceArgumentRegister ArgumentRegister,
        string? ArgumentBaseRegister,
        int ArgumentDisplacement,
        int CallRva,
        int PathInstructionOffset);
}
