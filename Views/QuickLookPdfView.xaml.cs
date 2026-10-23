using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Services;

namespace TabBrowser.Views;

public partial class QuickLookPdfView : UserControl, IDisposable
{
    private readonly string _filePath;
    private readonly QuickLookPdfEnvironmentScope _environmentScope;
    private Task _loadTask = Task.CompletedTask;
    private readonly CancellationTokenSource _loadCancellation = new();
    private Uri? _documentUri;
    private bool _loadStarted;
    private bool _isDisposed;
    private WindowTaskSupervisor? _releaseOwner;
    internal Task ReleaseCompletion { get; private set; } = Task.CompletedTask;

    public QuickLookPdfView(string filePath, bool isPrivate = false)
    {
        InitializeComponent();
        _filePath = Path.GetFullPath(filePath);
        _environmentScope = new QuickLookPdfEnvironmentScope(isPrivate);
        Loaded += QuickLookPdfView_Loaded;
    }

    internal QuickLookPdfView(string filePath, QuickLookPdfEnvironmentScope scope, Task initialization, WindowTaskSupervisor owner)
        : this(filePath)
    {
        _environmentScope = scope;
        _loadTask = initialization;
        _releaseOwner = owner;
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
        CaptureReleaseOwner();
        ReleaseCompletion = _environmentScope.ReleaseAsync(_loadTask, ReleaseWebViewResources);
        _releaseOwner?.TryTrack("quicklook-pdf-release", ReleaseCompletion);
    }

    internal void SetReleaseOwner(WindowTaskSupervisor? owner) => _releaseOwner ??= owner;

    private void CaptureReleaseOwner()
    {
        if (_releaseOwner is not null) return;
        for (var owner = Window.GetWindow(this); owner is not null; owner = owner.Owner)
        {
            if (owner is MainWindow main)
            {
                _releaseOwner = main.QuickLookReleases;
                break;
            }
        }
    }

    private void ReleaseWebViewResources()
    {
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

        CaptureReleaseOwner();
        _loadStarted = true;
        _loadTask = LoadDocumentAsync();
        await _loadTask;
    }

    private async Task LoadDocumentAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                throw new FileNotFoundException("PDFファイルが見つかりません。", _filePath);
            }

            var cancellationToken = _loadCancellation.Token;
            var environment = await _environmentScope.GetAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await PdfWebView.EnsureCoreWebView2Async(environment, _environmentScope.CreateControllerOptions(environment));
            cancellationToken.ThrowIfCancellationRequested();
            _environmentScope.VerifyProfile(PdfWebView.CoreWebView2.Profile.IsInPrivateModeEnabled,
                PdfWebView.CoreWebView2.Profile.ProfileName);
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
