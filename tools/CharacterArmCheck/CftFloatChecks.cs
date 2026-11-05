using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

static partial class Program
{
    static void RunCftFloatChecks(string run)
    {
        float[] values = [1.4f, -2.25f, 12.5f, .125f, 0f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), float.Epsilon, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(new byte[8718]); stream.Position = 0; writer.Write(Encoding.ASCII.GetBytes("su")); stream.Position = 6; writer.Write(3); writer.Write(values.Length);
            string[] columns = ["_Name", "_Scale", "_Counter"]; int[] types = [1, 5, 3];
            for (int c = 0; c < 3; c++) { stream.Position = 14 + c * 64; writer.Write(Encoding.ASCII.GetBytes(columns[c])); stream.Position = 14 + 128 * 64 + c * 4; writer.Write(types[c]); }
            stream.Position = 8718;
            for (int row = 0; row < values.Length; row++) { writer.Write(1500 + row); byte[] label = Encoding.Unicode.GetBytes("Value" + row); writer.Write((ushort)label.Length); writer.Write(label); writer.Write(values[row]); writer.Write(-10 - row); }
            writer.Write(Encoding.ASCII.GetBytes("END" + new string(' ', 61))); writer.Write((byte)0);
        }
        byte[] data = stream.ToArray(); object?[] args = [data, null];
        Check("CFT float fixture decodes", (bool)Call("CrossFireCftTableDecoder", "TryDecode", args)!); object table = args[1]!;
        object[] rows = ((System.Collections.IEnumerable)table.GetType().GetProperty("Rows")!.GetValue(table)!).Cast<object>().ToArray();
        Check("CFT float rows preserve their source count", rows.Length == values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            object?[] cells = ((System.Collections.IEnumerable)rows[row].GetType().GetProperty("ColumnValues")!.GetValue(rows[row])!).Cast<object?>().ToArray();
            Check("CFT type 5 preserves IEEE float32 bits at row " + row, cells[1] is float value && BitConverter.SingleToInt32Bits(value) == BitConverter.SingleToInt32Bits(values[row]));
            Check("CFT integer and text columns stay typed at row " + row, cells[0] is string s && s == "Value" + row && cells[2] is int n && n == -10 - row);
        }
        string xlsx = Path.Combine(run, "float-cells.xlsx"); CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            MethodInfo export = App.GetType("CFRezManager.CftSpreadsheetExporter", true)!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "Export" && m.GetParameters()[0].ParameterType == typeof(byte[]));
            export.Invoke(null, [data, xlsx]);
        }
        finally { CultureInfo.CurrentCulture = saved; }
        using (var zip = ZipFile.OpenRead(xlsx))
        using (var input = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var sheet = XDocument.Load(input); var cells = sheet.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
            for (int row = 0; row < values.Length; row++)
            {
                var cell = cells["C" + (row + 2)];
                if (float.IsFinite(values[row]))
                {
                    string text = cell.Element(ns + "v")!.Value;
                    Check("XLSX float cell is numeric and culture independent at row " + row, cell.Attribute("t") is null && !text.Contains(',') && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && BitConverter.SingleToInt32Bits(parsed) == BitConverter.SingleToInt32Bits(values[row]));
                }
                else Check("XLSX nonfinite float is explicit text at row " + row, (string?)cell.Attribute("t") == "inlineStr" && cell.Descendants(ns + "t").Single().Value == values[row].ToString("R", CultureInfo.InvariantCulture));
                Check("XLSX neighboring integer cell is unchanged at row " + row, cells["D" + (row + 2)].Element(ns + "v")!.Value == (-10 - row).ToString(CultureInfo.InvariantCulture));
            }
            Check("XLSX float export retains source row order", sheet.Descendants(ns + "row").Skip(1).Select(r => r.Elements(ns + "c").First().Element(ns + "v")!.Value).SequenceEqual(Enumerable.Range(1500, values.Length).Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }
        var app = new CFRezManager.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = (Window)Activator.CreateInstance(App.GetType("CFRezManager.CftPreviewWindow", true)!, BindingFlags.NonPublic | BindingFlags.Instance, null, ["Float.CFT", table], CultureInfo.InvariantCulture)!;
        try
        {
            var grid = (DataGrid)window.FindName("TableGrid"); var view = (DataView)grid.ItemsSource;
            Check("CFT preview retains typed float and integer columns", view.Table!.Columns[2].DataType == typeof(float) && view.Table.Columns[3].DataType == typeof(int));
            Check("CFT preview retains fractional values", view[0]["C1"] is float value && value == 1.4f);
            view.RowFilter = "C0 IN ('Value0','Value1','Value2','Value3')"; view.Sort = "C1 ASC";
            Check("CFT preview sorts float values numerically", view.Cast<DataRowView>().Select(r => (float)r["C1"]).SequenceEqual(new[] { -2.25f, .125f, 1.4f, 12.5f }));
            Check("CFT preview keeps read-only grid and frozen RecordId", grid.IsReadOnly && grid.FrozenColumnCount == 1);
        }
        finally { window.Close(); app.Shutdown(); }
    }
}
