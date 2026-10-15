namespace CFRezManager;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        if (LithTechLtbHeaderInspectCommand.IsInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            int exitCode = LithTechLtbHeaderInspectCommand.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (LithTechTextureMapAuditCommand.IsInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            int exitCode = LithTechTextureMapAuditCommand.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (LithTechObjExportCommand.IsInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            int exitCode = LithTechObjExportCommand.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (CfgScanCommand.IsInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            int exitCode = CfgScanCommand.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (CfgDecodeCommand.IsInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            int exitCode = CfgDecodeCommand.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (PreviewTool.IsPreviewInvocation(e.Args))
        {
            LocalizedText.UseSavedLanguage();
            ThemeManager.ApplySavedTheme();
            string? errorMessage = null;
            if (PreviewTool.TryGetPreviewPath(e.Args, out string previewPath) &&
                PreviewTool.TryCreateWindow(previewPath, out System.Windows.Window? previewWindow, out errorMessage) &&
                previewWindow is not null)
            {
                MainWindow = previewWindow;
                previewWindow.Show();
            }
            else
            {
                System.Windows.MessageBox.Show(
                    errorMessage ?? LocalizedText.T("PreviewUnsupportedFile"),
                    LocalizedText.T("PreviewFailedTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                Shutdown(1);
            }

            return;
        }

        LocalizedText.UseSavedLanguage();
        ThemeManager.ApplySavedTheme();
        ThumbnailDiskCache.TryClearLegacyCache();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
