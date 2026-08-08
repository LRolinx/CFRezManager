using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfControl = System.Windows.Controls.Control;

namespace CFRezManager;

public partial class SettingsWindow : Window
{
    private string _languageCode;
    private AppTheme _theme;

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string Chinese, string English)>
        {
            ["Settings"] = ("\u8bbe\u7f6e", "Settings"),
            ["Language"] = ("\u8bed\u8a00", "Language"),
            ["Theme"] = ("\u4e3b\u9898", "Theme"),
            ["Export"] = ("\u5bfc\u51fa", "Export"),
            ["Cache"] = ("\u7f13\u5b58", "Cache"),
            ["Light"] = ("\u4eae\u8272", "Light"),
            ["Dark"] = ("\u6697\u8272", "Dark"),
            ["ExportSettings"] = ("\u5bfc\u51fa\u683c\u5f0f\u8bbe\u7f6e...", "Export Format Settings..."),
            ["ClearThumbnailCache"] = ("\u6e05\u9664\u7f29\u7565\u56fe", "Clear Thumbnails"),
            ["Close"] = ("\u5173\u95ed", "Close")
        };

    public SettingsWindow(string languageCode, AppTheme theme)
    {
        _languageCode = NormalizeLanguageCode(languageCode);
        _theme = theme;

        InitializeComponent();
        WindowThemeHelper.Apply(this, _theme);
        ApplyLanguage(_languageCode);
        SelectLanguage(_languageCode);
        SelectTheme(_theme);
    }

    public event Action<string>? LanguageChanged;
    public event Action<AppTheme>? ThemeChanged;
    public event EventHandler? ExportSettingsRequested;
    public event EventHandler? ClearThumbnailCacheRequested;

    public void ApplyLanguage(string languageCode)
    {
        _languageCode = NormalizeLanguageCode(languageCode);

        Title = T("Settings");
        TitleText.Text = T("Settings");
        LanguageLabelText.Text = T("Language");
        ThemeLabelText.Text = T("Theme");
        ExportLabelText.Text = T("Export");
        CacheLabelText.Text = T("Cache");
        ChineseLanguageButton.Content = "\u4e2d\u6587";
        EnglishLanguageButton.Content = "English";
        LightThemeButton.Content = T("Light");
        DarkThemeButton.Content = T("Dark");
        ExportSettingsButton.Content = T("ExportSettings");
        ClearThumbnailCacheButton.Content = T("ClearThumbnailCache");
        CloseButton.Content = T("Close");

        RefreshSelectionButtons();
    }

    public void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        WindowThemeHelper.Apply(this, theme);
        SelectTheme(theme);
    }

    public void SetBusy(bool isBusy)
    {
        ChineseLanguageButton.IsEnabled = !isBusy;
        EnglishLanguageButton.IsEnabled = !isBusy;
        LightThemeButton.IsEnabled = !isBusy;
        DarkThemeButton.IsEnabled = !isBusy;
        ExportSettingsButton.IsEnabled = !isBusy;
        ClearThumbnailCacheButton.IsEnabled = !isBusy;
        CloseButton.IsEnabled = !isBusy;
    }

    public void SetCacheStatus(string text)
    {
        CacheStatusText.Text = text;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string languageCode })
        {
            return;
        }

        languageCode = NormalizeLanguageCode(languageCode);
        if (string.Equals(languageCode, _languageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _languageCode = languageCode;
        RefreshSelectionButtons();
        LanguageChanged?.Invoke(languageCode);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string themeValue })
        {
            return;
        }

        AppTheme theme = ThemeManager.Parse(themeValue);
        if (theme == _theme)
        {
            return;
        }

        _theme = theme;
        RefreshSelectionButtons();
        ThemeChanged?.Invoke(theme);
    }

    private void ClearThumbnailCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearThumbnailCacheRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ExportSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SelectLanguage(string languageCode)
    {
        RefreshSelectionButtons();
    }

    private void SelectTheme(AppTheme theme)
    {
        RefreshSelectionButtons();
    }

    private void RefreshSelectionButtons()
    {
        SetOptionButtonSelected(ChineseLanguageButton, string.Equals(_languageCode, "zh", StringComparison.OrdinalIgnoreCase));
        SetOptionButtonSelected(EnglishLanguageButton, string.Equals(_languageCode, "en", StringComparison.OrdinalIgnoreCase));
        SetOptionButtonSelected(LightThemeButton, _theme == AppTheme.Light);
        SetOptionButtonSelected(DarkThemeButton, _theme == AppTheme.Dark);
    }

    private void SetOptionButtonSelected(WpfButton button, bool isSelected)
    {
        if (isSelected)
        {
            button.SetResourceReference(WpfControl.BackgroundProperty, "AppSelectionHoverBrush");
            button.SetResourceReference(WpfControl.BorderBrushProperty, "AppSelectionBorderBrush");
            button.SetResourceReference(WpfControl.ForegroundProperty, "AppTextBrush");
            return;
        }

        button.SetResourceReference(WpfControl.BackgroundProperty, "AppButtonBrush");
        button.SetResourceReference(WpfControl.BorderBrushProperty, "AppButtonBorderBrush");
        button.SetResourceReference(WpfControl.ForegroundProperty, "AppTextBrush");
    }

    private string T(string key)
    {
        if (!Texts.TryGetValue(key, out (string Chinese, string English) text))
        {
            return key;
        }

        return string.Equals(_languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? text.English
            : text.Chinese;
    }

    private static string NormalizeLanguageCode(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh";
    }
}
