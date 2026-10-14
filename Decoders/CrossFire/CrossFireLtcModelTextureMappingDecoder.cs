using System.Globalization;
using System.IO;

namespace CFRezManager;

internal sealed record CrossFireLtcModelTextureMapping(
    int RowIndex,
    string ModelReference,
    IReadOnlyList<string> TextureReferences,
    IReadOnlyList<CrossFireLtcTextureFieldReference> TextureFieldReferences,
    IReadOnlyList<string> AuxiliaryTextureReferences,
    IReadOnlyList<string> IdentityAliases,
    string RowType,
    string ModelField,
    IReadOnlyList<string> TextureFields);

internal sealed record CrossFireLtcTextureFieldReference(string FieldName, string Path);

/// <summary>
/// Extracts only same-row LTC bindings whose model and texture fields have a
/// known CrossFire role relationship. It deliberately does not cross-pair all
/// paths found in a table row.
/// </summary>
internal static class CrossFireLtcModelTextureMappingDecoder
{
    private static readonly string[] ModelExtensions = [".ltb", ".lta", ".ltc", ".dat"];
    private static readonly string[] TextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".bin"];

    public static IReadOnlyList<CrossFireLtcModelTextureMapping> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        bool hasModelPath = text.Contains("Models/", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Models\\", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Model/", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Model\\", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("ArmModel\\", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("UCC\\ModelAssets\\", StringComparison.OrdinalIgnoreCase);
        bool hasTexturePath = text.Contains("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("ModelTextures\\", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("TexFX/", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("TexFX\\", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("UCC\\ModelAssets\\", StringComparison.OrdinalIgnoreCase);
        if (!hasModelPath || !hasTexturePath)
        {
            return [];
        }

        var mappings = new List<CrossFireLtcModelTextureMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rowIndex = 0;
        foreach (LtcList row in new LtcParser(text).ParseAll())
        {
            int currentRowIndex = rowIndex++;
            string rowType = GetAtom(row.Items.FirstOrDefault()) ?? string.Empty;
            if (rowType.Length == 0)
            {
                continue;
            }

            List<LtcField> fields = row.Items
                .OfType<LtcList>()
                .Select(TryReadField)
                .Where(field => field is not null)
                .Cast<LtcField>()
                .ToList();
            string[] identityAliases = fields
                .Where(field => IsIdentityField(field.Name) ||
                                field.Name.Equals("WeaponIndex", StringComparison.OrdinalIgnoreCase))
                .SelectMany(field => field.Name.Equals("WeaponIndex", StringComparison.OrdinalIgnoreCase)
                    ? field.Values.Select(value => $"WeaponIndex:{value}")
                    : field.Values)
                .Select(value => value.Trim())
                .Where(value => value.Length is > 0 and <= 256 && !value.Any(char.IsControl))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (LtcField modelField in fields)
            {
                if (!IsModelField(modelField.Name))
                {
                    continue;
                }

                foreach (string rawModelReference in modelField.Values)
                {
                    string modelReference = NormalizeResourcePath(rawModelReference);
                    if (!IsModelPath(modelReference))
                    {
                        continue;
                    }

                    string[] compatibleFields = GetCompatibleTextureFields(modelField.Name, fields);
                    if (compatibleFields.Length == 0)
                    {
                        continue;
                    }

                    LtcField[] compatibleTextureFields = fields
                        .Where(field => compatibleFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                    string[] textures = compatibleTextureFields
                        .SelectMany(field => field.Values)
                        .Select(NormalizeResourcePath)
                        .Where(IsTexturePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    CrossFireLtcTextureFieldReference[] textureFieldReferences = compatibleTextureFields
                        .SelectMany(field => field.Values
                            .Select(value => new CrossFireLtcTextureFieldReference(field.Name, NormalizeResourcePath(value))))
                        .Where(reference => IsTexturePath(reference.Path))
                        .Distinct()
                        .ToArray();
                    string[] auxiliaryTextures = compatibleTextureFields
                        .SelectMany(field => field.Values)
                        .Select(NormalizeResourcePath)
                        .Where(IsAuxiliaryTexturePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (textures.Length == 0 && auxiliaryTextures.Length == 0)
                    {
                        continue;
                    }

                    string[] usedFields = fields
                        .Where(field => compatibleFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase) &&
                                        field.Values.Any(value =>
                                        {
                                            string normalized = NormalizeResourcePath(value);
                                            return IsTexturePath(normalized) || IsAuxiliaryTexturePath(normalized);
                                        }))
                        .Select(field => field.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    string deduplicationKey = string.Join(
                        "|",
                        rowType,
                        currentRowIndex.ToString(CultureInfo.InvariantCulture),
                        modelField.Name,
                        modelReference,
                        string.Join("|", textureFieldReferences.Select(reference => $"{reference.FieldName}:{reference.Path}")),
                        string.Join("|", textures),
                        string.Join("|", auxiliaryTextures),
                        string.Join("|", identityAliases));
                    if (seen.Add(deduplicationKey))
                    {
                        mappings.Add(new CrossFireLtcModelTextureMapping(
                            currentRowIndex,
                            modelReference,
                            textures,
                            textureFieldReferences,
                            auxiliaryTextures,
                            identityAliases,
                            rowType,
                            modelField.Name,
                            usedFields));
                    }
                }
            }
        }

        return mappings;
    }

    private static string[] GetCompatibleTextureFields(string modelField, IReadOnlyList<LtcField> fields)
    {
        if (modelField.StartsWith("GRModelFile", StringComparison.OrdinalIgnoreCase))
        {
            return GetVariantTextureFields(modelField, fields, "GRModelFile", "GRTextureFile");
        }

        if (modelField.StartsWith("BLModelFile", StringComparison.OrdinalIgnoreCase))
        {
            return GetVariantTextureFields(modelField, fields, "BLModelFile", "BLTextureFile");
        }

        if (modelField.StartsWith("GRSocketItemModelFile", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("GRWSocketItemModelFile", StringComparison.OrdinalIgnoreCase))
        {
            return ["GRTexFile", "GRTexFile2"];
        }

        if (modelField.StartsWith("BLSocketItemModelFile", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("BLWSocketItemModelFile", StringComparison.OrdinalIgnoreCase))
        {
            return ["BLTexFile", "BLTexFile2"];
        }

        if (modelField.StartsWith("ProjectileModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            return ["ProjectileSkinFileName"];
        }

        if (modelField.StartsWith("ThrowModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            return GetVariantTextureFields(modelField, fields, "ThrowModelFileName", "ThrowSkinFileName");
        }

        if (modelField.Contains("ChangeModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            return GetVariantTextureFields(modelField, fields, "ChangeModelFileName", "ChangeSkinFileName");
        }

        if (modelField.StartsWith("DebrisModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            return ["DebrisSkinFileName"];
        }

        if (modelField.StartsWith("SocketModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = modelField["SocketModelFileName".Length..];
            return ["SocketSkinFileName" + suffix];
        }

        if (modelField.StartsWith("PViewModelFileName", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("PreViewModelFileName", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("LeftHandModelFileName", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("WLeftHandModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            string modelPrefix = GetMatchingPrefix(
                modelField,
                "WLeftHandModelFileName",
                "LeftHandModelFileName",
                "PreViewModelFileName",
                "PViewModelFileName");
            return GetVariantTextureFields(modelField, fields, modelPrefix, "PViewSkinFileName");
        }

        if (modelField.StartsWith("ModelFileName", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("WModelFileName", StringComparison.OrdinalIgnoreCase) ||
            modelField.StartsWith("MountedStandModelFileName", StringComparison.OrdinalIgnoreCase))
        {
            string modelPrefix = GetMatchingPrefix(
                modelField,
                "MountedStandModelFileName",
                "WModelFileName",
                "ModelFileName");
            return GetVariantTextureFields(modelField, fields, modelPrefix, "SkinFileName");
        }

        return [];
    }

    private static string[] GetVariantTextureFields(
        string modelField,
        IReadOnlyList<LtcField> fields,
        string modelPrefix,
        string texturePrefix)
    {
        int modelPrefixIndex = modelField.LastIndexOf(modelPrefix, StringComparison.OrdinalIgnoreCase);
        if (modelPrefixIndex < 0)
        {
            return [];
        }

        int modelSuffixStart = modelPrefixIndex + modelPrefix.Length;
        string modelSuffix = modelField[modelSuffixStart..];
        if (modelSuffix.Length > 0 && !IsSafeVariantSuffix(modelSuffix))
        {
            return [];
        }

        LtcField[] textureFields = fields
            .Where(field => field.Name.StartsWith(texturePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(field => IsSafeVariantSuffix(field.Name[texturePrefix.Length..]))
            .ToArray();
        if (modelSuffix.Length > 0)
        {
            string exactName = texturePrefix + modelSuffix;
            string[] exact = textureFields
                .Where(field => field.Name.Equals(exactName, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Name)
                .ToArray();
            if (exact.Length > 0)
            {
                return exact;
            }

            return textureFields
                .Where(field => field.Name.Equals(texturePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Name)
                .ToArray();
        }

        // Numbered/suffixed texture fields can be additional material slots. Treat one as
        // a variant pair only when the row also has a model field with that same suffix.
        bool hasSuffixedModelVariant = fields.Any(field =>
        {
            int siblingPrefixIndex = field.Name.LastIndexOf(modelPrefix, StringComparison.OrdinalIgnoreCase);
            if (siblingPrefixIndex < 0)
            {
                return false;
            }

            string suffix = field.Name[(siblingPrefixIndex + modelPrefix.Length)..];
            return suffix.Length > 0 && IsSafeVariantSuffix(suffix) &&
                   textureFields.Any(texture => texture.Name.Equals(texturePrefix + suffix, StringComparison.OrdinalIgnoreCase));
        });
        if (hasSuffixedModelVariant)
        {
            return textureFields
                .Where(field => field.Name.Equals(texturePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Name)
                .ToArray();
        }

        return textureFields.Select(field => field.Name).ToArray();
    }

    private static bool IsSafeVariantSuffix(string suffix)
    {
        return suffix.All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string GetMatchingPrefix(string value, params string[] prefixes)
    {
        return prefixes.First(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsModelField(string name)
    {
        return name.Contains("Model", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("RenderStyle", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Child", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Motion", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Map", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, "ButePath", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelPath(string value)
    {
        string extension = Path.GetExtension(value);
        return HasKnownResourceRoot(value) &&
               (extension.Length == 0 || ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsIdentityField(string fieldName)
    {
        return fieldName.Equals("ItemName", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("ResourceName", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("WeaponName", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("ItemCode", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("ItemID", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Equals("ItemIndex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTexturePath(string value)
    {
        return (value.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase)) &&
               TextureExtensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAuxiliaryTexturePath(string value)
    {
        return value.StartsWith("TexFX/", StringComparison.OrdinalIgnoreCase) &&
               TextureExtensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasKnownResourceRoot(string value)
    {
        return value.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Model/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourcePath(string value)
    {
        return value.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static LtcField? TryReadField(LtcList list)
    {
        if (list.Items.Count < 2 || GetAtom(list.Items[0]) is not { Length: > 0 } name)
        {
            return null;
        }

        string[] values = list.Items.Skip(1)
            .OfType<LtcAtom>()
            .Select(atom => atom.Value)
            .ToArray();
        return values.Length == 0 ? null : new LtcField(name, values);
    }

    private static string? GetAtom(object? value) => value is LtcAtom atom ? atom.Value : null;

    private sealed record LtcField(string Name, IReadOnlyList<string> Values);

    private sealed class LtcList
    {
        public List<object> Items { get; } = [];
    }

    private sealed class LtcAtom(string value)
    {
        public string Value { get; } = value;
    }

    private sealed class LtcParser(string text)
    {
        private const int MaximumDepth = 128;
        private int _position;

        public IEnumerable<LtcList> ParseAll()
        {
            while (SkipTrivia())
            {
                if (text[_position] != '(')
                {
                    _position++;
                    continue;
                }

                yield return ParseList(0);
            }
        }

        private LtcList ParseList(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException("LTC table nesting is too deep.");
            }

            _position++;
            var list = new LtcList();
            while (SkipTrivia())
            {
                char current = text[_position];
                if (current == ')')
                {
                    _position++;
                    return list;
                }

                list.Items.Add(current == '(' ? ParseList(depth + 1) : ParseAtom());
            }

            throw new InvalidDataException("LTC table ended inside a record.");
        }

        private LtcAtom ParseAtom()
        {
            if (text[_position] != '"')
            {
                int start = _position;
                while (_position < text.Length &&
                       !char.IsWhiteSpace(text[_position]) &&
                       text[_position] is not '(' and not ')')
                {
                    _position++;
                }

                return new LtcAtom(text[start.._position]);
            }

            _position++;
            var builder = new System.Text.StringBuilder();
            while (_position < text.Length)
            {
                char current = text[_position++];
                if (current == '"')
                {
                    return new LtcAtom(builder.ToString());
                }

                if (current == '\\' && _position < text.Length)
                {
                    char escaped = text[_position];
                    if (escaped is '"' or '\\')
                    {
                        builder.Append(escaped);
                        _position++;
                    }
                    else if ((escaped is 'n' or 'r' or 't') && !IsResourcePathPrefix(builder))
                    {
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            _ => '\t'
                        });
                        _position++;
                    }
                    else
                    {
                        // Keep Windows path separators such as "\nano" intact.
                        // Interpreting every path-segment prefix as a C escape
                        // corrupts directories named Nano/Nano6 into controls.
                        builder.Append('\\');
                    }
                }
                else
                {
                    builder.Append(current);
                }
            }

            throw new InvalidDataException("LTC table contains an unterminated string.");
        }

        private static bool IsResourcePathPrefix(System.Text.StringBuilder value)
        {
            string prefix = value.ToString().Replace('\\', '/');
            return prefix.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
                   prefix.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
                   prefix.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
                   prefix.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase) ||
                   prefix.StartsWith("UCC\\ModelAssets/", StringComparison.OrdinalIgnoreCase);
        }

        private bool SkipTrivia()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                    continue;
                }

                if (text[_position] == '#' || text[_position] == ';')
                {
                    SkipLine();
                    continue;
                }

                if (text[_position] == '/' && _position + 1 < text.Length)
                {
                    if (text[_position + 1] == '/')
                    {
                        _position += 2;
                        SkipLine();
                        continue;
                    }

                    if (text[_position + 1] == '*')
                    {
                        _position += 2;
                        int end = text.IndexOf("*/", _position, StringComparison.Ordinal);
                        _position = end < 0 ? text.Length : end + 2;
                        continue;
                    }
                }

                return true;
            }

            return false;
        }

        private void SkipLine()
        {
            while (_position < text.Length && text[_position] is not '\r' and not '\n')
            {
                _position++;
            }
        }
    }
}
