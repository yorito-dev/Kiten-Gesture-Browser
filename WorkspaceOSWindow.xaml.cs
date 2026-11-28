using System.Windows;
using System.Windows.Interop;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using TabBrowser.Views;

namespace TabBrowser;

public partial class WorkspaceOSWindow : Window
{
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private readonly WorkspaceOSViewModel _viewModel;
    private HwndSource? _windowSource;

    public Guid InputScopeId { get; }

    public WorkspaceOSWindow(
        WorkspaceService workspaceService,
        Guid? initialWorkspaceId,
        AppSettings settings,
        WorkspaceTabPort tabs,
        WorkspaceMiniPlayerPort miniPlayers,
        WorkspaceWindowPort windows,
        WorkspaceThumbnailPort thumbnails,
        Guid inputScopeId,
        Func<long?> tryBeginGestureInteraction,
        Func<long?> tryBeginRightWheelInteraction,
        Action<long> completeInteraction,
        Func<string?, string?, Task> executeGestureCommandAsync,
        Action<string> openLocalVideoInMiniPlayer,
        EventHandler<QuickLookPaneLayoutChangedEventArgs> quickLookPaneLayoutChanged,
        EventHandler<QuickLookTextDisplayChangedEventArgs> quickLookTextDisplayChanged,
        Guid? shellCopyOwnerId = null)
    {
        InitializeComponent();
        InputScopeId = inputScopeId;
        _viewModel = new WorkspaceOSViewModel(
            workspaceService,
            initialWorkspaceId,
            tabs,
            miniPlayers,
            windows,
            thumbnails,
            shellCopyOwnerId: shellCopyOwnerId);
        DataContext = _viewModel;
        WorkspaceView.ConfigureShellFolderTabs(tabs.OpenShellFolderInTab);
        WorkspaceView.ConfigureLocalVideoPlayer(openLocalVideoInMiniPlayer);
        WorkspaceView.ConfigureQuickLook(settings.QuickLook);
        WorkspaceView.QuickLookPaneLayoutChanged += quickLookPaneLayoutChanged;
        WorkspaceView.QuickLookTextDisplayChanged += quickLookTextDisplayChanged;
        WorkspaceView.ConfigureGestures(
            settings,
            executeGestureCommandAsync,
            tryBeginGestureInteraction,
            completeInteraction,
            tryBeginRightWheelInteraction);

        Activated += (_, _) => _viewModel.RefreshOpenTabs();
        StateChanged += (_, _) => WorkspaceView.EndInteractiveResizePreview();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            WorkspaceView.BeginInteractiveResizePreview();
        }
        else if (msg == WmExitSizeMove)
        {
            WorkspaceView.EndInteractiveResizePreview();
        }

        return IntPtr.Zero;
    }

    public void ConfigureRecentItems(RecentItemsService service, bool recordActions)
    {
        _viewModel.ConfigureRecentItems(service, recordActions);
    }

    public void ConfigureRecentItemOpening(Func<RecentItem, Task> openRecentItemAsync)
    {
        WorkspaceView.ConfigureRecentItemOpening(openRecentItemAsync);
    }

    public void ConfigureRecentItemThumbnailFetching(
        Func<RecentItem, Task<string>> fetchRecentItemThumbnailAsync)
    {
        WorkspaceView.ConfigureRecentItemThumbnailFetching(fetchRecentItemThumbnailAsync);
    }

    public void ConfigureExternalBrowserOpening(
        Func<ExternalBrowserKind, bool> isExternalBrowserAvailable,
        Func<IReadOnlyList<string>, ExternalBrowserKind, Task> openExternalBrowserAsync)
    {
        WorkspaceView.ConfigureExternalBrowserOpening(
            isExternalBrowserAvailable,
            openExternalBrowserAsync);
    }

    public void ConfigureWorkspaceNoteAppend(Func<Guid, Guid, Window?, Task> appendWorkspaceNoteAsync)
    {
        WorkspaceView.ConfigureWorkspaceNoteAppend(appendWorkspaceNoteAsync);
    }

    public void UpdateGestureSettings(AppSettings settings)
    {
        WorkspaceView.UpdateGestureSettings(settings);
    }

    public void UpdateQuickLookSettings(QuickLookSettings settings)
    {
        WorkspaceView.UpdateQuickLookSettings(settings);
    }

    public void UpdateQuickLookPaneLayout(QuickLookSettings settings)
    {
        WorkspaceView.UpdateQuickLookPaneLayout(settings);
    }

    public void RefreshOpenTabs()
    {
        WorkspaceView.RefreshOpenTabs();
    }

    public void ReloadWorkspace()
    {
        WorkspaceView.ReloadWorkspace();
    }

    public void CancelGesture()
    {
        WorkspaceView.CancelGesture();
    }

    internal IReadOnlyList<QuickLookHostDiagnosticSnapshot> GetQuickLookDiagnosticSnapshots()
    {
        return WorkspaceView.GetQuickLookDiagnosticSnapshots();
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        WorkspaceView.EndInteractiveResizePreview();
        WorkspaceView.Dispose();

        Content = null;
        DataContext = null;
        base.OnClosed(e);
    }
}
