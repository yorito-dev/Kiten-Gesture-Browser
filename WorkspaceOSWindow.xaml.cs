using System.Windows;
using System.Windows.Interop;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
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
        Func<string, Task> openUrlAsync,
        Func<IReadOnlyList<SessionTab>, Task<int>> openSessionTabsAsync,
        Func<IReadOnlyList<SessionTab>, Task> openSessionTabsInNewWindowAsync,
        Func<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>> captureYouTubeMiniPlayers,
        Func<Task<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>>> captureCurrentYouTubeMiniPlayersAsync,
        Func<YouTubeMiniPlayerSetItem, Task> openYouTubeMiniPlayerSetAsync,
        Func<string, string, Task<System.Windows.Media.ImageSource?>> captureUrlThumbnailAsync,
        Action<Guid> saveLastWorkspaceId,
        Func<IReadOnlyList<CapturedTabSnapshot>> captureOpenTabs,
        Func<CapturedTabSnapshot?> captureCurrentTab,
        Func<Guid, Task> closeTabAsync,
        Guid inputScopeId,
        Func<long?> tryBeginGestureInteraction,
        Func<long?> tryBeginRightWheelInteraction,
        Action<long> completeInteraction,
        Func<string?, string?, Task> executeGestureCommandAsync,
        Action<FolderItem, Guid?> openFolderWindow,
        Action<string> openShellFolderInTab,
        Action<string> openLocalVideoInMiniPlayer,
        EventHandler<QuickLookPaneLayoutChangedEventArgs> quickLookPaneLayoutChanged,
        EventHandler<QuickLookTextDisplayChangedEventArgs> quickLookTextDisplayChanged,
        Func<string?>? getDeferredRestoreMessage = null)
    {
        InitializeComponent();
        InputScopeId = inputScopeId;
        _viewModel = new WorkspaceOSViewModel(
            workspaceService,
            initialWorkspaceId,
            new WorkspaceTabPort(openUrlAsync, openSessionTabsAsync,
                captureOpenTabs, captureCurrentTab, closeTabAsync, openShellFolderInTab, getDeferredRestoreMessage),
            new WorkspaceMiniPlayerPort(captureYouTubeMiniPlayers,
                captureCurrentYouTubeMiniPlayersAsync, openYouTubeMiniPlayerSetAsync),
            new WorkspaceWindowPort(openSessionTabsInNewWindowAsync, openFolderWindow, saveLastWorkspaceId, getDeferredRestoreMessage),
            new WorkspaceThumbnailPort(captureUrlThumbnailAsync));
        DataContext = _viewModel;
        WorkspaceView.ConfigureShellFolderTabs(openShellFolderInTab);
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
