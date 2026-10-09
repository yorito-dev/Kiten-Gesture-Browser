using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Services;

namespace TabBrowser.Views;

public partial class QuickLookPdfView : UserControl, IDisposable
{
    private readonly string _filePath;
    private readonly CancellationTokenSource _loadCancellation = new();
    private Uri? _documentUri;
    private bool _loadStarted;
    private bool _isDisposed;

    public QuickLookPdfView(string filePath)
    {
        InitializeComponent();
        _filePath = Path.GetFullPath(filePath);
        Loaded += QuickLookPdfView_Loaded;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= QuickLookPdfView_Loaded;
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        if (PdfWebView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= CoreWebView2_NavigationStarting;
            core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            core.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            core.ProcessFailed -= CoreWebView2_ProcessFailed;
            try
            {
                core.Stop();
            }
            catch (InvalidOperationException)
            {
            }
        }

        PdfWebView.Dispose();
    }

    private async void QuickLookPdfView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadStarted || _isDisposed)
        {
            return;
        }

        _loadStarted = true;
        try
        {
            if (!File.Exists(_filePath))
            {
                throw new FileNotFoundException("PDFファイルが見つかりません。", _filePath);
            }

            var cancellationToken = _loadCancellation.Token;
            var environment = await QuickLookPdfEnvironmentProvider.Shared.GetAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await PdfWebView.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            ConfigureCore(PdfWebView.CoreWebView2);
            _documentUri = new Uri(_filePath);
            PdfWebView.Source = _documentUri;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            ShowError("PDFを表示できませんでした: " + ex.Message);
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.NavigationStarting += CoreWebView2_NavigationStarting;
        core.NavigationCompleted += CoreWebView2_NavigationCompleted;
        core.NewWindowRequested += CoreWebView2_NewWindowRequested;
        core.ProcessFailed += CoreWebView2_ProcessFailed;
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_documentUri is null
            || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var destination)
            || !string.Equals(destination.LocalPath, _documentUri.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError($"PDFを表示できませんでした: {e.WebErrorStatus}");
    }

    private static void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ShowError("PDF表示用WebView2が停止しました。QuickLookを開き直してください。");
    }

    private void ShowError(string message)
    {
        if (_isDisposed)
        {
            return;
        }

        StateText.Text = message;
        StateText.Foreground = System.Windows.Media.Brushes.IndianRed;
        StatePanel.Visibility = Visibility.Visible;
    }

}
