using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using TabBrowser.Views;

namespace TabBrowser;

public partial class WorkspaceFolderWindow : Window
{
    private void DeletedItemsButton_Click(object sender, RoutedEventArgs e)
        => ModelessWindowService.Show(new WorkspaceTrashWindow(_viewModel.PersistenceService) { Owner = this });

    private readonly Guid _workspaceId;
    private readonly Guid _folderId;
    private readonly Views.WorkspaceGestureController _gestureController;
    private readonly WorkspaceOSViewModel _viewModel;
    private readonly Func<string?, string?, Task> _executeGestureCommandAsync;
    private bool _initialized;

    public Guid InputScopeId { get; }

    public WorkspaceFolderWindowPlacementSettings? CapturedWindowPlacement { get; private set; }

    public WorkspaceFolderWindow(
        WorkspaceService workspaceService,
        Guid workspaceId,
        FolderItem folder,
        Guid? selectedItemId,
        AppSettings settings,
        WorkspaceFolderWindowPlacementSettings? initialPlacement,
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
        ApplyWindowPlacement(initialPlacement);
        WindowPlacementVisibilityService.Attach(this);
        _workspaceId = workspaceId;
        _folderId = folder.Id;
        _executeGestureCommandAsync = executeGestureCommandAsync;
        InputScopeId = inputScopeId;
        _viewModel = new WorkspaceOSViewModel(
            workspaceService,
            workspaceId,
            new WorkspaceTabPort(openUrlAsync, openSessionTabsAsync,
                captureOpenTabs, captureCurrentTab, closeTabAsync, openShellFolderInTab, getDeferredRestoreMessage),
            new WorkspaceMiniPlayerPort(captureYouTubeMiniPlayers,
                captureCurrentYouTubeMiniPlayersAsync, openYouTubeMiniPlayerSetAsync),
            new WorkspaceWindowPort(openSessionTabsInNewWindowAsync, openFolderWindow, saveLastWorkspaceId, getDeferredRestoreMessage),
            new WorkspaceThumbnailPort(captureUrlThumbnailAsync),
            folder.Id,
            selectedItemId);
        DataContext = _viewModel;
        WorkspaceCanvas.OpenShellFolderInTab = openShellFolderInTab;
        WorkspaceCanvas.OpenLocalVideoInMiniPlayer = openLocalVideoInMiniPlayer;
        WorkspaceCanvas.ConfigureQuickLook(settings.QuickLook);
        WorkspaceCanvas.QuickLookPaneLayoutChanged += quickLookPaneLayoutChanged;
        WorkspaceCanvas.QuickLookTextDisplayChanged += quickLookTextDisplayChanged;
        _gestureController = new Views.WorkspaceGestureController(
            this,
            settings,
            ExecuteGestureCommandAsync,
            tryBeginGestureInteraction,
            completeInteraction,
            tryBeginRightWheelInteraction,
            gestureScope: GestureCommandScope.Workspace);
        Loaded += WorkspaceFolderWindow_Loaded;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            CapturedWindowPlacement = CreateWindowPlacement();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _gestureController.Dispose();
        WorkspaceCanvas.Dispose();
        _viewModel.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }

    public async Task ShowItemAsync(Guid? selectedItemId)
    {
        await _viewModel.LoadWorkspaceAsync(
            _workspaceId,
            folderId: _folderId,
            selectedItemId: selectedItemId);
    }

    public void UpdateGestureSettings(AppSettings settings)
    {
        _gestureController.UpdateSettings(settings);
    }

    public void UpdateQuickLookSettings(QuickLookSettings settings)
    {
        WorkspaceCanvas.ConfigureQuickLook(settings);
    }

    public void UpdateQuickLookPaneLayout(QuickLookSettings settings)
    {
        WorkspaceCanvas.UpdateQuickLookPaneLayout(settings);
    }

    public void ConfigureRecentItems(RecentItemsService service, bool recordActions)
    {
        _viewModel.ConfigureRecentItems(service, recordActions);
    }

    public void ConfigureExternalBrowserOpening(
        Func<ExternalBrowserKind, bool> isExternalBrowserAvailable,
        Func<IReadOnlyList<string>, ExternalBrowserKind, Task> openExternalBrowserAsync)
    {
        WorkspaceCanvas.ConfigureExternalBrowserOpening(
            isExternalBrowserAvailable,
            openExternalBrowserAsync);
    }

    public void ConfigureWorkspaceNoteAppend(Func<Guid, Guid, Window?, Task> appendWorkspaceNoteAsync)
    {
        WorkspaceCanvas.EditWorkspaceNoteAsync = appendWorkspaceNoteAsync;
    }

    public void RefreshOpenTabs()
    {
        _viewModel.RefreshOpenTabs();
    }

    public void CancelGesture()
    {
        _gestureController.Cancel();
    }

    internal QuickLookHostDiagnosticSnapshot GetQuickLookDiagnosticSnapshot()
    {
        return WorkspaceCanvas.GetQuickLookDiagnosticSnapshot();
    }

    private async Task ExecuteGestureCommandAsync(string? command, string? selectedText)
    {
        switch (command)
        {
            case ContextCommandProvider.WorkspaceArrangeCompactActionId:
                WorkspaceCanvas.ArrangeSelectedCompact();
                return;
            case ContextCommandProvider.WorkspaceRestoreArrangementActionId:
                WorkspaceCanvas.RestoreArrangement();
                return;
            case ContextCommandProvider.WorkspaceQuickLookActionId:
                await WorkspaceCanvas.ShowQuickLookForSelectionAsync();
                return;
        }

        await _executeGestureCommandAsync(command, selectedText);
    }

    private async void WorkspaceFolderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WorkspaceCanvas.TryHandleQuickLookKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None
            || Keyboard.FocusedElement is TextBoxBase
                or PasswordBox
                or ComboBox { IsEditable: true })
        {
            return;
        }

        if (e.Key is Key.D0 or Key.NumPad0)
        {
            WorkspaceCanvas.ResetPanOffset();
            e.Handled = true;
        }
    }

    private void ApplyWindowPlacement(WorkspaceFolderWindowPlacementSettings? placement)
    {
        if (WorkspaceFolderWindowPlacementProfile.Normalize(placement) is not { } normalized)
        {
            return;
        }

        var width = Math.Min(
            Math.Max(MinWidth, normalized.Width),
            Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        var height = Math.Min(
            Math.Max(MinHeight, normalized.Height),
            Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        var left = normalized.Left;
        var top = normalized.Top;

        if (!HasSufficientVisibleArea(left, top, width, height))
        {
            left = SystemParameters.WorkArea.Left + 40;
            top = SystemParameters.WorkArea.Top + 40;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private WorkspaceFolderWindowPlacementSettings? CreateWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        return WorkspaceFolderWindowPlacementProfile.Normalize(
            new WorkspaceFolderWindowPlacementSettings
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = Math.Max(MinWidth, bounds.Width),
                Height = Math.Max(MinHeight, bounds.Height)
            });
    }

    private static bool HasSufficientVisibleArea(double left, double top, double width, double height)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        var minimumVisibleWidth = Math.Min(80, width);
        var minimumVisibleHeight = Math.Min(32, height);

        return left + width >= screenLeft + minimumVisibleWidth
            && left <= screenRight - minimumVisibleWidth
            && top + height >= screenTop + minimumVisibleHeight
            && top <= screenBottom - minimumVisibleHeight;
    }
}
