using System.Windows;
using System.Windows.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace CFRezManager;

public partial class ExportOptionsWindow : Window
{
    private sealed record FormatRow(
        ImageExportFormatDefinition Definition,
        TextBlock NameText,
        TextBlock DescriptionText,
        WpfComboBox ModeComboBox,
        ImageExportMode InitialMode);

    private readonly string _languageCode;
    private readonly bool _continueExport;
    private readonly List<FormatRow> _rows = [];

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string Chinese, string English)>
        {
            ["Title"] = ("导出格式设置", "Export Format Settings"),
            ["Description"] = ("请为每类可解码图片资源选择导出源文件，或导出解码后的标准 PNG。", "For each decodable image resource type, choose whether to export the source file or a decoded standard PNG."),
            ["Format"] = ("文件格式", "File format"),
            ["KnownLayouts"] = ("已知编码 / 压缩布局", "Known encoding / compression layouts"),
            ["Output"] = ("导出内容", "Export content"),
            ["SourceFile"] = ("源文件（原扩展名）", "Source file (original extension)"),
            ["DecodedPng"] = ("解码图片（.png）", "Decoded image (.png)"),
            ["Fallback"] = ("无法识别为图片的 BIN（例如脚本或配置表）以及解码失败的文件会保留原扩展名和源数据。当前选择会自动保存。", "BIN files that are not images (such as scripts or configuration tables), and files that fail to decode, keep their original extension and source data. These choices are saved automatically."),
            ["DoNotShowAgain"] = ("下次导出不再显示此窗口（可在设置中重新打开）", "Do not show this window on future exports (reopen it from Settings)"),
            ["Cancel"] = ("取消", "Cancel"),
            ["Export"] = ("继续导出", "Continue"),
            ["Save"] = ("保存设置", "Save Settings")
        };

    public ExportOptionsWindow(
        string languageCode,
        AppTheme theme,
        IReadOnlyDictionary<string, string>? savedModes,
        bool doNotShowAgain,
        bool continueExport)
    {
        _languageCode = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        _continueExport = continueExport;

        InitializeComponent();
        WindowThemeHelper.Apply(this, theme);
        DoNotShowAgainCheckBox.IsChecked = doNotShowAgain;
        BuildFormatRows(savedModes);
        ApplyLanguage();
    }

    internal ImageExportOptions? SelectedOptions { get; private set; }
    internal bool DoNotShowAgain => DoNotShowAgainCheckBox.IsChecked == true;

    private void BuildFormatRows(IReadOnlyDictionary<string, string>? savedModes)
    {
        foreach (ImageExportFormatDefinition definition in ImageExportOptions.KnownFormats)
        {
            var nameText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(14, 14, 8, 14),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var descriptionText = new TextBlock
            {
                Margin = new Thickness(14, 14, 14, 14),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("AppMutedTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var modeComboBox = new WpfComboBox
            {
                Margin = new Thickness(14, 12, 14, 12),
                MinHeight = 34,
                VerticalAlignment = VerticalAlignment.Center
            };

            ImageExportMode savedMode = ImageExportOptions.ResolveSavedMode(savedModes, definition);

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
            Grid.SetColumn(descriptionText, 1);
            Grid.SetColumn(modeComboBox, 2);
            rowGrid.Children.Add(nameText);
            rowGrid.Children.Add(descriptionText);
            rowGrid.Children.Add(modeComboBox);

            var border = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)FindResource("AppBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = rowGrid
            };
            FormatRowsPanel.Children.Add(border);
            _rows.Add(new FormatRow(definition, nameText, descriptionText, modeComboBox, savedMode));
        }
    }

    private void ApplyLanguage()
    {
        Title = T("Title");
        TitleText.Text = T("Title");
        DescriptionText.Text = T("Description");
        FormatHeaderText.Text = T("Format");
        KnownLayoutsHeaderText.Text = T("KnownLayouts");
        OutputHeaderText.Text = T("Output");
        FallbackText.Text = T("Fallback");
        DoNotShowAgainCheckBox.Content = T("DoNotShowAgain");
        CancelButton.Content = T("Cancel");
        ExportButton.Content = T(_continueExport ? "Export" : "Save");

        foreach (FormatRow row in _rows)
        {
            int selection = row.ModeComboBox.SelectedIndex >= 0
                ? row.ModeComboBox.SelectedIndex
                : row.InitialMode == ImageExportMode.DecodedPng ? 1 : 0;
            row.NameText.Text = IsEnglish ? row.Definition.EnglishName : row.Definition.ChineseName;
            row.DescriptionText.Text = IsEnglish ? row.Definition.EnglishDescription : row.Definition.ChineseDescription;
            row.ModeComboBox.Items.Clear();
            row.ModeComboBox.Items.Add(T("SourceFile"));
            row.ModeComboBox.Items.Add(T("DecodedPng"));
            row.ModeComboBox.SelectedIndex = selection;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var modes = new Dictionary<string, ImageExportMode>(StringComparer.OrdinalIgnoreCase);
        foreach (FormatRow row in _rows)
        {
            modes[row.Definition.Extension] = row.ModeComboBox.SelectedIndex == 1
                ? ImageExportMode.DecodedPng
                : ImageExportMode.SourceFile;
        }

        SelectedOptions = new ImageExportOptions(modes);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool IsEnglish => string.Equals(_languageCode, "en", StringComparison.OrdinalIgnoreCase);

    private string T(string key)
    {
        if (!Texts.TryGetValue(key, out (string Chinese, string English) text))
        {
            return key;
        }

        return IsEnglish ? text.English : text.Chinese;
    }
}
