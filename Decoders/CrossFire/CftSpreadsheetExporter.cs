using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CFRezManager;

internal sealed record CftSpreadsheetExportResult(string Path, int Rows, int Columns);

internal static class CftSpreadsheetExporter
{
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly Regex EscapedTextPattern = new("_x[0-9A-Fa-f]{4}_", RegexOptions.CultureInvariant);

    public static CftSpreadsheetExportResult Export(byte[] data, string outputPath)
    {
        if (!CrossFireCftTableDecoder.TryDecodeWithDiagnostics(data, out CrossFireCftTable? table, out _, out _, out string? error) || table is null)
            throw new InvalidDataException(error ?? "The CFT table could not be decoded.");
        return Export(table, outputPath);
    }

    internal static CftSpreadsheetExportResult Export(CrossFireCftTable table, string outputPath)
    {
        if (!Path.GetExtension(outputPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output file must have the .xlsx extension.", nameof(outputPath));
        if (table.Rows.Count > 1_048_575 || table.Columns.Count > 16_383)
            throw new InvalidDataException("The CFT table exceeds the XLSX row or column limit.");

        string path = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteContentTypes(archive);
                WriteRelationships(archive, "_rels/.rels", [("rId1", "officeDocument", "xl/workbook.xml")]);
                WriteRelationships(archive, "xl/_rels/workbook.xml.rels", [("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml")]);
                WriteXml(archive, "xl/workbook.xml", writer =>
                {
                    writer.WriteStartElement("workbook", SheetNamespace);
                    writer.WriteAttributeString("xmlns", "r", null, RelationshipNamespace);
                    writer.WriteStartElement("sheets", SheetNamespace);
                    writer.WriteStartElement("sheet", SheetNamespace);
                    writer.WriteAttributeString("name", "Data");
                    writer.WriteAttributeString("sheetId", "1");
                    writer.WriteAttributeString("r", "id", RelationshipNamespace, "rId1");
                    writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
                });
                WriteStyles(archive);
                WriteWorksheet(archive, table);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new(path, table.Rows.Count, table.Columns.Count + 1);
    }

    private static void WriteXml(ZipArchive archive, string name, Action<XmlWriter> write)
    {
        using Stream stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize, CloseOutput = false
        });
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        WriteXml(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", ContentTypeNamespace);
            foreach (var (extension, type) in new[] { ("rels", "application/vnd.openxmlformats-package.relationships+xml"), ("xml", "application/xml") })
            {
                writer.WriteStartElement("Default", ContentTypeNamespace);
                writer.WriteAttributeString("Extension", extension); writer.WriteAttributeString("ContentType", type);
                writer.WriteEndElement();
            }
            foreach (var (part, type) in new[] { ("/xl/workbook.xml", "sheet.main"), ("/xl/worksheets/sheet1.xml", "worksheet"), ("/xl/styles.xml", "styles") })
            {
                writer.WriteStartElement("Override", ContentTypeNamespace);
                writer.WriteAttributeString("PartName", part);
                writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml." + type + "+xml");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        });
    }

    private static void WriteRelationships(ZipArchive archive, string name, (string Id, string Type, string Target)[] relationships)
    {
        WriteXml(archive, name, writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
            foreach (var relationship in relationships)
            {
                writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
                writer.WriteAttributeString("Id", relationship.Id);
                writer.WriteAttributeString("Type", RelationshipNamespace + "/" + relationship.Type);
                writer.WriteAttributeString("Target", relationship.Target);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        });
    }

    private static void WriteStyles(ZipArchive archive)
    {
        WriteXml(archive, "xl/styles.xml", writer =>
        {
            writer.WriteStartElement("styleSheet", SheetNamespace);
            writer.WriteStartElement("fonts", SheetNamespace); writer.WriteAttributeString("count", "2");
            for (int index = 0; index < 2; index++)
            {
                writer.WriteStartElement("font", SheetNamespace);
                if (index == 1) { writer.WriteStartElement("b", SheetNamespace); writer.WriteEndElement(); }
                WriteStyleValue(writer, "sz", "val", "11"); WriteStyleValue(writer, "name", "val", "Calibri");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("fills", SheetNamespace); writer.WriteAttributeString("count", "3");
            foreach (string pattern in new[] { "none", "gray125", "solid" })
            {
                writer.WriteStartElement("fill", SheetNamespace);
                writer.WriteStartElement("patternFill", SheetNamespace); writer.WriteAttributeString("patternType", pattern);
                if (pattern == "solid") WriteStyleValue(writer, "fgColor", "rgb", "FFDCE6F1");
                writer.WriteEndElement(); writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("borders", SheetNamespace); writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("border", SheetNamespace); writer.WriteEndElement(); writer.WriteEndElement();
            writer.WriteStartElement("cellStyleXfs", SheetNamespace); writer.WriteAttributeString("count", "1");
            WriteStyleXf(writer, 0, 0, 0); writer.WriteEndElement();
            writer.WriteStartElement("cellXfs", SheetNamespace); writer.WriteAttributeString("count", "3");
            WriteStyleXf(writer, 0, 0, 0); WriteStyleXf(writer, 0, 1, 2); WriteStyleXf(writer, 49, 0, 0);
            writer.WriteEndElement();
            writer.WriteStartElement("cellStyles", SheetNamespace); writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("cellStyle", SheetNamespace);
            writer.WriteAttributeString("name", "Normal"); writer.WriteAttributeString("xfId", "0"); writer.WriteAttributeString("builtinId", "0");
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        });
    }

    private static void WriteStyleValue(XmlWriter writer, string name, string attribute, string value)
    {
        writer.WriteStartElement(name, SheetNamespace); writer.WriteAttributeString(attribute, value); writer.WriteEndElement();
    }

    private static void WriteStyleXf(XmlWriter writer, int format, int font, int fill)
    {
        writer.WriteStartElement("xf", SheetNamespace);
        writer.WriteAttributeString("numFmtId", format.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", "0"); writer.WriteEndElement();
    }

    private static void WriteWorksheet(ZipArchive archive, CrossFireCftTable table)
    {
        WriteXml(archive, "xl/worksheets/sheet1.xml", writer =>
        {
            string range = "A1:" + ColumnName(table.Columns.Count) + (table.Rows.Count + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("worksheet", SheetNamespace);
            WriteStyleValue(writer, "dimension", "ref", range);
            writer.WriteStartElement("sheetViews", SheetNamespace);
            writer.WriteStartElement("sheetView", SheetNamespace); writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane", SheetNamespace);
            writer.WriteAttributeString("xSplit", "1"); writer.WriteAttributeString("ySplit", "1"); writer.WriteAttributeString("topLeftCell", "B2");
            writer.WriteAttributeString("activePane", "bottomRight"); writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            WriteStyleValue(writer, "sheetFormatPr", "defaultRowHeight", "15");
            writer.WriteStartElement("cols", SheetNamespace);
            for (int column = 0; column <= table.Columns.Count; column++)
            {
                writer.WriteStartElement("col", SheetNamespace);
                string ordinal = (column + 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteAttributeString("min", ordinal); writer.WriteAttributeString("max", ordinal);
                writer.WriteAttributeString("width", column == 0 ? "12" : "36"); writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData", SheetNamespace);
            writer.WriteStartElement("row", SheetNamespace); writer.WriteAttributeString("r", "1");
            WriteTextCell(writer, "A1", "RecordId", header: true);
            for (int column = 0; column < table.Columns.Count; column++)
                WriteTextCell(writer, ColumnName(column + 1) + "1", table.Columns[column], header: true);
            writer.WriteEndElement();
            for (int index = 0; index < table.Rows.Count; index++)
            {
                CrossFireCftRow row = table.Rows[index];
                string rowNumber = (index + 2).ToString(CultureInfo.InvariantCulture);
                writer.WriteStartElement("row", SheetNamespace); writer.WriteAttributeString("r", rowNumber);
                WriteIntegerCell(writer, "A" + rowNumber, row.RecordId);
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    object? value = row.ColumnValues.Count == table.Columns.Count
                        ? row.ColumnValues[column] : row.Values.GetValueOrDefault(table.Columns[column]);
                    string reference = ColumnName(column + 1) + rowNumber;
                    if (value is int integer) WriteIntegerCell(writer, reference, integer);
                    else if (value is string text) WriteTextCell(writer, reference, text);
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            WriteStyleValue(writer, "autoFilter", "ref", range);
            writer.WriteEndElement();
        });
    }

    private static void WriteIntegerCell(XmlWriter writer, string reference, int value)
    {
        writer.WriteStartElement("c", SheetNamespace); writer.WriteAttributeString("r", reference);
        writer.WriteElementString("v", SheetNamespace, value.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
    }

    private static void WriteTextCell(XmlWriter writer, string reference, string value, bool header = false)
    {
        if (value.Length > 32_767) throw new InvalidDataException($"Cell {reference} exceeds the XLSX text limit.");
        writer.WriteStartElement("c", SheetNamespace); writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr"); writer.WriteAttributeString("s", header ? "1" : "2");
        writer.WriteStartElement("is", SheetNamespace); writer.WriteStartElement("t", SheetNamespace);
        writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        string escaped = EscapedTextPattern.Replace(value, match => "_x005F_" + match.Value[1..]);
        var text = new StringBuilder(escaped.Length);
        foreach (char character in escaped)
        {
            if (character < 0x20 && character is not ('\t' or '\r' or '\n') || character is '\uFFFE' or '\uFFFF')
                text.Append("_x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            else text.Append(character);
        }
        writer.WriteString(text.ToString()); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
    }

    private static string ColumnName(int index)
    {
        string result = string.Empty;
        do { result = (char)('A' + index % 26) + result; index = index / 26 - 1; } while (index >= 0);
        return result;
    }
}
