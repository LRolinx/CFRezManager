using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CFRezManager;

public partial class CftPreviewWindow : Window
{
    private readonly CrossFireCftTable _table;
    private readonly string _fileName;

    internal CftPreviewWindow(string fileName, CrossFireCftTable table)
    {
        _table = table;
        _fileName = fileName;
        InitializeComponent();
        WindowThemeHelper.Apply(this, ThemeManager.Parse(UserSettings.Load().Theme));
        Rect area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, area.Width - 80);
        MaxHeight = Math.Max(MinHeight, area.Height - 80);
        Title = fileName;
        FileNameText.Text = fileName;
        ExportButton.Content = LocalizedText.T("CftExportXlsx");
        StatusText.Text = LocalizedText.Format("CftTableSize", table.Rows.Count, table.Columns.Count + 1);
        BuildGrid();
    }

    private void BuildGrid()
    {
        var data = new DataTable { Locale = CultureInfo.InvariantCulture };
        data.Columns.Add("RecordId", typeof(int));
        AddColumn("RecordId", "RecordId", numeric: true, width: 88);
        for (int index = 0; index < _table.Columns.Count; index++)
        {
            string key = "C" + index.ToString(CultureInfo.InvariantCulture);
            bool numeric = _table.ColumnTypes[index] != 1;
            data.Columns.Add(key, numeric ? typeof(int) : typeof(string));
            AddColumn(key, _table.Columns[index], numeric, width: numeric ? 150 : 240);
        }
        TableGrid.FrozenColumnCount = 1;
        foreach (CrossFireCftRow row in _table.Rows)
        {
            var cells = new object?[_table.Columns.Count + 1];
            cells[0] = row.RecordId;
            for (int index = 0; index < _table.Columns.Count; index++)
            {
                cells[index + 1] = row.ColumnValues.Count == _table.Columns.Count
                    ? row.ColumnValues[index] : row.Values.GetValueOrDefault(_table.Columns[index]);
            }
            data.Rows.Add(cells);
        }
        TableGrid.ItemsSource = data.DefaultView;
    }

    private void AddColumn(string key, string name, bool numeric, int width)
    {
        var binding = new Binding("[" + key + "]") { Mode = BindingMode.OneWay };
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 3, 8, 3)));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, numeric ? TextAlignment.Right : TextAlignment.Left));
        textStyle.Setters.Add(new Setter(ToolTipProperty, binding));
        TableGrid.Columns.Add(new DataGridTextColumn
        {
            Header = name, Binding = binding, SortMemberPath = key, Width = width,
            MinWidth = 70, ElementStyle = textStyle
        });
    }

    private void TableGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizedText.T("CftExportXlsx"), Filter = LocalizedText.T("CftXlsxFilter"),
            DefaultExt = ".xlsx", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_fileName) + ".xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportButton.IsEnabled = false;
        try
        {
            CftSpreadsheetExportResult result = await Task.Run(() => CftSpreadsheetExporter.Export(_table, dialog.FileName));
            StatusText.Text = LocalizedText.Format("CftExportComplete", result.Rows, result.Path);
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(this, error.Message, LocalizedText.T("CftExportFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ExportButton.IsEnabled = true; }
    }
}
