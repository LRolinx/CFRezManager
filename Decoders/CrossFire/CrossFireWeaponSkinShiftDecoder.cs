using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal static class CrossFireWeaponSkinShiftDecoder
{
    private static readonly Regex DeclaredCountRegex = new(
        @"\bVVIPSkinWeaponShift_ScriptIndex_Count\s*<-\s*(?<count>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EntryStartRegex = new(
        @"(?m)^[ \t]*VVIPSkinWeaponShift_ScriptIndex_(?<index>\d+)[ \t]*<-[ \t]*\{",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StringFieldRegex = new(
        @"(?m)^[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*""(?<value>(?:\\.|[^""\\])*)""[ \t]*,?[ \t]*(?://[^\r\n]*)?\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string text, out CrossFireWeaponSkinShiftDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text) ||
            !text.Contains("VVIPSkinWeaponShift_ScriptIndex_", StringComparison.Ordinal))
        {
            return false;
        }

        Match countMatch = DeclaredCountRegex.Match(text);
        int? declaredCount = countMatch.Success &&
                             int.TryParse(countMatch.Groups["count"].Value, out int count)
            ? count
            : null;

        var entries = new List<CrossFireWeaponSkinShiftEntry>();
        var malformedIndices = new List<int>();
        var seenIndices = new HashSet<int>();
        foreach (Match entryMatch in EntryStartRegex.Matches(text))
        {
            if (!int.TryParse(entryMatch.Groups["index"].Value, out int scriptIndex))
            {
                continue;
            }

            int openBrace = entryMatch.Index + entryMatch.Length - 1;
            int closeBrace = FindMatchingBrace(text, openBrace);
            if (closeBrace < 0)
            {
                malformedIndices.Add(scriptIndex);
                continue;
            }

            if (!seenIndices.Add(scriptIndex))
            {
                malformedIndices.Add(scriptIndex);
                continue;
            }

            string body = text[(openBrace + 1)..closeBrace];
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match fieldMatch in StringFieldRegex.Matches(body))
            {
                fields[fieldMatch.Groups["name"].Value] = DecodeStringLiteral(fieldMatch.Groups["value"].Value);
            }

            entries.Add(new CrossFireWeaponSkinShiftEntry(scriptIndex, fields));
        }

        document = new CrossFireWeaponSkinShiftDocument(
            declaredCount,
            entries.OrderBy(entry => entry.ScriptIndex).ToArray(),
            malformedIndices.Distinct().Order().ToArray());
        return true;
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        char stringDelimiter = '\0';
        bool escaped = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = openBrace; index < text.Length; index++)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }

                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == stringDelimiter)
                {
                    inString = false;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                inString = true;
                stringDelimiter = current;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string DecodeStringLiteral(string value)
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

            char escaped = value[++index];
            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => escaped
            });
        }

        return builder.ToString();
    }
}

internal sealed record CrossFireWeaponSkinShiftDocument(
    int? DeclaredEntryCount,
    IReadOnlyList<CrossFireWeaponSkinShiftEntry> Entries,
    IReadOnlyList<int> MalformedScriptIndices);

internal sealed record CrossFireWeaponSkinShiftEntry(
    int ScriptIndex,
    IReadOnlyDictionary<string, string> Fields)
{
    public string? GetValue(string fieldName)
    {
        return Fields.TryGetValue(fieldName, out string? value) ? value : null;
    }
}
