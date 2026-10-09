using System.Windows;
using TabBrowser.Services;

namespace TabBrowser;

public partial class RestoreIncompleteWarningWindow : Window
{
    public RestoreIncompleteWarningWindow()
    {
        InitializeComponent();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        WindowThemeService.ApplyDarkTitleBar(this);
    }
}
