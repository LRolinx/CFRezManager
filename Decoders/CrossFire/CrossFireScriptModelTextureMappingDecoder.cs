using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal sealed record CrossFireScriptModelTextureMapping(
    string ModelReference,
    IReadOnlyList<string> TextureReferences,
    IReadOnlyList<string> ModelFieldNames,
    IReadOnlyList<string> TextureFieldNames);

/// <summary>
/// Extracts explicit model/texture field associations from decoded CrossFire
/// scripts. Values are paired only within the same Squirrel table scope and in
/// field order; arbitrary model and texture strings in a script are not paired.
/// </summary>
internal static class CrossFireScriptModelTextureMappingDecoder
{
    private static readonly Regex DuplicateSeparatorRegex = new("/{2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] ModelExtensions = [".ltb", ".lta", ".ltc", ".dat"];
    private static readonly string[] TextureExtensions = [".dtx", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp"];

    public static IReadOnlyList<CrossFireScriptModelTextureMapping> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<ScriptField> fields = ParseStringAssignments(text);
        var mappings = new List<CrossFireScriptModelTextureMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<int, ScriptField> scope in fields
                     .Where(field => field.ScopeId >= 0)
                     .GroupBy(field => field.ScopeId))
        {
            ScriptField? currentModel = null;
            var textures = new List<ScriptField>();
            foreach (ScriptField field in scope.OrderBy(field => field.Position))
            {
                if (IsModelField(field.Name) && IsModelPath(field.Value))
                {
                    AddCurrentMapping(currentModel, textures, mappings, seen);
                    currentModel = field;
                    textures = [];
                }
                else if (currentModel is not null && IsTextureField(field.Name) && IsTexturePath(field.Value))
                {
                    textures.Add(field);
                }
            }

            AddCurrentMapping(currentModel, textures, mappings, seen);
        }

        return mappings;
    }

    private static void AddCurrentMapping(
        ScriptField? model,
        IReadOnlyList<ScriptField> textureFields,
        ICollection<CrossFireScriptModelTextureMapping> mappings,
        ISet<string> seen)
    {
        if (model is null || textureFields.Count == 0)
        {
            return;
        }

        ScriptField[] uniqueTextures = textureFields
            .GroupBy(field => field.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        string key = model.Value + "|" + string.Join("|", uniqueTextures.Select(field => field.Value));
        if (!seen.Add(key))
        {
            return;
        }

        mappings.Add(new CrossFireScriptModelTextureMapping(
            model.Value,
            uniqueTextures.Select(field => field.Value).ToArray(),
            [model.Name],
            uniqueTextures.Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static List<ScriptField> ParseStringAssignments(string text)
    {
        var fields = new List<ScriptField>();
        var scopes = new Stack<int>();
        int position = 0;
        while (position < text.Length)
        {
            char current = text[position];
            if (current == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                position += 2;
                while (position < text.Length && text[position] is not ('\r' or '\n')) position++;
                continue;
            }

            if (current == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                int commentEnd = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = commentEnd < 0 ? text.Length : commentEnd + 2;
                continue;
            }

            if (current == '"')
            {
                position = SkipQuotedString(text, position);
                continue;
            }

            if (current == '{')
            {
                scopes.Push(position++);
                continue;
            }

            if (current == '}')
            {
                if (scopes.Count > 0) scopes.Pop();
                position++;
                continue;
            }

            if (!IsIdentifierStart(current))
            {
                position++;
                continue;
            }

            int identifierStart = position++;
            while (position < text.Length && IsIdentifierPart(text[position])) position++;
            string name = text[identifierStart..position];
            int afterIdentifier = SkipWhitespace(text, position);
            if (afterIdentifier >= text.Length || text[afterIdentifier] != '=' ||
                afterIdentifier + 1 < text.Length && text[afterIdentifier + 1] == '=')
            {
                continue;
            }

            int valueStart = SkipWhitespace(text, afterIdentifier + 1);
            if (valueStart >= text.Length || text[valueStart] != '"')
            {
                continue;
            }

            if (!TryReadQuotedString(text, valueStart, out string rawValue, out int afterValue))
            {
                position = valueStart + 1;
                continue;
            }

            string normalized = NormalizePath(UnescapeString(rawValue));
            int scopeId = scopes.Count > 0 ? scopes.Peek() : -1;
            if (IsModelPath(normalized) || IsTexturePath(normalized))
            {
                fields.Add(new ScriptField(name, normalized, scopeId, identifierStart));
            }

            position = afterValue;
        }

        return fields;
    }

    private static bool IsModelField(string name)
    {
        return string.Equals(name, "BlockName", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Model", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("HitBox", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Effect", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Motion", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("RenderStyle", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Child", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Map", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextureField(string name)
    {
        return name.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Skin", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("TexFile", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Albedo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelPath(string value)
    {
        string extension = Path.GetExtension(value);
        return ModelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
               (value.StartsWith("Models/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ArmModel/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTexturePath(string value)
    {
        string extension = Path.GetExtension(value);
        return TextureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
               (value.StartsWith("ModelTextures/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UCC/ModelAssets/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        return DuplicateSeparatorRegex.Replace(normalized, "/");
    }

    private static string UnescapeString(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            char next = value[++index];
            builder.Append(next switch
            {
                '\\' => '\\',
                '"' => '"',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => '\\'
            });
            if (next is not ('\\' or '"' or 'n' or 'r' or 't')) builder.Append(next);
        }

        return builder.ToString();
    }

    private static bool TryReadQuotedString(string text, int start, out string value, out int afterValue)
    {
        value = string.Empty;
        afterValue = start;
        var builder = new StringBuilder();
        for (int index = start + 1; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '"')
            {
                value = builder.ToString();
                afterValue = index + 1;
                return true;
            }

            if (current == '\\' && index + 1 < text.Length)
            {
                builder.Append(current);
                builder.Append(text[++index]);
                continue;
            }

            builder.Append(current);
        }

        return false;
    }

    private static int SkipQuotedString(string text, int start)
    {
        for (int index = start + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
            }
            else if (text[index] == '"')
            {
                return index + 1;
            }
        }

        return text.Length;
    }

    private static int SkipWhitespace(string text, int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        return position;
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private sealed record ScriptField(string Name, string Value, int ScopeId, int Position);
}
